using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Selfrun.Api.Controllers;
using Selfrun.Common.Contracts;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Services;
using Npgsql;
using Xunit.Abstractions;

namespace Selfrun.Tests;

[Collection("Runtime ingestion diagnostics")]
public sealed class RuntimeLogIngestionPostgresTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MaximumBatchesKeepCountsAndSourceAcknowledgmentsOnReplay()
    {
        await WithPostgres(async options =>
        {
            var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var seed = await SeedAsync(options, observedAt);
            var initial = Enumerable.Range(0, 500).Select(i => Record(i, observedAt)).ToArray();
            var mixed = initial.Skip(250).Concat(Enumerable.Range(500, 200).Select(i => Record(i, observedAt)))
                .Concat(Enumerable.Range(700, 50).Select(i => Record(i, observedAt) with { Source = "service:unknown", Service = "unknown" }))
                .ToArray();
            var samples = new BatchObserver();
            using var listener = samples.Listen();

            for (var run = 0; run <= 5; run++)
            {
                var elapsed = TimeSpan.FromSeconds(run);
                var fresh = initial.Select(record => record with { ObservedAt = record.ObservedAt.Add(elapsed) }).ToArray();
                var overlap = mixed.Select(record => record with { ObservedAt = record.ObservedAt.Add(elapsed) }).ToArray();

                await Measure("fresh", fresh, 500, 0, "498", "499");
                await Measure("mixed", overlap, 200, 50, "698", "699");
                await Measure("retry", fresh, 0, 0, "498", "499");

                async Task Measure(string name, RuntimeLogIngestRecord[] records, int accepted, int rejected, string caddy, string api)
                {
                    var (response, sample) = await IngestAsync(options, seed.Token, records, samples);
                    CheckResponse(response, accepted, rejected, caddy, api);
                    Assert.Equal(1, sample.PruneCalls);
                    Assert.InRange(sample.Commands, 1, 8);
                    if (run > 0)
                    {
                        output.WriteLine(FormattableString.Invariant(
                            $"{name} {run}: commands={sample.Commands}; duplicates={(500 - accepted - rejected) / 500d:P0}; accepted={accepted}; rejected={rejected}; caddy={caddy}; service:api={api}; total_ms={sample.Elapsed.TotalMilliseconds:F3}; prune_ms={sample.PruneMilliseconds:F3}"));
                    }
                }
            }

            await using var verify = new SelfrunDbContext(options);
            Assert.Equal(6 * 700, await verify.RuntimeLogRecords.CountAsync(x => x.RuntimeServerId == seed.ServerId));
        });
    }

    [Fact]
    public async Task ConcurrentOverlappingRetriesAcknowledgeEveryEligibleSourceAndWriteEachKeyOnce()
    {
        await WithPostgres(async options =>
        {
            var ticks = DateTimeOffset.UtcNow.AddMinutes(-1).Ticks;
            var observedAt = new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
            var seed = await SeedAsync(options, observedAt);
            var otherTenant = await SeedAsync(options, observedAt);
            var records = Enumerable.Range(0, 12).Select(i => Record(i, observedAt)).ToArray();
            var observer = new BatchObserver();
            var arrivalCount = 0;
            var bothPruning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            observer.BeforePrune = async cancellationToken =>
            {
                if (Interlocked.Increment(ref arrivalCount) == 2)
                    bothPruning.SetResult();
                await bothPruning.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            };

            // Both requests must finish checking for existing records before either can write.
            var first = IngestAsync(options, seed.Token, records.Take(10).ToArray(), observer);
            var second = IngestAsync(options, seed.Token, records.Skip(2).Reverse().ToArray(), observer);
            var results = await Task.WhenAll(first, second);
            Assert.Equal(12, results.Sum(result => result.Response.Accepted));
            Assert.InRange(results[0].Response.Accepted, 2, 10);
            Assert.InRange(results[1].Response.Accepted, 2, 10);
            CheckResponse(results[0].Response, results[0].Response.Accepted, 0, "8", "9");
            CheckResponse(results[1].Response, results[1].Response.Accepted, 0, "2", "3");

            var retry = await IngestAsync(options, seed.Token, records, new BatchObserver());
            CheckResponse(retry.Response, 0, 0, "10", "11");
            var sameDatabaseTimestamp = records[0] with { ObservedAt = observedAt.AddTicks(1), Message = "duplicate" };
            var independent = await IngestAsync(options, otherTenant.Token, [records[0], sameDatabaseTimestamp, .. records.Skip(1)], new BatchObserver());
            CheckResponse(independent.Response, 12, 0, "10", "11");

            await using var verify = new SelfrunDbContext(options);
            Assert.Equal(12, await verify.RuntimeLogRecords.CountAsync(x => x.RuntimeServerId == seed.ServerId));
            Assert.Equal(12, await verify.RuntimeLogRecords.CountAsync(x => x.RuntimeServerId == otherTenant.ServerId));
            var stored = await verify.RuntimeLogRecords.SingleAsync(x => x.RuntimeServerId == otherTenant.ServerId && x.Cursor == "0");
            Assert.Equal(records[0].Message, stored.Content);
            Assert.Equal(otherTenant.AppId, stored.AppId);
            Assert.Equal(otherTenant.OrganizationId, stored.OrganizationId);
        });
    }

    private static RuntimeLogIngestRecord Record(int i, DateTimeOffset observedAt) =>
        i % 2 == 0 ? new("caddy", null, "stdout", observedAt, $"{i}", $"synthetic caddy event {i}")
            : new("service:api", "api", "stderr", observedAt, $"{i}", $"synthetic api event {i}");

    private static void CheckResponse(RuntimeLogIngestResponse response, int accepted, int rejected, string caddy, string api)
    {
        Assert.Equal(accepted, response.Accepted);
        Assert.Equal(rejected, response.Rejected);
        Assert.Equal(2, response.LastAcceptedCursorBySource.Count);
        Assert.Equal(caddy, response.LastAcceptedCursorBySource["caddy"]);
        Assert.Equal(api, response.LastAcceptedCursorBySource["service:api"]);
    }

    private static async Task<(RuntimeLogIngestResponse Response, BatchSample Sample)> IngestAsync(
        DbContextOptions<SelfrunDbContext> options, string token, RuntimeLogIngestRecord[] records, BatchObserver observer)
    {
        await using var db = new SelfrunDbContext(new DbContextOptionsBuilder<SelfrunDbContext>(options).AddInterceptors(observer).Options);
        var controller = new RuntimeController { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.Authorization = $"Bearer {token}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sample = observer.Start();
        var start = Stopwatch.GetTimestamp();
        var result = await controller.IngestLogs(new(records), db, new RuntimeTokenService(db, TimeProvider.System),
            new IdGenerator(), TimeProvider.System, timeout.Token);
        sample.Elapsed = Stopwatch.GetElapsedTime(start);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        return (Assert.IsType<RuntimeLogIngestResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value), sample);
    }

    private static async Task<(string Token, string ServerId, string AppId, string OrganizationId)> SeedAsync(
        DbContextOptions<SelfrunDbContext> options, DateTimeOffset now)
    {
        await using var db = new SelfrunDbContext(options);
        var key = Guid.NewGuid().ToString("N");
        var organization = new Organization { Id = $"org-{key}", Name = key, Slug = key, CreatedAt = now, UpdatedAt = now };
        var app = new SelfrunApp { Id = $"app-{key}", OrganizationId = organization.Id, Name = key, Slug = key, CreatedAt = now, UpdatedAt = now };
        var deployment = new Deployment { Id = $"dep-{key}", AppId = app.Id, OrganizationId = organization.Id,
            Status = DeploymentStatus.Succeeded, CreatedAt = now, UpdatedAt = now };
        deployment.Services.Add(new DeploymentService
        {
            Id = $"svc-{key}", DeploymentId = deployment.Id, Name = "api", Image = "example.invalid/api", Path = "/", HealthPath = "/health"
        });
        var server = new RuntimeServer { Id = $"server-{key}", AppId = app.Id, OrganizationId = organization.Id, Provider = "hetzner",
            ProviderServerId = key, CreatedAt = now, UpdatedAt = now };
        var service = new RuntimeTokenService(db, TimeProvider.System);
        var token = service.Generate();

        db.AddRange(organization, app, deployment, server, new RuntimeToken
        {
            Id = $"token-{key}", RuntimeServerId = server.Id, TokenPrefix = token.Prefix, TokenHash = service.Hash(token.Token),
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (token.Token, server.Id, app.Id, organization.Id);
    }

    private static async Task WithPostgres(Func<DbContextOptions<SelfrunDbContext>, Task> test)
    {
        var local = Environment.GetEnvironmentVariable("SELFRUN_RUNTIME_LOG_TEST_CONNECTION");
        if (local is not null)
        {
            var connection = new NpgsqlConnectionStringBuilder(local);
            Assert.Equal("127.0.0.1", connection.Host);
            Assert.Equal("runtime_log_test", connection.Database);
            await Run(connection.ConnectionString);
        }
        else
        {
            var container = $"mc-runtime-log-{Guid.NewGuid():N}";
            await Docker("run", "--rm", "-d", "--name", container, "-e", $"POSTGRES_PASSWORD={container}",
                "-e", "POSTGRES_DB=runtime_log_test", "-p", "127.0.0.1::5432", "postgres:16-alpine");
            try
            {
                var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
                await Run($"Host=127.0.0.1;Port={port};Database=runtime_log_test;Username=postgres;Password={container}");
            }
            finally
            {
                await Docker("rm", "-f", container);
            }
        }

        async Task Run(string connection)
        {
            var options = new DbContextOptionsBuilder<SelfrunDbContext>().UseNpgsql(connection).Options;
            await using var setup = new SelfrunDbContext(options);
            for (var attempt = 0; attempt < 60 && !await setup.Database.CanConnectAsync(); attempt++)
                await Task.Delay(250);
            await setup.Database.MigrateAsync();
            await test(options);
        }
    }

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private sealed class BatchSample
    {
        public int Commands { get; set; }
        public int PruneCalls { get; set; }
        public double PruneMilliseconds { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    private sealed class BatchObserver : DbCommandInterceptor
    {
        private readonly AsyncLocal<BatchSample?> _current = new();
        public Func<CancellationToken, Task>? BeforePrune { get; set; }

        public BatchSample Start() => _current.Value = new();

        public MeterListener Listen()
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, current) =>
                {
                    if (instrument.Meter.Name == "Selfrun.RuntimeIngestion" &&
                        instrument.Name == "selfrun.runtime.ingestion.logs.prune.duration")
                        current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, seconds, _, _) =>
            {
                if (_current.Value is { } sample)
                {
                    sample.PruneCalls++;
                    sample.PruneMilliseconds += seconds * 1000;
                }
            });
            listener.Start();
            return listener;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (_current.Value is { } sample) sample.Commands++;
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_current.Value is { } sample) sample.Commands++;
            if (BeforePrune is not null && command.CommandText.StartsWith("DELETE FROM runtime_log_records", StringComparison.Ordinal))
                await BeforePrune(cancellationToken);
            return result;
        }
    }
}
