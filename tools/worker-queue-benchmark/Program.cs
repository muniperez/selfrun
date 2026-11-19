using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Worker;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Queue;
using Npgsql;

// Local worker claim-polling benchmark (issue #125). Runs the real PostgresDeploymentQueueConsumer
// against a throwaway PostgreSQL database with a fake pipeline executor. Synthetic data only.
var admin = Environment.GetEnvironmentVariable("WORKER_BENCH_POSTGRES")
    ?? throw new InvalidOperationException("Set WORKER_BENCH_POSTGRES, e.g. Host=127.0.0.1;Port=55432;Username=postgres;Database=postgres");
var scenario = Env("WORKER_BENCH_SCENARIO", "all");
var settings = new Settings(
    IdleSeconds: EnvInt("WORKER_BENCH_IDLE_SECONDS", 120),
    WarmupSeconds: EnvInt("WORKER_BENCH_WARMUP_SECONDS", 10),
    Deployments: EnvInt("WORKER_BENCH_DEPLOYMENTS", 40),
    Apps: EnvInt("WORKER_BENCH_APPS", 8),
    HistoryApps: EnvInt("WORKER_BENCH_HISTORY_APPS", 200),
    HistoryDeployments: EnvInt("WORKER_BENCH_HISTORY_DEPLOYMENTS", 2000),
    MeanGapMs: EnvInt("WORKER_BENCH_MEAN_GAP_MS", 1500),
    StepMs: EnvInt("WORKER_BENCH_STEP_MS", 250),
    PollMs: EnvInt("WORKER_BENCH_POLL_MS", 2000),
    Seed: EnvInt("WORKER_BENCH_SEED", 125),
    PostmasterPid: EnvInt("WORKER_BENCH_PG_PID", 0));

foreach (var name in scenario == "all" ? new[] { "idle", "latency" } : [scenario])
{
    var result = await RunAsync(name, admin, settings);
    Console.WriteLine(JsonSerializer.Serialize(result));
}

static async Task<Dictionary<string, object>> RunAsync(string scenario, string adminConnection, Settings s)
{
    var database = $"worker_bench_{Guid.NewGuid():N}"[..28];
    await ExecuteAdminAsync(adminConnection, $"CREATE DATABASE {database}");
    var connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database }.ConnectionString;
    try
    {
        var plain = new DbContextOptionsBuilder<MinicloudDbContext>().UseNpgsql(connection).Options;
        await SeedAsync(plain, s);

        var counter = new CommandCounter();
        var logs = new LogCounter();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Information);
            // console: the worker's default console provider, written to stderr (redirect it to /dev/null).
            if (Env("WORKER_BENCH_LOG", "count") == "console")
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            if (Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("WORKER_BENCH_EF_COMMAND_LOG"), out var efLevel))
                builder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", efLevel);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddDbContext<MinicloudDbContext>(options =>
        {
            MinicloudDatabase.ConfigureDbContext(options, "Postgres", connection);
            options.AddInterceptors(counter, counter.Transactions);
        });
        services.AddScoped<IDeploymentPipelineExecutor>(sp => new FakeExecutor(plain, s.StepMs));
        await using var provider = services.BuildServiceProvider();
        var consumer = new PostgresDeploymentQueueConsumer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions
            {
                WorkerId = "bench-worker",
                PollingInterval = TimeSpan.FromMilliseconds(s.PollMs),
                DeploymentTimeout = TimeSpan.FromMinutes(5)
            }),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<PostgresDeploymentQueueConsumer>>());

        // Warm up so JIT and EF model/query compilation are excluded from the measured window.
        await consumer.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(s.WarmupSeconds));
        var before = await SnapshotAsync(adminConnection, database, s.PostmasterPid);
        counter.Reset();
        logs.Reset();
        var stopwatch = Stopwatch.StartNew();
        var enqueued = new List<string>();
        if (scenario == "idle")
        {
            await Task.Delay(TimeSpan.FromSeconds(s.IdleSeconds));
        }
        else
        {
            var random = new Random(s.Seed);
            for (var i = 0; i < s.Deployments; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(-s.MeanGapMs * Math.Log(1 - random.NextDouble())));
                enqueued.Add(await EnqueueAsync(plain, i, s));
            }
            var deadline = Stopwatch.StartNew();
            while (await PendingAsync(plain) > 0)
            {
                if (deadline.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("Deployments did not finish.");
                await Task.Delay(100);
            }
        }
        stopwatch.Stop();
        var after = await SnapshotAsync(adminConnection, database, s.PostmasterPid);
        await consumer.StopAsync(CancellationToken.None);

        var seconds = stopwatch.Elapsed.TotalSeconds;
        var result = new Dictionary<string, object>
        {
            ["scenario"] = scenario,
            ["seconds"] = Math.Round(seconds, 1),
            ["poll_ms"] = s.PollMs,
            ["history_deployments"] = s.HistoryDeployments,
            ["client_commands"] = counter.Total,
            ["client_transactions"] = counter.Transactions.Started,
            ["commands_by_kind"] = counter.ByKind(),
            ["pg_xacts"] = after.Xacts - before.Xacts,
            ["pg_tuples_read"] = after.TuplesRead - before.TuplesRead,
            ["pg_blocks"] = after.Blocks - before.Blocks,
            ["pg_cpu_ms"] = s.PostmasterPid > 0 ? after.PostgresCpuMs - before.PostgresCpuMs : -1,
            ["worker_cpu_ms"] = Math.Round(after.ProcessCpuMs - before.ProcessCpuMs, 1),
            ["worker_alloc_mb"] = Math.Round((after.Allocated - before.Allocated) / 1048576.0, 2),
            ["log_lines"] = logs.Lines,
            ["log_kib"] = Math.Round(logs.Bytes / 1024.0, 1),
            ["log_lines_by_category"] = logs.ByCategory(),
        };
        if (scenario == "idle")
        {
            var perHour = 3600 / seconds;
            result["per_hour"] = new Dictionary<string, double>
            {
                ["client_commands"] = Math.Round(counter.Total * perHour),
                ["client_transactions"] = Math.Round(counter.Transactions.Started * perHour),
                ["pg_xacts"] = Math.Round((after.Xacts - before.Xacts) * perHour),
                ["pg_tuples_read"] = Math.Round((after.TuplesRead - before.TuplesRead) * perHour),
                ["pg_cpu_s"] = s.PostmasterPid > 0 ? Math.Round((after.PostgresCpuMs - before.PostgresCpuMs) * perHour / 1000, 2) : -1,
                ["worker_cpu_s"] = Math.Round((after.ProcessCpuMs - before.ProcessCpuMs) * perHour / 1000, 2),
                ["worker_alloc_mb"] = Math.Round((after.Allocated - before.Allocated) * perHour / 1048576.0, 1),
                ["log_lines"] = Math.Round(logs.Lines * perHour),
                ["log_mib"] = Math.Round(logs.Bytes * perHour / 1048576.0, 2),
            };
        }
        else
        {
            await using var db = new MinicloudDbContext(plain);
            var rows = await db.Deployments.AsNoTracking().Where(d => enqueued.Contains(d.Id))
                .Select(d => new { d.Id, d.CreatedAt, d.StartedAt, d.CompletedAt, d.Status }).ToListAsync();
            var events = await db.DeploymentEvents.AsNoTracking()
                .Where(e => enqueued.Contains(e.DeploymentId) && e.MetadataJson != null && e.MetadataJson.Contains("queue_wait"))
                .Select(e => e.MetadataJson!).ToListAsync();
            var waits = events.Select(json => JsonDocument.Parse(json).RootElement.GetProperty("duration_ms").GetDouble()).Order().ToArray();
            var claimToTerminal = rows.Where(r => r.StartedAt != null && r.CompletedAt != null)
                .Select(r => (r.CompletedAt!.Value - r.StartedAt!.Value).TotalMilliseconds).Order().ToArray();
            result["deployments"] = rows.Count;
            result["succeeded"] = rows.Count(r => r.Status == DeploymentStatus.Succeeded);
            result["claims_per_deployment"] = Math.Round(waits.Length / (double)rows.Count, 3);
            result["queue_wait_ms"] = Percentiles(waits);
            result["claim_to_terminal_ms"] = Percentiles(claimToTerminal);
            result["per_deployment"] = new Dictionary<string, double>
            {
                ["client_commands"] = Math.Round(counter.Total / (double)rows.Count, 1),
                ["worker_cpu_ms"] = Math.Round((after.ProcessCpuMs - before.ProcessCpuMs) / rows.Count, 1),
                ["pg_cpu_ms"] = s.PostmasterPid > 0 ? Math.Round((after.PostgresCpuMs - before.PostgresCpuMs) / (double)rows.Count, 1) : -1,
            };
        }
        return result;
    }
    finally
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(adminConnection, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
    }
}

static Dictionary<string, double> Percentiles(double[] sorted) => sorted.Length == 0
    ? new()
    : new()
    {
        ["p50"] = Math.Round(sorted[(int)Math.Ceiling(sorted.Length * 0.50) - 1], 1),
        ["p95"] = Math.Round(sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], 1),
        ["max"] = Math.Round(sorted[^1], 1),
    };

static async Task SeedAsync(DbContextOptions<MinicloudDbContext> options, Settings s)
{
    await using var db = new MinicloudDbContext(options);
    await db.Database.MigrateAsync();
    var now = DateTimeOffset.UtcNow;
    db.BootstrapRecords.Add(new BootstrapRecord { InstallationId = "bench", OperationId = "bench", State = "ready", CreatedAt = now, UpdatedAt = now });
    db.Organizations.Add(new Organization { Id = "org_bench", Name = "Bench", Slug = "bench", CreatedAt = now, UpdatedAt = now });
    for (var a = 0; a < Math.Max(s.Apps, s.HistoryApps); a++)
    {
        db.Apps.Add(new MinicloudApp { Id = $"app_{a}", OrganizationId = "org_bench", Name = $"App {a}", Slug = $"app-{a}",
            Plan = AppPlan.P0V0, Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now });
    }
    for (var d = 0; d < s.HistoryDeployments; d++)
    {
        var at = now.AddMinutes(-d - 10);
        db.Deployments.Add(new Deployment { Id = $"hist_{d}", OrganizationId = "org_bench", AppId = $"app_{d % Math.Max(1, s.HistoryApps)}",
            Status = d % 10 == 0 ? DeploymentStatus.Failed : DeploymentStatus.Succeeded, Database = DatabaseMode.None,
            CreatedAt = at, UpdatedAt = at.AddMinutes(1), StartedAt = at, CompletedAt = at.AddMinutes(1) });
    }
    await db.SaveChangesAsync();
    await db.Database.ExecuteSqlRawAsync("ANALYZE");
}

static async Task<string> EnqueueAsync(DbContextOptions<MinicloudDbContext> options, int index, Settings s)
{
    await using var db = new MinicloudDbContext(options);
    var now = DateTimeOffset.UtcNow;
    var id = $"bench_{index}";
    db.Deployments.Add(new Deployment { Id = id, OrganizationId = "org_bench", AppId = $"app_{index % s.Apps}",
        Status = DeploymentStatus.Queued, Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now });
    await db.SaveChangesAsync();
    return id;
}

static async Task<int> PendingAsync(DbContextOptions<MinicloudDbContext> options)
{
    await using var db = new MinicloudDbContext(options);
    return await db.Deployments.CountAsync(d => d.Id.StartsWith("bench_") && d.CompletedAt == null);
}

static async Task ExecuteAdminAsync(string connection, string sql)
{
    await using var conn = new NpgsqlConnection(connection);
    await conn.OpenAsync();
    await using var command = new NpgsqlCommand(sql, conn);
    await command.ExecuteNonQueryAsync();
}

static async Task<Snapshot> SnapshotAsync(string adminConnection, string database, int postmasterPid)
{
    // Ending pooled backends flushes their cumulative statistics before pg_stat_database is read.
    NpgsqlConnection.ClearAllPools();
    await Task.Delay(1500);
    await using var conn = new NpgsqlConnection(adminConnection);
    await conn.OpenAsync();
    await using var command = new NpgsqlCommand(
        "SELECT xact_commit + xact_rollback, tup_returned + tup_fetched, blks_hit + blks_read FROM pg_stat_database WHERE datname = @d", conn);
    command.Parameters.AddWithValue("d", database);
    await using var reader = await command.ExecuteReaderAsync();
    await reader.ReadAsync();
    var process = Process.GetCurrentProcess();
    return new Snapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
        process.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(true), PostgresCpuMs(postmasterPid));
}

// Postmaster self + reaped children (cutime/cstime) + live children, in milliseconds (assumes 100 Hz ticks).
static long PostgresCpuMs(int postmasterPid)
{
    if (postmasterPid <= 0) return 0;
    long ticks = 0;
    foreach (var dir in Directory.EnumerateDirectories("/proc"))
    {
        if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
        string stat;
        try { stat = File.ReadAllText(Path.Combine(dir, "stat")); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        var ppid = int.Parse(fields[1], CultureInfo.InvariantCulture);
        if (pid == postmasterPid)
            ticks += long.Parse(fields[11]) + long.Parse(fields[12]) + long.Parse(fields[13]) + long.Parse(fields[14]);
        else if (ppid == postmasterPid)
            ticks += long.Parse(fields[11]) + long.Parse(fields[12]);
    }
    return ticks * 10;
}

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

internal sealed record Settings(int IdleSeconds, int WarmupSeconds, int Deployments, int Apps, int HistoryApps, int HistoryDeployments,
    int MeanGapMs, int StepMs, int PollMs, int Seed, int PostmasterPid);

internal sealed record Snapshot(long Xacts, long TuplesRead, long Blocks, double ProcessCpuMs, long Allocated, long PostgresCpuMs);

// Completes the claimed deployment the way the real pipeline's terminal transition does, without remote calls.
internal sealed class FakeExecutor(DbContextOptions<MinicloudDbContext> options, int stepMs) : IDeploymentPipelineExecutor
{
    public async Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        await Task.Delay(stepMs, cancellationToken);
        await using var db = new MinicloudDbContext(options);
        var now = DateTimeOffset.UtcNow;
        await db.Deployments.Where(d => d.Id == deployment.Id && d.OwnershipToken == deployment.OwnershipToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(d => d.Status, DeploymentStatus.Succeeded)
                .SetProperty(d => d.CompletedAt, now)
                .SetProperty(d => d.UpdatedAt, now)
                .SetProperty(d => d.WorkerId, (string?)null)
                .SetProperty(d => d.OwnershipToken, (string?)null)
                .SetProperty(d => d.OwnershipAcquiredAt, (DateTimeOffset?)null), cancellationToken);
    }
}

internal sealed partial class CommandCounter : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<string, int> _kinds = new();
    private int _total;
    public TransactionCounter Transactions { get; } = new();
    public int Total => Volatile.Read(ref _total);

    public void Reset()
    {
        _kinds.Clear();
        Interlocked.Exchange(ref _total, 0);
        Transactions.Reset();
    }

    public SortedDictionary<string, int> ByKind() => new(_kinds);

    private void Count(DbCommand command)
    {
        Interlocked.Increment(ref _total);
        var sql = command.CommandText;
        var verb = Verb().Match(sql) is { Success: true } v ? v.Value.ToLowerInvariant() : "other";
        var table = verb == "select" && Table().Match(sql) is { Success: true } t ? t.Groups[1].Value
            : TargetTable().Match(sql) is { Success: true } target ? target.Groups[1].Value : "";
        var locking = sql.Contains("SKIP LOCKED", StringComparison.Ordinal) ? " skip-locked"
            : sql.Contains("FOR SHARE", StringComparison.Ordinal) ? " for-share" : "";
        _kinds.AddOrUpdate($"{verb} {table}{locking}".Trim(), 1, (_, n) => n + 1);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r) { Count(c); return r; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Count(c); return ValueTask.FromResult(r); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r) { Count(c); return r; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Count(c); return ValueTask.FromResult(r); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand c, CommandEventData e, InterceptionResult<object> r) { Count(c); return r; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<object> r, CancellationToken ct = default) { Count(c); return ValueTask.FromResult(r); }

    [GeneratedRegex(@"^\s*(\w+)")] private static partial Regex Verb();
    [GeneratedRegex(@"\bFROM\s+""?(\w+)""?", RegexOptions.IgnoreCase)] private static partial Regex Table();
    [GeneratedRegex(@"^\s*(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+""?(\w+)""?", RegexOptions.IgnoreCase)] private static partial Regex TargetTable();
}

internal sealed class TransactionCounter : DbTransactionInterceptor
{
    private int _started;
    public int Started => Volatile.Read(ref _started);
    public void Reset() => Interlocked.Exchange(ref _started, 0);
    public override DbTransaction TransactionStarted(DbConnection c, TransactionEndEventData e, DbTransaction t) { Interlocked.Increment(ref _started); return t; }
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection c, TransactionEndEventData e, DbTransaction t, CancellationToken ct = default) { Interlocked.Increment(ref _started); return ValueTask.FromResult(t); }
}

internal sealed class LogCounter : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, int> _categories = new();
    private int _lines;
    private long _bytes;
    public int Lines => Volatile.Read(ref _lines);
    public long Bytes => Interlocked.Read(ref _bytes);
    public void Reset() { _categories.Clear(); Interlocked.Exchange(ref _lines, 0); Interlocked.Exchange(ref _bytes, 0); }
    public SortedDictionary<string, int> ByCategory() => new(_categories);
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() { }

    private sealed class Logger(LogCounter owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            Interlocked.Add(ref owner._bytes, System.Text.Encoding.UTF8.GetByteCount(formatter(state, exception)));
            Interlocked.Increment(ref owner._lines);
            owner._categories.AddOrUpdate(category, 1, (_, n) => n + 1);
        }
    }
}
