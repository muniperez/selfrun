using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

if (args is not [var tracePath] || !File.Exists(tracePath))
{
    Console.Error.WriteLine("Usage: RuntimeAgentAllocationReport <nettrace file under .local/artifacts>");
    return 2;
}

if (!Path.GetFullPath(tracePath).StartsWith(Path.GetFullPath(".local/artifacts") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
{
    Console.Error.WriteLine("Trace files must be under this repository's .local/artifacts; run from the repository root.");
    return 2;
}

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
