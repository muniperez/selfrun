using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Telemetry.Data;
using Minicloud.Telemetry.Services;

namespace Minicloud.Tests;

public sealed class TelemetryQuotaTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ControlledClock _clock = new();
    private readonly SaveBarrier _barrier = new();
    private QuotaFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new QuotaFactory(_connection, _clock, _barrier);
        _factory.CreateClient().Dispose();
    }

    [Fact]
    public async Task RequestScopes_ShareRateState_Refill_AndIsolateApps()
    {
        Configure(burst: 100, sustained: 10);
        Assert.True((await Reserve("app-a", "org-a", 100)).Admitted);
        var denied = await Reserve("app-a", "org-a", 20);
        Assert.Equal(new TelemetryQuotaDecision(false, "Per-app ingest rate exceeded.", TimeSpan.FromSeconds(2)), denied);
        Assert.True((await Reserve("app-b", "org-b", 100)).Admitted);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await Reserve("app-a", "org-a", 20)).Admitted);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await Reserve("app-a", "org-a", 20)).Admitted);

        Assert.Equal(120, (await Usage()).Single(x => x.AppId == "app-a").AdmittedBytes);
        Assert.Equal(100, (await Usage()).Single(x => x.AppId == "app-b").AdmittedBytes);

        Assert.True((await Reserve("idle-app", "org-a", 0)).Admitted);
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True((await Reserve("idle-app", "org-a", 100)).Admitted);
        Assert.Equal("Per-app ingest rate exceeded.", (await Reserve("idle-app", "org-a", 1)).Reason);
    }

    [Fact]
    public async Task Capacity_DeniesNewApps_AndReclaimsOnlyFullyRefilledState()
    {
        Configure(burst: 100, sustained: 10, appDaily: 0);
        for (var i = 0; i < 4096; i++)
        {
            var denied = await Reserve($"app-{i}", "org", 100);
            Assert.Equal("Per-app daily admission budget exceeded.", denied.Reason);
        }

        var capacity = await Reserve("new-app", "org", 100);
        Assert.False(capacity.Admitted);
        Assert.Contains("tracking capacity", capacity.Reason);
        Assert.Equal(TimeSpan.FromSeconds(10), capacity.RetryAfter);
        Assert.Equal("Per-app ingest rate exceeded.", (await Reserve("app-0", "org", 1)).Reason);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("Per-app daily admission budget exceeded.", (await Reserve("app-0", "org", 50)).Reason);
        capacity = await Reserve("new-app", "org", 100);
        Assert.Contains("tracking capacity", capacity.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), capacity.RetryAfter);

        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("Per-app daily admission budget exceeded.", (await Reserve("new-app", "org", 100)).Reason);
        var retained = await Reserve("app-0", "org", 100);
        Assert.Equal("Per-app ingest rate exceeded.", retained.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), retained.RetryAfter);
        Assert.Equal("Per-app ingest rate exceeded.", (await Reserve("new-app", "org", 1)).Reason);
        Assert.Empty(await Usage());
    }

    [Theory]
    [InlineData("app", "Per-app daily admission budget exceeded.")]
    [InlineData("organization", "Per-organization daily admission budget exceeded.")]
    [InlineData("global", "Whole-stack daily admission budget exceeded.")]
    public async Task OverlappingRequestScopes_SerializeDailyTotals(string budget, string reason)
    {
        Configure(
            burst: 1000, sustained: 100,
            appDaily: budget == "app" ? 300 : 10000,
            orgDaily: budget == "organization" ? 300 : 10000,
            globalDaily: budget == "global" ? 300 : 10000);
        _barrier.Enable();
        var first = Reserve("app-0", "org-0", 100);
        await _barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<TelemetryQuotaDecision>[] pending;
        try
        {
            pending = Enumerable.Range(1, 9)
                .Select(i => Reserve(
                    budget == "app" ? "app-0" : $"app-{i}",
                    budget == "global" ? $"org-{i}" : "org-0", 100))
                .Prepend(first)
                .ToArray();
        }
        finally
        {
            _barrier.Release.TrySetResult(true);
        }

        var results = await Task.WhenAll(pending);
        Assert.Equal(3, results.Count(x => x.Admitted));
        Assert.All(results.Where(x => !x.Admitted), x =>
        {
            Assert.Equal(reason, x.Reason);
            Assert.Equal(TimeSpan.FromHours(12), x.RetryAfter);
        });
        var rows = await Usage();
        Assert.Equal(300, rows.Sum(x => x.AdmittedBytes));
        Assert.All(rows, row => Assert.Equal(
            budget == "global" ? row.AppId.Replace("app-", "org-") : "org-0", row.OrganizationId));
        if (budget == "app")
            Assert.Equal(300, Assert.Single(rows).AdmittedBytes);
        else if (budget == "organization")
            Assert.Equal(300, rows.Where(x => x.OrganizationId == "org-0").Sum(x => x.AdmittedBytes));
    }

    [Fact]
    public async Task OwnershipAndInputRejections_DoNotChangePersistedUsage()
    {
        Configure(burst: 100, sustained: 10);
        Assert.Equal(new TelemetryQuotaDecision(false, "Invalid byte count.", null),
            await Reserve("app", "org", -1));
        var oversized = await Reserve("app", "org", 101);
        Assert.False(oversized.Admitted);
        Assert.Contains("per-request burst budget", oversized.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), oversized.RetryAfter);
        Assert.True((await Reserve("app", "org", 10)).Admitted);
        Assert.Equal(new TelemetryQuotaDecision(false, "Telemetry budget ownership mismatch.", null),
            await Reserve("app", "other-org", 10));
        var row = Assert.Single(await Usage());
        Assert.Equal("org", row.OrganizationId);
        Assert.Equal(10, row.AdmittedBytes);
    }

    private void Configure(long burst, long sustained, long appDaily = 10000, long orgDaily = 10000, long globalDaily = 10000)
    {
        var options = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelemetryOptions>>().Value;
        options.PerAppBurstBytes = burst;
        options.PerAppSustainedBytesPerSecond = sustained;
        options.PerAppDailyBytes = appDaily;
        options.PerOrganizationDailyBytes = orgDaily;
        options.WholeStackDailyBytes = globalDaily;
    }

    private async Task<TelemetryQuotaDecision> Reserve(string app, string org, long bytes)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TelemetryQuotaService>()
            .ReserveAsync(app, org, bytes, CancellationToken.None);
    }

    private async Task<List<TelemetryDailyUsage>> Usage()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TelemetryDbContext>()
            .DailyUsages.AsNoTracking().ToListAsync();
    }

    public async Task DisposeAsync()
    {
        _barrier.Release.TrySetResult(true);
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class ControlledClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delay) => _now += delay;
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _enabled;
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enable() => _enabled = 1;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _enabled, 0) == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class QuotaFactory(SqliteConnection connection, TimeProvider clock, SaveBarrier barrier)
        : WebApplicationFactory<Minicloud.Telemetry.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Telemetry"] = "Data Source=:memory:" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                services.RemoveAll<DbContextOptions<TelemetryDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TelemetryDbContext>>();
                services.AddDbContext<TelemetryDbContext>(options => options.UseSqlite(connection).AddInterceptors(barrier));
            });
        }
    }
}
