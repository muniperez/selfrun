using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.WorkspaceGateway;

public sealed class WorkspaceGatewayRelay : IDisposable
{
    public const string CookieName = "__Host-minicloud-workspace";
    private readonly IGatewayControlPlane control;
    private readonly DevelopmentWorkspaceGatewayOptions options;
    private readonly HttpClient client;
    private readonly object gate = new();
    private readonly Dictionary<string, int> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> waitingSessions = new(StringComparer.Ordinal);
    private TaskCompletionSource admissionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int waitingRequests;
    private readonly SemaphoreSlim certificateAdmissions = new(8);
    private int connections;
    private static readonly HashSet<string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Accept", "Accept-Language", "Content-Type", "Range", "If-None-Match", "If-Modified-Since", "User-Agent" };
    private static readonly HashSet<string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Content-Type", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified", "Vary" };
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Cookie", "Origin", "Referer", "Forwarded", "X-Real-IP", "Content-Length", "Connection",
      "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization" };

    public WorkspaceGatewayRelay(IGatewayControlPlane control, IOptions<DevelopmentWorkspaceGatewayOptions> configured)
    {
        this.control = control;
        options = configured.Value;
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxConnectionsPerServer = 32, ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseHeadersLength = 32, AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, ct) => PrivateGatewayTransport.ConnectAsync(context, options.RelayHost, ct)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var host = context.Request.Host.Value ?? "";
        if (!context.Request.IsHttps || !options.AcceptHost(host)) { context.Response.StatusCode = 421; return; }
        if (context.Request.Headers.Origin.Count > 0 &&
            context.Request.Headers.Origin.ToString() != "https://" + host)
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Path == "/__minicloud/launch")
        {
            if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Query.Count != 1 ||
                context.Request.Query["code"].Count != 1 || context.Request.Query["code"].ToString().Length != 64)
            { context.Response.StatusCode = 400; return; }
            try
            {
                var result = await control.RedeemAsync(host, context.Request.Query["code"].ToString(), context.RequestAborted);
                if (result is null) { context.Response.StatusCode = 401; return; }
                context.Response.Cookies.Append(CookieName, result.Session, new CookieOptions
                {
                    Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", Expires = result.ExpiresAt,
                    IsEssential = true
                });
                context.Response.Redirect("/");
            }

            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { context.Response.StatusCode = 503; }
            return;
        }
        if (context.Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site")
        { context.Response.StatusCode = 403; return; }
        if (context.Request.Path.StartsWithSegments("/__minicloud"))
        { context.Response.StatusCode = 404; return; }
        var session = context.Request.Cookies[CookieName];
        if (session?.Length != 64) { context.Response.StatusCode = 401; return; }
        var sessionKey = DevelopmentWorkspaceGatewayOptions.Digest(session);
        using var admissionTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        admissionTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.HttpTimeoutSeconds, 1, 300)));
        try
        {
            if (!await AdmitAsync(sessionKey, IsBootstrapRequest(context), admissionTimeout.Token))
            { context.Response.StatusCode = 429; return; }
        }
        catch (OperationCanceledException)
        { context.Response.StatusCode = 504; return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ConnectionLifetimeSeconds, 1, 3600)));
        try
        {
            var target = await control.ResolveAsync(host, session, lifetime.Token);
            if (!ValidTarget(target)) { context.Response.StatusCode = 401; return; }
            if (context.WebSockets.IsWebSocketRequest)
            {
                if (!HttpMethods.IsGet(context.Request.Method))
                { context.Response.StatusCode = 400; return; }
                if (target!.Bearer is not null && (context.Request.Path != "/ws" ||
                    context.Request.Query["orchestrationProtocol"] != "2" ||
                    (context.Request.Query.ContainsKey("wsTicket") &&
                     (context.Request.Query["wsTicket"].Count != 1 || string.IsNullOrEmpty(context.Request.Query["wsTicket"])))))
                { context.Response.StatusCode = 400; return; }
                await RelayWebSocketAsync(context, target!, host, session, lifetime);
            }
            else
            {
                if (context.Request.Headers.ContainsKey("Upgrade") || HttpMethods.IsConnect(context.Request.Method) ||
                    HttpMethods.IsTrace(context.Request.Method))
                { context.Response.StatusCode = 400; return; }
                await RelayHttpAsync(context, target!, lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or OperationCanceledException or IOException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = ex is OperationCanceledException ? 504 : 502;
            else context.Abort();
        }
        finally { Release(sessionKey); }
    }

    public async Task HandleCertificateAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.StatusCode = 404;
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !IPAddress.TryParse(options.TrustedProxyAddress, out var trustedProxy) ||
            !DevelopmentWorkspaceGatewayOptions.IsPrivateAddress(options.TrustedProxyAddress) ||
            context.Connection.RemoteIpAddress is not { } remote ||
            !NormalizeAddress(remote).Equals(NormalizeAddress(trustedProxy)) ||
            context.Request.Query.Count != 1 || context.Request.Query["domain"].Count != 1 ||
            !options.TryParseHost(context.Request.Query["domain"].ToString(), out _, out _))
            return;
        if (!await certificateAdmissions.WaitAsync(0, context.RequestAborted)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (await control.AuthorizeCertificateAsync(context.Request.Query["domain"].ToString(), timeout.Token))
                context.Response.StatusCode = 200;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        finally { certificateAdmissions.Release(); }
    }

    private static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private bool ValidTarget(DevelopmentWorkspaceGatewayTarget? target) =>
        target is not null && target.LoopbackPort >= options.MinimumRelayPort &&
        target.LoopbackPort <= options.MaximumRelayPort && (target.Bearer is null ||
            target.Bearer.Length is > 0 and <= 8192 && !target.Bearer.Any(char.IsControl));

    private bool IsBootstrapRequest(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest ||
            !options.TryParseHost(context.Request.Host.Value ?? "", out _, out var service) || service != "t3")
            return false;
        var request = context.Request;
        return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
                request.Path.StartsWithSegments("/assets") ||
            HttpMethods.IsGet(request.Method) &&
                (request.Path == "/.well-known/t3/environment" || request.Path == "/api/auth/session") ||
            HttpMethods.IsPost(request.Method) && request.Path == "/api/auth/websocket-ticket";
    }

    private bool AdmitUnderLock(string session)
    {
        var current = sessions.GetValueOrDefault(session);
        if (connections >= Math.Clamp(options.MaxConnections, 1, 1024) ||
            current >= Math.Clamp(options.MaxConnectionsPerSession, 1, 64)) return false;
        connections++;
        sessions[session] = current + 1;
        return true;
    }

    private async Task<bool> AdmitAsync(string session, bool waitForBootstrap, CancellationToken ct)
    {
        lock (gate)
        {
            if (AdmitUnderLock(session)) return true;
            if (!waitForBootstrap || waitingRequests >= Math.Clamp(options.MaxConnections, 1, 1024) * 4 ||
                waitingSessions.GetValueOrDefault(session) >= Math.Clamp(options.MaxConnectionsPerSession, 1, 64) * 32)
                return false;
            waitingRequests++;
            waitingSessions[session] = waitingSessions.GetValueOrDefault(session) + 1;
        }
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Task changed;
                lock (gate)
                {
                    if (AdmitUnderLock(session)) return true;
                    changed = admissionChanged.Task;
                }
                await changed.WaitAsync(ct);
            }
        }
        finally
        {
            lock (gate)
            {
                waitingRequests--;
                if (--waitingSessions[session] == 0) waitingSessions.Remove(session);
            }
        }
    }

    private void Release(string session)
    {
        TaskCompletionSource changed;
        lock (gate)
        {
            connections--;
            if (--sessions[session] == 0) sessions.Remove(session);
            changed = admissionChanged;
            admissionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    private Uri Upstream(HttpContext context, int port, string scheme)
    {
        var authority = new UriBuilder(scheme, options.RelayHost, port).Uri.GetLeftPart(UriPartial.Authority);
        return new($"{authority}{context.Request.Path.ToUriComponent()}{context.Request.QueryString}");
    }

    private async Task RelayHttpAsync(HttpContext context, DevelopmentWorkspaceGatewayTarget target, CancellationToken ct)
    {
        var request = context.Request;
        var maxBody = Math.Clamp(options.MaxRequestBodyBytes, 1024, 16 * 1024 * 1024);
        if (request.ContentLength > maxBody) { context.Response.StatusCode = 413; return; }
        var uri = Upstream(context, target.LoopbackPort, "http");
        var normalizedPath = new PathString(Uri.UnescapeDataString(uri.AbsolutePath));
        // Check the canonical upstream path, not a dot-segment spelling supplied by a client.
        if (target.Bearer is not null && (normalizedPath.StartsWithSegments("/oauth") ||
            normalizedPath.StartsWithSegments("/mcp") ||
            normalizedPath.StartsWithSegments("/api/auth") &&
            !(HttpMethods.IsGet(request.Method) && normalizedPath == "/api/auth/session") &&
            !(HttpMethods.IsPost(request.Method) && normalizedPath == "/api/auth/websocket-ticket")))
        { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.HttpTimeoutSeconds, 1, 300)));
        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            upstream.Content = new StreamContent(new BoundedReadStream(request.Body, maxBody));
        var connectionHeaders = request.Headers.Connection.ToString().Split(',').Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
            if ((RequestHeaders.Contains(header.Key) || target.Bearer is null &&
                !PrivateHeaders.Contains(header.Key) && !connectionHeaders.Contains(header.Key) &&
                !header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.StartsWith("X-Minicloud-", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)) &&
                !upstream.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                upstream.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        upstream.Headers.Host = request.Host.Value;
        upstream.Headers.Add("X-Forwarded-Proto", "https");
        upstream.Headers.Add("X-Forwarded-Host", request.Host.Value);
        if (request.Headers.ContainsKey("Origin")) upstream.Headers.Add("Origin", "https://" + request.Host.Value);
        if (target.Bearer is not null)
        {
            upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Bearer);
            upstream.Headers.Add("x-t3-orchestration-protocol", "2");
        }
        else
        {
            var cookies = ForwardedCookies(request);
            if (cookies.Length > 0) upstream.Headers.Add("Cookie", cookies);
        }
        using var response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (ResponseHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        if (response.Headers.Location is { } location)
        {
            if (!location.IsAbsoluteUri) context.Response.Headers.Location = location.ToString();
            else if (location.Host == options.RelayHost && location.Port == target.LoopbackPort)
                context.Response.Headers.Location = "https://" + request.Host.Value + location.PathAndQuery;
            else if (location.Scheme == "https") context.Response.Headers.Location = location.ToString();
            else { context.Response.StatusCode = 502; return; }
        }
        if (target.Bearer is null && response.Headers.TryGetValues("Set-Cookie", out var appCookies))
            foreach (var cookie in appCookies)
                if (!cookie.StartsWith(CookieName + "=", StringComparison.OrdinalIgnoreCase) &&
                    !cookie.Contains("domain=", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers.Append("Set-Cookie", cookie);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        await new BoundedReadStream(body, 32 * 1024 * 1024).CopyToAsync(context.Response.Body, 16384, timeout.Token);
    }

    private async Task RelayWebSocketAsync(HttpContext context, DevelopmentWorkspaceGatewayTarget target,
        string host, string session, CancellationTokenSource lifetime)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        upstream.Options.SetRequestHeader("Host", host);
        upstream.Options.SetRequestHeader("Origin", "https://" + host);
        upstream.Options.SetRequestHeader("X-Forwarded-Proto", "https");
        if (target.Bearer is not null) upstream.Options.SetRequestHeader("Authorization", "Bearer " + target.Bearer);
        else
        {
            var cookies = ForwardedCookies(context.Request);
            if (cookies.Length > 0) upstream.Options.SetRequestHeader("Cookie", cookies);
        }
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            if (protocol.Length > 128 || context.WebSockets.WebSocketRequestedProtocols.Count > 8)
            { context.Response.StatusCode = 400; return; }
            upstream.Options.AddSubProtocol(protocol);
        }
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(10));
        await upstream.ConnectAsync(Upstream(context, target.LoopbackPort, "ws"), client, connect.Token);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        var monitor = MonitorAsync();
        var lastActivityReport = 0L;
        try
        {
            await Task.WhenAny(
                PumpAsync(downstream, upstream, lifetime.Token, target.Bearer is null ? null : ObserveInputAsync),
                PumpAsync(upstream, downstream, lifetime.Token), monitor);
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Task.WhenAll(CloseAsync(upstream, close.Token), CloseAsync(downstream, close.Token));
        }
        finally { lifetime.Cancel(); upstream.Abort(); downstream.Abort(); await monitor; }

        async Task<bool> ObserveInputAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            if (!T3UsefulInputObserver.IsUsefulInput(message)) return true;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastActivityReport != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastActivityReport, now) < TimeSpan.FromSeconds(1))
                return true;
            if (!await control.ReportActivityAsync(host, session, ct)) return false;
            lastActivityReport = now;
            return true;
        }

        async Task MonitorAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.ReauthorizationSeconds, 1, 10)), lifetime.Token);
                    var refreshed = await control.ResolveAsync(host, session, lifetime.Token);
                    if (!ValidTarget(refreshed) || refreshed != target) { lifetime.Cancel(); return; }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException) { lifetime.Cancel(); }
        }
    }

    private async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken ct,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<bool>>? observe = null)
    {
        var buffer = new byte[16384];
        var messageBytes = 0;
        using var message = observe is null ? null : new MemoryStream(16384);
        var observeMessage = observe is not null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                messageBytes = checked(messageBytes + result.Count);
                if (messageBytes > Math.Clamp(options.MaxWebSocketMessageBytes, 1024, 4 * 1024 * 1024)) return;
                if (observeMessage && messageBytes <= T3UsefulInputObserver.MaximumMessageBytes)
                    message!.Write(buffer, 0, result.Count);
                else observeMessage = false;
                if (result.EndOfMessage && observeMessage &&
                    !await observe!(message!.GetBuffer().AsMemory(0, (int)message.Length), ct)) return;
                await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
                if (result.EndOfMessage)
                {
                    messageBytes = 0;
                    message?.SetLength(0);
                    observeMessage = observe is not null;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException) { }
    }

    private static async Task CloseAsync(WebSocket socket, CancellationToken ct)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Session closed", ct);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    public void Dispose()
    {
        client.Dispose();
        certificateAdmissions.Dispose();
    }

    private static string ForwardedCookies(HttpRequest request) =>
        string.Join("; ", request.Headers.Cookie.ToString().Split(';').Select(x => x.Trim()).Where(x =>
            x.IndexOf('=') is var separator && separator > 0 &&
            !x[..separator].Trim().Equals(CookieName, StringComparison.OrdinalIgnoreCase)));

    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private long bytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => bytes; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, maximum - bytes + 1)], cancellationToken);
            bytes += count;
            if (bytes > maximum) throw new IOException("Relay body limit exceeded.");
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
