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
using Minicloud.Common.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Minicloud.Worker.Tests;

public sealed class OperatorDiagnosticsTests
{
    [Fact]
    public async Task SyntheticRequestProducesSafeCorrelatedHttpAndDatabaseMeasurements()
    {
        const string secret = "synthetic-secret-DO-NOT-EXPORT";
        var spans = new ConcurrentBag<(string Name, string TraceId, string SpanId, string Parent, string Tags)>();
        var measurements = new ConcurrentBag<(string Name, string Tags)>();
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
        builder.Services.AddOperatorDiagnostics(builder.Configuration, "test", true);
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new CaptureProcessor(spans)));
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        builder.Services.AddDbContext<TestDb>((provider, options) => options.UseSqlite(connection)
            .AddInterceptors(provider.GetRequiredService<DbCommandDiagnosticsInterceptor>()));
        await using var app = builder.Build();
        app.MapGet("/synthetic/{id}", async (TestDb db) =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT {secret}");
            return Results.Ok();
        });
        app.MapGet("/health", () => Results.Ok());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{address}/synthetic/{secret}?token={secret}");
        request.Headers.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        using (SuppressInstrumentationScope.Begin())
            Assert.True((await client.SendAsync(request)).IsSuccessStatusCode);
        using var health = new HttpRequestMessage(HttpMethod.Get, $"{address}/health");
        health.Headers.Add("traceparent", "00-fedcba9876543210fedcba9876543210-0123456789abcdef-01");
        using (SuppressInstrumentationScope.Begin())
            await client.SendAsync(health);
        await app.StopAsync();

        Assert.Contains(measurements, m => m.Name == "http.server.request.duration" && m.Tags.Contains("/synthetic/{id}"));
        Assert.Contains(measurements, m => m.Name == "minicloud.db.command.duration" && m.Tags.Contains("outcome=success"));
        var server = Assert.Single(spans, s => s.Name == "HTTP" && s.TraceId == "0123456789abcdef0123456789abcdef");
        var dbSpan = Assert.Single(spans, s => s.Name == "db.command" && s.TraceId == server.TraceId);
        Assert.Equal(server.SpanId, dbSpan.Parent);
        Assert.DoesNotContain(spans, span => span.TraceId == "fedcba9876543210fedcba9876543210");
        Assert.DoesNotContain(spans, span => span.Tags.Contains(secret));
        Assert.DoesNotContain(spans, span => span.Tags.Contains("url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RawCommandOutcomesAreBounded()
    {
        var outcomes = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OperatorDiagnostics.Name && instrument.Name == "minicloud.db.commands")
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "outcome") outcomes.Add((string)tag.Value!);
        });
        listener.Start();
        await OperatorDiagnostics.ExecuteCommandAsync("test", () => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OperatorDiagnostics.ExecuteCommandAsync<int>("test", () => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => OperatorDiagnostics.ExecuteCommandAsync<int>("test", () => throw new OperationCanceledException()));
        Assert.Equal(new[] { "success", "error", "cancelled" }, outcomes);
    }

    private sealed class TestDb(DbContextOptions<TestDb> options) : DbContext(options);

    private sealed class CaptureProcessor(ConcurrentBag<(string Name, string TraceId, string SpanId, string Parent, string Tags)> spans) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => spans.Add((activity.DisplayName, activity.TraceId.ToString(),
            activity.SpanId.ToString(), activity.ParentSpanId.ToString(),
            string.Join(";", activity.TagObjects.Select(t => $"{t.Key}={t.Value}"))));
    }
}
