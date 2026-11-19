namespace Minicloud.Common.Services;

public sealed class DevelopmentWorkspaceOptions
{
    public const string SectionName = "DevelopmentWorkspaces";

    public bool Enabled { get; set; }
    public int MaxActiveVms { get; set; } = 9;
    public int MaxVmsPerWorkspace { get; set; } = 3;
    public int MaxVmsPerOrganization { get; set; } = 3;
    public decimal MaxEstimatedMonthlySpendPerOrganization { get; set; }
    public decimal MaxEstimatedMonthlySpend { get; set; }
    public decimal VolumeMonthlyRatePerGb { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset? PricingUpdatedAt { get; set; }
    public int MaxPricingAgeDays { get; set; } = 30;
    public int IdleGraceMinutes { get; set; } = 360;
    public int TaskLeaseSeconds { get; set; } = 300;
    public int HeartbeatTimeoutSeconds { get; set; } = 120;
    public int OperationLeaseSeconds { get; set; } = 120;
    public Dictionary<string, DevelopmentWorkspaceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DevelopmentWorkspaceImage> Images { get; set; } = new(StringComparer.Ordinal);
    public List<DevelopmentWorkspaceRepositoryPolicy> Repositories { get; set; } = [];
}

public sealed class DevelopmentWorkspaceProfile
{
    public int MemoryGiB { get; set; }
    public int CpuCount { get; set; }
    public decimal ComputeHourlyRate { get; set; }
    public string[] AllowedPlanIds { get; set; } = [];
    public string[] DefaultPlanIdsForNewWorkspaces { get; set; } = [];
    public string[] AllowedSkus { get; set; } = [];
    public string[] AllowedRegions { get; set; } = [];
}

public sealed class DevelopmentWorkspaceImage
{
    public string? DisplayName { get; set; }
    public string ProviderImageId { get; set; } = string.Empty;
    public string Architecture { get; set; } = "linux/amd64";
    public string ManifestSha256 { get; set; } = string.Empty;
    public string ProviderAccountId { get; set; } = string.Empty;
    public string[] AllowedRegions { get; set; } = [];
    public bool Enabled { get; set; }
    public bool RuntimeQualified { get; set; }
    public DateTimeOffset? QualifiedAt { get; set; }
    public bool T3ConnectForNewWorkspaces { get; set; }
}

public sealed class DevelopmentWorkspaceRepositoryPolicy
{
    public string OrganizationId { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string[] AllowedBaseRefs { get; set; } = [];
    public bool AllowPush { get; set; }
    public string? GitHubConnectionId { get; set; }
    public StoredSecretReference? Credential { get; set; }
    public DateTimeOffset? CredentialExpiresAt { get; set; }
}
