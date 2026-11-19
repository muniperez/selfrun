using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Minicloud.RuntimeAgent;

if (args is not [var workDirectory, "idle" or "overlap", "mock" or "children"] ||
    !Directory.Exists(workDirectory) || Directory.EnumerateFileSystemEntries(workDirectory).Any())
{
    Console.Error.WriteLine("Usage: RuntimeAgentBenchmark <empty scratch directory> <idle|overlap> <mock|children>");
    return 2;
}

await RunAsync(Path.Combine(workDirectory, "warmup"), 8);
Console.WriteLine(JsonSerializer.Serialize(await RunAsync(Path.Combine(workDirectory, "measured"), 240)));
return 0;

async Task<object> RunAsync(string directory, int cycles)
{
    Directory.CreateDirectory(directory);
    var config = new AgentConfig("srv", "org", "app", "http://synthetic.invalid", "minicloud-bench", "/unused", "/unused", Path.Combine(directory, "state.json"));
    var commands = new SyntheticCommands(args[1] == "overlap", args[2] == "children");
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
    var requestPath = Path.Combine(directory, "routing-request.json");
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var start = Stopwatch.GetTimestamp();
    for (var cycle = 0; cycle < cycles; cycle++)
    {
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
            fileBytes += new FileInfo(config.StatePath).Length;
        }
        if (File.GetLastWriteTimeUtc(requestPath) != oldRequest)
        {
            requestWrites++;
            fileBytes += new FileInfo(requestPath).Length;
        }
        clock.Advance();
    }
    return new
    {
        cycles, workload = args[1], runner = args[2], containers = 4, routes = 2,
        wallMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds, cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        commands.Calls, commands.LogInputBytes, commands.LogInputLines,
        handler.Submitted, handler.Acknowledged, handler.InsertedLogs,
        handler.StatusRows, handler.RouteRows, handler.RequestBytes, handler.Requests, tlsProbes = probe.Calls,
        stateWrites, requestWrites, fileBytes
    };
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
    private string _version = "0000000000";
    public int InsertedLogs => _logs.Count;
    public int Submitted { get; private set; }
    public int Acknowledged { get; private set; }
    public int StatusRows { get; private set; }
    public int RouteRows { get; private set; }
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
                foreach (var record in batch.Records)
                {
                    if (_logs.Add((record.Source, record.Cursor, record.ObservedAt))) { inserted++; }
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

sealed class SyntheticCommands(bool overlap, bool childProcesses) : IProcessRunner
{
    private static readonly string Inspection = "[" + string.Join(',', Enumerable.Range(1, 4).Select(n =>
        $$$"""{"Id":"id{{{n}}}","Name":"/bench-{{{n}}}","Config":{"Image":"app:v1","Labels":{"com.docker.compose.service":"svc{{{n}}}"}},"State":{"Status":"running"},"RestartCount":0}""")) + "]";
    private static readonly string ContainerLog = Output(40, "container");
    private static readonly string JournalLog = Output(24, "caddy");
    private readonly ProcessRunner _runner = new();
    public Dictionary<string, int> Calls { get; } = [];
    public long LogInputBytes { get; private set; }
    public long LogInputLines { get; private set; }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var command = fileName == "docker" ? $"docker {args[0]}" : fileName;
        Calls[command] = Calls.GetValueOrDefault(command) + 1;
        ProcessResult result = command switch
        {
            "docker ps" => new(0, "id1\nid2\nid3\nid4\n", ""),
            "docker inspect" => new(0, Inspection, ""),
            "docker logs" when overlap => Log(ContainerLog, ContainerLog, 80),
            "journalctl" when overlap => Log(JournalLog, "", 24),
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
}
