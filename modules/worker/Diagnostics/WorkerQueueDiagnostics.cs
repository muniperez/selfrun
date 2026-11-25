using System.Diagnostics;
using System.Diagnostics.Metrics;
using SharedDiagnostics = Selfrun.Common.Diagnostics.OperatorDiagnostics;

namespace Selfrun.Worker.Diagnostics;

// Opt-in (OperatorDiagnostics:Enabled) claim, queue and step signals; tags are fixed and never carry IDs.
internal static class WorkerQueueDiagnostics
{
    public const string ClaimsName = "selfrun.worker.claims";
    public const string QueueWaitName = "selfrun.worker.queue.wait";
    public const string StepDurationName = "selfrun.worker.step.duration";

    private static readonly Counter<long> Claims = SharedDiagnostics.Meter.CreateCounter<long>(ClaimsName, "{claim}");
    private static readonly Histogram<double> QueueWait = SharedDiagnostics.Meter.CreateHistogram<double>(QueueWaitName, "s");
    private static readonly Histogram<double> StepDuration = SharedDiagnostics.Meter.CreateHistogram<double>(StepDurationName, "s");

    public static void Claim(string outcome) => Claims.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void QueueWaited(TimeSpan wait) => QueueWait.Record(Math.Max(0, wait.TotalSeconds));

    // Root span for one claimed deployment; starts at the durable claim time so the trace begins on claim.
    public static Activity? StartDeployment(DateTimeOffset claimedAt)
    {
        var activity = SharedDiagnostics.Activities.StartActivity(
            "worker.deployment", ActivityKind.Internal, parentContext: default, startTime: claimedAt);
        activity?.SetTag("operation", "deployment");
        return activity;
    }

    // Each step gets its own span so concurrent DNS and SSH intervals stay distinct.
    public static Activity? StartStep(string step)
    {
        var activity = SharedDiagnostics.Activities.StartActivity("worker.step", ActivityKind.Internal);
        activity?.SetTag("operation", step);
        return activity;
    }

    public static void StepCompleted(Activity? activity, string step, TimeSpan elapsed, string outcome)
    {
        StepDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("operation", step), new KeyValuePair<string, object?>("outcome", outcome));
        Complete(activity, outcome);
    }

    public static void Complete(Activity? activity, string outcome)
    {
        if (activity is null) return;
        activity.SetTag("outcome", outcome);
        if (outcome == "failed") activity.SetStatus(ActivityStatusCode.Error);
    }
}
