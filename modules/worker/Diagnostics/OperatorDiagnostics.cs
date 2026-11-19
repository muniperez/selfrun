using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Minicloud.Worker.Diagnostics;

public sealed class OperatorDiagnostics
{
    internal const string MeterName = "Minicloud.OperatorDiagnostics";
    internal const string ActivitySourceName = "Minicloud.OperatorDiagnostics";
    internal const string ServiceName = "minicloud-worker";
    internal const string OperationName = "deployment.pipeline";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> OperationCount =
        Meter.CreateCounter<long>("minicloud_worker_deployment_pipeline_count", "{operation}");
    private static readonly Counter<long> OperationBytes =
        Meter.CreateCounter<long>("minicloud_worker_deployment_pipeline_bytes", "By");
    private static readonly Histogram<double> OperationDuration =
        Meter.CreateHistogram<double>("minicloud_worker_deployment_pipeline_duration", "s");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly object _gate = new();
    private long _count;
    private long _failures;
    private long _cancellations;
    private long _totalDurationMilliseconds;
    private long _bytes;
    private string? _lastTraceId;

    internal OperatorDiagnostics()
    {
    }

    internal static Activity? StartDeploymentPipeline()
    {
        var activity = ActivitySource.StartActivity(OperationName, ActivityKind.Internal);
        activity?.SetTag("service", ServiceName);
        activity?.SetTag("operation", OperationName);
        return activity;
    }

    internal void RecordDeploymentPipeline(TimeSpan duration, long bytes, string outcome, string? traceId)
    {
        var tags = new TagList
        {
            { "service", ServiceName },
            { "operation", OperationName },
            { "outcome", outcome }
        };
        OperationCount.Add(1, tags);
        OperationBytes.Add(bytes, tags);
        OperationDuration.Record(duration.TotalSeconds, tags);

        lock (_gate)
        {
            _count++;
            _totalDurationMilliseconds += (long)duration.TotalMilliseconds;
            _bytes += bytes;
            if (outcome == "failed")
            {
                _failures++;
            }
            else if (outcome == "canceled")
            {
                _cancellations++;
            }
            if (traceId is not null)
            {
                _lastTraceId = traceId;
            }
        }
    }

    internal OperatorDiagnosticSnapshot SnapshotAndReset()
    {
        lock (_gate)
        {
            var snapshot = new OperatorDiagnosticSnapshot(
                _count, _failures, _cancellations, _totalDurationMilliseconds, _bytes, _lastTraceId);
            _count = _failures = _cancellations = _totalDurationMilliseconds = _bytes = 0;
            _lastTraceId = null;
            return snapshot;
        }
    }
}

internal sealed record OperatorDiagnosticSnapshot(
    long Count,
    long Failures,
    long Cancellations,
    long TotalDurationMilliseconds,
    long Bytes,
    string? TraceId);
