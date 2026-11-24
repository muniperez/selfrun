using System.Text.Json;

namespace Minicloud.RuntimeAgent;

public sealed class CursorStore(string path)
{
    private readonly Dictionary<string, string> _cursors = new(StringComparer.Ordinal);
    private byte[]? _lastPersistedBytes;
    private bool _dirty;

    public IReadOnlyDictionary<string, string> Cursors => _cursors;
    public long WriteCount { get; private set; }
    public long SkippedWriteCount { get; private set; }
    public long BytesWritten { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            _lastPersistedBytes = null;
            return;
        }

        var rawBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var state = JsonSerializer.Deserialize<AgentState>(rawBytes, JsonDefaults.Options);
        _cursors.Clear();
        foreach (var pair in state?.Cursors ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            {
                _cursors[pair.Key] = pair.Value;
            }
        }

        _lastPersistedBytes = SerializeCanonical(_cursors);
        _dirty = false;
    }

    public int PruneInactiveSources(IReadOnlySet<string> activeSources)
    {
        ArgumentNullException.ThrowIfNull(activeSources);
        List<string>? stale = null;
        foreach (var key in _cursors.Keys)
        {
            if (!activeSources.Contains(key))
            {
                stale ??= [];
                stale.Add(key);
            }
        }

        if (stale is null)
        {
            return 0;
        }

        foreach (var key in stale)
        {
            _cursors.Remove(key);
        }

        _dirty = true;
        return stale.Count;
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, string> acceptedCursors, CancellationToken cancellationToken)
    {
        foreach (var pair in acceptedCursors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            {
                continue;
            }

            if (!_cursors.TryGetValue(pair.Key, out var saved) || !string.Equals(saved, pair.Value, StringComparison.Ordinal))
            {
                _cursors[pair.Key] = pair.Value;
                _dirty = true;
            }
        }

        if (!_dirty)
        {
            SkippedWriteCount++;
            RuntimeAgentDiagnostics.RecordCursorWrite("skipped");
            return;
        }

        var payload = SerializeCanonical(_cursors);
        if (_lastPersistedBytes is not null && payload.AsSpan().SequenceEqual(_lastPersistedBytes) && File.Exists(path))
        {
            _dirty = false;
            SkippedWriteCount++;
            RuntimeAgentDiagnostics.RecordCursorWrite("skipped");
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        var createdTempFile = false;
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                createdTempFile = true;
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
            _lastPersistedBytes = payload;
            WriteCount++;
            BytesWritten += payload.Length;
            _dirty = false;
            RuntimeAgentDiagnostics.RecordCursorWrite("written");
        }
        catch
        {
            RuntimeAgentDiagnostics.RecordCursorWrite("failure");
            if (createdTempFile && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of a partial temporary file; keep _dirty set for retry.
                }
            }

            throw;
        }
    }

    private static byte[] SerializeCanonical(Dictionary<string, string> cursors)
    {
        var sorted = new SortedDictionary<string, string>(cursors, StringComparer.Ordinal);
        return JsonSerializer.SerializeToUtf8Bytes(new AgentState(sorted), JsonDefaults.Options);
    }
}

public sealed record AgentState(IReadOnlyDictionary<string, string> Cursors);
