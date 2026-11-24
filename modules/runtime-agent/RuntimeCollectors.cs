using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minicloud.RuntimeAgent;

// One instance per agent cycle. Only successful discovery is shared.
public sealed class RuntimeCollector(AgentConfig config, IProcessRunner processRunner) : IDisposable
{
    private JsonDocument? _containers;

    private async Task<JsonDocument> InspectAsync(CancellationToken cancellationToken)
    {
        if (_containers is not null)
        {
            return _containers;
        }

        var list = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "docker", ["ps", "-aq", "--filter", $"label=com.docker.compose.project={config.ComposeProject}"], cancellationToken);
        RequireSuccess(list, "container discovery");
        var ids = Lines(list.Stdout).ToArray();
        if (ids.Length == 0)
        {
            return _containers = JsonDocument.Parse("[]");
        }
        var inspect = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "docker", new[] { "inspect" }.Concat(ids).ToArray(), cancellationToken);
        RequireSuccess(inspect, "container inspection");
        var containers = JsonDocument.Parse(inspect.Stdout);
        if (containers.RootElement.ValueKind != JsonValueKind.Array)
        {
            containers.Dispose();
            throw new InvalidOperationException("Docker inspection did not return a container array.");
        }
        return _containers = containers;
    }

    public async Task<IReadOnlyList<AgentLogRecord>> CollectLogsAsync(CancellationToken cancellationToken)
    {
        var records = new List<AgentLogRecord>();
        var seen = new HashSet<(string Source, string Cursor, DateTimeOffset ObservedAt)>();
        void Add(AgentLogRecord record)
        {
            if (records.Count < AgentRuntimeLimits.MaxBatchRecords && seen.Add((record.Source, record.Cursor, record.ObservedAt)))
            {
                records.Add(record);
            }
        }
        void CollectDockerStream(string service, string cursorPrefix, string output, string streamPrefix, DateTimeOffset observedAt)
        {
            if (records.Count >= AgentRuntimeLimits.MaxBatchRecords || string.IsNullOrEmpty(output))
            {
                return;
            }

            foreach (var line in Lines(output))
            {
                if (records.Count >= AgentRuntimeLimits.MaxBatchRecords)
                {
                    break;
                }

                var separator = line.IndexOf(' ');
                var tagged = separator > 0 ? line.Insert(separator + 1, streamPrefix) : line;
                foreach (var record in LogParsing.ParseDockerLine(service, cursorPrefix, StableSequence(tagged), tagged, observedAt))
                {
                    Add(record);
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        var containers = await InspectAsync(cancellationToken);
        foreach (var container in containers.RootElement.EnumerateArray())
        {
            var service = ServiceName(container);
            var logs = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "docker", ["logs", "--timestamps", "--since", "20s", "--tail", "100", container.GetProperty("Id").GetString()!], cancellationToken);
            if (records.Count >= AgentRuntimeLimits.MaxBatchRecords)
            {
                continue;
            }
            if (logs.ExitCode != 0)
            {
                Add(SelfLog("minicloud-agent", $"Log collection failed for {service}.", now));
                continue;
            }
            var cursorPrefix = $"service:{service}";
            CollectDockerStream(service, cursorPrefix, logs.Stdout, "stdout ", now);
            CollectDockerStream(service, cursorPrefix, logs.Stderr, "stderr ", now);
        }
        var caddyLogs = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "journalctl", ["-u", "caddy", "--since", "20 seconds ago", "--output", "short-iso", "--no-pager"], cancellationToken);
        if (records.Count < AgentRuntimeLimits.MaxBatchRecords)
        {
            foreach (var line in Lines(caddyLogs.ExitCode == 0 ? caddyLogs.Stdout : caddyLogs.Stderr))
            {
                if (records.Count >= AgentRuntimeLimits.MaxBatchRecords)
                {
                    break;
                }
                if (IsJournalNoEntriesSentinel(line))
                {
                    continue;
                }
                foreach (var record in LogParsing.ParseJournalLine("caddy", "caddy", StableSequence(line), line, now))
                {
                    Add(record);
                }
            }
        }
        var result = records.ToArray();
        RuntimeAgentDiagnostics.RecordRecords("logs", result.Length);
        return result;
    }

    public async Task<RuntimeStatusIngestRequest> CollectStatusAsync(string agentVersion, CancellationToken cancellationToken)
    {
        var containers = await InspectAsync(cancellationToken);
        var statuses = containers.RootElement.EnumerateArray().Select(container =>
        {
            var state = container.GetProperty("State");
            return new RuntimeServiceStatusDto(ServiceName(container), container.GetProperty("Name").GetString()?.TrimStart('/'),
                state.GetProperty("Status").GetString()!,
                state.TryGetProperty("Health", out var health) ? health.GetProperty("Status").GetString() : null,
                container.GetProperty("Config").GetProperty("Image").GetString(), container.GetProperty("RestartCount").GetInt32());
        }).ToArray();
        var caddy = await RuntimeAgentDiagnostics.RunChildAsync(processRunner, "systemctl", ["is-active", "caddy"], cancellationToken);
        return new RuntimeStatusIngestRequest(agentVersion, DateTimeOffset.UtcNow, config.ComposeProject, statuses,
            new RuntimeCaddyStatusDto(caddy.ExitCode == 0 && caddy.Stdout.Trim() == "active"));
    }

    public void Dispose() => _containers?.Dispose();

    private static string ServiceName(JsonElement container) => container.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.service").GetString()!;
    private static bool IsJournalNoEntriesSentinel(string line) =>
        string.Equals(line.Trim(), "-- No entries --", StringComparison.Ordinal);
    private static void RequireSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker {operation} failed (exit {result.ExitCode}).");
        }
    }
    private static IEnumerable<string> Lines(string value)
    {
        for (var start = 0; start < value.Length;)
        {
            var end = value.IndexOf('\n', start);
            if (end < 0) { end = value.Length; }
            var line = value.AsSpan(start, end - start).Trim().ToString();
            if (line.Length != 0) { yield return line; }
            start = end + 1;
        }
    }
    private static AgentLogRecord SelfLog(string source, string message, DateTimeOffset observedAt) => new(source, null, "stderr", observedAt, $"{source}:{StableSequence(message):000000000000}", RuntimeLogRedactor.Redact(message));
    private static long StableSequence(string value)
    {
        var maxBytes = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> hash = stackalloc byte[32];
        if (maxBytes <= 1024)
        {
            Span<byte> buffer = stackalloc byte[maxBytes];
            var written = Encoding.UTF8.GetBytes(value, buffer);
            SHA256.TryHashData(buffer[..written], hash, out _);
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                var written = Encoding.UTF8.GetBytes(value, rented);
                SHA256.TryHashData(rented.AsSpan(0, written), hash, out _);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return Math.Abs(BitConverter.ToInt64(hash) % 1_000_000_000_000);
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            throw;
        }
    }
}

public sealed record RuntimeStatusIngestRequest(
    string AgentVersion,
    DateTimeOffset ObservedAt,
    string ComposeProject,
    IReadOnlyList<RuntimeServiceStatusDto> Services,
    RuntimeCaddyStatusDto Caddy);

public sealed record RuntimeServiceStatusDto(
    string Name,
    string? Container,
    string State,
    string? Health,
    string? Image,
    int? RestartCount);

public sealed record RuntimeCaddyStatusDto(bool Active);
