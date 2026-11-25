using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Selfrun.Api;

internal static class RuntimeIngestionDiagnostics
{
    private const string MeterName = "Selfrun.RuntimeIngestion";
    private static readonly Meter Meter = new(MeterName);
    private static readonly ActivitySource ActivitySource = new(MeterName);
    private static readonly Counter<long> Calls = Meter.CreateCounter<long>("selfrun.runtime.ingestion.calls");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("selfrun.runtime.ingestion.duration", "s");
    private static readonly Counter<long> Bytes = Meter.CreateCounter<long>("selfrun.runtime.ingestion.bytes", "By");
    private static readonly Counter<long> Records = Meter.CreateCounter<long>("selfrun.runtime.ingestion.records", "{record}");
    private static readonly Histogram<double> LogPruneDuration = Meter.CreateHistogram<double>("selfrun.runtime.ingestion.logs.prune.duration", "s");

    public static Measurement Start(string operation, long bytes, long records, CancellationToken cancellationToken) =>
        new(operation, bytes, records, cancellationToken, Stopwatch.GetTimestamp(), ActivitySource.StartActivity(operation, ActivityKind.Server));

    public static void RecordLogPrune(long startedAt) =>
        LogPruneDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds);

    internal sealed class Measurement(
        string operation,
        long bytes,
        long records,
        CancellationToken cancellationToken,
        long startedAt,
        Activity? activity) : IDisposable
    {
        private string _outcome = "failure";

        public void Complete(string outcome) => _outcome = outcome switch
        {
            "success" or "failure" or "cancelled" => outcome,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        public void Dispose()
        {
            var outcome = cancellationToken.IsCancellationRequested && _outcome == "failure" ? "cancelled" : _outcome;
            var tags = new TagList
            {
                { "operation", operation },
                { "outcome", outcome }
            };
            Calls.Add(1, tags);
            Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
            if (bytes >= 0)
            {
                Bytes.Add(bytes, tags);
            }
            Records.Add(records, tags);
            if (activity is not null)
            {
                activity.SetTag("operation", operation);
                activity.SetTag("outcome", outcome);
                activity.Dispose();
            }
        }
    }
}
