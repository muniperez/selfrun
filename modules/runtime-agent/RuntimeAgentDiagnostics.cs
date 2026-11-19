using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Minicloud.RuntimeAgent;

public static class RuntimeAgentDiagnostics
{
    public const string MeterName = "Minicloud.RuntimeAgent";
    public const string ActivitySourceName = "Minicloud.RuntimeAgent";

    private static readonly Meter Meter = new(MeterName);
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly Counter<long> OperationCalls = Meter.CreateCounter<long>("minicloud.runtime.agent.operation.calls");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("minicloud.runtime.agent.operation.duration", "s");
    private static readonly Counter<long> ChildLaunches = Meter.CreateCounter<long>("minicloud.runtime.agent.child.launches");
    private static readonly Counter<long> Bytes = Meter.CreateCounter<long>("minicloud.runtime.agent.bytes", "By");
    private static readonly Counter<long> Records = Meter.CreateCounter<long>("minicloud.runtime.agent.records", "{record}");

    internal static OperationMeasurement StartOperation(string operation) =>
        new(operation, Stopwatch.GetTimestamp(), ActivitySource.StartActivity(operation, ActivityKind.Internal));

    internal static void RecordChildLaunch(string operation)
    {
        if (ChildLaunches.Enabled)
        {
            var tags = new TagList { { "operation", NormalizeOperation(operation) } };
            ChildLaunches.Add(1, tags);
        }
    }

    internal static void RecordBytes(string operation, long bytes)
    {
        if (bytes > 0 && Bytes.Enabled)
        {
            var tags = new TagList { { "operation", NormalizeOperation(operation) } };
            Bytes.Add(bytes, tags);
        }
    }

    internal static void RecordOutputBytes(string operation, string stdout, string stderr)
    {
        if (Bytes.Enabled)
        {
            RecordBytes(operation, (long)System.Text.Encoding.UTF8.GetByteCount(stdout) + System.Text.Encoding.UTF8.GetByteCount(stderr));
        }
    }

    internal static void RecordRecords(string operation, long records)
    {
        if (records > 0 && Records.Enabled)
        {
            var tags = new TagList { { "operation", NormalizeOperation(operation) } };
            Records.Add(records, tags);
        }
    }

    internal static async Task<ProcessResult> RunChildAsync(IProcessRunner runner, string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var operation = ProcessOperation(fileName, args);
        using var measurement = StartOperation(operation);
        var outcome = "failure";
        try
        {
            RecordChildLaunch(operation);
            var result = await runner.RunAsync(fileName, args, cancellationToken);
            RecordOutputBytes(operation, result.Stdout, result.Stderr);
            outcome = result.ExitCode == 0 ? "success" : "failure";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            measurement.Complete(outcome);
        }
    }

    private static string ProcessOperation(string fileName, IReadOnlyList<string> args) => fileName switch
    {
        "docker" when args.Count > 0 && args[0] == "logs" => "docker.logs",
        "docker" when args.Count > 0 && args[0] == "ps" => "docker.ps",
        "docker" => "docker.inspect",
        "sudo" => "routing.apply",
        "journalctl" => "journal.logs",
        "systemctl" => "systemctl.status",
        _ => "other"
    };

    private static string NormalizeOperation(string operation) => operation switch
    {
        "heartbeat" or "status" or "routing" or "logs" or
        "docker.ps" or "docker.inspect" or "docker.logs" or "routing.apply" or "journal.logs" or "systemctl.status" => operation,
        _ => "other"
    };

    internal sealed class OperationMeasurement(string operation, long startedAt, Activity? activity) : IDisposable
    {
        private string _outcome = "failure";
        private bool _completed;

        public void Complete(string outcome)
        {
            if (outcome is not ("success" or "failure" or "cancelled"))
            {
                throw new ArgumentOutOfRangeException(nameof(outcome));
            }

            _outcome = outcome;
            _completed = true;
        }

        public void Dispose()
        {
            var boundedOperation = NormalizeOperation(operation);
            var tags = new TagList
            {
                { "operation", boundedOperation },
                { "outcome", _completed ? _outcome : "failure" }
            };
            OperationCalls.Add(1, tags);
            OperationDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
            if (activity is not null)
            {
                activity.SetTag("operation", boundedOperation);
                activity.SetTag("outcome", _completed ? _outcome : "failure");
                activity.Dispose();
            }
        }
    }
}
