namespace Minicloud.Common.Services;

public sealed class DevelopmentWorkspaceOptions
{
    public const string SectionName = "DevelopmentWorkspaces";

    public bool Enabled { get; set; }
    public int MaxActiveWorkspaces { get; set; } = 3;
    public int MaxWorkspacesPerOrganization { get; set; } = 2;
    public decimal MaxEstimatedMonthlySpend { get; set; }
    public decimal VolumeMonthlyRatePerGb { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset? PricingUpdatedAt { get; set; }
    public int IdleGraceMinutes { get; set; } = 30;
    public int TaskLeaseSeconds { get; set; } = 300;
    public Dictionary<string, DevelopmentWorkspaceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DevelopmentWorkspaceImage> Images { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DevelopmentWorkspaceProfile
{
    public int MemoryGiB { get; set; }
    public int CpuCount { get; set; }
    public decimal ComputeHourlyRate { get; set; }
    public string[] AllowedPlanIds { get; set; } = [];
}

public sealed class DevelopmentWorkspaceImage
{
    public string ProviderImageId { get; set; } = string.Empty;
    public string Architecture { get; set; } = "linux/amd64";
    public string ManifestSha256 { get; set; } = string.Empty;
}
