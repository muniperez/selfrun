using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Selfrun.RuntimeAgent;

var parsed = BenchmarkArgs.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine(
        "Usage: RuntimeAgentBenchmark <empty scratch directory> <idle|overlap|burst|churn|protocol|verify> <mock|children> [off|on|on-optimized] [--seed <int>] [--cycles <int>] [--start-file <path>] [--ready-file <path>] [--stop-file <path>]");
    return 2;
}

using var shutdownCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdownCts.Cancel(); });

await WaitForStartFileAsync(parsed.StartFile, shutdownCts.Token);

if (parsed.Workload == "verify")
{
    await VerifyAsync(parsed.WorkDirectory);
    Console.WriteLine(
        """{"verified":["overlap","burst","churn","acknowledgement-before-cursor-save","restart-replay","protocol","cursor-write-optimization","profiling-three-modes","aggregate-only-summary"]}""");
    await CoordinateGracefulExitAsync(parsed, shutdownCts.Token);
    return 0;
}

await RunAsync(Path.Combine(parsed.WorkDirectory, "warmup"), parsed with { Cycles = 8 }, isWarmup: true);
var result = await RunAsync(Path.Combine(parsed.WorkDirectory, "measured"), parsed, isWarmup: false);
Console.WriteLine(JsonSerializer.Serialize(result));
await CoordinateGracefulExitAsync(parsed, shutdownCts.Token);
return 0;

static async Task WaitForStartFileAsync(string? startFile, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(startFile))
    {
        return;
    }

    var deadline = Stopwatch.GetTimestamp() + 30L * Stopwatch.Frequency;
    while (!cancellationToken.IsCancellationRequested &&
           !File.Exists(startFile) &&
           Stopwatch.GetTimestamp() < deadline)
    {
        try
        {
            await Task.Delay(25, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

static async Task CoordinateGracefulExitAsync(BenchmarkArgs options, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ReadyFile))
    {
        return;
    }

    var readyDir = Path.GetDirectoryName(options.ReadyFile);
    if (!string.IsNullOrWhiteSpace(readyDir))
    {
        Directory.CreateDirectory(readyDir);
    }

    await File.WriteAllTextAsync(options.ReadyFile, "ready\n", cancellationToken);

    if (string.IsNullOrWhiteSpace(options.StopFile))
    {
        return;
    }

    // Bounded wait so an attached EventPipe session can finish (Stop + drain final transport marker)
    // while the target process stays alive after closing all Activity spans.
    var deadline = Stopwatch.GetTimestamp() + 30L * Stopwatch.Frequency;
    while (!cancellationToken.IsCancellationRequested &&
           !File.Exists(options.StopFile) &&
           Stopwatch.GetTimestamp() < deadline)
    {
        try
        {
            await Task.Delay(25, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

async Task<object> RunAsync(string directory, BenchmarkArgs options, bool isWarmup)
{
    Directory.CreateDirectory(directory);
    var profilingEnabled = options.ProfilingMode is "on" or "on-optimized";
    using var syntheticCollector = new SyntheticLoopbackCollector(outageStartCall: 2, outageLength: 1);
    using var collectorHttp = new HttpClient(syntheticCollector);
    using var journalBuffer = new StringWriter();
    var profilingConfig = profilingEnabled
        ? new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: "http://127.0.0.1:4318",
            SummaryIntervalSeconds: 60,
            ExportTimeoutMs: 500,
            MaxQueueSize: 16,
            EmitJournalSummary: true,
            ExportOtlp: true)
        : new AgentProfilingConfig(Enabled: false);

    var config = new AgentConfig(
        "srv", "org", "app", "http://synthetic.invalid", "selfrun-bench", "/unused", "/unused",
        Path.Combine(directory, "state.json"),
        profilingConfig);
    var commands = new SyntheticCommands(options.Workload, options.Runner == "children", options.Seed);
    var clock = new VirtualClock();
    var probe = new SyntheticProbe(options.Workload == "protocol");
    using var handler = new SyntheticApi(config, clock, options.Workload == "protocol");
    using var http = new HttpClient(handler);
    var api = new RuntimeApiClient(http, config, "synthetic");
    var cursors = new CursorStore(config.StatePath);
#if BASELINE
    var routing = new RoutingApplier(config, commands, probe);
    var collector = new RuntimeCollector(config, commands);
#else
    var routing = new RoutingApplier(config, commands, probe, clock);
    var publisher = new RuntimeStatusPublisher(clock);
#endif
    await using var profiler = RuntimeAgentProfiler.Start(
        profilingConfig,
        journalWriter: journalBuffer,
        exportHttpClient: collectorHttp,
        timeProvider: clock,
        startBackgroundLoop: false);

    var stateWrites = 0;
    var requestWrites = 0;
    long fileBytes = 0;
    long stateBytes = 0;
    long requestFileBytes = 0;
    long priorCursorWrites = 0;
    long priorCursorWritesSkipped = 0;
    long priorCursorBytesWritten = 0;
    var routeChangeObserved = false;
    var probeFailureRecovered = false;
    var deploymentGateObserved = false;
    var restartPendingAckReplayed = false;
    var prunedCursorCount = 0;
    var requestPath = Path.Combine(directory, "routing-request.json");
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var start = Stopwatch.GetTimestamp();

    for (var cycle = 0; cycle < options.Cycles; cycle++)
    {
        commands.Cycle = cycle;
        handler.Cycle = cycle;
        probe.Cycle = cycle;
#if !BASELINE
        using var collector = new RuntimeCollector(config, commands);
#endif
        var oldState = File.GetLastWriteTimeUtc(config.StatePath);
        var oldRequest = File.GetLastWriteTimeUtc(requestPath);

        if (options.Workload == "protocol")
        {
            await AgentOperations.TryAsync("heartbeat", () => api.SendHeartbeatAsync("bench", CancellationToken.None));
            await AgentOperations.TryAsync("status", async () =>
            {
                var status = await collector.CollectStatusAsync("bench", CancellationToken.None);
#if BASELINE
                await api.SendStatusAsync(status, CancellationToken.None);
#else
                await publisher.PublishAsync(status, api.SendStatusAsync, CancellationToken.None);
#endif
            });
            await AgentOperations.TryAsync("routing", async () =>
            {
                RuntimeDesiredRoutingResponse desired;
                try
                {
                    desired = await api.GetRoutingAsync(CancellationToken.None);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    deploymentGateObserved = true;
#if !BASELINE
                    routing.Invalidate();
#endif
                    throw;
                }

                if (desired.Routes.Any(r => r.ServicePort == 8081))
                {
                    routeChangeObserved = true;
                }
#if BASELINE
                var results = await routing.ApplyAsync(desired, CancellationToken.None);
                await api.SendRoutingApplyResultsAsync(new(desired.Version, results), CancellationToken.None);
                return results;
#else
                var results = await routing.ReconcileAsync(desired, api.SendRoutingApplyResultsAsync, CancellationToken.None);
                if (probe.ObservedFailure && results.All(r => r.SslStatus == "ready"))
                {
                    probeFailureRecovered = true;
                }
                return results;
#endif
            }, results => results.Any(result => result.ApplyStatus == "failed"));

            await AgentOperations.TryAsync("logs", async () =>
            {
                var logs = await collector.CollectLogsAsync(CancellationToken.None);
                if (logs.Count != 0)
                {
                    var response = await api.SendLogsAsync(logs, CancellationToken.None);
                    if (!isWarmup && cycle == 210)
                    {
                        // Simulate agent restart with a pending acknowledgement before cursor save.
                        priorCursorWrites += cursors.WriteCount;
                        priorCursorWritesSkipped += cursors.SkippedWriteCount;
                        priorCursorBytesWritten += cursors.BytesWritten;
                        var restarted = new CursorStore(config.StatePath);
                        await restarted.LoadAsync(CancellationToken.None);
                        using var replayCollector = new RuntimeCollector(config, commands);
                        var replay = await replayCollector.CollectLogsAsync(CancellationToken.None);
                        var replayResponse = await api.SendLogsAsync(replay, CancellationToken.None);
                        if (replayResponse.Accepted == 0 && replayResponse.Rejected == 0)
                        {
                            restartPendingAckReplayed = true;
                        }
                        await restarted.SaveAsync(replayResponse.LastAcceptedCursorBySource, CancellationToken.None);
                        cursors = restarted;
                    }
                    else
                    {
                        if (options.ProfilingMode == "on-optimized" && cycle > 206)
                        {
                            var activeSources = commands.Services.Select(s => $"service:{s}").Append("caddy").ToHashSet(StringComparer.Ordinal);
                            prunedCursorCount += cursors.PruneInactiveSources(activeSources);
                        }
                        await cursors.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
                    }
                }
            });
        }
        else
        {
            await api.SendHeartbeatAsync("bench", CancellationToken.None);
            var status = await collector.CollectStatusAsync("bench", CancellationToken.None);
#if BASELINE
            await api.SendStatusAsync(status, CancellationToken.None);
            var desired = await api.GetRoutingAsync(CancellationToken.None);
            var results = await routing.ApplyAsync(desired, CancellationToken.None);
            await api.SendRoutingApplyResultsAsync(new(desired.Version, results), CancellationToken.None);
#else
            await publisher.PublishAsync(status, api.SendStatusAsync, CancellationToken.None);
            var desired = await api.GetRoutingAsync(CancellationToken.None);
            await routing.ReconcileAsync(desired, api.SendRoutingApplyResultsAsync, CancellationToken.None);
#endif
            var logs = await collector.CollectLogsAsync(CancellationToken.None);
            if (logs.Count != 0)
            {
                var response = await api.SendLogsAsync(logs, CancellationToken.None);
                if (options.ProfilingMode == "on-optimized" && options.Workload == "churn")
                {
                    var activeSources = commands.Services.Select(s => $"service:{s}").Append("caddy").ToHashSet(StringComparer.Ordinal);
                    prunedCursorCount += cursors.PruneInactiveSources(activeSources);
                }
                await cursors.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
            }
        }

        if (profilingEnabled && (cycle + 1) % 60 == 0)
        {
            profiler.FlushSummary();
        }

        if (File.GetLastWriteTimeUtc(config.StatePath) != oldState)
        {
            stateWrites++;
            var bytes = new FileInfo(config.StatePath).Length;
            stateBytes += bytes;
            fileBytes += bytes;
        }
        if (File.GetLastWriteTimeUtc(requestPath) != oldRequest)
        {
            requestWrites++;
            var bytes = new FileInfo(requestPath).Length;
            requestFileBytes += bytes;
            fileBytes += bytes;
        }
        clock.Advance();
    }

    if (profilingEnabled)
    {
        profiler.FlushSummary();
        await profiler.DrainExportsAsync(TimeSpan.FromSeconds(2));
    }

    var summaryLines = journalBuffer.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var line in summaryLines)
    {
        RuntimeAgentDiagnosticSummary.ValidateAggregateOnlyJson(line);
    }

    return new
    {
        cycles = options.Cycles,
        workload = options.Workload,
        runner = options.Runner,
        profilingMode = options.ProfilingMode,
        seed = options.Seed,
        containers = 4,
        routes = 2,
        wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
        cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        commands.Calls,
        commands.LogInputBytes,
        commands.LogInputLines,
        handler.Submitted,
        handler.Acknowledged,
        handler.InsertedLogs,
        handler.SubmittedBySource,
        handler.InsertedBySource,
        handler.MaxBatch,
        recordDigest = handler.RecordDigest,
        handler.StatusRows,
        handler.RouteRows,
        handler.RequestBytes,
        handler.Requests,
        tlsProbes = probe.Calls,
        stateWrites,
        cursorWrites = priorCursorWrites + cursors.WriteCount,
        cursorWritesSkipped = priorCursorWritesSkipped + cursors.SkippedWriteCount,
        cursorBytesWritten = priorCursorBytesWritten + cursors.BytesWritten,
        prunedCursorCount,
        requestWrites,
        stateBytes,
        requestFileBytes,
        fileBytes,
        protocolScenarios = options.Workload == "protocol"
            ? new
            {
                routeChangeObserved,
                probeFailureRecovered,
                deploymentGateObserved,
                restartPendingAckReplayed
            }
            : null,
        profilingCollectorFootprint = profilingEnabled
            ? new
            {
                summaryCount = summaryLines.Length,
                collectorRequests = syntheticCollector.RequestCount,
                collectorAccepted = syntheticCollector.AcceptedCount,
                collectorRejectedOutage = syntheticCollector.RejectedOutageCount,
                collectorBytesReceived = syntheticCollector.BytesReceived,
                exporterDrops = profiler.TotalExporterDrops,
                queueLag = profiler.CurrentQueueLag
            }
            : null
    };
}

async Task VerifyAsync(string directory)
{
    foreach (var workload in new[] { "overlap", "burst", "churn" })
    {
        var config = new AgentConfig("srv", "org", "app", "http://synthetic.invalid", "selfrun-bench", "/unused", "/unused", Path.Combine(directory, workload, "state.json"));
        var commands = new SyntheticCommands(workload, false, seed: 1);
        using var handler = new SyntheticApi(config, new VirtualClock(), protocolMode: false);
        using var http = new HttpClient(handler);
        var api = new RuntimeApiClient(http, config, "synthetic");
        var store = new CursorStore(config.StatePath);
        for (var cycle = 0; cycle < 8; cycle++)
        {
            commands.Cycle = cycle;
            using var collector = new RuntimeCollector(config, commands);
            var status = await collector.CollectStatusAsync("bench", CancellationToken.None);
            var records = await collector.CollectLogsAsync(CancellationToken.None);
            var expectedServices = commands.Services;
            Require(status.Services.Select(x => x.Name).SequenceEqual(expectedServices), "Churn discovery changed.");
            Require(records.Count == (workload == "overlap" ? 344 : 500), "Unexpected batch size.");
            Require(records.All(x => !x.Message.Contains("example-token", StringComparison.Ordinal)), "Token escaped redaction.");
            Require(records.All(x => x.Message.Length <= AgentRuntimeLimits.MaxLineLength), "Line limit exceeded.");
            var perContainer = workload == "overlap" ? 80 : 100;
            for (var slot = 0; slot < 4; slot++)
            {
                var logs = records.Skip(slot * perContainer).Take(perContainer).ToArray();
                Require(logs.All(x => x.Service == expectedServices[slot]), "Collection ordering changed.");
                Require(logs.Count(x => x.Stream == "stdout") == perContainer / 2 &&
                    logs.Count(x => x.Stream == "stderr") == perContainer / 2, "Stream identity changed.");
                Require(logs.Select(x => x.Cursor).Distinct().Count() == perContainer, "Same-timestamp records collapsed.");
            }
            Require(records.Count(x => x.Source == "caddy") == (workload == "overlap" ? 24 : 100), "Journal cap ordering changed.");
            Require(commands.Calls["docker ps"] == cycle * 2 + 1, "Discovery was not shared within a cycle.");

            var response = await api.SendLogsAsync(records, CancellationToken.None);
            var expectedInserted = workload == "overlap" ? (cycle == 0 ? 344 : 0)
                : cycle == 0 ? 500
                : workload == "churn" && cycle % 4 == 0 ? 460 : 260;
            Require(response.Accepted == expectedInserted && response.Rejected == 0, "Burst/churn progress or overlap deduplication changed.");
            var previousState = File.Exists(config.StatePath) ? await File.ReadAllBytesAsync(config.StatePath) : null;
            // Simulate loss of the acknowledgement before the atomic cursor save.
            var restarted = new CursorStore(config.StatePath);
            await restarted.LoadAsync(CancellationToken.None);
            Require(store.Cursors.OrderBy(x => x.Key).SequenceEqual(restarted.Cursors.OrderBy(x => x.Key)), "Restart lost the last persisted acknowledgement.");
            using var replayCollector = new RuntimeCollector(config, commands);
            var replay = await replayCollector.CollectLogsAsync(CancellationToken.None);
            Require(records.SequenceEqual(replay), "Restart replay changed log fields or cursors.");
            var replayResponse = await api.SendLogsAsync(replay, CancellationToken.None);
            Require(replayResponse.Accepted == 0 && replayResponse.Rejected == 0, "Replay created duplicate modeled inserts.");
            await restarted.SaveAsync(replayResponse.LastAcceptedCursorBySource, CancellationToken.None);
            var savedState = await File.ReadAllBytesAsync(config.StatePath);
            var writesBeforeUnchanged = restarted.WriteCount;
            await restarted.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
            Require(restarted.WriteCount == writesBeforeUnchanged && restarted.SkippedWriteCount >= 1, "Unchanged acknowledgement triggered a cursor disk write.");
            var unchangedState = await File.ReadAllBytesAsync(config.StatePath);
            Require(savedState.SequenceEqual(unchangedState), "Unchanged acknowledgement changed persisted state.");
            if (workload == "overlap" && previousState is not null)
            {
                Require(previousState.SequenceEqual(savedState), "Overlap acknowledgement changed persisted cursors.");
            }
            store = restarted;
        }
        Require(handler.InsertedBySource["caddy"] == (workload == "overlap" ? 24 : 520), "Deferred journal records were not replayed on the next cycle.");
    }

    // Verify all three profiling modes against the full #119 acceptance protocol workload.
    string? baselineDigest = null;
    foreach (var mode in new[] { "off", "on", "on-optimized" })
    {
        var protocolDir = Path.Combine(directory, $"protocol-{mode}");
        var protocolResult = await RunAsync(protocolDir, new BenchmarkArgs(protocolDir, "protocol", "mock", mode, Seed: 1, Cycles: 240, null, null), isWarmup: false);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(protocolResult));
        var root = doc.RootElement;
        var scenarios = root.GetProperty("protocolScenarios");
        Require(scenarios.GetProperty("routeChangeObserved").GetBoolean(), "Protocol route change was not exercised.");
        Require(scenarios.GetProperty("probeFailureRecovered").GetBoolean(), "Protocol failed probe recovery was not exercised.");
        Require(scenarios.GetProperty("deploymentGateObserved").GetBoolean(), "Protocol deployment gate (409) was not exercised.");
        Require(scenarios.GetProperty("restartPendingAckReplayed").GetBoolean(), "Protocol pending-ack restart replay was not exercised.");

        var digest = root.GetProperty("recordDigest").GetString()!;
        baselineDigest ??= digest;
        Require(digest == baselineDigest, $"Protocol mode '{mode}' altered completed log work digest.");

        if (mode != "off")
        {
            var footprint = root.GetProperty("profilingCollectorFootprint");
            Require(footprint.GetProperty("summaryCount").GetInt32() >= 4, "Expected bounded periodic summaries in profiling mode.");
            Require(footprint.GetProperty("collectorRejectedOutage").GetInt32() >= 1, "Expected synthetic collector outage injection.");
            Require(footprint.GetProperty("exporterDrops").GetInt64() >= 1, "Expected exporter drop accounting during collector outage.");
        }
    }
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

sealed record BenchmarkArgs(
    string WorkDirectory,
    string Workload,
    string Runner,
    string ProfilingMode,
    int Seed,
    int Cycles,
    string? StartFile = null,
    string? ReadyFile = null,
    string? StopFile = null)
{
    public static BenchmarkArgs? Parse(string[] args)
    {
        if (args.Length < 3)
        {
            return null;
        }

        var workDirectory = args[0];
        var workload = args[1];
        var runner = args[2];
        if (workload is not ("idle" or "overlap" or "burst" or "churn" or "protocol" or "verify") ||
            runner is not ("mock" or "children") ||
            !Directory.Exists(workDirectory) ||
            Directory.EnumerateFileSystemEntries(workDirectory).Any())
        {
            return null;
        }

        var profilingMode = "off";
        var seed = 1;
        var cycles = 240;
        string? startFile = null;
        string? readyFile = null;
        string? stopFile = null;

        var index = 3;
        if (index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal))
        {
            profilingMode = args[index++];
            if (profilingMode is not ("off" or "on" or "on-optimized"))
            {
                return null;
            }
        }

        while (index < args.Length)
        {
            var flag = args[index++];
            if (index >= args.Length)
            {
                return null;
            }

            var value = args[index++];
            switch (flag)
            {
                case "--seed":
                    if (!int.TryParse(value, out seed) || seed < 1) { return null; }
                    break;
                case "--cycles":
                    if (!int.TryParse(value, out cycles) || cycles < 1 || cycles > 2000) { return null; }
                    break;
                case "--start-file":
                    startFile = value;
                    break;
                case "--ready-file":
                    readyFile = value;
                    break;
                case "--stop-file":
                    stopFile = value;
                    break;
                default:
                    return null;
            }
        }

        return new BenchmarkArgs(workDirectory, workload, runner, profilingMode, seed, cycles, startFile, readyFile, stopFile);
    }
}

sealed class VirtualClock : TimeProvider
{
    private long _seconds;
    public override long TimestampFrequency => 1;
    public override long GetTimestamp() => _seconds;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
    public void Advance() => _seconds += 15;
}

sealed class SyntheticLoopbackCollector(int outageStartCall, int outageLength) : HttpMessageHandler
{
    private int _requests;
    private int _accepted;
    private int _rejectedOutage;
    private long _bytesReceived;

    public int RequestCount => Volatile.Read(ref _requests);
    public int AcceptedCount => Volatile.Read(ref _accepted);
    public int RejectedOutageCount => Volatile.Read(ref _rejectedOutage);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _requests);
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Interlocked.Add(ref _bytesReceived, body.Length);
        }

        if (call >= outageStartCall && call < outageStartCall + outageLength)
        {
            Interlocked.Increment(ref _rejectedOutage);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        Interlocked.Increment(ref _accepted);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

sealed class SyntheticApi(AgentConfig config, VirtualClock clock, bool protocolMode) : HttpMessageHandler
{
    private readonly HashSet<(string Source, string Cursor, DateTimeOffset ObservedAt)> _logs = [];
    private readonly IncrementalHash _recordHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string _version = "0000000000";
    public int Cycle { get; set; }
    public int InsertedLogs => _logs.Count;
    public int Submitted { get; private set; }
    public int Acknowledged { get; private set; }
    public int StatusRows { get; private set; }
    public int RouteRows { get; private set; }
    public int MaxBatch { get; private set; }
    public Dictionary<string, int> SubmittedBySource { get; } = [];
    public Dictionary<string, int> InsertedBySource { get; } = [];
    public string RecordDigest => Convert.ToHexString(_recordHash.GetCurrentHash());
    public Dictionary<string, long> RequestBytes { get; } = [];
    public Dictionary<string, int> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests[path] = Requests.GetValueOrDefault(path) + 1;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            RequestBytes[path] = RequestBytes.GetValueOrDefault(path) + bytes.Length;
            if (path == "/api/runtime/ingest/logs") { _recordHash.AppendData(bytes); }
        }
        switch (path)
        {
            case "/api/runtime/heartbeat":
            case "/api/runtime/ingest/status":
                if (path.EndsWith("/status", StringComparison.Ordinal)) { StatusRows++; }
                return Ok(new { accepted = true });
            case "/api/runtime/routing":
                if (protocolMode && Cycle == 205)
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = JsonContent.Create(new { code = "deployment_in_progress" })
                    };
                }
                var firstPort = protocolMode && Cycle >= 200 ? 8081 : 8080;
                return Ok(new RuntimeDesiredRoutingResponse(_version, config.AppId, config.ComposeProject, [
                    new("dom1", "first.example.test", "/", "svc1", firstPort),
                    new("dom2", "second.example.test", "/", "svc2", 8080)]));
            case "/api/runtime/routing/apply-results":
                var routing = (await request.Content!.ReadFromJsonAsync<RuntimeRoutingApplyResultsRequest>(cancellationToken))!;
                RouteRows += routing.Results.Count;
                _version = clock.GetTimestamp().ToString("0000000000");
                return Ok(new { accepted = routing.Results.Count });
            case "/api/runtime/ingest/logs":
                var batch = (await request.Content!.ReadFromJsonAsync<RuntimeLogIngestRequest>(cancellationToken))!;
                var cursors = new Dictionary<string, string>();
                var inserted = 0;
                MaxBatch = Math.Max(MaxBatch, batch.Records.Count);
                foreach (var record in batch.Records)
                {
                    SubmittedBySource[record.Source] = SubmittedBySource.GetValueOrDefault(record.Source) + 1;
                    if (_logs.Add((record.Source, record.Cursor, record.ObservedAt)))
                    {
                        inserted++;
                        InsertedBySource[record.Source] = InsertedBySource.GetValueOrDefault(record.Source) + 1;
                    }
                    cursors[record.Source] = record.Cursor;
                }
                Submitted += batch.Records.Count;
                Acknowledged += batch.Records.Count;
                return Ok(new RuntimeLogIngestResponse(inserted, 0, cursors));
            default:
                throw new InvalidOperationException($"Unexpected request: {path}");
        }
    }

    private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _recordHash.Dispose(); }
        base.Dispose(disposing);
    }
}

sealed class SyntheticProbe(bool injectProtocolFailure = false) : IHttpsProbe
{
    public int Cycle { get; set; }
    public int Calls { get; private set; }
    public bool ObservedFailure { get; private set; }

    public Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken)
    {
        Calls++;
        if (injectProtocolFailure && Cycle == 200 && hostname == "first.example.test")
        {
            ObservedFailure = true;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}

sealed class SyntheticCommands(string workload, bool childProcesses, int seed = 1) : IProcessRunner
{
    private static readonly string ContainerLog = Output(40, "container");
    private static readonly string JournalLog = Output(24, "caddy");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly ProcessRunner _runner = new();
    private int _cycle;
    private string _burstContainer = "";
    private string _burstJournal = "";
    public Dictionary<string, int> Calls { get; } = [];
    public long LogInputBytes { get; private set; }
    public long LogInputLines { get; private set; }
    public int Cycle
    {
        get => _cycle;
        set
        {
            _cycle = value;
            if (workload is "burst" or "churn" || (workload == "protocol" && value is >= 160 and < 200))
            {
                _burstContainer = BurstOutput(25, "container");
                _burstJournal = BurstOutput(60, "caddy");
            }
        }
    }
    public string[] Services => Enumerable.Range(0, 4).Select(slot =>
        workload == "churn"
            ? $"svc{(slot + Cycle) % 4 + 1}-generation{Cycle / 4}"
            : workload == "protocol" && Cycle >= 206
                ? $"svc{slot + 1}-deploy2"
                : $"svc{slot + 1}").ToArray();
    private string ContainerId(int slot) =>
        workload == "churn"
            ? $"id{slot + 1}-generation{Cycle / 4}"
            : workload == "protocol" && Cycle >= 206
                ? $"id{slot + 1}-deploy2"
                : $"id{slot + 1}";

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = fileName == "docker" ? $"docker {args[0]}" : fileName;
        Calls[command] = Calls.GetValueOrDefault(command) + 1;
        ProcessResult result = command switch
        {
            "docker ps" => new(0, string.Join('\n', Enumerable.Range(0, 4).Select(ContainerId)) + "\n", ""),
            "docker inspect" => new(0, "[" + string.Join(',', Services.Select((service, slot) =>
                $$$"""{"Id":"{{{ContainerId(slot)}}}","Name":"/bench-{{{slot + 1}}}","Config":{"Image":"app:v{{{(workload == "protocol" && Cycle >= 206 ? 2 : 1)}}}","Labels":{"com.docker.compose.service":"{{{service}}}"}},"State":{"Status":"running"},"RestartCount":0}""")) + "]", ""),
            "docker logs" when workload == "overlap" => Log(ContainerLog, ContainerLog, 80),
            "journalctl" when workload == "overlap" => Log(JournalLog, "", 24),
            "docker logs" when workload is "burst" or "churn" => Log(_burstContainer, _burstContainer, 100),
            "journalctl" when workload is "burst" or "churn" => Log(_burstJournal, "", 120),
            "docker logs" when workload == "protocol" => ProtocolDockerLogs(),
            "journalctl" when workload == "protocol" => ProtocolJournalLogs(),
            "docker logs" or "journalctl" or "sudo" => new(0, "", ""),
            "systemctl" => new(0, "active\n", ""),
            _ => throw new InvalidOperationException($"Unexpected command: {command}")
        };
        return childProcesses
            ? _runner.RunAsync("/bin/sh", ["-c", "printf '%s' \"$1\"; printf '%s' \"$2\" >&2", "fixture", result.Stdout, result.Stderr], cancellationToken)
            : Task.FromResult(result);
    }

    private ProcessResult ProtocolDockerLogs()
    {
        // Cycles 0..119 = 30 virtual minutes idle; 120..159 = fixed-rate replay with duplicates;
        // 160..199 = burst near 500-record cap; 200..239 = steady post-route-change/deploy traffic.
        if (Cycle < 120)
        {
            return new(0, "", "");
        }
        if (Cycle < 160)
        {
            return Log(ContainerLog, ContainerLog, 80);
        }
        if (Cycle < 200)
        {
            return Log(_burstContainer, _burstContainer, 100);
        }

        var steady = SteadyOutput(4, "container");
        return Log(steady, steady, 8);
    }

    private ProcessResult ProtocolJournalLogs()
    {
        if (Cycle < 120)
        {
            return new(0, "-- No entries --\n", "");
        }
        if (Cycle < 160)
        {
            return Log(JournalLog, "", 24);
        }
        if (Cycle < 200)
        {
            return Log(_burstJournal, "", 120);
        }

        var steady = SteadyOutput(4, "caddy");
        return Log(steady, "", 4);
    }

    private ProcessResult Log(string stdout, string stderr, int lines)
    {
        LogInputBytes += Encoding.UTF8.GetByteCount(stdout) + Encoding.UTF8.GetByteCount(stderr);
        LogInputLines += lines;
        return new(0, stdout, stderr);
    }

    private static string Output(int count, string message) => string.Concat(Enumerable.Range(1, count)
        .Select(n => $"2026-01-01T00:00:00Z {message} log line {n} token=example-token\n"));

    private string SteadyOutput(int count, string message) => string.Concat(Enumerable.Range(1, count)
        .Select(n => $"{Epoch.AddSeconds(Cycle * 15):yyyy-MM-ddTHH:mm:ssZ} {message} seed={seed} cycle {Cycle} line {n} token=example-token\n"));

    private string BurstOutput(int halfCount, string message) => string.Concat(new[] { Cycle - 1, Cycle }.SelectMany(cycle =>
        Enumerable.Range(1, halfCount).Select(n =>
            $"{Epoch.AddSeconds(cycle * 15):yyyy-MM-ddTHH:mm:ssZ} {message} cycle {cycle} line {n} token=example-token\n")));
}
