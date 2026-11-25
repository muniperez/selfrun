using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Selfrun.Profiling.EventPipe;

if (args.Length >= 2 && args[0] == "--capture-benchmark")
{
    var traceOutput = args[1];
    var workload = args.Length >= 3 ? args[2] : "protocol";
    var profilingMode = args.Length >= 4 ? args[3] : "on";
    var cycles = args.Length >= 5 && int.TryParse(args[4], out var parsedCycles) ? parsedCycles : 12;
    try
    {
        var fullTrace = EventPipeArtifactGuard.RequireTraceUnderArtifacts(traceOutput);
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullTrace)!, "..", "..", ".."));
        while (!Directory.Exists(Path.Combine(repoRoot, ".git")) && !File.Exists(Path.Combine(repoRoot, ".git")))
        {
            var parent = Directory.GetParent(repoRoot);
            if (parent is null) { repoRoot = Directory.GetCurrentDirectory(); break; }
            repoRoot = parent.FullName;
        }

        var configDir = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var benchDll = Path.Combine(repoRoot, "tools", "runtime-agent-benchmark", "bin", configDir, "net10.0", "RuntimeAgentBenchmark.dll");
        if (!File.Exists(benchDll))
        {
            var altConfig = configDir == "Release" ? "Debug" : "Release";
            benchDll = Path.Combine(repoRoot, "tools", "runtime-agent-benchmark", "bin", altConfig, "net10.0", "RuntimeAgentBenchmark.dll");
        }
        if (!File.Exists(benchDll))
        {
            Console.Error.WriteLine($"RuntimeAgentBenchmark.dll not found at {benchDll}. Build tools/runtime-agent-benchmark first.");
            return 2;
        }

        var scratchRoot = Path.Combine(Path.GetDirectoryName(fullTrace)!, $"capture-scratch-{Guid.NewGuid():N}");
        var benchScratch = Path.Combine(scratchRoot, "bench");
        Directory.CreateDirectory(benchScratch);
        var startFile = Path.Combine(scratchRoot, "start.flag");
        var readyFile = Path.Combine(scratchRoot, "ready.flag");
        var stopFile = Path.Combine(scratchRoot, "stop.flag");
        try
        {
            var dotnet = Environment.ProcessPath is { Length: > 0 } p &&
                         Path.GetFileNameWithoutExtension(p).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? p
                : "dotnet";
            var startInfo = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(benchDll);
            startInfo.ArgumentList.Add(benchScratch);
            startInfo.ArgumentList.Add(workload);
            startInfo.ArgumentList.Add("mock");
            startInfo.ArgumentList.Add(profilingMode);
            startInfo.ArgumentList.Add("--cycles");
            startInfo.ArgumentList.Add(cycles.ToString());
            startInfo.ArgumentList.Add("--start-file");
            startInfo.ArgumentList.Add(startFile);
            startInfo.ArgumentList.Add("--ready-file");
            startInfo.ArgumentList.Add(readyFile);
            startInfo.ArgumentList.Add("--stop-file");
            startInfo.ArgumentList.Add(stopFile);

            var providers = EventPipeSessionCoordinator.CreateRuntimeAgentProviders(
                "Selfrun.RuntimeAgent",
                includeActivities: true,
                includeGcAllocations: false,
                includeMeters: false);

            var receipt = await EventPipeSessionCoordinator.CaptureLaunchedProcessWhileAliveAsync(
                startInfo,
                fullTrace,
                providers,
                waitForWorkloadWhileCapturing: async (process, ct) =>
                {
                    await File.WriteAllTextAsync(startFile, "go\n", ct);
                    var deadline = Stopwatch.GetTimestamp() + 20L * Stopwatch.Frequency;
                    while (!File.Exists(readyFile))
                    {
                        if (process.HasExited)
                        {
                            var stderr = await process.StandardError.ReadToEndAsync(ct);
                            throw new InvalidOperationException($"Benchmark exited early ({process.ExitCode}): {stderr}");
                        }
                        if (Stopwatch.GetTimestamp() >= deadline)
                        {
                            throw new TimeoutException("Timed out waiting for benchmark ready-file.");
                        }
                        await Task.Delay(25, ct);
                    }
                },
                releaseTargetAfterCapture: async (_, ct) => await File.WriteAllTextAsync(stopFile, "stop\n", ct),
                stopDrainTimeout: TimeSpan.FromSeconds(15),
                circularBufferMb: 16,
                repositoryRoot: repoRoot);

            var report = EventPipeTraceInspector.InspectAggregateOnly(fullTrace, "Selfrun.RuntimeAgent", repoRoot);
            var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            EventPipeActivityClosureReport.ValidateAggregateOnlyJson(reportJson);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                capture = new
                {
                    gracefulStopCompleted = receipt.GracefulStopCompleted,
                    bytesWritten = receipt.BytesWritten,
                    stopDrainDurationMs = receipt.StopDrainDurationMs
                },
                report
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return receipt.GracefulStopCompleted && report.ActivityClosureCertified ? 0 : 1;
        }
        finally
        {
            if (Directory.Exists(scratchRoot))
            {
                Directory.Delete(scratchRoot, recursive: true);
            }
        }
    }
    catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or TimeoutException)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

if (args.Length is 2 or 3 && args[0] == "--verify-activity")
{
    var path = args[1];
    var sourceFilter = args.Length == 3 ? args[2] : "Selfrun.RuntimeAgent";
    try
    {
        var report = EventPipeTraceInspector.InspectAggregateOnly(path, sourceFilter);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        EventPipeActivityClosureReport.ValidateAggregateOnlyJson(json);
        Console.WriteLine(json);
        return report.ActivityClosureCertified ? 0 : 1;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

if (args is not [var tracePath] || !File.Exists(tracePath))
{
    Console.Error.WriteLine("Usage: RuntimeAgentAllocationReport [--verify-activity|--capture-benchmark] <nettrace file under .local/artifacts> [...]");
    return 2;
}

try
{
    EventPipeArtifactGuard.RequireTraceUnderArtifacts(tracePath);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var closure = EventPipeTraceInspector.InspectAggregateOnly(tracePath);
var etlx = TraceLog.CreateFromEventPipeDataFile(tracePath);
using var trace = new TraceLog(etlx);
var frames = new Dictionary<string, long>(StringComparer.Ordinal);
var stacks = new Dictionary<string, long>(StringComparer.Ordinal);
var buckets = new Dictionary<string, long>(StringComparer.Ordinal);
long sampledBytes = 0;
long missingStackBytes = 0;
var ticks = 0;
foreach (var traceEvent in trace.Events)
{
    if (traceEvent is not GCAllocationTickTraceData allocation) { continue; }
    ticks++;
    var bytes = allocation.AllocationAmount64;
    sampledBytes += bytes;
    var stack = allocation.CallStack();
    if (stack is null)
    {
        missingStackBytes += bytes;
        continue;
    }
    var names = new List<string>();
    for (; stack is not null; stack = stack.Caller)
    {
        names.Add(stack.CodeAddress.FullMethodName);
    }
    foreach (var name in names.Distinct(StringComparer.Ordinal))
    {
        frames[name] = frames.GetValueOrDefault(name) + bytes;
    }
    bool Has(string text) => names.Any(x => x.Contains(text, StringComparison.Ordinal));
    var bucket = Has("LogRedactor.Redact") ? "redaction"
        : Has("RuntimeCollector.Lines") ? "collector-lines"
        : Has("RuntimeCollector.StableSequence") ? "cursor-hash"
        : Has("LogParsing.SplitLogicalLines") ? "logical-lines-and-records"
        : Has("LogParsing.ParseDockerLine") ? "docker-fields"
        : Has("LogParsing.ParseJournalLine") ? "journal-fields"
        : Has("System.Linq") && Has("Group") && Has("CollectLogsAsync") ? "grouping"
        : Has("CollectLogsAsync") ? "collector-other"
        : "outside-log-collection";
    buckets[bucket] = buckets.GetValueOrDefault(bucket) + bytes;
    var key = $"{allocation.TypeName}: {string.Join(" <- ", names)}";
    stacks[key] = stacks.GetValueOrDefault(key) + bytes;
}
if (ticks == 0) { throw new InvalidOperationException("No GC allocation ticks; collect with the CLR GC keyword at verbose level."); }
Console.WriteLine(JsonSerializer.Serialize(new
{
    streamCompletedCleanly = closure.StreamCompletedCleanly,
    eventsLost = closure.EventsLost,
    activityStarts = closure.ActivityStarts,
    activityStops = closure.ActivityStops,
    activityClosureCertified = closure.ActivityClosureCertified,
    ticks,
    sampledBytes,
    missingStackBytes,
    allocationBuckets = buckets.OrderByDescending(x => x.Value).Select(x => new { bucket = x.Key, sampledBytes = x.Value }),
    inclusiveFrames = frames.OrderByDescending(x => x.Value).Take(40).Select(x => new { frame = x.Key, sampledBytes = x.Value }),
    collectorFrames = frames.Where(x => x.Key.Contains("LogParsing", StringComparison.Ordinal) || x.Key.Contains("RuntimeCollector", StringComparison.Ordinal))
        .OrderByDescending(x => x.Value).Select(x => new { frame = x.Key, sampledBytes = x.Value }),
    allocationStacks = stacks.OrderByDescending(x => x.Value).Take(20).Select(x => new { stack = x.Key, sampledBytes = x.Value })
}));
return 0;
