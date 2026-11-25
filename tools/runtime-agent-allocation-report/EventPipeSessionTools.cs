using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.Serialization;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Selfrun.Profiling.EventPipe;

public static class EventPipeArtifactGuard
{
    public static string RequireTraceUnderArtifacts(string tracePath, string? repositoryRoot = null)
    {
        if (string.IsNullOrWhiteSpace(tracePath))
        {
            throw new InvalidOperationException("Trace path is required.");
        }

        var fullTracePath = Path.GetFullPath(tracePath);
        var artifactsRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(repositoryRoot)
                ? Path.Combine(".local", "artifacts")
                : Path.Combine(repositoryRoot, ".local", "artifacts"));

        var requiredPrefix = artifactsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? artifactsRoot
            : artifactsRoot + Path.DirectorySeparatorChar;

        if (!fullTracePath.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Trace files must stay under this repository's .local/artifacts directory; raw trace exports elsewhere are disallowed.");
        }

        if (!fullTracePath.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Trace file must use the .nettrace extension.");
        }

        return fullTracePath;
    }
}

public sealed record EventPipeCaptureReceipt(
    string TracePath,
    int TargetProcessId,
    bool GracefulStopCompleted,
    long BytesWritten,
    double StopDrainDurationMs);

public sealed record EventPipeDurationSummary(
    int Count,
    double MinMs,
    double MedianMs,
    double P95Ms,
    double MaxMs,
    double TotalMs);

public sealed record EventPipeActivityClosureReport(
    string Schema,
    bool StreamCompletedCleanly,
    long TotalEvents,
    int EventsLost,
    int ActivityStarts,
    int ActivityStops,
    int MatchedActivities,
    int UnmatchedActivityStarts,
    int UnmatchedActivityStops,
    bool ActivityClosureCertified,
    int GcAllocationTicks,
    long GcSampledAllocatedBytes,
    int MeterEvents,
    EventPipeDurationSummary? MatchedDurationMs,
    IReadOnlyDictionary<string, int> MatchedByOperation)
{
    public const string SchemaVersion = "selfrun.eventpipe.aggregate.v1";

    private static readonly HashSet<string> AllowedFields = new(StringComparer.Ordinal)
    {
        "schema",
        "streamCompletedCleanly",
        "totalEvents",
        "eventsLost",
        "activityStarts",
        "activityStops",
        "matchedActivities",
        "unmatchedActivityStarts",
        "unmatchedActivityStops",
        "activityClosureCertified",
        "gcAllocationTicks",
        "gcSampledAllocatedBytes",
        "meterEvents",
        "matchedDurationMs",
        "matchedByOperation"
    };

    private static readonly HashSet<string> AllowedDurationFields = new(StringComparer.Ordinal)
    {
        "count",
        "minMs",
        "medianMs",
        "p95Ms",
        "maxMs",
        "totalMs"
    };

    public static void ValidateAggregateOnlyJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Aggregate EventPipe report must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!AllowedFields.Contains(property.Name))
            {
                throw new InvalidOperationException($"Disallowed field '{property.Name}' in aggregate EventPipe report.");
            }
        }

        if (!document.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != SchemaVersion)
        {
            throw new InvalidOperationException("Aggregate EventPipe report schema mismatch.");
        }

        if (document.RootElement.TryGetProperty("matchedDurationMs", out var duration) &&
            duration.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in duration.EnumerateObject())
            {
                if (!AllowedDurationFields.Contains(field.Name))
                {
                    throw new InvalidOperationException($"Disallowed duration field '{field.Name}' in aggregate EventPipe report.");
                }
            }
        }
    }
}

public static class EventPipeSessionCoordinator
{
    public static IReadOnlyList<EventPipeProvider> CreateRuntimeAgentProviders(
        string activitySourceName = "Selfrun.RuntimeAgent",
        bool includeActivities = true,
        bool includeGcAllocations = true,
        bool includeMeters = true)
    {
        var providers = new List<EventPipeProvider>();
        if (includeActivities)
        {
            providers.Add(new EventPipeProvider(
                "Microsoft-Diagnostics-DiagnosticSource",
                EventLevel.Verbose,
                0x3,
                new Dictionary<string, string>
                {
                    ["FilterAndPayloadSpecs"] = $"[AS]{activitySourceName}\r\n"
                }));
            // Enable async ActivityID flow so Activity/Start and Activity/Stop correlate across await thread hops.
            providers.Add(new EventPipeProvider(
                "System.Threading.Tasks.TplEventSource",
                EventLevel.Informational,
                0x80));
        }

        if (includeGcAllocations)
        {
            providers.Add(new EventPipeProvider(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Verbose,
                0x1));
        }

        if (includeMeters)
        {
            providers.Add(new EventPipeProvider(
                "System.Diagnostics.Metrics",
                EventLevel.Informational,
                0x3,
                new Dictionary<string, string>
                {
                    ["Metrics"] = $"{activitySourceName};System.Runtime",
                    ["RefreshInterval"] = "1"
                }));
        }

        return providers;
    }

    /// <summary>
    /// Attaches an EventPipe session to a live process, runs the bounded workload callback,
    /// and finishes the EventPipe session (Stop + drain to clean EOF with final transport marker)
    /// WHILE the target process is still alive.
    /// </summary>
    public static async Task<EventPipeCaptureReceipt> CaptureWhileAliveAsync(
        int targetProcessId,
        string traceOutputPath,
        IReadOnlyCollection<EventPipeProvider> providers,
        Func<CancellationToken, Task> runWorkloadWhileCapturing,
        TimeSpan? stopDrainTimeout = null,
        int circularBufferMb = 64,
        string? repositoryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(runWorkloadWhileCapturing);

        var fullTracePath = EventPipeArtifactGuard.RequireTraceUnderArtifacts(traceOutputPath, repositoryRoot);
        var directory = Path.GetDirectoryName(fullTracePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var drainBudget = stopDrainTimeout ?? TimeSpan.FromSeconds(15);
        if (drainBudget <= TimeSpan.Zero || drainBudget > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(stopDrainTimeout), "Stop drain timeout must be between 1ms and 2 minutes.");
        }

        var client = new DiagnosticsClient(targetProcessId);
        return await CaptureSessionWhileAliveAsync(
            client,
            targetProcessId,
            fullTracePath,
            providers,
            runWorkloadWhileCapturing,
            drainBudget,
            circularBufferMb,
            diagnosticTempDir: null,
            cancellationToken);
    }

    /// <summary>
    /// Launches a target .NET process with a short diagnostic TMPDIR (avoiding Linux Unix domain
    /// socket sun_path truncation when parent TMPDIR is long), attaches a public DiagnosticsClient
    /// EventPipe session once the process IPC socket is ready, runs the live workload callback,
    /// and stops/drains the EventPipe session to clean EOF BEFORE releasing the target process to exit.
    /// </summary>
    public static async Task<EventPipeCaptureReceipt> CaptureLaunchedProcessWhileAliveAsync(
        ProcessStartInfo startInfo,
        string traceOutputPath,
        IReadOnlyCollection<EventPipeProvider> providers,
        Func<Process, CancellationToken, Task> waitForWorkloadWhileCapturing,
        Func<Process, CancellationToken, Task> releaseTargetAfterCapture,
        TimeSpan? stopDrainTimeout = null,
        int circularBufferMb = 64,
        string? repositoryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(waitForWorkloadWhileCapturing);
        ArgumentNullException.ThrowIfNull(releaseTargetAfterCapture);

        var fullTracePath = EventPipeArtifactGuard.RequireTraceUnderArtifacts(traceOutputPath, repositoryRoot);
        var directory = Path.GetDirectoryName(fullTracePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var drainBudget = stopDrainTimeout ?? TimeSpan.FromSeconds(15);
        if (drainBudget <= TimeSpan.Zero || drainBudget > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(stopDrainTimeout), "Stop drain timeout must be between 1ms and 2 minutes.");
        }

        var diagnosticTempDir = ResolveShortDiagnosticTempDir();
        if (!OperatingSystem.IsWindows())
        {
            startInfo.Environment["TMPDIR"] = diagnosticTempDir;
        }
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "1";

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to launch target process for EventPipe capture.");

            await WaitForDiagnosticIpcReadyAsync(process, diagnosticTempDir, TimeSpan.FromSeconds(15), cancellationToken);

            var client = new DiagnosticsClient(process.Id);
            EventPipeCaptureReceipt receipt;
            try
            {
                receipt = await CaptureSessionWhileAliveAsync(
                    client,
                    process.Id,
                    fullTracePath,
                    providers,
                    ct => waitForWorkloadWhileCapturing(process, ct),
                    drainBudget,
                    circularBufferMb,
                    diagnosticTempDir,
                    cancellationToken);
            }
            finally
            {
                try
                {
                    await releaseTargetAfterCapture(process, cancellationToken);
                }
                catch
                {
                }
            }

            if (!process.HasExited)
            {
                using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(exitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }

            return receipt;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                process.Dispose();
            }
        }
    }

    private static string ResolveShortDiagnosticTempDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.GetTempPath();
        }

        var current = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        // Linux sockaddr_un.sun_path max is 108 bytes; dotnet appends "/dotnet-diagnostic-<pid>-<key>-socket" (~44 bytes).
        if (current.Length <= 48 && Directory.Exists(current))
        {
            return current;
        }

        return Directory.Exists("/tmp") ? "/tmp" : current;
    }

    private static async Task WaitForDiagnosticIpcReadyAsync(
        Process process,
        string diagnosticTempDir,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Delay(150, cancellationToken);
            return;
        }

        var pattern = $"dotnet-diagnostic-{process.Id}-*-socket";
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Target process {process.Id} exited ({process.ExitCode}) before diagnostic IPC became ready.");
            }

            if (Directory.EnumerateFiles(diagnosticTempDir, pattern).Any())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for diagnostic IPC socket '{pattern}' in '{diagnosticTempDir}'.");
    }

    private static async Task<EventPipeCaptureReceipt> CaptureSessionWhileAliveAsync(
        DiagnosticsClient client,
        int targetProcessId,
        string fullTracePath,
        IReadOnlyCollection<EventPipeProvider> providers,
        Func<CancellationToken, Task> runWorkloadWhileCapturing,
        TimeSpan drainBudget,
        int circularBufferMb,
        string? diagnosticTempDir,
        CancellationToken cancellationToken)
    {
        var previousTmpDir = OperatingSystem.IsWindows() || diagnosticTempDir is null
            ? null
            : Environment.GetEnvironmentVariable("TMPDIR");
        EventPipeSession session;
        try
        {
            if (!OperatingSystem.IsWindows() && diagnosticTempDir is not null)
            {
                Environment.SetEnvironmentVariable("TMPDIR", diagnosticTempDir);
            }

            session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: circularBufferMb);
        }
        finally
        {
            if (!OperatingSystem.IsWindows() && diagnosticTempDir is not null)
            {
                Environment.SetEnvironmentVariable("TMPDIR", previousTmpDir);
            }
        }

        using (session)
        {
            using var copyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            long bytesWritten = 0;
            var copyTask = Task.Run(async () =>
            {
                await using var output = new FileStream(fullTracePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await session.EventStream.ReadAsync(buffer.AsMemory(), copyCts.Token);
                    if (read <= 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), copyCts.Token);
                    Interlocked.Add(ref bytesWritten, read);
                }

                await output.FlushAsync(copyCts.Token);
            }, CancellationToken.None);

            Exception? workloadError = null;
            try
            {
                await runWorkloadWhileCapturing(cancellationToken);
            }
            catch (Exception ex)
            {
                workloadError = ex;
            }

            var stopStartedAt = Stopwatch.GetTimestamp();
            var gracefulStopCompleted = false;
            try
            {
                if (!OperatingSystem.IsWindows() && diagnosticTempDir is not null)
                {
                    Environment.SetEnvironmentVariable("TMPDIR", diagnosticTempDir);
                }

                // Target is still alive here: stop EventPipe and wait for final transport EOF before returning.
                session.Stop();
                await copyTask.WaitAsync(drainBudget, cancellationToken);
                gracefulStopCompleted = true;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or ServerNotAvailableException or IOException)
            {
                await copyCts.CancelAsync();
                try
                {
                    await copyTask.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch
                {
                }
            }
            finally
            {
                if (!OperatingSystem.IsWindows() && diagnosticTempDir is not null)
                {
                    Environment.SetEnvironmentVariable("TMPDIR", previousTmpDir);
                }
            }

            var stopDrainMs = Math.Round(Stopwatch.GetElapsedTime(stopStartedAt).TotalMilliseconds, 3);
            if (workloadError is not null)
            {
                throw workloadError;
            }

            return new EventPipeCaptureReceipt(
                fullTracePath,
                targetProcessId,
                gracefulStopCompleted,
                Interlocked.Read(ref bytesWritten),
                stopDrainMs);
        }
    }
}

public static class EventPipeTraceInspector
{
    /// <summary>
    /// Inspects a local .nettrace file under .local/artifacts using Microsoft.Diagnostics.Tracing.TraceEvent
    /// and returns strictly aggregate-only transport completion and Activity closure metrics.
    /// </summary>
    public static EventPipeActivityClosureReport InspectAggregateOnly(
        string tracePath,
        string? activitySourceFilter = null,
        string? repositoryRoot = null)
    {
        var fullPath = EventPipeArtifactGuard.RequireTraceUnderArtifacts(tracePath, repositoryRoot);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Trace file not found.", fullPath);
        }

        var streamCompletedCleanly = false;
        long totalEvents = 0;
        var eventsLost = 0;
        var activityStarts = 0;
        var activityStops = 0;
        var matchedActivities = 0;
        var unmatchedStops = 0;
        var gcTicks = 0;
        long gcSampledBytes = 0;
        var meterEvents = 0;

        var openActivities = new Dictionary<string, Stack<(double StartMs, string Operation)>>(StringComparer.Ordinal);
        var durationsMs = new List<double>();
        var matchedByOperation = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            using var source = new EventPipeEventSource(fullPath);

            source.Clr.GCAllocationTick += allocation =>
            {
                gcTicks++;
                gcSampledBytes += allocation.AllocationAmount64;
            };

            source.Dynamic.All += traceEvent =>
            {
                totalEvents++;

                if (string.Equals(traceEvent.ProviderName, "System.Diagnostics.Metrics", StringComparison.Ordinal))
                {
                    meterEvents++;
                    return;
                }

                if (!string.Equals(traceEvent.ProviderName, "Microsoft-Diagnostics-DiagnosticSource", StringComparison.Ordinal))
                {
                    return;
                }

                var isStart = string.Equals(traceEvent.EventName, "Activity/Start", StringComparison.Ordinal) ||
                              string.Equals(traceEvent.EventName, "ActivityStart", StringComparison.Ordinal);
                var isStop = string.Equals(traceEvent.EventName, "Activity/Stop", StringComparison.Ordinal) ||
                             string.Equals(traceEvent.EventName, "ActivityStop", StringComparison.Ordinal);
                if (!isStart && !isStop)
                {
                    return;
                }

                var sourceName = TryGetStringPayload(traceEvent, "SourceName");
                if (!string.IsNullOrWhiteSpace(activitySourceFilter) &&
                    !string.Equals(sourceName, activitySourceFilter, StringComparison.Ordinal))
                {
                    return;
                }

                var operationName = NormalizeBoundedOperation(TryGetStringPayload(traceEvent, "ActivityName"));
                var correlationKey = BuildCorrelationKey(traceEvent, sourceName, operationName);

                if (isStart)
                {
                    activityStarts++;
                    if (!openActivities.TryGetValue(correlationKey, out var stack))
                    {
                        stack = new Stack<(double, string)>();
                        openActivities[correlationKey] = stack;
                    }

                    stack.Push((traceEvent.TimeStampRelativeMSec, operationName));
                }
                else
                {
                    activityStops++;
                    if (openActivities.TryGetValue(correlationKey, out var stack) && stack.Count > 0)
                    {
                        var (startMs, op) = stack.Pop();
                        if (stack.Count == 0)
                        {
                            openActivities.Remove(correlationKey);
                        }

                        matchedActivities++;
                        durationsMs.Add(Math.Max(0.0, traceEvent.TimeStampRelativeMSec - startMs));
                        matchedByOperation[op] = matchedByOperation.GetValueOrDefault(op) + 1;
                    }
                    else
                    {
                        unmatchedStops++;
                    }
                }
            };

            source.Process();
            eventsLost = source.EventsLost;
            streamCompletedCleanly = HasFinalTransportEndMarker(fullPath);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or InvalidDataException or SerializationException or IOException or ApplicationException)
        {
            streamCompletedCleanly = false;
        }

        var unmatchedStarts = openActivities.Values.Sum(stack => stack.Count);
        EventPipeDurationSummary? durationSummary = null;
        if (durationsMs.Count > 0)
        {
            durationsMs.Sort();
            var median = durationsMs[durationsMs.Count / 2];
            var p95 = durationsMs[(int)Math.Floor(0.95 * (durationsMs.Count - 1))];
            durationSummary = new EventPipeDurationSummary(
                durationsMs.Count,
                Math.Round(durationsMs[0], 3),
                Math.Round(median, 3),
                Math.Round(p95, 3),
                Math.Round(durationsMs[^1], 3),
                Math.Round(durationsMs.Sum(), 3));
        }

        var certified = streamCompletedCleanly &&
                        eventsLost == 0 &&
                        activityStarts > 0 &&
                        activityStarts == activityStops &&
                        unmatchedStarts == 0 &&
                        unmatchedStops == 0;

        return new EventPipeActivityClosureReport(
            EventPipeActivityClosureReport.SchemaVersion,
            streamCompletedCleanly,
            totalEvents,
            eventsLost,
            activityStarts,
            activityStops,
            matchedActivities,
            unmatchedStarts,
            unmatchedStops,
            certified,
            gcTicks,
            gcSampledBytes,
            meterEvents,
            durationSummary,
            new SortedDictionary<string, int>(matchedByOperation, StringComparer.Ordinal));
    }

    /// <summary>
    /// Verifies that the NetTrace v4+ FastSerialization container terminates cleanly with
    /// EndObject (0x06) followed by NullReference (0x01) at EOF, matching CoreCLR EventPipeFile::Close().
    /// </summary>
    private static bool HasFinalTransportEndMarker(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.Length < 20)
        {
            return false;
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || !header.SequenceEqual("Nettrace"u8))
        {
            return false;
        }

        stream.Seek(-2, SeekOrigin.End);
        Span<byte> trailer = stackalloc byte[2];
        if (stream.Read(trailer) != 2)
        {
            return false;
        }

        // FastSerialization Tags.EndObject (0x06) closes the final block, followed by Tags.NullReference (0x01) at clean EOF.
        return trailer[0] == 0x06 && trailer[1] == 0x01;
    }

    private static string BuildCorrelationKey(TraceEvent traceEvent, string? sourceName, string operationName)
    {
        var activityId = traceEvent.ActivityID;
        if (activityId != Guid.Empty)
        {
            return $"{sourceName}:{activityId:N}";
        }

        return $"{sourceName}:{operationName}";
    }

    private static string? TryGetStringPayload(TraceEvent traceEvent, string fieldName)
    {
        try
        {
            return traceEvent.PayloadByName(fieldName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeBoundedOperation(string? operation) => operation switch
    {
        "heartbeat" or "status" or "routing" or "logs" or
        "docker.ps" or "docker.inspect" or "docker.logs" or "routing.apply" or "journal.logs" or "systemctl.status" => operation,
        _ => "other"
    };
}
