namespace Minicloud.Common.Contracts;

public sealed record CreateDevelopmentWorkspaceRequest(
    string OrganizationId,
    string Name,
    string ImageId,
    string Profile,
    string PlanId,
    int VolumeSizeGb,
    string IdempotencyKey);

public sealed record DevelopmentWorkspaceResponse(
    string Id,
    string OrganizationId,
    string Name,
    string ImageId,
    string Profile,
    string PlanId,
    string ProviderAccountId,
    string Region,
    string Sku,
    int VolumeSizeGb,
    string DesiredState,
    string Phase,
    long Generation,
    string? ProviderServerId,
    string? ProviderVolumeId,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? ShutdownDeadline,
    DateTimeOffset? LastReadyAt,
    decimal EstimatedMonthlyCompute,
    decimal EstimatedMonthlyStorage,
    string Currency,
    DateTimeOffset? PricingUpdatedAt,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DevelopmentWorkspaceTaskLeaseRequest(string TaskId);
