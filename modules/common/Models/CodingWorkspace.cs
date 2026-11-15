namespace Minicloud.Common.Models;

public sealed class CodingWorkspace
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string NormalizedName { get; set; } = default!;
    public string PlanId { get; set; } = default!;
    public string PlanName { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string? Image { get; set; }
    public string ProviderSshKeyId { get; set; } = default!;
    public string SshPublicKey { get; set; } = default!;
    public string SshFingerprint { get; set; } = default!;
    public string Repository { get; set; } = default!;
    public string Agent { get; set; } = "codex";
    public string AgentVersion { get; set; } = default!;
    public string T3Version { get; set; } = "0.1.9";
    public string WorkspaceUser { get; set; } = "coding";
    public string? ProviderServerId { get; set; }
    public string? IpAddress { get; set; }
    public string Stage { get; set; } = "requested";
    public string Connectivity { get; set; } = "unknown";
    public string? BootstrapRevision { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CodingWorkspaceOperation
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "requested";
    public int AttemptCount { get; set; }
    public string? OwnershipToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
