using System.Text.Json.Serialization;

namespace Minicloud.Common.Contracts;

public sealed record WorkspaceSelectionOption(
    string ImageId, string ImageName, string Profile, string PlanId, string PlanName,
    string ProviderAccountName, string Region, string Sku, int CpuCount, int MemoryGiB,
    decimal ComputeHourlyRate, bool IsDefault);

public sealed record WorkspaceSelections(
    bool Available, string? UnavailableReason, IReadOnlyList<WorkspaceSelectionOption> Options,
    string? Currency = null, decimal? VolumeMonthlyRatePerGb = null);

public sealed record CreateDevelopmentWorkspaceRequest(
    string OrganizationId,
    string Name,
    string ImageId,
    string Profile,
    string PlanId,
    int VolumeSizeGb,
    string IdempotencyKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    int? IdleTimeoutMinutes = null);

public sealed record UpdateDevelopmentWorkspaceIdleTimeoutRequest(
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? IdleTimeoutMinutes);

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
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DevelopmentWorkspaceVmResponse>? Vms = null,
    string? PrimaryVmId = null,
    string? VolumeStatus = null,
    string? CreatedBy = null,
    int? IdleTimeoutMinutes = null,
    int? EffectiveIdleTimeoutMinutes = null);

public sealed record DevelopmentWorkspaceTaskLeaseRequest(string TaskId, long Generation);
public sealed record DevelopmentWorkspaceGenerationRequest(long Generation);
public sealed record DevelopmentWorkspaceResizeRequest(long Generation, string ImageId, string Profile, string PlanId);
public sealed record DevelopmentWorkspaceDiscardRequest(long Generation, bool DiscardLocalData, bool AcknowledgeIgnoredDataLoss);
public sealed record CompleteDevelopmentWorkspaceTaskRequest(long Generation, bool Cancelled = false);
public sealed record LaunchEphemeralDevelopmentWorkspaceVmRequest(
    string ImageId, string Profile, string PlanId, string Repository, string BaseRef, string BaseCommit,
    bool PushAuthorized, bool AcknowledgeIgnoredDataLoss, string IdempotencyKey,
    string? RepositoryCredentialRef = null);

public sealed record DevelopmentWorkspaceVmResponse(
    string Id, string Kind, long Generation, string DesiredState, string Phase,
    string ImageId, string Profile, string PlanId, string ProviderAccountId, string Region, string Sku,
    string? ProviderServerId, string? ErrorCode, string? ErrorMessage,
    DateTimeOffset? LastActivityAt, DateTimeOffset? ShutdownDeadline, DateTimeOffset? LastReadyAt,
    string? Repository, string? BaseRef, string? BaseCommit, string? WorkBranch,
    bool PushAuthorized, bool IgnoredDataAcknowledged, string GitStatus,
    string? FinalHead, string? VerifiedRemoteRef, string? VerifiedRemoteCommit,
    string? NextImageId, string? NextProfile, string? NextPlanId,
    decimal ComputeHourlyRate, decimal EstimatedMonthlyCompute, bool ComputeReserved, long Revision,
    IReadOnlyList<DevelopmentWorkspaceTaskLeaseResponse>? TaskLeases = null,
    bool StartAfterStop = false,
    string? StartupStage = null,
    DevelopmentWorkspaceVmResources? Resources = null);

public sealed record DevelopmentWorkspaceTaskLeaseResponse(
    string VmId, string TaskId, long Generation, string Status, DateTimeOffset ExpiresAt);

public sealed record DevelopmentWorkspaceGatewayLaunchRequest(long Generation, string Service = "t3");
public sealed record DevelopmentWorkspaceGatewayLaunchResponse(string Url, DateTimeOffset ExpiresAt);
