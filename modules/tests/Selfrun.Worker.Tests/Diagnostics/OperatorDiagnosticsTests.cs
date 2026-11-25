using Microsoft.Extensions.Logging;
using Selfrun.Worker.Diagnostics;

namespace Selfrun.Worker.Tests.Diagnostics;

public sealed class OperatorDiagnosticsTests
{
    [Fact]
    public void SnapshotAggregatesSafeOperationTotalsAndResetsTheWindow()
    {
        var diagnostics = new OperatorDiagnostics();

        diagnostics.RecordDeploymentPipeline(TimeSpan.FromMilliseconds(125), 2048, "succeeded", "trace-a");
        diagnostics.RecordDeploymentPipeline(TimeSpan.FromMilliseconds(375), 1024, "failed", "trace-b");
        diagnostics.RecordDeploymentPipeline(TimeSpan.FromMilliseconds(80), 0, "canceled", null);

        var snapshot = diagnostics.SnapshotAndReset();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(1, snapshot.Failures);
        Assert.Equal(1, snapshot.Cancellations);
        Assert.Equal(580, snapshot.TotalDurationMilliseconds);
        Assert.Equal(3072, snapshot.Bytes);
        Assert.Equal("trace-b", snapshot.TraceId);
        Assert.Equal(new OperatorDiagnosticSnapshot(0, 0, 0, 0, 0, null), diagnostics.SnapshotAndReset());
    }

    [Fact]
    public void SummaryUsesOnlyStructuredDiagnosticFieldNames()
    {
        var logger = new CapturingLogger();

        OperatorDiagnosticLogs.Summary(
            logger, "selfrun-worker", "deployment.pipeline", "succeeded",
            60_000, 1, 42, 12, 0, "0123456789abcdef0123456789abcdef");

        var expectedFields = new[]
        {
            "Service", "Operation", "Outcome", "WindowDurationMs", "Count",
            "TotalDurationMs", "Bytes", "Failures", "TraceId"
        };
        var actualFields = logger.State.Keys
            .Where(key => key != "{OriginalFormat}")
            .OrderBy(key => key, StringComparer.Ordinal);

        Assert.Equal(expectedFields.OrderBy(key => key, StringComparer.Ordinal), actualFields);
    }

    private sealed class CapturingLogger : ILogger
    {
        public Dictionary<string, object?> State { get; private set; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                State = values.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }
    }
}
