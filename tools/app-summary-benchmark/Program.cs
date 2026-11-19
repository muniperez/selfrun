using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Api.Auth;
using Minicloud.Api.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

var connection = Environment.GetEnvironmentVariable("MINICLOUD_PERF_DATABASE")
    ?? throw new InvalidOperationException("Set MINICLOUD_PERF_DATABASE to a disposable local PostgreSQL database.");
var cs = new Npgsql.NpgsqlConnectionStringBuilder(connection);
if (cs.Host is not ("localhost" or "127.0.0.1") || string.IsNullOrEmpty(cs.Database) || !cs.Database.StartsWith("apps_perf", StringComparison.Ordinal))
    throw new InvalidOperationException("Benchmark requires a local database named apps_perf*.");

var httpContextAccessor = new HttpContextAccessor();
var recorder = new Commands(httpContextAccessor);
DbContextOptions<MinicloudDbContext> Options() => new DbContextOptionsBuilder<MinicloudDbContext>()
    .UseNpgsql(connection, o => o.CommandTimeout(10))
    .AddInterceptors(recorder)
    .Options;

await using (var db = new MinicloudDbContext(Options()))
{
    await db.Database.OpenConnectionAsync();
    await using var check = db.Database.GetDbConnection().CreateCommand();
    check.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public')";
    if ((bool)(await check.ExecuteScalarAsync())!) throw new InvalidOperationException("Use a fresh empty benchmark database.");
    await db.Database.MigrateAsync();
}

Console.WriteLine($".NET {Environment.Version}; CPUs {Environment.ProcessorCount}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
await using (var db = new MinicloudDbContext(Options()))
{
    await db.Database.OpenConnectionAsync();
    using var cmd = db.Database.GetDbConnection().CreateCommand();
    cmd.CommandText = "SELECT version()";
    Console.WriteLine(await cmd.ExecuteScalarAsync());
}

var apiKeys = new Dictionary<string, ApiKeyToken>(StringComparer.Ordinal);
var runtimeTokens = new Dictionary<string, RuntimeTokenValue>(StringComparer.Ordinal);
var workloads = new[] { ("small", 6, 10), ("history", 6, 1000), ("scaling", 50, 1000) };
foreach (var (org, roots, history) in workloads)
{
    var tokens = await Seed(org, roots, history);
    apiKeys.Add(org, tokens.ApiKey);
    runtimeTokens.Add(org, tokens.RuntimeToken);
}

var requests = new ConcurrentDictionary<string, RequestMetrics>(StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
builder.Logging.ClearProviders();
builder.Services.AddDbContext<MinicloudDbContext>(options => options
    .UseNpgsql(connection, o => o.CommandTimeout(10))
    .AddInterceptors(recorder));
builder.Services.AddControllers().AddApplicationPart(typeof(ApplicationsController).Assembly);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IApiKeyTokenService, ApiKeyTokenService>();
builder.Services.AddSingleton<IIdGenerator, IdGenerator>();
builder.Services.AddScoped<IRuntimeTokenService, RuntimeTokenService>();
builder.Services.AddScoped<ControlPlaneAuthorization>();
builder.Services.AddScoped<BrowserSessionService>();
builder.Services.AddSingleton<IKratosSessionValidator, NoKratosSessions>();
builder.Services.AddScoped<ControlPlanePrincipalResolver>();
builder.Services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);
builder.Services.AddAuthentication(ControlPlaneAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ControlPlaneAuthenticationHandler>(ControlPlaneAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

await using var app = builder.Build();
app.Use(async (context, next) =>
{
    var startedAllocatedBytes = GC.GetTotalAllocatedBytes(false);
    if (context.Request.Headers.TryGetValue("X-Benchmark-Request-Id", out var requestId) &&
        requests.TryGetValue(requestId.ToString(), out var metrics))
    {
        context.Items[Commands.MetricsKey] = metrics;
    }

    try
    {
        await next();
    }
    finally
    {
        if (context.Items[Commands.MetricsKey] is RequestMetrics requestMetrics)
            requestMetrics.AllocatedBytes = GC.GetTotalAllocatedBytes(false) - startedAllocatedBytes;
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
using var http = new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
Console.WriteLine($"HTTP loopback endpoint: {address}");
Console.WriteLine("Workload: 3 independent runs; 30 reads/run, 5 read clients, 30 status-ingest requests/run; allocations measured in a separate sequential read pass.");

var forbidden = await Send(http, "/api/apps?organizationId=small", apiKeys["history"].Token);
if (forbidden.StatusCode != HttpStatusCode.Forbidden)
    throw new InvalidOperationException($"Cross-tenant read was not rejected: HTTP {(int)forbidden.StatusCode}.");
Console.WriteLine("Tenant-scope check: history API key cannot read small organization.");

var query = "/api/apps?organizationId=history";
recorder.ClearSql();
var validation = await ReadOnce(query, apiKeys["history"].Token, 6, 1000, captureSql: true);
await WritePlans(validation.Sql);

foreach (var (org, roots, history) in workloads)
{
    var path = $"/api/apps?organizationId={Uri.EscapeDataString(org)}";
    var expectedSignature = (await ReadOnce(path, apiKeys[org].Token, roots, history)).Signature;
    var noIngestion = new List<RunSummary>();
    var withIngestion = new List<RunSummary>();
    for (var run = 1; run <= 3; run++)
    {
        var baseline = await RunReaders(path, apiKeys[org].Token, roots, history, null);
        noIngestion.Add(baseline);
        var concurrent = await RunReaders(path, apiKeys[org].Token, roots, history,
            () => Ingest(runtimeTokens[org].Token, 30));
        withIngestion.Add(concurrent);
        if (baseline.Signature != expectedSignature || concurrent.Signature != expectedSignature)
            throw new InvalidOperationException($"Authenticated response changed for {org}.");
        PrintRun(org, "no-ingestion", run, baseline);
        PrintRun(org, "concurrent-ingestion", run, concurrent);
    }

    PrintSpread(org, "no-ingestion", noIngestion);
    PrintSpread(org, "concurrent-ingestion", withIngestion);
    var allocations = await MeasureAllocations(path, apiKeys[org].Token, roots, history);
    Console.WriteLine($"{org}: sequential HTTP allocation/read={allocations:F0} bytes; response={noIngestion[0].AverageResponseBytes:F0} bytes; SQL/read={noIngestion[0].AverageCommands:F1} commands, {noIngestion[0].AverageSqlMs:F2} ms");
}

await app.StopAsync();

async Task<(ApiKeyToken ApiKey, RuntimeTokenValue RuntimeToken)> Seed(string org, int roots, int history)
{
    await using var db = new MinicloudDbContext(Options());
    var now = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
    var apiKeyService = new ApiKeyTokenService();
    var apiKey = apiKeyService.Generate();
    var runtimeTokenService = new RuntimeTokenService(db, TimeProvider.System);
    var runtimeToken = runtimeTokenService.Generate();
    db.Organizations.Add(new Organization { Id = org, Name = org, Slug = org, CreatedAt = now, UpdatedAt = now });
    db.ApiKeys.Add(new ApiKey
    {
        Id = $"key-{org}", OrganizationId = org, Name = "benchmark", KeyPrefix = apiKey.Prefix,
        KeyHash = apiKeyService.Hash(apiKey.Token), ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead }),
        CreatedAt = now, UpdatedAt = now
    });

    for (var r = 0; r < roots; r++)
    {
        var root = $"{org}-{r:D3}-0";
        var application = $"appl-{org}-{r:D3}";
        db.Applications.Add(new MinicloudApplication
        {
            Id = application, OrganizationId = org, Name = root, Slug = root, NormalizedSlug = root,
            DefaultEnvironmentId = root, LegacyRootAppId = root, CreatedAt = now, UpdatedAt = now
        });
        for (var e = 0; e < 3; e++)
        {
            var id = $"{org}-{r:D3}-{e}";
            db.Apps.Add(new MinicloudApp
            {
                Id = id, OrganizationId = org, ApplicationId = application, Name = id, Slug = id,
                ParentAppId = e == 0 ? null : root, EnvironmentName = $"env{e}", NormalizedEnvironmentName = $"env{e}",
                BranchName = e == 2 ? "feature" : null, EnvironmentKind = e == 2 ? EnvironmentKinds.Preview : EnvironmentKinds.Persistent,
                Plan = AppPlan.P0H0, CreatedAt = now, UpdatedAt = now
            });
            for (var d = 0; d < 9; d++)
            {
                db.Domains.Add(new Domain
                {
                    Id = $"domain-{id}-{d}", OrganizationId = org, AppId = id, ServiceName = "web",
                    Kind = "custom", DnsStatus = "ready", ApplyStatus = "applied",
                    Hostname = $"d{d}-{id}.example.test", Status = d == 8 ? "disabled" : "active",
                    SslStatus = "active", CreatedAt = now, UpdatedAt = now
                });
            }
        }
    }

    var observedAt = DateTimeOffset.UtcNow;
    var serverId = $"server-{org}";
    var appId = $"{org}-000-0";
    db.RuntimeServers.Add(new RuntimeServer
    {
        Id = serverId, OrganizationId = org, AppId = appId, Provider = "benchmark",
        AgentVersion = "benchmark", LastSeenAt = observedAt, CreatedAt = now, UpdatedAt = now
    });
    db.RuntimeTokens.Add(new RuntimeToken
    {
        Id = $"token-{org}", RuntimeServerId = serverId, TokenPrefix = runtimeTokenService.ExtractPrefix(runtimeToken.Token)!,
        TokenHash = runtimeTokenService.Hash(runtimeToken.Token), CreatedAt = now, UpdatedAt = now
    });
    db.RuntimeStatusSnapshots.Add(new RuntimeStatusSnapshot
    {
        Id = $"status-{org}", OrganizationId = org, AppId = appId, RuntimeServerId = serverId,
        StatusJson = JsonSerializer.Serialize(new StoredRuntimeStatus("benchmark", observedAt, "bench", [new("web", null, "running", "healthy", null, 0)], null), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        ObservedAt = observedAt, ReceivedAt = observedAt
    });
    await db.SaveChangesAsync();
    await db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO deployments(id,organization_id,app_id,number,database,status,created_at,updated_at)
        SELECT a.id || '-deploy-' || lpad(n::text, 5, '0'), a.organization_id, a.id,
          to_char(TIMESTAMPTZ '2026-11-01 00:00:00+00' + n * INTERVAL '1 second', 'YYYYMMDD') || '.' ||
            row_number() OVER (PARTITION BY a.organization_id ORDER BY a.id, n)::text,
          'None',
          CASE WHEN n % 3 = 0 THEN 'Failed' ELSE 'Succeeded' END,
          TIMESTAMPTZ '2026-11-01 00:00:00+00' + n * INTERVAL '1 second', TIMESTAMPTZ '2026-11-01 00:00:00+00'
        FROM apps a CROSS JOIN generate_series(1, {history}) n WHERE a.organization_id = {org}
        """);
    await db.Database.ExecuteSqlRawAsync("ANALYZE");
    return (apiKey, runtimeToken);
}

async Task<ReadResult> ReadOnce(string path, string token, int roots, int history, bool captureSql = false)
{
    var response = await Send(http, path, token, captureSql: captureSql);
    if (response.StatusCode != HttpStatusCode.OK)
        throw new InvalidOperationException($"App read failed: HTTP {(int)response.StatusCode}: {response.Body}");
    using var document = JsonDocument.Parse(response.Body);
    var applications = document.RootElement;
    var organizationId = path.Split("organizationId=", 2)[1];
    if (applications.GetArrayLength() != roots)
        throw new InvalidOperationException($"Expected {roots} applications, received {applications.GetArrayLength()}.");
    var stable = new List<string>();
    foreach (var application in applications.EnumerateArray())
    {
        if (application.GetProperty("organizationId").GetString() != organizationId ||
            !application.GetProperty("id").GetString()!.StartsWith($"appl-{organizationId}-", StringComparison.Ordinal))
            throw new InvalidOperationException("Response contains an unexpected tenant or application.");
        var environments = application.GetProperty("environments");
        if (environments.GetArrayLength() != 3)
            throw new InvalidOperationException($"Expected three environments for {application.GetProperty("id").GetString()}.");
        foreach (var environment in environments.EnumerateArray())
        {
            var id = environment.GetProperty("id").GetString()!;
            var deployment = environment.GetProperty("latestDeployment").GetProperty("id").GetString();
            var runtimeState = environment.GetProperty("runtimeObservation").GetProperty("state").GetString();
            if (environment.GetProperty("domains").GetArrayLength() != 8 || deployment != $"{id}-deploy-{history:D5}")
                throw new InvalidOperationException($"Summary contract failed for {id}.");
            if (runtimeState != (id == $"{organizationId}-000-0" ? "healthy" : "unprovisioned"))
                throw new InvalidOperationException($"Runtime observation contract failed for {id}: {runtimeState}.");
            stable.Add($"{application.GetProperty("id").GetString()}|{id}|{deployment}|{environment.GetProperty("domains").GetArrayLength()}|{runtimeState}");
        }
    }
    return new ReadResult(response.Body, string.Join('\n', stable), response.Metrics!, response.Sql);
}

async Task<RunSummary> RunReaders(string path, string token, int roots, int history, Func<Task<IngestSummary>>? ingestion)
{
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var writer = ingestion is null
        ? Task.FromResult<IngestSummary?>(null)
        : Task.Run<IngestSummary?>(async () => { await start.Task; return await ingestion(); });
    var readers = Enumerable.Range(0, 5).Select(async _ =>
    {
        await start.Task;
        var samples = new List<Sample>();
        for (var i = 0; i < 6; i++)
        {
            var result = await ReadOnce(path, token, roots, history);
            samples.Add(new Sample(result.Metrics.ElapsedMs, result.Metrics.Commands, result.Metrics.SqlMs, Encoding.UTF8.GetByteCount(result.Body)));
        }
        return samples;
    }).ToArray();
    start.SetResult();
    var samples = (await Task.WhenAll(readers)).SelectMany(x => x).ToArray();
    var ingestionSummary = await writer;
    return new RunSummary(samples, (await ReadOnce(path, token, roots, history)).Signature, ingestionSummary);
}

async Task<IngestSummary> Ingest(string token, int count)
{
    var observedAt = DateTimeOffset.UtcNow;
    var samples = new List<Sample>();
    for (var i = 0; i < count; i++)
    {
        var body = new RuntimeStatusIngestRequest("benchmark", observedAt.AddTicks(i), "bench",
            [new RuntimeServiceStatusDto("web", null, "running", "healthy", null, 0)], null);
        var response = await Send(http, "/api/runtime/ingest/status", token, body);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Runtime ingestion failed: HTTP {(int)response.StatusCode}: {response.Body}");
        using var document = JsonDocument.Parse(response.Body);
        if (!document.RootElement.GetProperty("accepted").GetBoolean())
            throw new InvalidOperationException("Runtime status ingestion was not accepted.");
        samples.Add(new Sample(response.Metrics!.ElapsedMs, response.Metrics.Commands, response.Metrics.SqlMs, Encoding.UTF8.GetByteCount(response.Body)));
    }
    return new IngestSummary(samples);
}

async Task<double> MeasureAllocations(string path, string token, int roots, int history)
{
    for (var i = 0; i < 3; i++) await ReadOnce(path, token, roots, history);
    var allocations = new List<long>();
    for (var i = 0; i < 10; i++)
        allocations.Add((await ReadOnce(path, token, roots, history)).Metrics.AllocatedBytes);
    return allocations.Average();
}

async Task<ApiResponse> Send(HttpClient client, string path, string token, object? body = null, bool captureSql = false)
{
    var id = Guid.NewGuid().ToString("N");
    var metrics = new RequestMetrics();
    requests[id] = metrics;
    using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.Add("X-Benchmark-Request-Id", id);
    if (body is not null) request.Content = JsonContent.Create(body);
    var timer = Stopwatch.StartNew();
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    timer.Stop();
    requests.TryRemove(id, out _);
    metrics.ElapsedMs = timer.Elapsed.TotalMilliseconds;
    return new ApiResponse(response.StatusCode, text, metrics, captureSql ? recorder.SqlSnapshot() : []);
}

async Task WritePlans(IReadOnlyList<SqlEntry> sql)
{
    var dir = Environment.GetEnvironmentVariable("MINICLOUD_PERF_OUTPUT") ?? Path.Combine(FindRepositoryRoot(), ".local/artifacts/apps-data-fetch");
    Directory.CreateDirectory(dir);
    var queries = sql.Where(x => x.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        .GroupBy(x => x.Sql, StringComparer.Ordinal).Select(x => x.First()).Take(20).ToArray();
    for (var i = 0; i < queries.Length; i++)
    {
        var entry = queries[i];
        await File.WriteAllTextAsync(Path.Combine(dir, $"query-{i + 1}.sql"), entry.Sql);
        await using var db = new MinicloudDbContext(Options());
        await db.Database.OpenConnectionAsync();
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS) " + entry.Sql;
        foreach (var p in entry.Parameters) cmd.Parameters.Add(new Npgsql.NpgsqlParameter(p.Name, p.Value));
        await using var rows = await cmd.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await rows.ReadAsync()) lines.Add(rows.GetString(0));
        await File.WriteAllLinesAsync(Path.Combine(dir, $"plan-{i + 1}.txt"), lines);
    }
    Console.WriteLine($"SQL and PostgreSQL plans: {dir}; captured {queries.Length} unique SELECTs.");
}

string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Could not locate the repository root for benchmark artifacts.");
}

void PrintRun(string org, string scenario, int run, RunSummary summary)
{
    var times = summary.Samples.Select(x => x.ElapsedMs).Order().ToArray();
    Console.WriteLine($"{org} {scenario} run={run}: reads={times.Length} p95={Percentile(times, .95):F1}ms commands/read={summary.AverageCommands:F1} sql-ms/read={summary.AverageSqlMs:F2} response-bytes={summary.AverageResponseBytes:F0}");
    if (summary.Ingestion is { } ingestion)
        Console.WriteLine($"{org} concurrent-ingestion run={run}: writes={ingestion.Samples.Count} p95={ingestion.P95Ms:F1}ms commands/write={ingestion.AverageCommands:F1} sql-ms/write={ingestion.AverageSqlMs:F2} response-bytes={ingestion.AverageResponseBytes:F0}");
}

void PrintSpread(string org, string scenario, IReadOnlyList<RunSummary> runs)
{
    var p95s = runs.Select(x => Percentile(x.Samples.Select(y => y.ElapsedMs).Order().ToArray(), .95)).ToArray();
    Console.WriteLine($"{org} {scenario} run-to-run p95 spread: min={p95s.Min():F1}ms max={p95s.Max():F1}ms range={p95s.Max() - p95s.Min():F1}ms");
}

static double Percentile(double[] sorted, double percentile) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];

sealed record ApiResponse(HttpStatusCode StatusCode, string Body, RequestMetrics? Metrics, IReadOnlyList<SqlEntry> Sql);
sealed record ReadResult(string Body, string Signature, RequestMetrics Metrics, IReadOnlyList<SqlEntry> Sql);
sealed record Sample(double ElapsedMs, int Commands, double SqlMs, int ResponseBytes);
sealed record RunSummary(IReadOnlyList<Sample> Samples, string Signature, IngestSummary? Ingestion)
{
    public double AverageCommands => Samples.Average(x => x.Commands);
    public double AverageSqlMs => Samples.Average(x => x.SqlMs);
    public double AverageResponseBytes => Samples.Average(x => x.ResponseBytes);
}
sealed record IngestSummary(IReadOnlyList<Sample> Samples)
{
    public double P95Ms
    {
        get
        {
            var sorted = Samples.Select(x => x.ElapsedMs).Order().ToArray();
            return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * .95) - 1)];
        }
    }
    public double AverageCommands => Samples.Average(x => x.Commands);
    public double AverageSqlMs => Samples.Average(x => x.SqlMs);
    public double AverageResponseBytes => Samples.Average(x => x.ResponseBytes);
}
sealed record SqlEntry(string Sql, (string Name, object Value)[] Parameters);

sealed class RequestMetrics
{
    private long _sqlTicks;
    private int _commands;

    public double ElapsedMs { get; set; }
    public long AllocatedBytes { get; set; }
    public int Commands => Volatile.Read(ref _commands);
    public double SqlMs => TimeSpan.FromTicks(Interlocked.Read(ref _sqlTicks)).TotalMilliseconds;
    public void AddCommand(TimeSpan duration)
    {
        Interlocked.Increment(ref _commands);
        Interlocked.Add(ref _sqlTicks, duration.Ticks);
    }
}

sealed class Commands(IHttpContextAccessor accessor) : DbCommandInterceptor
{
    public const string MetricsKey = "benchmark.metrics";
    private readonly ConcurrentDictionary<DbCommand, long> _starts = new();
    private readonly ConcurrentQueue<SqlEntry> _sql = new();

    public Commands() : this(new HttpContextAccessor()) { }

    public IReadOnlyList<SqlEntry> SqlSnapshot() => _sql.ToArray();
    public void ClearSql()
    {
        while (_sql.TryDequeue(out _)) { }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Finish(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Finish(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Start(command);
        return ValueTask.FromResult(result);
    }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Finish(command);
        return ValueTask.FromResult(result);
    }

    private void Start(DbCommand command)
    {
        _starts[command] = Stopwatch.GetTimestamp();
        _sql.Enqueue(new SqlEntry(command.CommandText, command.Parameters.Cast<DbParameter>()
            .Select(p => (p.ParameterName, p.Value is DBNull ? DBNull.Value : p.Value!)).ToArray()));
    }

    private void Finish(DbCommand command)
    {
        if (!_starts.TryRemove(command, out var start)) return;
        var metrics = accessor.HttpContext?.Items[MetricsKey] as RequestMetrics;
        metrics?.AddCommand(Stopwatch.GetElapsedTime(start));
    }
}

sealed class NoKratosSessions : IKratosSessionValidator
{
    public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
}
