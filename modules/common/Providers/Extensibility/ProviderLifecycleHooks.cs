using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Extensibility;

public enum ProviderAllocationState
{
    Allocated,
    ProvisioningAsync,
    PartialFailureRequiresReconciliation
}

public sealed record ProviderAllocationRequest(
    CreateServerRequest ServerRequest,
    string IdempotencyKey,
    ProviderOwnershipEvidence Ownership,
    ProviderComputeFamily ComputeFamily = ProviderComputeFamily.VirtualServer,
    string? OrganizationId = null,
    string? ProviderAccountId = null);

public sealed record ProviderAllocationResult(
    ProviderServerInfo Server,
    ProviderAllocationState State,
    string? OperationId = null,
    string? PartialFailureDetail = null);

public enum ProviderReconciliationState
{
    NotFound,
    Matched,
    AmbiguousMultipleMatches,
    OwnershipMismatch,
    PartiallyAllocated
}

public sealed record ProviderReconciliationQuery(
    string ServerName,
    ProviderOwnershipEvidence Ownership,
    string? KnownProviderServerId = null,
    string? IdempotencyKey = null);

public sealed record ProviderReconciliationResult(
    ProviderReconciliationState State,
    ProviderServerInfo? Server = null,
    IReadOnlyList<string>? ConflictingServerIds = null,
    string? Detail = null);

public enum ProviderCleanupState
{
    Deleted,
    AlreadyAbsent,
    DeletingAsync,
    OwnershipConflict
}

public sealed record ProviderCleanupResult(
    ProviderCleanupState State,
    string? OperationId = null,
    string? Detail = null);

public enum ProviderOperationState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled,
    NotFound
}

public sealed record ProviderOperationStatus(
    string OperationId,
    ProviderOperationState State,
    int? ProgressPercent = null,
    string? ResourceId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsTerminal => State is ProviderOperationState.Succeeded or ProviderOperationState.Failed
        or ProviderOperationState.Canceled or ProviderOperationState.NotFound;
}

public sealed record ProviderBootstrapReadiness(
    bool ProviderReportsRunning,
    bool HasPublicAddress,
    bool SshPortReachable,
    bool HostKeyVerified,
    bool CloudInitCompleted,
    TimeSpan? EstimatedProvisioningLeadTime = null)
{
    public bool IsReadyForDeployment =>
        ProviderReportsRunning && HasPublicAddress && SshPortReachable && HostKeyVerified && CloudInitCompleted;
}

/// <summary>
/// Optional lifecycle and reconciliation extension contract for provider adapters that
/// support native idempotency tokens, explicit ownership reconciliation, async provisioning
/// handles, or multi-step partial-failure reporting.
/// </summary>
public interface IVpsProviderLifecycleClient
{
    Task<ProviderAllocationResult> AllocateServerAsync(
        ProviderAllocationRequest request,
        CancellationToken cancellationToken = default);

    Task<ProviderReconciliationResult> ReconcileServerAsync(
        ProviderReconciliationQuery query,
        CancellationToken cancellationToken = default);

    Task<ProviderCleanupResult> DeleteServerWithVerificationAsync(
        string serverId,
        ProviderOwnershipEvidence? expectedOwnership = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional asynchronous operation polling contract for providers whose server,
/// bare-metal, volume, or networking operations complete asynchronously
/// (e.g. GCP operations, Azure LROs, OVHcloud bare-metal deliveries).
/// </summary>
public interface IVpsProviderAsyncOperationClient
{
    Task<ProviderOperationStatus> GetOperationStatusAsync(
        string operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bridges any <see cref="IVpsProviderClient"/> (including existing Hetzner, DigitalOcean,
/// and Vultr adapters) onto the shared allocation, reconciliation, and cleanup hooks
/// without duplicating adapter logic.
/// </summary>
public sealed class VpsProviderLifecycleAdapter(
    IVpsProviderClient client,
    ProviderDescriptor descriptor,
    IProviderErrorClassifier? errorClassifier = null) : IVpsProviderLifecycleClient
{
    private readonly IVpsProviderClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly ProviderDescriptor _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    private readonly IProviderErrorClassifier _classifier = errorClassifier ?? DefaultProviderErrorClassifier.Instance;

    public async Task<ProviderAllocationResult> AllocateServerAsync(
        ProviderAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        if ((_descriptor.Capabilities.ComputeFamilies & request.ComputeFamily) != request.ComputeFamily)
        {
            throw new InvalidOperationException(
                $"Provider '{_descriptor.DisplayName}' does not support compute family '{request.ComputeFamily}'.");
        }

        if (_client is IVpsProviderLifecycleClient nativeLifecycle)
        {
            return await nativeLifecycle.AllocateServerAsync(request, cancellationToken);
        }

        var existing = await ReconcileServerAsync(
            new ProviderReconciliationQuery(request.ServerRequest.Name, request.Ownership, IdempotencyKey: request.IdempotencyKey),
            cancellationToken);

        if (existing.State == ProviderReconciliationState.Matched && existing.Server is not null)
        {
            return new ProviderAllocationResult(existing.Server, ProviderAllocationState.Allocated);
        }

        if (existing.State is ProviderReconciliationState.OwnershipMismatch or ProviderReconciliationState.AmbiguousMultipleMatches)
        {
            throw new InvalidOperationException(existing.Detail ?? "Discovered server identity does not match this app; refusing allocation.");
        }

        var server = await _client.CreateServerAsync(request.ServerRequest, cancellationToken);
        var state = _descriptor.Capabilities.SupportsAsynchronousOperations &&
                    string.IsNullOrWhiteSpace(server.Ipv4Address)
            ? ProviderAllocationState.ProvisioningAsync
            : ProviderAllocationState.Allocated;

        return new ProviderAllocationResult(server, state);
    }

    public async Task<ProviderReconciliationResult> ReconcileServerAsync(
        ProviderReconciliationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.ServerName);

        if (_client is IVpsProviderLifecycleClient nativeLifecycle)
        {
            return await nativeLifecycle.ReconcileServerAsync(query, cancellationToken);
        }

        var found = await _client.FindServerAsync(query.ServerName, cancellationToken);
        if (found is null)
        {
            return new ProviderReconciliationResult(ProviderReconciliationState.NotFound);
        }

        if (!string.IsNullOrWhiteSpace(query.KnownProviderServerId) &&
            !string.Equals(found.Id, query.KnownProviderServerId, StringComparison.Ordinal))
        {
            return new ProviderReconciliationResult(
                ProviderReconciliationState.OwnershipMismatch,
                found,
                [found.Id],
                $"Observed server ID '{found.Id}' does not match registered server ID '{query.KnownProviderServerId}'.");
        }

        if (!ProviderOwnershipVerifier.VerifyDiscoveredServer(_descriptor, found, query.ServerName, query.Ownership))
        {
            return new ProviderReconciliationResult(
                ProviderReconciliationState.OwnershipMismatch,
                found,
                [found.Id],
                "Discovered server identity or ownership labels do not match this application.");
        }

        return new ProviderReconciliationResult(ProviderReconciliationState.Matched, found);
    }

    public async Task<ProviderCleanupResult> DeleteServerWithVerificationAsync(
        string serverId,
        ProviderOwnershipEvidence? expectedOwnership = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        if (_client is IVpsProviderLifecycleClient nativeLifecycle)
        {
            return await nativeLifecycle.DeleteServerWithVerificationAsync(serverId, expectedOwnership, cancellationToken);
        }

        var status = await _client.GetServerStatusAsync(serverId, cancellationToken);
        if (string.Equals(status.Status, "not_found", StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderCleanupResult(ProviderCleanupState.AlreadyAbsent);
        }

        await _client.DeleteServerAsync(serverId, cancellationToken);
        return new ProviderCleanupResult(ProviderCleanupState.Deleted);
    }

    public ProviderErrorClassification ClassifyError(
        Exception exception,
        ProviderOperationSafety operationSafety = ProviderOperationSafety.NonIdempotentMutation) =>
        _classifier.Classify(exception, operationSafety);
}
