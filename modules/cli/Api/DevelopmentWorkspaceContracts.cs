using System.Text.Json.Serialization;

namespace Minicloud.Cli.Api;

internal sealed record WorkspaceSelectionOption(
    string ImageId, string ImageName, string Profile, string PlanId, string PlanName,
    string ProviderAccountName, string Region, string Sku, int CpuCount, int MemoryGiB,
    decimal ComputeHourlyRate, bool IsDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DevelopmentWorkspaceAgentResponse>? Agents = null);

/// <summary>A coding agent installed in a dev environment image, available through T3.</summary>
internal sealed record DevelopmentWorkspaceAgentResponse(string Id, string Name, string Version);

internal sealed record WorkspaceSelections(
    bool Available, string? UnavailableReason, IReadOnlyList<WorkspaceSelectionOption> Options,
    string? Currency = null, decimal? VolumeMonthlyRatePerGb = null);

internal sealed record CreateDevelopmentWorkspaceRequest(
    string OrganizationId,
    string Name,
    string ImageId,
    string Profile,
    string PlanId,
    int VolumeSizeGb,
    string IdempotencyKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    int? IdleTimeoutMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool KeepRunning = false,
    // Omitted when false so existing create digests and idempotent retries stay unchanged.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool StartPrimaryVm = false);

internal sealed record UpdateDevelopmentWorkspaceIdleTimeoutRequest(
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int? IdleTimeoutMinutes,
    bool KeepRunning = false);

internal sealed record DevelopmentWorkspaceResponse(
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
    int? EffectiveIdleTimeoutMinutes = null,
    bool KeepRunning = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<DevelopmentWorkspaceAgentResponse>? Agents = null);

internal sealed record DevelopmentWorkspaceGenerationRequest(long Generation);
internal sealed record DevelopmentWorkspaceResizeRequest(long Generation, string ImageId, string Profile, string PlanId);
internal sealed record DevelopmentWorkspaceDiscardRequest(long Generation, bool DiscardLocalData, bool AcknowledgeIgnoredDataLoss);
internal sealed record CompleteDevelopmentWorkspaceTaskRequest(long Generation, bool Cancelled = false);
internal sealed record LaunchEphemeralDevelopmentWorkspaceVmRequest(
    string ImageId, string Profile, string PlanId, string Repository, string BaseRef, string BaseCommit,
    bool PushAuthorized, bool AcknowledgeIgnoredDataLoss, string IdempotencyKey,
    string? RepositoryCredentialRef = null);

internal sealed record DevelopmentWorkspaceVmResponse(
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

internal sealed record DevelopmentWorkspaceTaskLeaseResponse(
    string VmId, string TaskId, long Generation, string Status, DateTimeOffset ExpiresAt);

internal sealed record DevelopmentWorkspaceGatewayLaunchRequest(long Generation, string Service = "t3");
internal sealed record DevelopmentWorkspaceGatewayLaunchResponse(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Latest dev environment resource sample. State is current, stale (no fresh sample for 120 seconds),
/// historical (the VM is not running) or unsupported (the image predates resource telemetry).
/// </summary>
internal sealed record DevelopmentWorkspaceVmResources(
    string State,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt,
    DevelopmentWorkspaceCpuResources? Cpu,
    DevelopmentWorkspaceMemoryResources? Memory,
    IReadOnlyList<DevelopmentWorkspaceFilesystemResources> Filesystems,
    IReadOnlyDictionary<string, string> Unavailable,
    bool DiskUsageSupported,
    bool DirectoryDeletionSupported);

internal sealed record DevelopmentWorkspaceCpuResources(int LogicalCount, double? UtilizationPercent, double? WindowSeconds);
internal sealed record DevelopmentWorkspaceMemoryResources(long TotalBytes, long AvailableBytes);
internal sealed record DevelopmentWorkspaceFilesystemResources(
    IReadOnlyList<string> Paths, long TotalBytes, long FreeBytes, long AvailableBytes,
    long? InodesTotal, long? InodesAvailable, int? NestedMountsExcluded,
    IReadOnlyList<string>? Roles = null, int? ScratchBindMounts = null);
