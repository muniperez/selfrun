namespace Selfrun.Telemetry.Services;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public string LokiBaseAddress { get; set; } = "http://loki:3100";
    public string GrafanaBaseAddress { get; set; } = "http://grafana:3000";

    public string LokiBaseUrl
    {
        get => LokiBaseAddress;
        set => LokiBaseAddress = value;
    }

    public string GrafanaBaseUrl
    {
        get => GrafanaBaseAddress;
        set => GrafanaBaseAddress = value;
    }
    public string QueryGatewayBaseUrl { get; set; } = "http://telemetry:8080/api/grafana/query-gateway";
    public string AccessOrigin { get; set; } = "http://localhost:8088";
    public string ProxyCookieName { get; set; } = "selfrun_grafana_session";
    public string ManagementApiKey { get; set; } = "selfrun-telemetry-management-dev-key";
    public string AuthSigningKey { get; set; } = "selfrun-telemetry-auth-signing-dev-key-must-be-long";
    public int MaxConcurrentIngestions { get; set; } = 32;
    public string Environment { get; set; } = "production";
    public int UpstreamTimeoutSeconds { get; set; } = 10;

    public long PerAppDailyBytes { get; set; } = Selfrun.Common.Services.TelemetryLimits.PerAppDailyBytes;
    public long PerOrganizationDailyBytes { get; set; } = Selfrun.Common.Services.TelemetryLimits.PerOrganizationDailyBytes;
    public long WholeStackDailyBytes { get; set; } = Selfrun.Common.Services.TelemetryLimits.WholeStackDailyBytes;
    public long PerAppBurstBytes { get; set; } = Selfrun.Common.Services.TelemetryLimits.PerAppBurstBytes;
    public long PerAppSustainedBytesPerSecond { get; set; } = Selfrun.Common.Services.TelemetryLimits.PerAppSustainedBytesPerSecond;
}
