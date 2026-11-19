namespace Minicloud.Common.Contracts;

/// <summary>
/// Latest dev environment resource sample. State is current, stale (no fresh sample for 120 seconds),
/// historical (the VM is not running) or unsupported (the image predates resource telemetry).
/// </summary>
public sealed record DevelopmentWorkspaceVmResources(
    string State,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt,
    DevelopmentWorkspaceCpuResources? Cpu,
    DevelopmentWorkspaceMemoryResources? Memory,
    IReadOnlyList<DevelopmentWorkspaceFilesystemResources> Filesystems,
    IReadOnlyDictionary<string, string> Unavailable,
    bool DiskUsageSupported,
    bool DirectoryDeletionSupported);

public sealed record DevelopmentWorkspaceCpuResources(int LogicalCount, double? UtilizationPercent, double? WindowSeconds);
public sealed record DevelopmentWorkspaceMemoryResources(long TotalBytes, long AvailableBytes);
public sealed record DevelopmentWorkspaceFilesystemResources(
    IReadOnlyList<string> Paths, long TotalBytes, long FreeBytes, long AvailableBytes,
    long? InodesTotal, long? InodesAvailable, int? NestedMountsExcluded);

public sealed record DevelopmentWorkspaceDiskUsageScanRequest(long Generation, string IdempotencyKey, string? TargetToken = null);
public sealed record DevelopmentWorkspaceDirectoryDeletionRequest(
    long Generation, string TargetToken, string ConfirmationPath, bool AcknowledgePermanentLoss, string IdempotencyKey);

public sealed record DevelopmentWorkspaceFilesystemJobResponse(
    string Id, string VmId, long Generation, string Kind, string State, string Path,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset ExpiresAt,
    bool CancelRequested, string? ErrorCode, string? ErrorMessage,
    DevelopmentWorkspaceDiskUsageResult? DiskUsage = null,
    DevelopmentWorkspaceDirectoryDeletionResult? Deletion = null);

public sealed record DevelopmentWorkspaceDiskUsageResult(
    long TotalBytes, long FilesHereBytes, bool Complete, string? LimitReached,
    int OmittedEntries, long OmittedBytes, bool HardLinksApproximate,
    IReadOnlyDictionary<string, int> Errors, DateTimeOffset? TargetsExpireAt,
    IReadOnlyList<DevelopmentWorkspaceDiskUsageEntry> Entries);

public sealed record DevelopmentWorkspaceDiskUsageEntry(
    string Name, string Path, long? Bytes, string State, IReadOnlyList<string> Errors,
    bool CanOpen, bool CanDelete, string? TargetToken, string? ProtectedReason);

public sealed record DevelopmentWorkspaceDirectoryDeletionResult(
    string Outcome, long RemovedEntries, long RemovedBytes, string? LimitReached, IReadOnlyDictionary<string, int> Errors);
