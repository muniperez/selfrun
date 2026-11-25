using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Selfrun.Common.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Selfrun.Worker.Tests;

public sealed class OperatorDiagnosticsTests
{
    [Fact]
    public async Task SyntheticRequestProducesSafeCorrelatedHttpAndDatabaseMeasurements()
    {
        const string secret = "synthetic-secret-DO-NOT-EXPORT";
        var spans = new ConcurrentBag<(string Name, string TraceId, string SpanId, string Parent, string Tags, string? TraceState)>();
        var measurements = new ConcurrentBag<(string Name, string Tags)>();
        var exportedMetrics = new ConcurrentBag<(string Name, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name is OperatorDiagnostics.Name or "Microsoft.AspNetCore.Hosting")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, string.Join(";", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, string.Join(";", tags.ToArray().Select(t => $"{t.Key}={t.Value}")))));
        listener.Start();

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OperatorDiagnostics:Enabled"] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOperatorDiagnostics(builder.Configuration, "test", tracing =>
            tracing.AddAspNetCoreInstrumentation(options => options.Filter = context =>
                OperatorDiagnostics.IsTracedServerPath(context.Request.Path.Value)));
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new CaptureProcessor(spans)));
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics =>
            metrics.AddReader(new PeriodicExportingMetricReader(new CaptureMetricExporter(exportedMetrics), 60000)));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        builder.Services.AddDbContext<TestDb>((provider, options) => options.UseSqlite(connection)
            .AddInterceptors(provider.GetRequiredService<DbCommandDiagnosticsInterceptor>()));
        await using var app = builder.Build();
        app.MapGet("/synthetic/{id}", async (TestDb db) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT {secret}");
            try { await db.Database.ExecuteSqlRawAsync("SELECT nonexistent_synthetic_secret"); }
            catch (SqliteException) { }
            return Results.Ok();
        });
        app.MapGet("/health", () => Results.Ok());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/synthetic/{secret}?token={secret}");
        request.Headers.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        request.Headers.Add("tracestate", $"vendor={secret}");
        using (SuppressInstrumentationScope.Begin())
            Assert.True((await client.SendAsync(request)).IsSuccessStatusCode);
        using var health = new HttpRequestMessage(HttpMethod.Get, $"{address}/health");
        health.Headers.Add("traceparent", "00-fedcba9876543210fedcba9876543210-0123456789abcdef-01");
        using (SuppressInstrumentationScope.Begin())
            await client.SendAsync(health);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush();
        await app.StopAsync();

        Assert.Contains(measurements, m => m.Name == "http.server.request.duration" && m.Tags.Contains("/synthetic/{id}"));
        Assert.Contains(measurements, m => m.Name == "selfrun.db.command.duration" && m.Tags.Contains("outcome=success"));
        Assert.Contains(measurements, m => m.Name == "selfrun.db.command.duration" && m.Tags.Contains("outcome=error"));
        Assert.Contains(exportedMetrics, m => m.Name == "http.server.request.duration" && m.Tags.Contains("/synthetic/{id}"));
        Assert.Contains(exportedMetrics, m => m.Name == "dotnet.gc.heap.total_allocated");
        Assert.Contains(exportedMetrics, m => m.Name == "dotnet.gc.pause.time");
        Assert.Contains(exportedMetrics, m => m.Name == "dotnet.thread_pool.queue.length");
        Assert.Contains(exportedMetrics, m => m.Name == "dotnet.process.cpu.time");
        Assert.Contains(exportedMetrics, m => m.Name == "dotnet.process.memory.working_set");
        Assert.DoesNotContain(exportedMetrics, metric => metric.Tags.Contains(secret));
        var server = Assert.Single(spans, s => s.Name == "HTTP" && s.TraceId == "0123456789abcdef0123456789abcdef");
        var dbSpans = spans.Where(s => s.Name == "db.command" && s.TraceId == server.TraceId).ToArray();
        Assert.Equal(2, dbSpans.Length);
        Assert.All(dbSpans, span => Assert.Equal(server.SpanId, span.Parent));
        Assert.DoesNotContain(spans, span => span.TraceId == "fedcba9876543210fedcba9876543210");
        Assert.DoesNotContain(spans, span => span.Tags.Contains(secret));
        Assert.DoesNotContain(spans, span => span.Tags.Contains("url", StringComparison.OrdinalIgnoreCase));
        Assert.All(spans, span => Assert.True(string.IsNullOrEmpty(span.TraceState)));
    }

    [Theory]
    [InlineData("/health", false)]
    [InlineData("/HEALTH/ready", false)]
    [InlineData("/v1/traces", false)]
    [InlineData("/healthz", true)]
    [InlineData("/v1/tracesx", true)]
    [InlineData("/api/apps", true)]
    public void ServerPathFilterExcludesInfrastructureSegments(string path, bool traced) =>
        Assert.Equal(traced, OperatorDiagnostics.IsTracedServerPath(path));

    [Fact]
    public async Task RawCommandOutcomesAreBounded()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperatorDiagnostics.Name && instrument.Name == "selfrun.db.commands")
                meterListener.EnableMeasurementEvents(instrument);
        };
        // The meter is process-wide and other tests record commands concurrently; count only this test's operation.
        const string operation = "test.raw-command-outcomes";
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? recordedOperation = null, outcome = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation") recordedOperation = tag.Value as string;
                else if (tag.Key == "outcome") outcome = tag.Value as string;
            }
            if (recordedOperation == operation && outcome is not null)
                lock (outcomes) outcomes.Add(outcome);
        });
        listener.Start();
        await OperatorDiagnostics.ExecuteCommandAsync(operation, () => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorDiagnostics.ExecuteCommandAsync<int>(operation, () => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => OperatorDiagnostics.ExecuteCommandAsync<int>(operation, () => throw new OperationCanceledException()));
        Assert.Equal(new[] { "success", "error", "cancelled" }, outcomes);
    }

    private sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options);

    private sealed class CaptureProcessor(ConcurrentBag<(string Name, string TraceId, string SpanId, string Parent, string Tags, string? TraceState)> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => spans.Add((activity.DisplayName, activity.TraceId.ToString(),
            activity.SpanId.ToString(), activity.ParentSpanId.ToString(),
            string.Join(";", activity.TagObjects.Select(t => $"{t.Key}={t.Value}")), activity.TraceStateString));
    }

    private sealed class CaptureMetricExporter(ConcurrentBag<(string Name, string Tags)> metrics) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new List<string>();
                    foreach (var tag in point.Tags) tags.Add($"{tag.Key}={tag.Value}");
                    metrics.Add((metric.Name, string.Join(";", tags)));
                }
            return ExportResult.Success;
        }
    }
}
