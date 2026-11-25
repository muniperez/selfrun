namespace Selfrun.Common.Services;

public sealed class TelemetryClientOptions
{
    public const string SectionName = "Telemetry";

    public string TelemetryServiceUrl { get; set; } = "http://telemetry:8080";
    public string IngestionUrl { get; set; } = "http://localhost:8088/otlp/api/logs";
    public string AccessOrigin { get; set; } = "http://localhost:8088";
    public string ManagementApiKey { get; set; } = "selfrun-telemetry-management-dev-key";
    public string AuthSigningKey { get; set; } = "selfrun-telemetry-auth-signing-dev-key-must-be-long";
    public int SyncIntervalSeconds { get; set; } = 15;
}
