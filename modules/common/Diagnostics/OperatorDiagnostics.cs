using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Minicloud.Common.Diagnostics;

public static class OperatorDiagnostics
{
    public const string Name = "Minicloud.Operator";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("minicloud.db.commands", "{command}");
    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>("minicloud.db.command.duration", "s");
    public static IServiceCollection AddOperatorDiagnostics(
        this IServiceCollection services, IConfiguration configuration, string serviceName, bool webHost)
    {
        if (!configuration.GetValue<bool>("OperatorDiagnostics:Enabled"))
            return services;

        services.AddSingleton<DbCommandDiagnosticsInterceptor>();
        services.AddHostedService<DiagnosticsSummaryService>();
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(Name, "Microsoft.AspNetCore.Hosting", "System.Net.Http")
                    .AddRuntimeInstrumentation()
                    .AddView(instrument => (instrument.Meter.Name, instrument.Name) switch
                    {
                        (Name, "minicloud.db.commands" or "minicloud.db.command.duration") =>
                            new MetricStreamConfiguration { TagKeys = ["operation", "outcome"] },
                        ("Microsoft.AspNetCore.Hosting", "http.server.request.duration") =>
                            new MetricStreamConfiguration { TagKeys = ["http.request.method", "http.response.status_code", "http.route", "network.protocol.version"] },
                        ("System.Net.Http", "http.client.request.duration") =>
                            new MetricStreamConfiguration { TagKeys = ["http.request.method", "http.response.status_code", "network.protocol.version"] },
                        ("System.Runtime", "dotnet.process.cpu.time") =>
                            new MetricStreamConfiguration { TagKeys = ["cpu.mode"] },
                        ("System.Runtime", "dotnet.process.memory.working_set" or "dotnet.gc.heap.total_allocated" or "dotnet.gc.pause.time" or "dotnet.thread_pool.queue.length") =>
                            new MetricStreamConfiguration { TagKeys = [] },
                        _ => MetricStreamConfiguration.Drop
                    })
                    .AddOtlpExporter();
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(Name)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)))
                    .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request =>
                        !IsInfrastructure(request.RequestUri, configuration))
                    .AddProcessor(new SafeActivityProcessor())
                    .AddOtlpExporter();
                if (webHost)
                    tracing.AddAspNetCoreInstrumentation(options => options.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/health")
                        && !context.Request.Path.StartsWithSegments("/v1/logs")
                        && !context.Request.Path.StartsWithSegments("/v1/traces")
                        && !context.Request.Path.StartsWithSegments("/v1/metrics"));
            });
        return services;
    }

    private static bool IsInfrastructure(Uri? uri, IConfiguration configuration)
    {
        if (uri is null) return true;
        if (uri.AbsolutePath.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return true;
        var endpoint = configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]
            ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var exporter)
            && uri.Scheme == exporter.Scheme && uri.Host == exporter.Host && uri.Port == exporter.Port;
    }

    public static void RecordCommand(string operation, string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Commands.Add(1, tags);
        CommandDuration.Record(duration.TotalSeconds, tags);
    }

    public static async Task<T> ExecuteCommandAsync<T>(string operation, Func<Task<T>> execute)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = Activities.StartActivity("db.command", ActivityKind.Client);
        activity?.SetTag("operation", operation);
        var outcome = "success";
        try { return await execute(); }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "error"; activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally
        {
            activity?.SetTag("outcome", outcome);
            RecordCommand(operation, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    public static T ExecuteCommand<T>(string operation, Func<T> execute)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = Activities.StartActivity("db.command", ActivityKind.Client);
        activity?.SetTag("operation", operation);
        var outcome = "success";
        try { return execute(); }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch { outcome = "error"; activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally
        {
            activity?.SetTag("outcome", outcome);
            RecordCommand(operation, outcome, Stopwatch.GetElapsedTime(started));
        }
    }
}

internal sealed class SafeActivityProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "http.request.method", "http.response.status_code", "http.route",
        "network.protocol.version", "operation", "outcome"
    };

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in activity.TagObjects.ToArray())
            if (!AllowedTags.Contains(tag.Key)) activity.SetTag(tag.Key, null);
        activity.TraceStateString = string.Empty;
        if (activity.Source.Name != OperatorDiagnostics.Name)
            activity.DisplayName = "HTTP";
        if (activity.Status == ActivityStatusCode.Error)
            activity.SetStatus(ActivityStatusCode.Error);
    }
}

internal sealed partial class DiagnosticsSummaryService(ILogger<DiagnosticsSummaryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var process = Process.GetCurrentProcess();
            Summary(logger, process.WorkingSet64, ThreadPool.PendingWorkItemCount);
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information,
        Message = "Operator diagnostics: working set {WorkingSetBytes} bytes, thread pool queue {QueueLength}")]
    private static partial void Summary(ILogger logger, long workingSetBytes, long queueLength);
}
