using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;
using Minicloud.WorkspaceGateway;

namespace Minicloud.WorkspaceGateway.Tests;

public sealed class GatewayRelayTests
{
    [Fact]
    public async Task HttpAuthenticatesSingleUseLaunchAndIgnoresSpoofedRoutingHeaders()
    {
        await using var fixture = await Fixture.StartAsync();
        using var anonymous = await fixture.SendAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var launch = await fixture.SendAsync("/__minicloud/launch?code=" + Fixture.Code);
        Assert.Equal(HttpStatusCode.Redirect, launch.StatusCode);
        var cookie = Assert.Single(launch.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("Domain", cookie, StringComparison.OrdinalIgnoreCase);
        using var replay = await fixture.SendAsync("/__minicloud/launch?code=" + Fixture.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/t3/environment");
        request.Headers.Add("Cookie", fixture.Cookie);
        request.Headers.Add("Authorization", "Bearer attacker");
        request.Headers.Add("X-Forwarded-Host", "169.254.169.254");
        request.Headers.Add("X-Forwarded-Proto", "http");
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Bearer secret-t3", body);
        Assert.Contains(fixture.Host, body);
        Assert.Contains("https", body);
        Assert.DoesNotContain("attacker", body);
        Assert.DoesNotContain("169.254", body);
        Assert.DoesNotContain(Fixture.Session, body);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("https://evil.example", null, 403)]
    [InlineData(null, "evil.example", 421)]
    [InlineData(null, "vm-forged-g1.workspace.example", 401)]
    public async Task RejectsOriginAndHostConfusion(string? origin, string? host, int status)
    {
        await using var fixture = await Fixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", fixture.Cookie);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        using var result = await fixture.Client.SendAsync(request);
        Assert.Equal(status, (int)result.StatusCode);
    }

    [Fact]
    public async Task RejectsOversizeBodyArbitraryTargetAndCredentialManagement()
    {
        await using var fixture = await Fixture.StartAsync();
        using var body = new HttpRequestMessage(HttpMethod.Post, "/submit") { Content = new StringContent(new string('x', 2048)) };
        body.Headers.Add("Cookie", fixture.Cookie);
        using var oversized = await fixture.Client.SendAsync(body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var credentials = await fixture.SendAsync("/api/auth/sessions", authenticated: true);
        Assert.Equal(HttpStatusCode.Forbidden, credentials.StatusCode);
        using var oauth = await fixture.SendAsync("/oauth/token", authenticated: true);
        Assert.Equal(HttpStatusCode.Forbidden, oauth.StatusCode);
        fixture.Control.Target = new(80, "secret");
        using var invalidPort = await fixture.SendAsync("/", authenticated: true);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidPort.StatusCode);
        fixture.Control.Target = null;
        using var revoked = await fixture.SendAsync("/", authenticated: true);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task WebSocketForwardsProtocolTwoTicketAndDisconnectsOnRevocation()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync("/ws?wsTicket=ticket-only&orchestrationProtocol=2");
        await socket.SendAsync("terminal-input"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var bytes = new byte[128];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(bytes, timeout.Token);
        Assert.Equal("terminal-input", Encoding.UTF8.GetString(bytes, 0, result.Count));
        Assert.Contains("wsTicket=ticket-only", fixture.LastWebSocketQuery);
        Assert.Contains("orchestrationProtocol=2", fixture.LastWebSocketQuery);
        fixture.Control.Target = null;
        var close = await socket.ReceiveAsync(bytes, timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
    }

    [Fact]
    public async Task WebSocketConnectionCapAndMessageCapAreEnforced()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync("/ws?wsTicket=ticket-only&orchestrationProtocol=2");
        using var capped = await fixture.SendAsync("/", authenticated: true);
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
        await socket.SendAsync(new byte[2048], WebSocketMessageType.Binary, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(new byte[32], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }

    [Fact]
    public async Task BootstrapAssetsWaitForCapacityWithoutRelaxingOtherConnectionCaps()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = fixture.SendAsync("/assets/held.js", authenticated: true);
        await fixture.AssetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = fixture.SendAsync("/assets/main.css", authenticated: true);
        using var rejected = await fixture.SendAsync("/", authenticated: true);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        fixture.ReleaseAsset.TrySetResult();
        using var firstResponse = await first;
        using var assetResponse = await queued;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, assetResponse.StatusCode);
    }

    [Fact]
    public async Task BootstrapQueueIsBoundedAndCanceledWaitersReleaseCapacity()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = fixture.SendAsync("/assets/held.js", authenticated: true);
        await fixture.AssetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        using var canceledRequest = new HttpRequestMessage(HttpMethod.Get, "/assets/canceled.js");
        canceledRequest.Headers.Add("Cookie", fixture.Cookie);
        var canceled = fixture.Client.SendAsync(canceledRequest, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var requests = Enumerable.Range(0, 33).Select(i =>
            fixture.SendAsync($"/assets/chunk-{i}.js", authenticated: true)).ToList();
        var rejected = await Task.WhenAny(requests).WaitAsync(TimeSpan.FromSeconds(5));
        using var rejectedResponse = await rejected;
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        requests.Remove(rejected);
        fixture.ReleaseAsset.TrySetResult();
        using var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        foreach (var request in requests)
        {
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task HttpTimeoutIsBounded()
    {
        await using var fixture = await Fixture.StartAsync();
        using var response = await fixture.SendAsync("/slow", authenticated: true);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task RegisteredPreviewForwardsOnlyItsOwnCookiesAndNoT3Bearer()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Control.Target = fixture.Control.Target! with { Bearer = null };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", fixture.Cookie + "; preview_session=app-only");
        using var response = await fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("preview_session=app-only", body);
        Assert.DoesNotContain(Fixture.Session, body);
        Assert.DoesNotContain("secret-t3", body);
    }

    [Fact]
    public async Task ExactPrivateDnsRelayHostSupportsHttpAndWebSocket()
    {
        await using var fixture = await Fixture.StartAsync("localhost");
        using var response = await fixture.SendAsync("/", authenticated: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var socket = await fixture.ConnectAsync("/ws?wsTicket=ticket-only&orchestrationProtocol=2");
        await socket.SendAsync("private-dns"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[32];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("private-dns", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    [Theory]
    [InlineData("172.29.110.2", true, false, 200)]
    [InlineData("::ffff:172.29.110.2", true, false, 200)]
    [InlineData("172.29.110.4", true, false, 404)]
    [InlineData("127.0.0.1", true, false, 404)]
    [InlineData("172.29.110.4", true, true, 404)]
    [InlineData("172.29.110.2", false, false, 404)]
    public async Task CertificateAskTrustsOnlyDirectCaddySource(string remote, bool knownHost, bool forgeHeaders, int status)
    {
        await using var fixture = await Fixture.StartAsync();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Method = "GET";
        context.Request.Path = "/internal/certificate";
        context.Request.Host = new HostString("172.29.110.3", 8080);
        context.Request.QueryString = new("?domain=" + (knownHost ? fixture.Host : fixture.Host.Replace("-g1.", "-g2.")));
        if (forgeHeaders)
        {
            context.Request.Headers["X-Forwarded-For"] = "172.29.110.2";
            context.Request.Headers["Forwarded"] = "for=172.29.110.2;proto=https";
        }
        await fixture.AskAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(remote is "172.29.110.2" or "::ffff:172.29.110.2" ? 1 : 0, fixture.Control.CertificateCalls);
    }

    [Theory]
    [InlineData("https://vm-00000000000000000000000000000000-g1.workspace.example")]
    [InlineData("vm-00000000000000000000000000000000-g1.workspace.example.evil")]
    [InlineData("vm-00000000000000000000000000000000-g1.workspace.example:443")]
    [InlineData("vm-00000000000000000000000000000000-g1.workspace.example.")]
    [InlineData("*.workspace.example")]
    [InlineData("vm-forged-g1.workspace.example")]
    public async Task CertificateAskRejectsNonCanonicalDomainsWithoutApiCall(string domain)
    {
        await using var fixture = await Fixture.StartAsync();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.29.110.2");
        context.Request.Method = "GET";
        context.Request.QueryString = new("?domain=" + Uri.EscapeDataString(domain));
        await fixture.AskAsync(context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(0, fixture.Control.CertificateCalls);
    }

    [Fact]
    public async Task TerminalWriteRenewsActivityWithoutChangingItsEffectPayload()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync("/ws?wsTicket=ticket-only&orchestrationProtocol=2");
        foreach (var passive in new[] { """{"_tag":"Ping"}""", T3UsefulInputObserverTests.Observe, "unrecognized-frame" })
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(passive), WebSocketMessageType.Text, true, CancellationToken.None);
            Assert.Equal(passive, await ReadMessageAsync(socket));
        }
        Assert.Equal(0, fixture.Control.ActivityCalls);
        var input = Encoding.UTF8.GetBytes(T3UsefulInputObserverTests.TerminalWrite);
        await socket.SendAsync(input.AsMemory(0, 40), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(input.AsMemory(40), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(T3UsefulInputObserverTests.TerminalWrite, await ReadMessageAsync(socket));
        Assert.Equal(1, fixture.Control.ActivityCalls);
        Assert.Equal((fixture.Host, Fixture.Session), fixture.Control.LastActivityBinding);
    }

    [Fact]
    public async Task DeniedUsefulInputClosesSocketBeforeForwardingAndPreviewNeverReportsIt()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Control.ActivityAllowed = false;
        using var socket = await fixture.ConnectAsync("/ws?wsTicket=ticket-only&orchestrationProtocol=2");
        await socket.SendAsync(Encoding.UTF8.GetBytes(T3UsefulInputObserverTests.TerminalWrite), WebSocketMessageType.Text, true, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(new byte[512], timeout.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(1, fixture.Control.ActivityCalls);

        await using var preview = await Fixture.StartAsync();
        preview.Control.Target = preview.Control.Target! with { Bearer = null };
        using var previewSocket = await preview.ConnectAsync("/preview-events");
        await previewSocket.SendAsync(Encoding.UTF8.GetBytes(T3UsefulInputObserverTests.TerminalWrite), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(T3UsefulInputObserverTests.TerminalWrite, await ReadMessageAsync(previewSocket));
        Assert.Equal(0, preview.Control.ActivityCalls);
    }

    private static async Task<string> ReadMessageAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private sealed class FakeControl(string host) : IGatewayControlPlane
    {
        public DevelopmentWorkspaceGatewayTarget? Target { get; set; }
        public int CertificateCalls { get; private set; }
        public int ActivityCalls { get; private set; }
        public bool ActivityAllowed { get; set; } = true;
        public (string Host, string Session)? LastActivityBinding { get; private set; }
        private int redeemed;
        public Task<bool> ReportActivityAsync(string requestedHost, string session, CancellationToken ct)
        {
            ActivityCalls++;
            LastActivityBinding = (requestedHost, session);
            return Task.FromResult(ActivityAllowed && requestedHost == host && session == Fixture.Session && Target is not null);
        }
        public Task<bool> AuthorizeCertificateAsync(string domain, CancellationToken ct)
        {
            CertificateCalls++;
            return Task.FromResult(domain == host && Target is not null);
        }
        public Task<DevelopmentWorkspaceGatewayRedemption?> RedeemAsync(string requestedHost, string code, CancellationToken ct) =>
            Task.FromResult(requestedHost == host && code == Fixture.Code && Interlocked.Exchange(ref redeemed, 1) == 0
                ? new DevelopmentWorkspaceGatewayRedemption(Fixture.Session, DateTimeOffset.UtcNow.AddMinutes(2)) : null);
        public Task<DevelopmentWorkspaceGatewayTarget?> ResolveAsync(string requestedHost, string session, CancellationToken ct) =>
            Task.FromResult(requestedHost == host && session == Fixture.Session ? Target : null);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Code = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string Session = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public string Host { get; private set; } = "";
        public string Cookie => $"{WorkspaceGatewayRelay.CookieName}={Session}";
        public string? LastWebSocketQuery { get; private set; }
        public HttpClient Client { get; private set; } = null!;
        public FakeControl Control { get; private set; } = null!;
        public TaskCompletionSource AssetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAsset { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebApplication upstream = null!;
        private TestServer gateway = null!;
        private WebApplication gatewayApplication = null!;
        private WorkspaceGatewayRelay relay = null!;

        public static async Task<Fixture> StartAsync(string relayHost = "127.0.0.1")
        {
            var fixture = new Fixture();
            var options = new DevelopmentWorkspaceGatewayOptions
            {
                Enabled = true, GatewayDomain = "workspace.example", ServiceCredential = new string('c', 32),
                MinimumRelayPort = 1024, MaximumRelayPort = 65535, MaxRequestBodyBytes = 1024,
                MaxWebSocketMessageBytes = 1024, MaxConnectionsPerSession = 1, ReauthorizationSeconds = 1,
                HttpTimeoutSeconds = 1
            };
            options.RelayHost = relayHost;
            options.TrustedProxyAddress = "172.29.110.2";
            fixture.Host = options.Host("workspacevm_0123456789abcdef0123456789abcdef", 1, "t3")!;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            fixture.upstream = builder.Build();
            fixture.upstream.UseWebSockets();
            fixture.upstream.Run(async context =>
            {
                if (context.Request.Path == "/assets/held.js")
                {
                    fixture.AssetStarted.TrySetResult();
                    await fixture.ReleaseAsset.Task.WaitAsync(context.RequestAborted);
                }
                if (context.Request.Path == "/slow") { await Task.Delay(3000, context.RequestAborted); return; }
                if (context.WebSockets.IsWebSocketRequest)
                {
                    fixture.LastWebSocketQuery = context.Request.QueryString.Value;
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[4096];
                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
                        }
                    }
                    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
                    return;
                }
                await context.Response.WriteAsync($"{context.Request.Headers.Authorization}|{context.Request.Host}|{context.Request.Headers["X-Forwarded-Host"]}|{context.Request.Headers["X-Forwarded-Proto"]}|{context.Request.Headers.Cookie}");
            });
            await fixture.upstream.StartAsync();
            var address = fixture.upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            fixture.Control = new FakeControl(fixture.Host) { Target = new(new Uri(address).Port, "secret-t3") };
            fixture.relay = new WorkspaceGatewayRelay(fixture.Control, Options.Create(options));
            var gatewayBuilder = WebApplication.CreateBuilder();
            gatewayBuilder.Logging.ClearProviders();
            gatewayBuilder.WebHost.UseTestServer();
            fixture.gatewayApplication = gatewayBuilder.Build();
            fixture.gatewayApplication.UseWebSockets();
            fixture.gatewayApplication.Run(fixture.relay.HandleAsync);
            await fixture.gatewayApplication.StartAsync();
            fixture.gateway = fixture.gatewayApplication.GetTestServer();
            fixture.Client = fixture.gateway.CreateClient();
            fixture.Client.BaseAddress = new Uri("https://" + fixture.Host);
            return fixture;
        }

        public Task<HttpResponseMessage> SendAsync(string path, bool authenticated = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (authenticated) request.Headers.Add("Cookie", Cookie);
            return Client.SendAsync(request);
        }

        public Task<WebSocket> ConnectAsync(string path)
        {
            var client = gateway.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["Cookie"] = Cookie;
            return client.ConnectAsync(new Uri("wss://" + Host + path), CancellationToken.None);
        }

        public Task AskAsync(HttpContext context) => relay.HandleCertificateAsync(context);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await gatewayApplication.DisposeAsync();
            relay.Dispose();
            await upstream.DisposeAsync();
        }
    }
}
