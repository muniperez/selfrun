using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Minicloud.Common.Diagnostics;

namespace Minicloud.Worker.Tests;

// Captures Minicloud.Operator spans and measurements produced under one test-owned trace,
// so parallel tests sharing the static meter and activity source do not interfere.
internal sealed class WorkerTelemetryCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly Activity _root;
    private ActivityTraceId _traceId;

    public ConcurrentQueue<Activity> Spans { get; } = new();
    public ConcurrentQueue<(string Name, double Value, string Tags)> Measurements { get; } = new();

    public WorkerTelemetryCapture()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name is OperatorDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == OperatorDiagnostics.Name && activity.TraceId == _traceId)
                    Spans.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(_activities);
        _root = new Activity("worker-telemetry-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        _traceId = _root.TraceId;

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OperatorDiagnostics.Name && instrument.Name.StartsWith("minicloud.worker.", StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (Activity.Current?.TraceId != _traceId) return;
        var text = string.Join(";", tags.ToArray().Select(t => $"{t.Key}={t.Value}"));
        Measurements.Enqueue((instrument.Name, value, text));
    }

    public void Dispose()
    {
        _root.Dispose();
        _meters.Dispose();
        _activities.Dispose();
    }
}
