namespace Minicloud.RuntimeAgent;

public sealed record AgentLogRecord(
    string Source,
    string? Service,
    string Stream,
    DateTimeOffset ObservedAt,
    string Cursor,
    string Message);

public static class LogParsing
{
    public static IReadOnlyList<AgentLogRecord> ParseDockerLine(string serviceName, string cursorPrefix, long sequence, string line, DateTimeOffset fallbackTimestamp)
    {
        var stream = "stdout";
        var observedAt = fallbackTimestamp;
        var content = line;

        var firstSeparator = line.IndexOf(' ');
        var secondSeparator = firstSeparator < 0 ? -1 : line.IndexOf(' ', firstSeparator + 1);
        var streamPart = secondSeparator < 0 ? default : line.AsSpan(firstSeparator + 1, secondSeparator - firstSeparator - 1).Trim();
        if (secondSeparator >= 0 && DateTimeOffset.TryParse(line.AsSpan(0, firstSeparator).Trim(), out var parsedTimestamp) &&
            (streamPart.SequenceEqual("stdout") || streamPart.SequenceEqual("stderr")))
        {
            observedAt = parsedTimestamp.ToUniversalTime();
            stream = streamPart.SequenceEqual("stderr") ? "stderr" : "stdout";
            content = line.AsSpan(secondSeparator + 1).Trim().ToString();
        }

        return SplitLogicalLines($"service:{serviceName}", serviceName, stream, observedAt, cursorPrefix, sequence, content);
    }

    public static IReadOnlyList<AgentLogRecord> ParseJournalLine(string source, string cursorPrefix, long sequence, string line, DateTimeOffset fallbackTimestamp)
    {
        var observedAt = fallbackTimestamp;
        var content = line;
        var separator = line.IndexOf(' ');
        if (separator > 0 && DateTimeOffset.TryParse(line.AsSpan(0, separator), out var parsedTimestamp))
        {
            observedAt = parsedTimestamp.ToUniversalTime();
            content = line[(separator + 1)..];
        }

        return SplitLogicalLines(source, null, "stdout", observedAt, cursorPrefix, sequence, content);
    }

    private static IReadOnlyList<AgentLogRecord> SplitLogicalLines(
        string source,
        string? service,
        string stream,
        DateTimeOffset observedAt,
        string cursorPrefix,
        long sequence,
        string content)
    {
        var records = new List<AgentLogRecord>(1);
        var start = 0;
        for (var i = 0; ; i++)
        {
            var end = content.IndexOf('\n', start);
            var last = end < 0;
            if (last) { end = content.Length; }
            var length = end - start;
            if (!last && length > 0 && content[end - 1] == '\r') { length--; }
            var line = start == 0 && length == content.Length && length <= AgentRuntimeLimits.MaxLineLength
                ? content
                : content.AsSpan(start, Math.Min(length, AgentRuntimeLimits.MaxLineLength)).ToString();
            records.Add(new AgentLogRecord(
                source,
                service,
                stream,
                observedAt,
                $"{cursorPrefix}:{sequence + i:000000000000}",
                RuntimeLogRedactor.Redact(line)));
            if (last) { break; }
            start = end + 1;
        }

        return records;
    }
}

public static class AgentRuntimeLimits
{
    public const int MaxLineLength = 16 * 1024;
    public const int MaxBatchRecords = 500;
}
