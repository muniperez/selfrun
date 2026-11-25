using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Diagnostics;
using Selfrun.Telemetry.Controllers;
using Selfrun.Telemetry.Data;
using Selfrun.Telemetry.Otlp;
using Selfrun.Telemetry.Services;

// Synthetic OTLP/HTTP logs ingestion benchmark: real telemetry gateway services on loopback Kestrel,
// a file-backed SQLite store (the self-hosted default) and a loopback fake Loki. No external services.
if (args is not [var scratch, var scenario] || scenario is not ("stages" or "load" or "faults" or "overload") ||
    !Directory.Exists(scratch) || Directory.EnumerateFileSystemEntries(scratch).Any())
{
    Console.Error.WriteLine("Usage: OtlpIngestionBenchmark <empty scratch directory> <stages|load|faults|overload>");
    return 2;
}

const int Concurrency = 8;
var fakeLoki = new FakeLoki(
    faults: scenario == "faults",
    delay: scenario == "overload" ? TimeSpan.FromMilliseconds(50) : TimeSpan.Zero);
await using var loki = await fakeLoki.StartAsync();
await using var gateway = await Gateway.StartAsync(scratch, fakeLoki.Address!, maxConcurrentIngestions: scenario == "overload" ? 2 : 32);
var seed = await gateway.SeedAsync();
fakeLoki.ExpectedTenant = seed.TenantId;

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = Concurrency };
using var http = new HttpClient(handler) { BaseAddress = new Uri(gateway.Address), Timeout = TimeSpan.FromSeconds(60) };
var stages = new StageRecorder();
using var listener = stages.Listen();

object result = scenario switch
{
    "stages" => new
    {
        scenario,
        payloads = new[]
        {
            await Sequential(Payload.Create(500, gzip: true), warmup: 10, measured: 100),
            await Sequential(Payload.Create(500, gzip: false), warmup: 10, measured: 100),
            await Sequential(Payload.Create(50, gzip: true), warmup: 10, measured: 100),
        },
    },
    _ => await Concurrent(Payload.Create(500, gzip: true), warmup: 16, measured: 300),
};

Console.WriteLine(JsonSerializer.Serialize(new
{
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        os = RuntimeInformation.OSDescription,
        arch = RuntimeInformation.ProcessArchitecture.ToString(),
        cpus = Environment.ProcessorCount,
        serverGc = System.Runtime.GCSettings.IsServerGC,
#if DEBUG
        configuration = "Debug",
#else
        configuration = "Release",
#endif
    },
    result,
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

async Task<object> Sequential(Payload payload, int warmup, int measured)
{
    for (var i = 0; i < warmup; i++)
    {
        await Send(payload);
    }

    stages.Reset(attributeResources: true);
    gateway.ResetRequestCounters();
    gateway.Logs.Reset();
    var latencies = new List<double>(measured);
    var statuses = new Dictionary<int, int>();
    for (var i = 0; i < measured; i++)
    {
        var started = Stopwatch.GetTimestamp();
        var (status, _, _) = await Send(payload);
        latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        statuses[status] = statuses.GetValueOrDefault(status) + 1;
    }

    var accepted = statuses.GetValueOrDefault(200);
    var acceptedMiB = accepted * payload.DecodedBytes / 1048576.0;
    var server = gateway.RequestTotals();
    return new
    {
        payload = payload.Describe(),
        requests = measured,
        statuses,
        clientLatencyMs = Summary.Of(latencies),
        serverPerRequest = new
        {
            wallMs = server.WallMs / measured,
            cpuMs = server.CpuMs / measured,
            allocatedBytes = server.AllocatedBytes / measured,
        },
        serverPerAcceptedMiB = acceptedMiB == 0 ? null : new
        {
            cpuMs = server.CpuMs / acceptedMiB,
            allocatedMiB = server.AllocatedBytes / 1048576.0 / acceptedMiB,
        },
        stagesPerRequest = stages.PerRequest(measured),
        logEventsPerRequest = gateway.Logs.PerRequest(measured),
    };
}

async Task<object> Concurrent(Payload payload, int warmup, int measured)
{
    await RunConcurrent(payload, warmup);
    stages.Reset(attributeResources: false);
    gateway.ResetRequestCounters();
    gateway.Logs.Reset();
    fakeLoki.Reset();
    GC.Collect();
    var cpuBefore = Cpu.ProcessMs();
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var started = Stopwatch.GetTimestamp();
    var outcomes = await RunConcurrent(payload, measured);
    var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
    var cpu = Cpu.ProcessMs() - cpuBefore;
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

    var statuses = outcomes.GroupBy(o => o.Status).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
    var accepted = statuses.GetValueOrDefault(200);
    var acceptedMiB = accepted * payload.DecodedBytes / 1048576.0;
    var retryable = outcomes.Count(o => o.Status is 429 or 503);
    return new
    {
        scenario,
        payload = payload.Describe(),
        concurrency = Concurrency,
        maxConcurrentIngestions = gateway.MaxConcurrentIngestions,
        requests = measured,
        statuses,
        acceptedRate = (double)accepted / measured,
        retryableRate = (double)retryable / measured,
        retryableWithRetryAfter = outcomes.Count(o => o.Status is 429 or 503 && o.HasRetryAfter),
        partialSuccessResponses = outcomes.Count(o => o.Status == 200 && o.Rejected > 0),
        upstream = fakeLoki.Snapshot(),
        throughput = new { requestsPerSecond = measured / wall, acceptedMiBPerSecond = acceptedMiB / wall },
        clientLatencyMs = Summary.Of(outcomes.Select(o => o.LatencyMs)),
        forwardStageMs = stages.Durations("otlp.forward"),
        processPerAcceptedMiB = acceptedMiB == 0 ? null : new
        {
            note = "whole process: gateway, loopback client and fake Loki",
            cpuMs = cpu / acceptedMiB,
            allocatedMiB = allocated / 1048576.0 / acceptedMiB,
        },
        logEventsPerRequest = gateway.Logs.PerRequest(measured),
    };
}

async Task<List<Outcome>> RunConcurrent(Payload payload, int count)
{
    var outcomes = new ConcurrentBag<Outcome>();
    var next = -1;
    await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(async _ =>
    {
        while (Interlocked.Increment(ref next) < count)
        {
            var started = Stopwatch.GetTimestamp();
            var (status, rejected, retryAfter) = await Send(payload);
            outcomes.Add(new Outcome(status, rejected, retryAfter, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
    }));
    return [.. outcomes];
}

async Task<(int Status, long Rejected, bool RetryAfter)> Send(Payload payload)
{
    using var content = new ByteArrayContent(payload.Body);
    content.Headers.ContentType = new(OtlpProtobuf.ProtobufContentType);
    if (payload.Gzip)
    {
        content.Headers.ContentEncoding.Add("gzip");
    }

    using var message = new HttpRequestMessage(HttpMethod.Post, "/otlp/v1/logs") { Content = content };
    message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {seed.RawToken}");
    using var response = await http.SendAsync(message);
    var body = await response.Content.ReadAsByteArrayAsync();
    var (rejected, _) = OtlpProtobuf.ParseExportResponse(body);
    return ((int)response.StatusCode, rejected, response.Headers.RetryAfter is not null);
}

sealed record Outcome(int Status, long Rejected, bool HasRetryAfter, double LatencyMs);

sealed record Seed(string TenantId, string RawToken);

sealed class Payload
{
    public required byte[] Body { get; init; }
    public required bool Gzip { get; init; }
    public required int Records { get; init; }
    public required long DecodedBytes { get; init; }

    public object Describe() => new
    {
        records = Records,
        encoding = Gzip ? "gzip" : "identity",
        encodedBytes = Body.Length,
        decodedBytes = DecodedBytes,
    };

    public static Payload Create(int records, bool gzip)
    {
        var random = new Random(42);
        var request = new OtlpExportRequest();
        var resource = new OtlpResourceLogs
        {
            Resource = new OtlpResource
            {
                Attributes =
                [
                    Attr("service.name", "orders"),
                    Attr("service.instance.id", "orders-7c9f8d6b5-x2k4p"),
                    Attr("host.name", "synthetic-host"),
                    Attr("deployment.environment", "production"),
                    Attr("telemetry.sdk.name", "opentelemetry"),
                ],
            },
        };
        var scope = new OtlpScopeLogs { Scope = new OtlpScope { Name = "Orders.Api", Version = "1.0.0" } };
        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        for (var i = 0; i < records; i++)
        {
            var route = $"/api/orders/{random.Next(100_000)}";
            scope.LogRecords.Add(new OtlpLogRecord
            {
                TimeUnixNano = now + (ulong)i * 1000,
                ObservedTimeUnixNano = now + (ulong)i * 1000,
                SeverityNumber = i % 10 == 0 ? 13 : 9,
                SeverityText = i % 10 == 0 ? "WARN" : "INFO",
                Body = new OtlpAnyValue
                {
                    Kind = OtlpValueKind.String,
                    StringValue = $"HTTP GET {route} responded {(i % 10 == 0 ? 404 : 200)} in {random.Next(1, 250)} ms for tenant shard {random.Next(16)} correlation {Guid.NewGuid():N}",
                },
                Attributes =
                [
                    Attr("http.request.method", "GET"),
                    Attr("http.route", "/api/orders/{id}"),
                    new OtlpKeyValue { Key = "http.response.status_code", Value = new OtlpAnyValue { Kind = OtlpValueKind.Int, IntValue = i % 10 == 0 ? 404 : 200 } },
                    Attr("user.id", $"u-{random.Next(5_000)}"),
                    Attr("request.id", Guid.NewGuid().ToString("N")),
                ],
                TraceId = RandomNumberGenerator.GetBytes(16),
                SpanId = RandomNumberGenerator.GetBytes(8),
            });
        }

        resource.ScopeLogs.Add(scope);
        request.ResourceLogs.Add(resource);
        var decoded = request.ToByteArray();
        var body = decoded;
        if (gzip)
        {
            using var output = new MemoryStream();
            using (var compressor = new GZipStream(output, CompressionLevel.Fastest))
            {
                compressor.Write(decoded);
            }

            body = output.ToArray();
        }

        return new Payload { Body = body, Gzip = gzip, Records = records, DecodedBytes = decoded.Length };
    }

    private static OtlpKeyValue Attr(string key, string value) =>
        new() { Key = key, Value = new OtlpAnyValue { Kind = OtlpValueKind.String, StringValue = value } };
}

sealed class FakeLoki(bool faults, TimeSpan delay)
{
    private long _requests;
    private long _bytes;
    private long _tenantViolations;
    private long _authorizationLeaks;
    private long _injected503;
    private long _injected429;
    private long _injectedPartial;

    public string? Address { get; private set; }
    public string? ExpectedTenant { get; set; }

    public void Reset()
    {
        _requests = _bytes = _tenantViolations = _authorizationLeaks = _injected503 = _injected429 = _injectedPartial = 0;
    }

    public object Snapshot() => new
    {
        requests = Interlocked.Read(ref _requests),
        forwardedMiB = Interlocked.Read(ref _bytes) / 1048576.0,
        tenantViolations = Interlocked.Read(ref _tenantViolations),
        authorizationLeaks = Interlocked.Read(ref _authorizationLeaks),
        injected503 = Interlocked.Read(ref _injected503),
        injected429 = Interlocked.Read(ref _injected429),
        injectedPartial = Interlocked.Read(ref _injectedPartial),
    };

    public async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapPost("/otlp/v1/logs", async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var n = Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _bytes, buffer.Length);
            if (context.Request.Headers["X-Scope-OrgID"].ToString() != ExpectedTenant)
            {
                Interlocked.Increment(ref _tenantViolations);
            }

            if (context.Request.Headers.ContainsKey("Authorization"))
            {
                Interlocked.Increment(ref _authorizationLeaks);
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            context.Response.ContentType = OtlpProtobuf.ProtobufContentType;
            if (faults && n % 10 == 0)
            {
                Interlocked.Increment(ref _injected503);
                context.Response.StatusCode = 503;
                context.Response.Headers.RetryAfter = "2";
                return;
            }

            if (faults && n % 20 == 5)
            {
                Interlocked.Increment(ref _injected429);
                context.Response.StatusCode = 429;
                return;
            }

            if (faults && n % 7 == 3)
            {
                Interlocked.Increment(ref _injectedPartial);
                await context.Response.Body.WriteAsync(OtlpProtobuf.EncodeExportResponse(3, "synthetic partial rejection"));
                return;
            }

            await context.Response.Body.WriteAsync(OtlpProtobuf.EncodeExportResponse(0, string.Empty));
        });
        await app.StartAsync();
        Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return app;
    }
}

sealed class Gateway : IAsyncDisposable
{
    private readonly WebApplication _app;
    private long _requests;
    private double _wallMs;
    private double _cpuMs;
    private long _allocated;

    private Gateway(WebApplication app, int maxConcurrentIngestions, CountingLoggerProvider logs)
    {
        _app = app;
        MaxConcurrentIngestions = maxConcurrentIngestions;
        Logs = logs;
        Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public string Address { get; }
    public int MaxConcurrentIngestions { get; }
    public CountingLoggerProvider Logs { get; }

    public static async Task<Gateway> StartAsync(string scratch, string lokiAddress, int maxConcurrentIngestions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:LokiBaseAddress"] = lokiAddress,
            ["Telemetry:Environment"] = "bench",
            ["Telemetry:MaxConcurrentIngestions"] = maxConcurrentIngestions.ToString(),
            // Daily budgets are raised so repeated runs measure the admitted path; burst and sustained limits keep defaults.
            ["Telemetry:PerAppDailyBytes"] = long.MaxValue.ToString(),
            ["Telemetry:PerOrganizationDailyBytes"] = long.MaxValue.ToString(),
            ["Telemetry:WholeStackDailyBytes"] = long.MaxValue.ToString(),
        });
        // Mirrors the logging defaults of the telemetry service (no appsettings: Information for all categories)
        // but counts events instead of writing them.
        var logs = new CountingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(logs);

        // Same registrations the OTLP path uses in Selfrun.Telemetry.Program.
        builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.SectionName));
        var database = Path.Combine(scratch, "telemetry.db");
        builder.Services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite($"Data Source={database}"));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<TelemetryStatusReporter>();
        builder.Services.AddSingleton<OtlpIngestionLimiter>();
        builder.Services.AddScoped<TelemetryAuthorityService>();
        builder.Services.AddScoped<TelemetryCredentialAuthService>();
        builder.Services.AddScoped<TelemetryQuotaService>();
        builder.Services.AddScoped<OtlpNormalizationService>();
        builder.Services.AddScoped<OtlpLokiForwarder>();
        builder.Services.AddControllers().AddApplicationPart(typeof(OtlpLogsController).Assembly);

        var app = builder.Build();
        Gateway? gateway = null;
        app.Use(async (context, next) =>
        {
            var wall = Stopwatch.GetTimestamp();
            var cpu = Cpu.ProcessMs();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            try
            {
                await next();
            }
            finally
            {
                gateway!.Record(
                    Stopwatch.GetElapsedTime(wall).TotalMilliseconds,
                    Cpu.ProcessMs() - cpu,
                    GC.GetTotalAllocatedBytes(precise: true) - allocated);
            }
        });
        app.UseRouting();
        app.MapControllers();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TelemetryDbContext>().Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        gateway = new Gateway(app, maxConcurrentIngestions, logs);
        return gateway;
    }

    public async Task<Seed> SeedAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rawToken = $"mcl_ingest_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
        var salt = RandomNumberGenerator.GetBytes(16);
        // Same PBKDF2 parameters the control plane uses for issued ingestion credentials.
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(rawToken), salt, 210_000, HashAlgorithmName.SHA256, 32);
        var tenantId = "mcl_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        db.AuthorityCheckpoints.Add(new TelemetryAuthorityCheckpoint { Id = 1, Sequence = 1, CheckpointTimestamp = now, ConfirmedAt = now });
        db.TenantBindings.Add(new TelemetryTenantBinding
        {
            AppId = "app_bench",
            OrganizationId = "org_bench",
            TenantId = tenantId,
            Enabled = true,
            RetentionPolicy = "7d",
            UpdatedAt = now,
        });
        db.CredentialReplicas.Add(new TelemetryCredentialReplica
        {
            Id = "tcred_bench",
            OrganizationId = "org_bench",
            AppId = "app_bench",
            ServiceName = "orders",
            TenantId = tenantId,
            KeyPrefix = rawToken[..19],
            KeyHash = $"pbkdf2_sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}",
            CredentialVersion = 0,
            Status = "active",
            ExpiresAt = now.AddDays(90),
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(tenantId, rawToken);
    }

    public void ResetRequestCounters()
    {
        lock (this)
        {
            _requests = 0;
            _wallMs = _cpuMs = 0;
            _allocated = 0;
        }
    }

    public (long Requests, double WallMs, double CpuMs, long AllocatedBytes) RequestTotals()
    {
        lock (this)
        {
            return (_requests, _wallMs, _cpuMs, _allocated);
        }
    }

    private void Record(double wallMs, double cpuMs, long allocated)
    {
        lock (this)
        {
            _requests++;
            _wallMs += wallMs;
            _cpuMs += cpuMs;
            _allocated += allocated;
        }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

// Attributes CPU and allocations to the gateway's sequential OTLP stage spans. Resource attribution is only
// meaningful when one request is in flight; concurrent runs record durations only.
sealed class StageRecorder
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<(double WallMs, double CpuMs, long Allocated)>> _stages = new();
    private volatile bool _attribute;

    public void Reset(bool attributeResources)
    {
        _stages.Clear();
        _attribute = attributeResources;
    }

    public ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OperatorDiagnostics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (_attribute)
                {
                    activity.SetCustomProperty("bench.cpu", Cpu.ProcessMs());
                    activity.SetCustomProperty("bench.alloc", GC.GetTotalAllocatedBytes(precise: true));
                }
            },
            ActivityStopped = activity =>
            {
                double cpu = 0;
                long allocated = 0;
                if (_attribute && activity.GetCustomProperty("bench.cpu") is double cpuStart &&
                    activity.GetCustomProperty("bench.alloc") is long allocStart)
                {
                    cpu = Cpu.ProcessMs() - cpuStart;
                    allocated = GC.GetTotalAllocatedBytes(precise: true) - allocStart;
                }

                _stages.GetOrAdd(activity.OperationName, _ => []).Add((activity.Duration.TotalMilliseconds, cpu, allocated));
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public object PerRequest(int requests) => _stages
        .OrderBy(s => Array.IndexOf(Order, s.Key))
        .ToDictionary(s => s.Key, s => new
        {
            count = s.Value.Count,
            wallMs = s.Value.Sum(x => x.WallMs) / requests,
            cpuMs = s.Value.Sum(x => x.CpuMs) / requests,
            allocatedBytes = s.Value.Sum(x => x.Allocated) / requests,
        });

    public object? Durations(string stage) =>
        _stages.TryGetValue(stage, out var values) ? Summary.Of(values.Select(v => v.WallMs)) : null;

    private static readonly string[] Order =
        ["otlp.auth", "otlp.read", "otlp.gzip", "otlp.decode", "otlp.normalize", "otlp.quota", "otlp.encode", "otlp.forward"];
}

sealed class CountingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Reset() => _counts.Clear();

    public object PerRequest(int requests) => _counts
        .OrderByDescending(c => c.Value)
        .ToDictionary(c => c.Key, c => (double)c.Value / requests);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CountingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Format like a console provider would so the cost of the message is included.
            _ = formatter(state, exception);
            owner._counts.AddOrUpdate($"{logLevel}:{category}", 1, (_, n) => n + 1);
        }
    }
}

static class Summary
{
    public static object Of(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new { count = 0 };
        }

        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new { count = sorted.Length, mean = sorted.Average(), p50 = P(0.50), p95 = P(0.95), p99 = P(0.99), max = sorted[^1] };
    }
}

static unsafe partial class Cpu
{
    // getrusage(RUSAGE_SELF) reports scheduler-accounted CPU with microsecond resolution on Linux,
    // unlike Process.TotalProcessorTime, which is tick-based there.
    public static double ProcessMs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds;
        }

        var usage = stackalloc long[18];
        if (GetRusage(0, usage) != 0)
        {
            throw new InvalidOperationException("getrusage failed");
        }

        return (usage[0] + usage[2]) * 1000.0 + (usage[1] + usage[3]) / 1000.0;
    }

    [LibraryImport("libc", EntryPoint = "getrusage")]
    private static partial int GetRusage(int who, long* usage);
}
