using System.Diagnostics;
using System.Text.Json;
using Minicloud.Profiling.EventPipe;
using Minicloud.RuntimeAgent;

namespace Minicloud.Tests;

[Collection("Runtime ingestion diagnostics")]
public sealed class EventPipeSessionCompletionTests
{
    [Theory]
    [InlineData("/tmp/disallowed.nettrace")]
    [InlineData("docs/disallowed.nettrace")]
    [InlineData(".local/artifacts/not-a-trace.json")]
    public void EventPipeArtifactGuard_RejectsPathsOutsideLocalArtifactsOrWrongExtension(string path)
    {
        var repoRoot = RuntimeAgentProfilingTests.FindRepositoryRoot();
        var candidate = Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path);
        Assert.Throws<InvalidOperationException>(() =>
            EventPipeArtifactGuard.RequireTraceUnderArtifacts(candidate, repoRoot));
    }

    [Fact]
    public async Task CaptureWhileAlive_CompletesTransportMarkerCertifiesActivityClosureAndDetectsTruncation()
    {
        var repoRoot = RuntimeAgentProfilingTests.FindRepositoryRoot();
        var artifactDir = Path.Combine(repoRoot, ".local", "artifacts", $"test-eventpipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDir);

        try
        {
            var benchmarkDll = FindBenchmarkAssemblyPath(repoRoot);
            var benchScratch = Path.Combine(artifactDir, "bench-scratch");
            Directory.CreateDirectory(benchScratch);
            var startFile = Path.Combine(artifactDir, "start.flag");
            var readyFile = Path.Combine(artifactDir, "ready.flag");
            var stopFile = Path.Combine(artifactDir, "stop.flag");
            var tracePath = Path.Combine(artifactDir, "runtime-agent-graceful.nettrace");

            var providers = EventPipeSessionCoordinator.CreateRuntimeAgentProviders(
                RuntimeAgentDiagnostics.ActivitySourceName,
                includeActivities: true,
                includeGcAllocations: false,
                includeMeters: false);

            var startInfo = new ProcessStartInfo(ResolveDotnetExecutable())
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(benchmarkDll);
            startInfo.ArgumentList.Add(benchScratch);
            startInfo.ArgumentList.Add("protocol");
            startInfo.ArgumentList.Add("mock");
            startInfo.ArgumentList.Add("on");
            startInfo.ArgumentList.Add("--cycles");
            startInfo.ArgumentList.Add("6");
            startInfo.ArgumentList.Add("--start-file");
            startInfo.ArgumentList.Add(startFile);
            startInfo.ArgumentList.Add("--ready-file");
            startInfo.ArgumentList.Add(readyFile);
            startInfo.ArgumentList.Add("--stop-file");
            startInfo.ArgumentList.Add(stopFile);

            var targetWasAliveWhenStopTriggered = false;
            var receipt = await EventPipeSessionCoordinator.CaptureLaunchedProcessWhileAliveAsync(
                startInfo,
                tracePath,
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

                    targetWasAliveWhenStopTriggered = !process.HasExited;
                },
                releaseTargetAfterCapture: async (process, ct) =>
                {
                    await File.WriteAllTextAsync(stopFile, "stop\n", ct);
                },
                stopDrainTimeout: TimeSpan.FromSeconds(15),
                circularBufferMb: 16,
                repositoryRoot: repoRoot);

            Assert.True(targetWasAliveWhenStopTriggered, "Target process must still be alive when EventPipe Stop + drain begins.");
            Assert.True(receipt.GracefulStopCompleted);
            Assert.True(receipt.BytesWritten > 0);
            Assert.True(File.Exists(tracePath));

            var report = EventPipeTraceInspector.InspectAggregateOnly(
                tracePath,
                RuntimeAgentDiagnostics.ActivitySourceName,
                repoRoot);

            Assert.True(report.StreamCompletedCleanly);
            Assert.Equal(0, report.EventsLost);
            Assert.True(report.ActivityStarts >= 12, $"Expected at least 12 Activity starts, got {report.ActivityStarts}.");
            Assert.Equal(report.ActivityStarts, report.ActivityStops);
            Assert.Equal(report.ActivityStarts, report.MatchedActivities);
            Assert.Equal(0, report.UnmatchedActivityStarts);
            Assert.Equal(0, report.UnmatchedActivityStops);
            Assert.True(report.ActivityClosureCertified);
            Assert.NotNull(report.MatchedDurationMs);
            Assert.Equal(report.MatchedActivities, report.MatchedDurationMs!.Count);
            Assert.Contains("heartbeat", report.MatchedByOperation.Keys);
            Assert.Contains("logs", report.MatchedByOperation.Keys);
            Assert.Contains("routing", report.MatchedByOperation.Keys);

            // Enforce aggregate-only JSON contract guardrail.
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            EventPipeActivityClosureReport.ValidateAggregateOnlyJson(json);

            // Negative guardrail: a truncated .nettrace lacking the final transport EndObject marker
            // (simulating killing the target before EventPipe session completion) must fail certification.
            var truncatedPath = Path.Combine(artifactDir, "runtime-agent-truncated.nettrace");
            var fullBytes = await File.ReadAllBytesAsync(tracePath);
            Assert.True(fullBytes.Length > 128);
            await File.WriteAllBytesAsync(truncatedPath, fullBytes[..^64]);

            var truncatedReport = EventPipeTraceInspector.InspectAggregateOnly(
                truncatedPath,
                RuntimeAgentDiagnostics.ActivitySourceName,
                repoRoot);

            Assert.False(truncatedReport.StreamCompletedCleanly);
            Assert.False(truncatedReport.ActivityClosureCertified);
        }
        finally
        {
            if (Directory.Exists(artifactDir))
            {
                Directory.Delete(artifactDir, recursive: true);
            }
        }
    }

    private static string ResolveDotnetExecutable()
    {
        var currentProcessPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcessPath) &&
            Path.GetFileNameWithoutExtension(currentProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return currentProcessPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    private static string FindBenchmarkAssemblyPath(string repoRoot)
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var preferred = Path.Combine(repoRoot, "tools", "runtime-agent-benchmark", "bin", configuration, "net10.0", "RuntimeAgentBenchmark.dll");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var fallbackConfig = configuration == "Release" ? "Debug" : "Release";
        var fallback = Path.Combine(repoRoot, "tools", "runtime-agent-benchmark", "bin", fallbackConfig, "net10.0", "RuntimeAgentBenchmark.dll");
        if (File.Exists(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException($"RuntimeAgentBenchmark.dll was not found at {preferred} or {fallback}.");
    }

    [Fact]
    public void AggregateOnlyGuardrail_RejectsUnexpectedRawPayloadFields()
    {
        const string invalidPayload = """
            {
              "schema": "minicloud.eventpipe.aggregate.v1",
              "streamCompletedCleanly": true,
              "rawTracePayload": "should-never-be-emitted"
            }
            """;

        Assert.Throws<InvalidOperationException>(() =>
            EventPipeActivityClosureReport.ValidateAggregateOnlyJson(invalidPayload));
    }
}
