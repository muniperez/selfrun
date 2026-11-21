using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.RuntimeAgent;

if (args is not [var workDirectory, "idle" or "overlap" or "burst" or "churn" or "verify", "mock" or "children"] ||
    !Directory.Exists(workDirectory) || Directory.EnumerateFileSystemEntries(workDirectory).Any())
{
    Console.Error.WriteLine("Usage: RuntimeAgentBenchmark <empty scratch directory> <idle|overlap|burst|churn|verify> <mock|children>");
    return 2;
}

if (args[1] == "verify")
{
    await VerifyAsync(workDirectory);
    Console.WriteLine("""{"verified":["overlap","burst","churn","acknowledgement-before-cursor-save","restart-replay"]}""");
    return 0;
}

await RunAsync(Path.Combine(workDirectory, "warmup"), 8);
Console.WriteLine(JsonSerializer.Serialize(await RunAsync(Path.Combine(workDirectory, "measured"), 240)));
return 0;

async Task<object> RunAsync(string directory, int cycles)
{
    Directory.CreateDirectory(directory);
    var config = new AgentConfig("srv", "org", "app", "http://synthetic.invalid", "minicloud-bench", "/unused", "/unused", Path.Combine(directory, "state.json"));
    var commands = new SyntheticCommands(args[1], args[2] == "children");
    var clock = new VirtualClock();
    var probe = new SyntheticProbe();
    using var handler = new SyntheticApi(config, clock);
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

    var stateWrites = 0;
    var requestWrites = 0;
    long fileBytes = 0;
    long stateBytes = 0;
    long requestFileBytes = 0;
    var requestPath = Path.Combine(directory, "routing-request.json");
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var start = Stopwatch.GetTimestamp();
    for (var cycle = 0; cycle < cycles; cycle++)
    {
        commands.Cycle = cycle;
#if !BASELINE
        using var collector = new RuntimeCollector(config, commands);
#endif
        var oldState = File.GetLastWriteTimeUtc(config.StatePath);
        var oldRequest = File.GetLastWriteTimeUtc(requestPath);
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
            await cursors.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
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
    return new
    {
        cycles,
        workload = args[1],
        runner = args[2],
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
        requestWrites,
        stateBytes,
        requestFileBytes,
        fileBytes
    };
}

async Task VerifyAsync(string directory)
{
    foreach (var workload in new[] { "overlap", "burst", "churn" })
    {
        var config = new AgentConfig("srv", "org", "app", "http://synthetic.invalid", "minicloud-bench", "/unused", "/unused", Path.Combine(directory, workload, "state.json"));
        var commands = new SyntheticCommands(workload, false);
        using var handler = new SyntheticApi(config, new VirtualClock());
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
            await restarted.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
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
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

sealed class VirtualClock : TimeProvider
{
    private long _seconds;
    public override long TimestampFrequency => 1;
    public override long GetTimestamp() => _seconds;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_seconds);
    public void Advance() => _seconds += 15;
}

sealed class SyntheticApi(AgentConfig config, VirtualClock clock) : HttpMessageHandler
{
    private readonly HashSet<(string Source, string Cursor, DateTimeOffset ObservedAt)> _logs = [];
    private readonly IncrementalHash _recordHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string _version = "0000000000";
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
                return Ok(new RuntimeDesiredRoutingResponse(_version, config.AppId, config.ComposeProject, [
                    new("dom1", "first.example.test", "/", "svc1", 8080),
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

sealed class SyntheticProbe : IHttpsProbe
{
    public int Calls { get; private set; }
    public Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(true);
    }
}

sealed class SyntheticCommands(string workload, bool childProcesses) : IProcessRunner
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
            if (workload is "burst" or "churn")
            {
                _burstContainer = BurstOutput(25, "container");
                _burstJournal = BurstOutput(60, "caddy");
            }
        }
    }
    public string[] Services => Enumerable.Range(0, 4).Select(slot =>
        workload == "churn" ? $"svc{(slot + Cycle) % 4 + 1}-generation{Cycle / 4}" : $"svc{slot + 1}").ToArray();
    private string ContainerId(int slot) => workload == "churn" ? $"id{slot + 1}-generation{Cycle / 4}" : $"id{slot + 1}";

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = fileName == "docker" ? $"docker {args[0]}" : fileName;
        Calls[command] = Calls.GetValueOrDefault(command) + 1;
        ProcessResult result = command switch
        {
            "docker ps" => new(0, string.Join('\n', Enumerable.Range(0, 4).Select(ContainerId)) + "\n", ""),
            "docker inspect" => new(0, "[" + string.Join(',', Services.Select((service, slot) =>
                $$$"""{"Id":"{{{ContainerId(slot)}}}","Name":"/bench-{{{slot + 1}}}","Config":{"Image":"app:v1","Labels":{"com.docker.compose.service":"{{{service}}}"}},"State":{"Status":"running"},"RestartCount":0}""")) + "]", ""),
            "docker logs" when workload == "overlap" => Log(ContainerLog, ContainerLog, 80),
            "journalctl" when workload == "overlap" => Log(JournalLog, "", 24),
            "docker logs" when workload is "burst" or "churn" => Log(_burstContainer, _burstContainer, 100),
            "journalctl" when workload is "burst" or "churn" => Log(_burstJournal, "", 120),
            "docker logs" or "journalctl" or "sudo" => new(0, "", ""),
            "systemctl" => new(0, "active\n", ""),
            _ => throw new InvalidOperationException($"Unexpected command: {command}")
        };
        return childProcesses
            ? _runner.RunAsync("/bin/sh", ["-c", "printf '%s' \"$1\"; printf '%s' \"$2\" >&2", "fixture", result.Stdout, result.Stderr], cancellationToken)
            : Task.FromResult(result);
    }

    private ProcessResult Log(string stdout, string stderr, int lines)
    {
        LogInputBytes += Encoding.UTF8.GetByteCount(stdout) + Encoding.UTF8.GetByteCount(stderr);
        LogInputLines += lines;
        return new(0, stdout, stderr);
    }

    private static string Output(int count, string message) => string.Concat(Enumerable.Range(1, count)
        .Select(n => $"2026-01-01T00:00:00Z {message} log line {n} token=example-token\n"));

    private string BurstOutput(int halfCount, string message) => string.Concat(new[] { Cycle - 1, Cycle }.SelectMany(cycle =>
        Enumerable.Range(1, halfCount).Select(n =>
            $"{Epoch.AddSeconds(cycle * 15):yyyy-MM-ddTHH:mm:ssZ} {message} cycle {cycle} line {n} token=example-token\n")));
}
