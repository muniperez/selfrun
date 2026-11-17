namespace Minicloud.Common.Models;

public sealed class DevelopmentWorkspace
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string CreatedBy { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ImageId { get; set; } = default!;
    public string Profile { get; set; } = default!;
    public string PlanId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string ProviderImage { get; set; } = default!;
    public int VolumeSizeGb { get; set; }
    public string DesiredState { get; set; } = "stopped";
    public string Phase { get; set; } = "stopped";
    public long Generation { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? OperationId { get; set; }
    public string? ProviderServerId { get; set; }
    public string? ProviderVolumeId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? ShutdownDeadline { get; set; }
    public string? ShutdownReason { get; set; }
    public DateTimeOffset? QuiescedAt { get; set; }
    public long? QuiescedGeneration { get; set; }
    public DateTimeOffset? LastReadyAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DevelopmentWorkspaceTaskLease
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string TaskId { get; set; } = default!;
    public long Generation { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
