using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Minicloud.Worker.Diagnostics;

internal static class OperatorProfilingExtensions
{
    public static void AddOperatorProfiling(this IHostApplicationBuilder builder)
    {
        var endpointValue = builder.Configuration["OperatorProfiling:Endpoint"];
        var bearerToken = builder.Configuration["OperatorProfiling:BearerToken"];
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Operator profiling requires a valid HTTP(S) OperatorProfiling:Endpoint.");
        }
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new InvalidOperationException("Operator profiling requires OperatorProfiling:BearerToken.");
        }

        builder.Logging
            .AddFilter<OpenTelemetryLoggerProvider>("", LogLevel.None)
            .AddFilter<OpenTelemetryLoggerProvider>("Minicloud.OperatorDiagnostics", LogLevel.Information)
            .AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = false;
                options.IncludeScopes = false;
                options.ParseStateValues = true;
                options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(OperatorDiagnostics.ServiceName));
                options.AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, bearerToken));
            });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(OperatorDiagnostics.ServiceName))
            .WithMetrics(metrics => metrics
                .AddMeter(OperatorDiagnostics.MeterName)
                .AddView("minicloud_worker_deployment_pipeline_duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.1, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300]
                })
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, bearerToken)))
            .WithTracing(tracing => tracing
                .AddSource(OperatorDiagnostics.ActivitySourceName)
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.05)))
                .AddOtlpExporter(exporter => ConfigureExporter(exporter, endpoint, bearerToken)));
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, Uri endpoint, string bearerToken)
    {
        exporter.Endpoint = endpoint;
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Headers = "Authorization=Bearer " + bearerToken;
        exporter.TimeoutMilliseconds = 10_000;
    }
}
