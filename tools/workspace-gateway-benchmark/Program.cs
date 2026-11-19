using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Minicloud.Common.Services;
using Minicloud.WorkspaceGateway;

// Synthetic workspace gateway benchmark. The real gateway Program runs in a child process with the recommended
// container shape (1 visible CPU, 192 MiB GC heap limit), behind a loopback "trusted proxy" that supplies
// X-Forwarded-Proto. A fake control plane and a fake T3 upstream run in this process. No external services.
if (args is ["child"]) return GatewayChildProcess.Run();
if (args is not [var scenario] || scenario is not ("footprint" or "relay" or "admission"))
{
    Console.Error.WriteLine("Usage: WorkspaceGatewayBenchmark <footprint|relay|admission>");
    return 2;
}

const string Credential = "synthetic-gateway-credential-0123456789abcdef";
var gatewayOptions = new DevelopmentWorkspaceGatewayOptions
{
    Enabled = true, GatewayDomain = "workspace.example", ServiceCredential = Credential,
    MinimumRelayPort = 1024, MaximumRelayPort = 65535
};
var host = gatewayOptions.Host("synthetic-vm", 1, "t3")!;
await using var upstream = await FakeUpstream.StartAsync();
await using var control = await FakeControlPlane.StartAsync(upstream.Port, Credential);
using var gateway = await GatewayChild.StartAsync(control.Port, Credential, gatewayOptions.GatewayDomain);
var driver = new Driver(gateway, host, upstream, control);
object result = scenario switch
{
    "footprint" => await driver.FootprintAsync(),
    "relay" => await driver.RelayAsync(),
    _ => await driver.AdmissionAsync()
};
Console.WriteLine(JsonSerializer.Serialize(new
{
    environment = new
    {
        dotnet = Environment.Version.ToString(), os = RuntimeInformation.OSDescription,
        arch = RuntimeInformation.OSArchitecture.ToString(), hostCpus = Environment.ProcessorCount,
        gatewayProcessorCount = 1, gatewayGcHeapHardLimitMiB = 192,
        configuration =
#if DEBUG
            "Debug"
#else
            "Release"
#endif
    },
    scenario,
    result
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

sealed class Driver(GatewayChild gateway, string host, FakeUpstream upstream, FakeControlPlane control)
{
    private static readonly int[] SocketSteps = [0, 32, 64, 128];

    public async Task<object> FootprintAsync()
    {
        await WarmAsync();
        var steps = new List<object>();
        var sockets = new List<ClientWebSocket>();
        var baseline = await gateway.StatsAsync(gc: true);
        steps.Add(Step(0, baseline));
        GatewayStats last = baseline;
        foreach (var target in SocketSteps.Skip(1))
        {
            while (sockets.Count < target)
            {
                var socket = await ConnectAsync(Session(sockets.Count / 8));
                await EchoAsync(socket, Payload(512, sockets.Count));
                sockets.Add(socket);
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
            last = await gateway.StatsAsync(gc: true);
            steps.Add(Step(target, last));
        }

        var resolvesBefore = control.Resolves;
        var idleStart = await gateway.StatsAsync(gc: false);
        var idleWatch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(30));
        var idleEnd = await gateway.StatsAsync(gc: false);
        var idleSeconds = idleWatch.Elapsed.TotalSeconds;
        var idle = new
        {
            openSockets = sockets.Count,
            seconds = Math.Round(idleSeconds, 1),
            cpuMsPerSecond = Math.Round((idleEnd.CpuMs - idleStart.CpuMs) / idleSeconds, 2),
            allocatedKiBPerSecond = Math.Round((idleEnd.AllocatedBytes - idleStart.AllocatedBytes) / 1024.0 / idleSeconds, 1),
            controlPlaneResolvesPerSecond = Math.Round((control.Resolves - resolvesBefore) / idleSeconds, 2),
            logLinesPerSecond = Math.Round((idleEnd.LogLines - idleStart.LogLines) / idleSeconds, 2),
        };

        var closeWatch = Stopwatch.StartNew();
        var closeResults = await Task.WhenAll(sockets.Select(CloseAsync));
        var closeMs = closeWatch.Elapsed.TotalMilliseconds;
        foreach (var socket in sockets) socket.Dispose();
        var drained = await WaitForAsync(stats => stats.ActiveConnections == 0);
        var afterClose = await gateway.StatsAsync(gc: true);
        var top = SocketSteps[^1];
        return new
        {
            steps,
            perSocketAt128 = new
            {
                rssKiB = Math.Round((last.RssBytes - baseline.RssBytes) / 1024.0 / top, 1),
                gcHeapKiB = Math.Round((last.GcHeapBytes - baseline.GcHeapBytes) / 1024.0 / top, 1),
                gcCommittedKiB = Math.Round((last.GcCommittedBytes - baseline.GcCommittedBytes) / 1024.0 / top, 1),
            },
            idle,
            close = new
            {
                sockets = closeResults.Length,
                completed = closeResults.Count(r => r is not null),
                statuses = closeResults.GroupBy(r => r?.ToString() ?? "failed").ToDictionary(g => g.Key, g => g.Count()),
                totalMs = Math.Round(closeMs, 1),
                activeConnectionsDrained = drained,
                retainedRssKiB = Math.Round((afterClose.RssBytes - baseline.RssBytes) / 1024.0, 0),
                retainedGcHeapKiB = Math.Round((afterClose.GcHeapBytes - baseline.GcHeapBytes) / 1024.0, 0),
                gatewayCloseOutcomes = afterClose.WebSocketCloses,
            },
            upstreamWebSockets = upstream.WebSocketsAccepted,
        };
    }

    public async Task<object> RelayAsync()
    {
        await WarmAsync();
        const int Concurrency = 32;
        const int Messages = 300;
        var sockets = new List<ClientWebSocket>();
        for (var i = 0; i < Concurrency; i++) sockets.Add(await ConnectAsync(Session(i / 8)));
        var runs = new List<object>();
        foreach (var size in new[] { 1024, 16 * 1024 })
        {
            foreach (var socket in sockets) await EchoAsync(socket, Payload(size, 0));
            await gateway.ResetAsync();
            var before = await gateway.StatsAsync(gc: false);
            var latencies = new ConcurrentBag<double>();
            var failures = 0;
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(sockets.Select(async (socket, index) =>
            {
                var payload = Payload(size, index);
                for (var n = 0; n < Messages; n++)
                {
                    var start = Stopwatch.GetTimestamp();
                    if (!await EchoAsync(socket, payload)) { Interlocked.Increment(ref failures); return; }
                    latencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }));
            var elapsed = watch.Elapsed.TotalSeconds;
            var after = await gateway.StatsAsync(gc: false);
            var relayedMiB = 2.0 * size * latencies.Count / 1024 / 1024;
            runs.Add(new
            {
                messageBytes = size, concurrency = Concurrency, messagesPerSocket = Messages,
                roundTrips = latencies.Count, failures,
                roundTripsPerSecond = Math.Round(latencies.Count / elapsed, 1),
                relayedMiBPerSecond = Math.Round(relayedMiB / elapsed, 2),
                roundTripMs = Percentiles(latencies),
                gatewayCpuMsPerRelayedMiB = Math.Round((after.CpuMs - before.CpuMs) / relayedMiB, 1),
                gatewayAllocatedKiBPerRoundTrip = Math.Round((after.AllocatedBytes - before.AllocatedBytes) / 1024.0 / latencies.Count, 2),
                gatewayAllocatedMiBPerRelayedMiB = Math.Round((after.AllocatedBytes - before.AllocatedBytes) / 1024.0 / 1024 / relayedMiB, 2),
                gatewayGen2Collections = after.Gen2Collections - before.Gen2Collections,
                gatewayRelayBytes = after.RelayBytes,
            });
        }
        var closes = await Task.WhenAll(sockets.Select(CloseAsync));
        foreach (var socket in sockets) socket.Dispose();
        await WaitForAsync(stats => stats.ActiveConnections == 0);
        var final = await gateway.StatsAsync(gc: false);
        return new
        {
            runs,
            unexpectedDisconnects = closes.Count(c => c is null),
            gatewayCloseOutcomes = final.WebSocketCloses,
            activityReports = control.Activities,
        };
    }

    public async Task<object> AdmissionAsync()
    {
        await WarmAsync();
        // Bootstrap requests from one browser session above the per-session cap (16) queue for admission.
        await gateway.ResetAsync();
        upstream.ResetConcurrency();
        var start = await gateway.StatsAsync(gc: false);
        const int Clients = 48;
        const int RequestsPerClient = 20;
        var latencies = new ConcurrentBag<double>();
        var statuses = new ConcurrentDictionary<int, int>();
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Clients).Select(async _ =>
        {
            for (var n = 0; n < RequestsPerClient; n++)
            {
                    var started = Stopwatch.GetTimestamp();
                var status = await GetAsync("/assets/app.js", Session(100));
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                statuses.AddOrUpdate(status, 1, (_, c) => c + 1);
            }
        }));
        var elapsed = watch.Elapsed.TotalSeconds;
        var queued = await gateway.StatsAsync(gc: false);
        var queuedResult = new
        {
            clients = Clients, requests = Clients * RequestsPerClient, upstreamDelayMs = FakeUpstream.AssetDelayMs,
            responseBytes = FakeUpstream.AssetBytes,
            statuses = statuses.ToDictionary(p => p.Key.ToString(), p => p.Value),
            requestsPerSecond = Math.Round(latencies.Count / elapsed, 1),
            clientLatencyMs = Percentiles(latencies),
            admissionWaitMs = queued.AdmissionWaitMs,
            upstreamMaxConcurrent = upstream.MaxConcurrent,
            gatewayCpuMsPerRequest = Math.Round((queued.CpuMs - start.CpuMs) / latencies.Count, 3),
            gatewayAllocatedKiBPerRequest = Math.Round((queued.AllocatedBytes - start.AllocatedBytes) / 1024.0 / latencies.Count, 1),
        };

        // Non-bootstrap requests over the per-session cap fail fast with 429 instead of queueing.
        await gateway.ResetAsync();
        upstream.ResetConcurrency();
        var rejectStatuses = new ConcurrentDictionary<int, int>();
        var rejectLatencies = new ConcurrentBag<double>();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
        {
            var start = Stopwatch.GetTimestamp();
            var status = await GetAsync("/held", Session(101));
            rejectLatencies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            rejectStatuses.AddOrUpdate(status, 1, (_, c) => c + 1);
        }));
        var rejected = await gateway.StatsAsync(gc: false);
        return new
        {
            bootstrapQueue = queuedResult,
            nonBootstrapOverCap = new
            {
                requests = 32, upstreamDelayMs = FakeUpstream.HeldDelayMs,
                statuses = rejectStatuses.ToDictionary(p => p.Key.ToString(), p => p.Value),
                upstreamMaxConcurrent = upstream.MaxConcurrent,
                admissionWaitMs = rejected.AdmissionWaitMs,
                clientLatencyMs = Percentiles(rejectLatencies),
            },
        };
    }

    private async Task WarmAsync()
    {
        for (var i = 0; i < 20; i++) await GetAsync("/assets/app.js", Session(200));
        using var socket = await ConnectAsync(Session(200));
        for (var i = 0; i < 20; i++) await EchoAsync(socket, Payload(1024, i));
        await CloseAsync(socket);
        await WaitForAsync(stats => stats.ActiveConnections == 0);
    }

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 256, PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(120) };

    private async Task<int> GetAsync(string path, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{gateway.Port}{path}");
        request.Headers.Host = host;
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Cookie", $"{WorkspaceGatewayRelay.CookieName}={session}");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
        return (int)response.StatusCode;
    }

    private async Task<ClientWebSocket> ConnectAsync(string session)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.SetRequestHeader("Host", host);
        socket.Options.SetRequestHeader("Origin", "https://" + host);
        socket.Options.SetRequestHeader("X-Forwarded-Proto", "https");
        socket.Options.SetRequestHeader("Cookie", $"{WorkspaceGatewayRelay.CookieName}={session}");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{gateway.Port}/ws?orchestrationProtocol=2"), CancellationToken.None);
        return socket;
    }

    private static async Task<bool> EchoAsync(ClientWebSocket socket, byte[] payload)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[payload.Length];
            var received = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(received), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                received += result.Count;
                if (result.EndOfMessage) return received == payload.Length;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { return false; }
    }

    private static async Task<WebSocketCloseStatus?> CloseAsync(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            return socket.CloseStatus;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { return null; }
    }

    private async Task<bool> WaitForAsync(Func<GatewayStats, bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition(await gateway.StatsAsync(gc: false))) return true;
            await Task.Delay(100);
        }
        return false;
    }

    // T3-style RPC envelope that is not "useful input", so the gateway parses it without reporting activity.
    private static byte[] Payload(int size, int seed)
    {
        var prefix = $"{{\"_tag\":\"Request\",\"id\":\"{seed}\",\"tag\":\"bench.echo\",\"headers\":[],\"payload\":{{\"data\":\"";
        const string suffix = "\"}}";
        var fill = Math.Max(0, size - prefix.Length - suffix.Length);
        return Encoding.UTF8.GetBytes(prefix + new string((char)('a' + seed % 26), fill) + suffix);
    }

    private static string Session(int index) => index.ToString("x").PadLeft(64, '0');

    private static object Step(int sockets, GatewayStats stats) => new
    {
        sockets,
        rssKiB = stats.RssBytes / 1024,
        rssAnonKiB = stats.RssAnonBytes / 1024,
        gcHeapKiB = stats.GcHeapBytes / 1024,
        gcCommittedKiB = stats.GcCommittedBytes / 1024,
        threads = stats.Threads,
        activeConnections = stats.ActiveConnections,
    };

    public static object Percentiles(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new { count = 0 };
        double At(double p) => Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)], 3);
        return new { count = sorted.Length, p50 = At(0.5), p95 = At(0.95), p99 = At(0.99), max = Math.Round(sorted[^1], 3) };
    }
}

sealed record GatewayStats(
    long RssBytes, long RssAnonBytes, long GcHeapBytes, long GcCommittedBytes, long AllocatedBytes, double CpuMs,
    int Threads, int Gen2Collections, long ActiveConnections, Dictionary<string, long> RelayBytes,
    Dictionary<string, long> WebSocketCloses, Dictionary<string, JsonElement> AdmissionWaitMs, long LogLines);

sealed class GatewayChild : IDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long logLines;
    public int Port { get; }

    private GatewayChild(Process process, int port) { this.process = process; Port = port; }

    public static async Task<GatewayChild> StartAsync(int controlPort, string credential, string domain)
    {
        var port = FreePort();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add("child");
        var env = start.Environment;
        foreach (var key in env.Keys.Where(k => k.StartsWith("OTEL_") || k.StartsWith("OperatorDiagnostics") ||
            k.StartsWith("DevelopmentWorkspaceGateway") || k.StartsWith("ASPNETCORE_") || k.StartsWith("Logging__")).ToList())
            env.Remove(key);
        env["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        env["DOTNET_ENVIRONMENT"] = "Production";
        env["DOTNET_PROCESSOR_COUNT"] = "1";
        env["DOTNET_GCHeapHardLimit"] = "0xC000000";
        env["Logging__Console__LogToStandardErrorThreshold"] = "Trace";
        env["DevelopmentWorkspaceGateway__Enabled"] = "true";
        env["DevelopmentWorkspaceGateway__GatewayDomain"] = domain;
        env["DevelopmentWorkspaceGateway__ServiceCredential"] = credential;
        env["DevelopmentWorkspaceGateway__ControlPlaneUrl"] = $"http://127.0.0.1:{controlPort}";
        env["DevelopmentWorkspaceGateway__RelayHost"] = "127.0.0.1";
        env["DevelopmentWorkspaceGateway__MinimumRelayPort"] = "1024";
        env["DevelopmentWorkspaceGateway__MaximumRelayPort"] = "65535";
        env["DevelopmentWorkspaceGateway__TrustedProxyAddress"] = "127.0.0.1";
        var process = Process.Start(start)!;
        var child = new GatewayChild(process, port);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Interlocked.Increment(ref child.logLines); };
        process.BeginErrorReadLine();
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 300; i++)
        {
            try
            {
                using var response = await probe.GetAsync($"http://127.0.0.1:{port}/");
                if (response.StatusCode == (HttpStatusCode)421) return child;
            }
            catch (HttpRequestException) { }
            if (process.HasExited) break;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Gateway child did not become ready.");
    }

    public Task ResetAsync() => CommandAsync("reset");

    public async Task<GatewayStats> StatsAsync(bool gc)
    {
        var json = await CommandAsync(gc ? "gc" : "stats");
        var stats = JsonSerializer.Deserialize<GatewayStats>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return stats with { LogLines = Interlocked.Read(ref logLines) };
    }

    private async Task<string> CommandAsync(string command)
    {
        await gate.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("Gateway child exited.");
                if (line.StartsWith(GatewayChildProcess.Prefix, StringComparison.Ordinal)) return line[GatewayChildProcess.Prefix.Length..];
            }
        }
        finally { gate.Release(); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        try { process.StandardInput.Close(); } catch (IOException) { }
        if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
        process.Dispose();
    }
}

static class GatewayChildProcess
{
    public const string Prefix = "BENCH ";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, long> RelayBytes = [];
    private static readonly Dictionary<string, long> WebSocketCloses = [];
    private static readonly Dictionary<string, List<double>> AdmissionWait = [];
    private static long activeConnections;

    public static int Run()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Minicloud.Operator" && instrument.Name.StartsWith("minicloud.gateway.", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (Sync)
            {
                if (instrument.Name.EndsWith(".connections.active", StringComparison.Ordinal)) activeConnections += value;
                else if (instrument.Name.EndsWith(".relay.bytes", StringComparison.Ordinal)) Add(RelayBytes, Key(tags), value);
                else if (instrument.Name.EndsWith(".websocket.closes", StringComparison.Ordinal)) Add(WebSocketCloses, Key(tags), value);
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (!instrument.Name.EndsWith(".admission.wait", StringComparison.Ordinal)) return;
            lock (Sync)
            {
                var key = Key(tags);
                if (!AdmissionWait.TryGetValue(key, out var values)) AdmissionWait[key] = values = [];
                values.Add(value * 1000);
            }
        });
        // GATEWAY_BENCH_METRICS=0 isolates footprint from the in-process listener; metric-derived fields stay empty.
        if (Environment.GetEnvironmentVariable("GATEWAY_BENCH_METRICS") != "0") listener.Start();
        var commands = new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (line == "reset")
                    lock (Sync) { RelayBytes.Clear(); WebSocketCloses.Clear(); AdmissionWait.Clear(); }
                if (line == "gc")
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                Console.Out.WriteLine(Prefix + Snapshot());
                Console.Out.Flush();
            }
            Environment.Exit(0);
        }) { IsBackground = true };
        commands.Start();
        var entry = typeof(WorkspaceGatewayRelay).Assembly.EntryPoint!;
        entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [Array.Empty<string>()]);
        return 0;
    }

    private static void Add(Dictionary<string, long> values, string key, long value) =>
        values[key] = values.GetValueOrDefault(key) + value;

    // Allocation-free for known fixed tag values so the listener does not skew per-message allocation.
    private static string Key(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 1 && tags[0].Value is string single) return single;
        if (tags.Length == 2 && tags[0].Value is string operation && tags[1].Value is string direction)
            return (operation, direction) switch
            {
                ("websocket", "upstream") => "websocket.upstream",
                ("websocket", "downstream") => "websocket.downstream",
                ("http", "upstream") => "http.upstream",
                ("http", "downstream") => "http.downstream",
                _ => operation + "." + direction,
            };
        var parts = new List<string>();
        foreach (var tag in tags) parts.Add($"{tag.Value}");
        return string.Join('.', parts);
    }

    private static string Snapshot()
    {
        var info = GC.GetGCMemoryInfo();
        using var self = Process.GetCurrentProcess();
        var (rss, rssAnon) = ReadRss();
        lock (Sync)
        {
            return JsonSerializer.Serialize(new
            {
                rssBytes = rss, rssAnonBytes = rssAnon,
                gcHeapBytes = info.HeapSizeBytes, gcCommittedBytes = info.TotalCommittedBytes,
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
                cpuMs = self.TotalProcessorTime.TotalMilliseconds,
                threads = self.Threads.Count,
                gen2Collections = GC.CollectionCount(2),
                activeConnections,
                relayBytes = RelayBytes,
                webSocketCloses = WebSocketCloses,
                admissionWaitMs = AdmissionWait.ToDictionary(p => p.Key, p => Driver.Percentiles(p.Value)),
            });
        }
    }

    private static (long Rss, long RssAnon) ReadRss()
    {
        long rss = 0, anon = 0;
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kib(line);
            else if (line.StartsWith("RssAnon:", StringComparison.Ordinal)) anon = Kib(line);
        }
        return (rss, anon);

        static long Kib(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
    }
}

sealed class FakeControlPlane : IAsyncDisposable
{
    private readonly WebApplication app;
    private long resolves;
    private long activities;
    public int Port { get; private set; }
    public long Resolves => Interlocked.Read(ref resolves);
    public long Activities => Interlocked.Read(ref activities);

    private FakeControlPlane(WebApplication app) => this.app = app;

    public static async Task<FakeControlPlane> StartAsync(int upstreamPort, string credential)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeControlPlane(app);
        app.MapPost("/internal/workspace-gateway/resolve", (HttpContext context) =>
        {
            if (context.Request.Headers["X-Minicloud-Gateway-Credential"] != credential) return Results.Unauthorized();
            Interlocked.Increment(ref fake.resolves);
            return Results.Json(new DevelopmentWorkspaceGatewayTarget(upstreamPort, "synthetic-upstream-bearer"));
        });
        app.MapPost("/internal/workspace-gateway/activity", () =>
        {
            Interlocked.Increment(ref fake.activities);
            return Results.NoContent();
        });
        await app.StartAsync();
        fake.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return fake;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

sealed class FakeUpstream : IAsyncDisposable
{
    public const int AssetDelayMs = 10;
    public const int AssetBytes = 32 * 1024;
    public const int HeldDelayMs = 500;
    private static readonly byte[] Asset = Encoding.ASCII.GetBytes(new string('x', AssetBytes));
    private readonly WebApplication app;
    private int concurrent;
    private int maxConcurrent;
    private int webSockets;
    public int Port { get; private set; }
    public int MaxConcurrent => Volatile.Read(ref maxConcurrent);
    public int WebSocketsAccepted => Volatile.Read(ref webSockets);

    private FakeUpstream(WebApplication app) => this.app = app;

    public void ResetConcurrency() => Volatile.Write(ref maxConcurrent, 0);

    public static async Task<FakeUpstream> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var fake = new FakeUpstream(app);
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                Interlocked.Increment(ref fake.webSockets);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[16 * 1024];
                try
                {
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                            return;
                        }
                        await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
                return;
            }
            var now = Interlocked.Increment(ref fake.concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref fake.maxConcurrent)) &&
                Interlocked.CompareExchange(ref fake.maxConcurrent, now, seen) != seen) { }
            try
            {
                if (context.Request.Path == "/held")
                {
                    await Task.Delay(HeldDelayMs, context.RequestAborted);
                    await context.Response.WriteAsync("held");
                    return;
                }
                await Task.Delay(AssetDelayMs, context.RequestAborted);
                context.Response.ContentType = "application/javascript";
                await context.Response.Body.WriteAsync(Asset, context.RequestAborted);
            }
            finally { Interlocked.Decrement(ref fake.concurrent); }
        });
        await app.StartAsync();
        fake.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        return fake;
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}
