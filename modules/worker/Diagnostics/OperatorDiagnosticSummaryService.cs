using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Minicloud.Worker.Diagnostics;

internal sealed partial class OperatorDiagnosticSummaryService(
    OperatorDiagnostics diagnostics,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ILogger _logger = loggerFactory.CreateLogger("Minicloud.OperatorDiagnostics");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Window, timeProvider);
        var failureActive = false;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = diagnostics.SnapshotAndReset();
            var outcome = GetOutcome(snapshot);
            OperatorDiagnosticLogs.Summary(
                _logger,
                OperatorDiagnostics.ServiceName,
                OperatorDiagnostics.OperationName,
                outcome,
                (long)Window.TotalMilliseconds,
                snapshot.Count,
                snapshot.TotalDurationMilliseconds,
                snapshot.Bytes,
                snapshot.Failures,
                snapshot.TraceId);

            if (snapshot.Failures > 0 && !failureActive)
            {
                OperatorDiagnosticLogs.FailureTransition(
                    _logger, OperatorDiagnostics.ServiceName, OperatorDiagnostics.OperationName,
                    "failed", snapshot.Failures, snapshot.TraceId);
                failureActive = true;
            }
            else if (snapshot.Count > 0 && snapshot.Failures == 0 && failureActive)
            {
                OperatorDiagnosticLogs.FailureTransition(
                    _logger, OperatorDiagnostics.ServiceName, OperatorDiagnostics.OperationName,
                    "recovered", 0, snapshot.TraceId);
                failureActive = false;
            }
        }
    }

    private static string GetOutcome(OperatorDiagnosticSnapshot snapshot)
    {
        if (snapshot.Count == 0)
        {
            return "idle";
        }
        if (snapshot.Failures > 0)
        {
            return snapshot.Failures == snapshot.Count ? "failed" : "mixed";
        }
        if (snapshot.Cancellations > 0)
        {
            return snapshot.Cancellations == snapshot.Count ? "canceled" : "mixed";
        }
        return "succeeded";
    }
}

internal static partial class OperatorDiagnosticLogs
{
    [LoggerMessage(
        EventId = 8100,
        Level = LogLevel.Information,
        Message = "Operator operation summary: service={Service} operation={Operation} outcome={Outcome} window_duration_ms={WindowDurationMs} count={Count} total_duration_ms={TotalDurationMs} bytes={Bytes} failures={Failures} trace_id={TraceId}")]
    internal static partial void Summary(
        ILogger logger,
        string service,
        string operation,
        string outcome,
        long windowDurationMs,
        long count,
        long totalDurationMs,
        long bytes,
        long failures,
        string? traceId);

    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Warning,
        Message = "Operator operation failure transition: service={Service} operation={Operation} outcome={Outcome} failures={Failures} trace_id={TraceId}")]
    internal static partial void FailureTransition(
        ILogger logger,
        string service,
        string operation,
        string outcome,
        long failures,
        string? traceId);
}
