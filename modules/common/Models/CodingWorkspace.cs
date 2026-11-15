namespace Minicloud.Common.Models;

public static class CodingWorkspaceVersions
{
    public const string Codex = "0.169.0";
    public const string T3 = "0.1.9";
    public const string BootstrapRevision = "coding-workspace-v1";
}

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
    public string AgentVersion { get; set; } = "pending";
    public string RequestedAgentVersion { get; set; } = default!;
    public string T3Version { get; set; } = "pending";
    public string RequestedT3Version { get; set; } = default!;
    public string WorkspaceUser { get; set; } = "coding";
    public string? ProviderServerId { get; set; }
    public string? IpAddress { get; set; }
    public bool AllocationReconciliationRequired { get; set; }
    public string ProviderStatus { get; set; } = "unknown";
    public string Stage { get; set; } = "requested";
    public string Connectivity { get; set; } = "unknown";
    public string BootstrapStatus { get; set; } = "unknown";
    public string T3Health { get; set; } = "unknown";
    public string CodexAuthentication { get; set; } = "unknown";
    public string GitHubAuthentication { get; set; } = "unknown";
    public string RepositoryStatus { get; set; } = "unknown";
    public string? BootstrapRevision { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public DateTimeOffset? ProviderObservedAt { get; set; }
    public DateTimeOffset? BootstrapObservedAt { get; set; }
    public DateTimeOffset? T3ObservedAt { get; set; }
    public DateTimeOffset? CodexAuthenticationObservedAt { get; set; }
    public DateTimeOffset? GitHubAuthenticationObservedAt { get; set; }
    public DateTimeOffset? RepositoryObservedAt { get; set; }
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
    public string? RequestedBy { get; set; }
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
