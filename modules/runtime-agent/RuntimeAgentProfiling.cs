using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Minicloud.RuntimeAgent;

/// <summary>
/// Bounded, opt-in profiling contract for a single operator-controlled runtime host.
/// Disabled by default; when enabled, remote export is strictly restricted to loopback receivers
/// so operator telemetry travels only via a local sidecar/tunnel and never delays product traffic.
/// </summary>
public sealed record AgentProfilingConfig(
    bool Enabled = false,
    string? ReceiverUrl = null,
    string? BearerTokenPath = null,
    int SummaryIntervalSeconds = 60,
    int ExportTimeoutMs = 2000,
    int MaxQueueSize = 16,
    bool EmitJournalSummary = true,
    bool ExportOtlp = false)
{
    public const int MinSummaryIntervalSeconds = 1;
    public const int MaxSummaryIntervalSeconds = 3600;
    public const int MinExportTimeoutMs = 100;
    public const int MaxExportTimeoutMs = 10_000;
    public const int MinQueueSize = 1;
    public const int MaxQueueSizeLimit = 128;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (SummaryIntervalSeconds is < MinSummaryIntervalSeconds or > MaxSummaryIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"Agent profiling SummaryIntervalSeconds must be between {MinSummaryIntervalSeconds} and {MaxSummaryIntervalSeconds}.");
        }

        if (ExportTimeoutMs is < MinExportTimeoutMs or > MaxExportTimeoutMs)
        {
            throw new InvalidOperationException(
                $"Agent profiling ExportTimeoutMs must be between {MinExportTimeoutMs} and {MaxExportTimeoutMs}.");
        }

        if (MaxQueueSize is < MinQueueSize or > MaxQueueSizeLimit)
        {
            throw new InvalidOperationException(
                $"Agent profiling MaxQueueSize must be between {MinQueueSize} and {MaxQueueSizeLimit}.");
        }

        if (!EmitJournalSummary && !ExportOtlp)
        {
            throw new InvalidOperationException(
                "Agent profiling requires EmitJournalSummary or ExportOtlp when enabled.");
        }

        if (ExportOtlp || !string.IsNullOrWhiteSpace(ReceiverUrl))
        {
            _ = ValidateLoopbackReceiverUri(ReceiverUrl);
        }
    }

    public static Uri ValidateLoopbackReceiverUri(string? receiverUrl)
    {
        if (string.IsNullOrWhiteSpace(receiverUrl) ||
            !Uri.TryCreate(receiverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Agent profiling ReceiverUrl must be a valid HTTP(S) loopback URI.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Agent profiling ReceiverUrl must not embed credentials.");
        }

        if (!IsLoopbackHost(uri))
        {
            throw new InvalidOperationException("Agent profiling ReceiverUrl must target a loopback address (127.0.0.1, ::1, or localhost).");
        }

        return uri;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}

public sealed record RuntimeAgentOperationSummary(
    string Operation,
    string Outcome,
    long Count,
    double TotalDurationMs,
    double P95DurationMs);

public sealed record RuntimeAgentDiagnosticSummary(
    string Schema,
    string Service,
    long WindowDurationMs,
    IReadOnlyList<RuntimeAgentOperationSummary> Operations,
    long ChildLaunches,
    long InputBytes,
    long Records,
    long CursorWrites,
    long CursorWritesSkipped,
    long AllocatedBytesDelta,
    double CpuSecondsDelta,
    long ExporterDrops,
    int QueueLag,
    string? TraceId)
{
    public const string SchemaVersion = "minicloud.runtime-agent.summary.v1";
    public const string ServiceName = "minicloud-runtime-agent";

    private static readonly HashSet<string> AllowedTopLevelFields = new(StringComparer.Ordinal)
    {
        "schema",
        "service",
        "windowDurationMs",
        "operations",
        "childLaunches",
        "inputBytes",
        "records",
        "cursorWrites",
        "cursorWritesSkipped",
        "allocatedBytesDelta",
        "cpuSecondsDelta",
        "exporterDrops",
        "queueLag",
        "traceId"
    };

    private static readonly HashSet<string> AllowedOperationFields = new(StringComparer.Ordinal)
    {
        "operation",
        "outcome",
        "count",
        "totalDurationMs",
        "p95DurationMs"
    };

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "heartbeat",
        "status",
        "routing",
        "logs",
        "docker.ps",
        "docker.inspect",
        "docker.logs",
        "routing.apply",
        "journal.logs",
        "systemctl.status",
        "other"
    };

    private static readonly HashSet<string> AllowedOutcomes = new(StringComparer.Ordinal)
    {
        "success",
        "failure",
        "cancelled"
    };

    private static readonly Regex TraceIdPattern = new(@"\A[0-9a-f]{32}\z", RegexOptions.Compiled);

    public static void ValidateAggregateOnlyJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Summary payload must be a JSON object.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!AllowedTopLevelFields.Contains(property.Name))
            {
                throw new InvalidOperationException($"Unexpected summary field '{property.Name}'.");
            }
        }

        if (!document.RootElement.TryGetProperty("schema", out var schema) || schema.GetString() != SchemaVersion)
        {
            throw new InvalidOperationException("Summary schema version mismatch.");
        }

        if (!document.RootElement.TryGetProperty("service", out var service) || service.GetString() != ServiceName)
        {
            throw new InvalidOperationException("Summary service name mismatch.");
        }

        if (document.RootElement.TryGetProperty("traceId", out var traceId) && traceId.ValueKind == JsonValueKind.String)
        {
            var value = traceId.GetString();
            if (value is null || !TraceIdPattern.IsMatch(value))
            {
                throw new InvalidOperationException("Summary traceId must be a 32-character lowercase hex W3C trace ID.");
            }
        }

        if (document.RootElement.TryGetProperty("operations", out var operations))
        {
            if (operations.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Summary operations field must be a JSON array.");
            }

            foreach (var op in operations.EnumerateArray())
            {
                if (op.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("Summary operation entry must be a JSON object.");
                }

                foreach (var field in op.EnumerateObject())
                {
                    if (!AllowedOperationFields.Contains(field.Name))
                    {
                        throw new InvalidOperationException($"Unexpected operation field '{field.Name}'.");
                    }
                }

                var name = op.GetProperty("operation").GetString();
                var outcome = op.GetProperty("outcome").GetString();
                if (name is null || !AllowedOperations.Contains(name))
                {
                    throw new InvalidOperationException($"Disallowed operation '{name}' in summary.");
                }

                if (outcome is null || !AllowedOutcomes.Contains(outcome))
                {
                    throw new InvalidOperationException($"Disallowed outcome '{outcome}' in summary.");
                }
            }
        }
    }
}

public sealed class RuntimeAgentProfiler : IAsyncDisposable
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly TimeSpan ShutdownDrainLimit = TimeSpan.FromSeconds(2);
    private const int MaxDurationSamplesPerBucket = 256;

    private readonly AgentProfilingConfig _config;
    private readonly TextWriter? _journalWriter;
    private readonly TimeProvider _clock;
    private readonly HttpClient? _exportClient;
    private readonly bool _ownsExportClient;
    private readonly Uri? _logsEndpoint;
    private readonly string? _bearerToken;
    private readonly MeterListener? _meterListener;
    private readonly ActivityListener? _activityListener;
    private readonly Channel<string>? _exportChannel;
    private readonly CancellationTokenSource? _cts;
    private readonly Task? _exportWorker;
    private readonly Task? _periodicWorker;

    private readonly object _gate = new();
    private readonly Dictionary<(string Operation, string Outcome), OperationAccumulator> _operations = [];
    private long _childLaunches;
    private long _inputBytes;
    private long _records;
    private long _cursorWrites;
    private long _cursorWritesSkipped;
    private long _exporterDrops;
    private int _queueDepth;
    private string? _lastTraceId;
    private long _windowStartTimestamp;
    private long _lastAllocatedBytes;
    private TimeSpan _lastProcessorTime;
    private bool _disposed;

    public bool IsEnabled { get; }
    public long TotalExporterDrops => Interlocked.Read(ref _exporterDrops);
    public int CurrentQueueLag => Volatile.Read(ref _queueDepth);

    private RuntimeAgentProfiler(
        AgentProfilingConfig config,
        TextWriter? journalWriter,
        HttpClient? httpClient,
        bool ownsHttpClient,
        TimeProvider clock,
        bool startBackgroundLoop)
    {
        _config = config;
        _journalWriter = journalWriter;
        _clock = clock;
        IsEnabled = config.Enabled;

        if (!config.Enabled)
        {
            return;
        }

        config.Validate();

        _windowStartTimestamp = _clock.GetTimestamp();
        _lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        using (var process = Process.GetCurrentProcess())
        {
            _lastProcessorTime = process.TotalProcessorTime;
        }

        if (config.ExportOtlp)
        {
            var baseUri = AgentProfilingConfig.ValidateLoopbackReceiverUri(config.ReceiverUrl);
            _logsEndpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/v1/logs");
            if (!string.IsNullOrWhiteSpace(config.BearerTokenPath) && File.Exists(config.BearerTokenPath))
            {
                _bearerToken = File.ReadAllText(config.BearerTokenPath).Trim();
            }

            _exportClient = httpClient ?? new HttpClient();
            _ownsExportClient = httpClient is null || ownsHttpClient;
            _exportChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(config.MaxQueueSize)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        _meterListener = CreateMeterListener();
        _activityListener = CreateActivityListener();

        _cts = new CancellationTokenSource();
        if (_exportChannel is not null)
        {
            _exportWorker = Task.Run(() => DrainExportQueueAsync(_cts.Token));
        }

        if (startBackgroundLoop)
        {
            _periodicWorker = Task.Run(() => RunPeriodicSummaryLoopAsync(_cts.Token));
        }
    }

    public static RuntimeAgentProfiler Start(
        AgentProfilingConfig? config,
        TextWriter? journalWriter = null,
        HttpClient? exportHttpClient = null,
        TimeProvider? timeProvider = null,
        bool startBackgroundLoop = true)
    {
        var effective = config ?? new AgentProfilingConfig(Enabled: false);
        return new RuntimeAgentProfiler(
            effective,
            journalWriter ?? Console.Out,
            exportHttpClient,
            ownsHttpClient: false,
            timeProvider ?? TimeProvider.System,
            startBackgroundLoop);
    }

    public RuntimeAgentDiagnosticSummary? FlushSummary()
    {
        if (!IsEnabled)
        {
            return null;
        }

        var summary = SnapshotAndReset();
        var json = JsonSerializer.Serialize(summary, CompactJson);

        if (_config.EmitJournalSummary && _journalWriter is not null)
        {
            _journalWriter.WriteLine(json);
        }

        if (_config.ExportOtlp && _exportChannel is not null)
        {
            var otlpPayload = BuildOtlpLogsJson(summary);
            if (_exportChannel.Writer.TryWrite(otlpPayload))
            {
                var lag = Interlocked.Increment(ref _queueDepth);
                RuntimeAgentDiagnostics.UpdateExporterQueueLag(lag);
            }
            else
            {
                RecordDrop("queue_full");
            }
        }

        return summary;
    }

    public async Task DrainExportsAsync(TimeSpan timeout)
    {
        if (!IsEnabled || _exportChannel is null || _exportWorker is null)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Volatile.Read(ref _queueDepth) > 0 && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!IsEnabled)
        {
            return;
        }

        _meterListener?.Dispose();
        _activityListener?.Dispose();
        _exportChannel?.Writer.TryComplete();

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        var pending = new List<Task>(2);
        if (_periodicWorker is not null)
        {
            pending.Add(_periodicWorker);
        }

        if (_exportWorker is not null)
        {
            pending.Add(_exportWorker);
        }

        if (pending.Count > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(ShutdownDrainLimit);
            }
            catch
            {
                // Bounded shutdown: never hang agent exit waiting on an unavailable operator receiver.
            }
        }

        RuntimeAgentDiagnostics.UpdateExporterQueueLag(0);
        _cts?.Dispose();
        if (_ownsExportClient)
        {
            _exportClient?.Dispose();
        }
    }

    private RuntimeAgentDiagnosticSummary SnapshotAndReset()
    {
        var nowTimestamp = _clock.GetTimestamp();
        var allocatedNow = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan cpuNow;
        using (var process = Process.GetCurrentProcess())
        {
            cpuNow = process.TotalProcessorTime;
        }

        lock (_gate)
        {
            var windowMs = Math.Max(0L, (long)_clock.GetElapsedTime(_windowStartTimestamp, nowTimestamp).TotalMilliseconds);
            _windowStartTimestamp = nowTimestamp;

            var allocDelta = Math.Max(0L, allocatedNow - _lastAllocatedBytes);
            _lastAllocatedBytes = allocatedNow;

            var cpuDelta = Math.Max(0.0, (cpuNow - _lastProcessorTime).TotalSeconds);
            _lastProcessorTime = cpuNow;

            var operations = _operations
                .OrderBy(x => x.Key.Operation, StringComparer.Ordinal)
                .ThenBy(x => x.Key.Outcome, StringComparer.Ordinal)
                .Select(x => x.Value.ToSummary(x.Key.Operation, x.Key.Outcome))
                .ToArray();
            _operations.Clear();

            var summary = new RuntimeAgentDiagnosticSummary(
                RuntimeAgentDiagnosticSummary.SchemaVersion,
                RuntimeAgentDiagnosticSummary.ServiceName,
                windowMs,
                operations,
                _childLaunches,
                _inputBytes,
                _records,
                _cursorWrites,
                _cursorWritesSkipped,
                allocDelta,
                Math.Round(cpuDelta, 6),
                Interlocked.Read(ref _exporterDrops),
                Volatile.Read(ref _queueDepth),
                _lastTraceId);

            _childLaunches = 0;
            _inputBytes = 0;
            _records = 0;
            _cursorWrites = 0;
            _cursorWritesSkipped = 0;
            _lastTraceId = null;
            return summary;
        }
    }

    private async Task RunPeriodicSummaryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.SummaryIntervalSeconds), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                FlushSummary();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainExportQueueAsync(CancellationToken cancellationToken)
    {
        if (_exportChannel is null || _exportClient is null || _logsEndpoint is null)
        {
            return;
        }

        try
        {
            await foreach (var payload in _exportChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(_config.ExportTimeoutMs);
                    using var request = new HttpRequestMessage(HttpMethod.Post, _logsEndpoint)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrWhiteSpace(_bearerToken))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                    }

                    using var response = await _exportClient.SendAsync(request, timeoutCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        RecordDrop("http_error");
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    RecordDrop("timeout");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException)
                {
                    RecordDrop("network_error");
                }
                catch
                {
                    RecordDrop("network_error");
                }
                finally
                {
                    var lag = Math.Max(0, Interlocked.Decrement(ref _queueDepth));
                    RuntimeAgentDiagnostics.UpdateExporterQueueLag(lag);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecordDrop(string reason)
    {
        Interlocked.Increment(ref _exporterDrops);
        RuntimeAgentDiagnostics.RecordExporterDrop(reason);
    }

    private MeterListener CreateMeterListener()
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == RuntimeAgentDiagnostics.MeterName)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? operation = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation") { operation = tag.Value?.ToString(); }
                else if (tag.Key == "outcome") { outcome = tag.Value?.ToString(); }
            }

            lock (_gate)
            {
                switch (instrument.Name)
                {
                    case "minicloud.runtime.agent.operation.calls" when operation is not null && outcome is not null:
                        GetOrAddAccumulator(operation, outcome).Count += measurement;
                        break;
                    case "minicloud.runtime.agent.child.launches":
                        _childLaunches += measurement;
                        break;
                    case "minicloud.runtime.agent.bytes":
                        _inputBytes += measurement;
                        break;
                    case "minicloud.runtime.agent.records":
                        _records += measurement;
                        break;
                    case "minicloud.runtime.agent.cursor.writes":
                        if (outcome == "written") { _cursorWrites += measurement; }
                        else if (outcome == "skipped") { _cursorWritesSkipped += measurement; }
                        break;
                }
            }
        });

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "minicloud.runtime.agent.operation.duration")
            {
                return;
            }

            string? operation = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation") { operation = tag.Value?.ToString(); }
                else if (tag.Key == "outcome") { outcome = tag.Value?.ToString(); }
            }

            if (operation is null || outcome is null)
            {
                return;
            }

            lock (_gate)
            {
                GetOrAddAccumulator(operation, outcome).AddDurationMs(measurement * 1000.0);
            }
        });

        listener.Start();
        return listener;
    }

    private ActivityListener CreateActivityListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeAgentDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                var traceId = activity.TraceId.ToString();
                if (!string.IsNullOrEmpty(traceId) && traceId != "00000000000000000000000000000000")
                {
                    lock (_gate)
                    {
                        _lastTraceId = traceId;
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private OperationAccumulator GetOrAddAccumulator(string operation, string outcome)
    {
        var key = (operation, outcome);
        if (!_operations.TryGetValue(key, out var acc))
        {
            acc = new OperationAccumulator();
            _operations[key] = acc;
        }

        return acc;
    }

    private static string BuildOtlpLogsJson(RuntimeAgentDiagnosticSummary summary)
    {
        var totalOps = summary.Operations.Sum(x => x.Count);
        var totalDurationMs = (long)Math.Round(summary.Operations.Sum(x => x.TotalDurationMs));
        var failures = summary.Operations.Where(x => x.Outcome == "failure").Sum(x => x.Count);
        var outcome = totalOps == 0 ? "idle" : failures > 0 ? (failures == totalOps ? "failed" : "mixed") : "succeeded";
        var nowUnixNano = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L).ToString();

        var attributes = new List<object>
        {
            Attr("Service", summary.Service),
            Attr("Operation", "agent.cycle"),
            Attr("Outcome", outcome),
            AttrInt("WindowDurationMs", summary.WindowDurationMs),
            AttrInt("Count", totalOps),
            AttrInt("TotalDurationMs", totalDurationMs),
            AttrInt("Bytes", summary.InputBytes),
            AttrInt("Failures", failures),
            AttrInt("ChildLaunches", summary.ChildLaunches),
            AttrInt("Records", summary.Records),
            AttrInt("CursorWrites", summary.CursorWrites),
            AttrInt("CursorWritesSkipped", summary.CursorWritesSkipped),
            AttrInt("AllocatedBytesDelta", summary.AllocatedBytesDelta),
            AttrInt("ExporterDrops", summary.ExporterDrops),
            AttrInt("QueueLag", summary.QueueLag)
        };
        if (summary.TraceId is not null)
        {
            attributes.Add(Attr("TraceId", summary.TraceId));
        }

        var payload = new
        {
            resourceLogs = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = new[] { Attr("service.name", RuntimeAgentDiagnosticSummary.ServiceName) }
                    },
                    scopeLogs = new[]
                    {
                        new
                        {
                            scope = new { name = RuntimeAgentDiagnostics.MeterName },
                            logRecords = new[]
                            {
                                new
                                {
                                    timeUnixNano = nowUnixNano,
                                    severityNumber = 9,
                                    severityText = "INFO",
                                    body = new { stringValue = "" },
                                    attributes
                                }
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload, CompactJson);

        static object Attr(string key, string value) => new { key, value = new { stringValue = value } };
        static object AttrInt(string key, long value) => new { key, value = new { intValue = value.ToString() } };
    }

    private sealed class OperationAccumulator
    {
        private readonly List<double> _samplesMs = [];
        public long Count;
        public double TotalDurationMs;

        public void AddDurationMs(double ms)
        {
            TotalDurationMs += ms;
            if (_samplesMs.Count < MaxDurationSamplesPerBucket)
            {
                _samplesMs.Add(ms);
            }
        }

        public RuntimeAgentOperationSummary ToSummary(string operation, string outcome)
        {
            double p95 = 0;
            if (_samplesMs.Count > 0)
            {
                _samplesMs.Sort();
                var index = (int)Math.Floor(0.95 * (_samplesMs.Count - 1));
                p95 = Math.Round(_samplesMs[index], 3);
            }

            return new RuntimeAgentOperationSummary(operation, outcome, Count, Math.Round(TotalDurationMs, 3), p95);
        }
    }
}
