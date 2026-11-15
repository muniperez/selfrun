using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.CodingWorkspaces;

public sealed class CodingWorkspaceOperationProcessor(
    MinicloudDbContext db,
    ProviderAccountClientFactory clients,
    TimeProvider clock,
    ILogger<CodingWorkspaceOperationProcessor>? logger = null,
    MaintenanceParticipant? maintenance = null)
{
    private readonly MaintenanceParticipant _maintenance = maintenance ?? MaintenanceParticipant.CreateLocal("worker", clock);

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        CodingWorkspaceOperation? operation = null;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            if (!await _maintenance.CanClaimAsync(db, ct))
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            var now = clock.GetUtcNow();
            var interrupted = await GetExpiredOperationsAsync(now, ct);
            foreach (var item in interrupted)
            {
                if (item.Kind.StartsWith("auth-", StringComparison.Ordinal))
                {
                    item.Status = "failed";
                    item.ErrorCode = "authorization_interrupted";
                    item.ErrorMessage = "The authorization session expired before completion. Retry authentication explicitly.";
                    item.CompletedAt = now;
                    var authWorkspace = await db.CodingWorkspaces.SingleOrDefaultAsync(x =>
                        x.Id == item.WorkspaceId && x.OrganizationId == item.OrganizationId, ct);
                    if (authWorkspace is not null)
                    {
                        if (item.Kind == "auth-codex") authWorkspace.CodexAuthentication = "failed";
                        if (item.Kind == "auth-github") authWorkspace.GitHubAuthentication = "failed";
                        if (item.Kind == "auth-codex") authWorkspace.CodexAuthenticationObservedAt = now;
                        if (item.Kind == "auth-github") authWorkspace.GitHubAuthenticationObservedAt = now;
                        authWorkspace.UpdatedAt = now;
                    }
                }
                else
                {
                    item.Status = "queued";
                }
                item.OwnershipToken = null;
                item.LeaseExpiresAt = null;
                item.UpdatedAt = now;
            }

            operation = await GetNextQueuedOperationAsync(ct);
            if (operation is not null)
            {
                operation.Status = "running";
                operation.OwnershipToken = Guid.NewGuid().ToString("N");
                operation.LeaseExpiresAt = now.AddMinutes(10);
                operation.AttemptCount++;
                operation.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
        }

        if (operation is null) return false;
        try
        {
            switch (operation.Kind)
            {
                case "provision":
                    await ProvisionAsync(operation, ct);
                    break;
                case "destroy":
                    await DestroyAsync(operation, ct);
                    break;
                default:
                    throw new WorkspaceOperationException("workspace_operation_unsupported", "The requested workspace operation is not supported.");
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            await FailAsync(operation, error, ct);
            logger?.LogWarning("Coding workspace operation {OperationId} failed with {ErrorCode}.",
                operation.Id, ErrorCode(error));
            return true;
        }
    }

    private async Task ProvisionAsync(CodingWorkspaceOperation operation, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.SingleAsync(x =>
            x.Id == operation.WorkspaceId && x.OrganizationId == operation.OrganizationId, ct);
        if (workspace.Provider != "hetzner" || workspace.Image != "ubuntu-24.04")
            throw new WorkspaceOperationException("workspace_image_unsupported",
                "Only Hetzner Ubuntu 24.04 coding workspaces can be provisioned by this bootstrap revision.");
        var account = await db.ProviderAccounts.SingleAsync(x =>
            x.Id == workspace.ProviderAccountId && x.OrganizationId == workspace.OrganizationId, ct);
        var sshKey = await db.ProviderSshKeys.SingleAsync(x =>
            x.Id == workspace.ProviderSshKeyId && x.ProviderAccountId == workspace.ProviderAccountId &&
            x.OrganizationId == workspace.OrganizationId, ct);
        if (sshKey.ProviderKeyId is null || sshKey.Fingerprint != workspace.SshFingerprint ||
            sshKey.PublicKey != workspace.SshPublicKey || sshKey.RegistrationState != "ready")
            throw new WorkspaceOperationException("ssh_key_changed", "The selected provider SSH key is no longer available or has changed.");

        workspace.Stage = workspace.ProviderServerId is null
            ? workspace.AllocationReconciliationRequired ? "reconciling" : "allocating"
            : "bootstrapping";
        operation.Stage = workspace.Stage;
        workspace.UpdatedAt = operation.UpdatedAt = clock.GetUtcNow();
        await SaveOwnedAsync(operation, ct);

        var client = await clients.CreateAsync(workspace.OrganizationId, workspace.ProviderAccountId, ct);
        var serverName = ServerName(workspace.Id);
        var server = workspace.ProviderServerId is null
            ? await client.FindServerAsync(serverName, ct)
            : null;
        if (server is not null && !MatchesWorkspace(server, workspace))
            throw new WorkspaceOperationException("provider_identity_conflict",
                $"Provider server {server.Id} has the reserved workspace name but does not have the expected ownership labels. No server was adopted or deleted; verify this resource before retrying.");

        if (workspace.ProviderServerId is null && server is null && workspace.AllocationReconciliationRequired)
        {
            await QueueReconciliationAsync(operation, workspace, ct);
            return;
        }

        if (workspace.ProviderServerId is null && server is null)
        {
            if (!account.Enabled)
                throw new WorkspaceOperationException("provider_disabled", "The provider account is disabled; enable it before allocating this workspace.");

            try
            {
                server = await client.CreateServerAsync(new CreateServerRequest(
                    serverName,
                    workspace.Sku,
                    workspace.Region,
                    [sshKey.ProviderKeyId],
                    CodingWorkspaceBootstrap.BuildCloudInit(workspace),
                    new Dictionary<string, string>
                    {
                        ["managed-by"] = "minicloud",
                        ["workspace"] = workspace.Id,
                        ["organization"] = workspace.OrganizationId
                    },
                    [$"minicloud-workspace-{workspace.Id}", "minicloud-managed"],
                    Image: workspace.Image,
                    DisableRetries: true), ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                if (IsDefinitiveCreateRejection(error))
                    throw new WorkspaceOperationException("provider_create_rejected",
                        "The provider definitively rejected workspace creation; check the provider account and quota before retrying.", error);

                workspace.AllocationReconciliationRequired = true;
                workspace.Stage = operation.Stage = "reconciling";
                workspace.UpdatedAt = operation.UpdatedAt = clock.GetUtcNow();
                await SaveOwnedAsync(operation, ct);
                using var reconcileTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                server = await client.FindServerAsync(serverName, reconcileTimeout.Token);
                if (server is null)
                {
                    await QueueReconciliationAsync(operation, workspace, ct);
                    return;
                }
                if (!MatchesWorkspace(server, workspace))
                    throw new WorkspaceOperationException("provider_create_reconciliation_required",
                        $"Provider server {server.Id} has the reserved workspace name but does not have the expected ownership labels. No server was adopted or deleted; verify this resource before retrying.",
                        error);
            }
        }

        if (server is not null && !MatchesWorkspace(server, workspace))
            throw new WorkspaceOperationException("provider_identity_conflict",
                $"Provider server {server.Id} was returned without the expected workspace ownership labels; no identity was adopted.");

        if (workspace.ProviderServerId is null)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Id))
                throw new WorkspaceOperationException("provider_create_reconciliation_required",
                    "Provider allocation returned no server identity; no retry will be made without reconciliation.");
            workspace.ProviderServerId = server.Id;
            workspace.AllocationReconciliationRequired = false;
            workspace.IpAddress = string.IsNullOrWhiteSpace(server.Ipv4Address) ? null : server.Ipv4Address;
            workspace.Stage = "bootstrapping";
            workspace.UpdatedAt = clock.GetUtcNow();
            operation.Stage = workspace.Stage;
            operation.UpdatedAt = workspace.UpdatedAt;
            await SaveOwnedAsync(operation, ct);
        }

        var status = await client.GetServerStatusAsync(workspace.ProviderServerId!, ct);
        if (!status.IsRunning || string.IsNullOrWhiteSpace(status.Ipv4Address))
        {
            workspace.Stage = "allocating";
            operation.Stage = "allocating";
            operation.Status = "queued";
            operation.OwnershipToken = null;
            operation.LeaseExpiresAt = null;
            workspace.UpdatedAt = operation.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return;
        }

        workspace.IpAddress = status.Ipv4Address;
        workspace.Stage = "awaiting-auth";
        workspace.Connectivity = "unknown";
        workspace.AgentVersion = "pending";
        workspace.ProviderStatus = status.Status;
        workspace.BootstrapStatus = "pending";
        workspace.BootstrapRevision = CodingWorkspaceBootstrap.Revision;
        workspace.LastObservedAt = clock.GetUtcNow();
        workspace.ProviderObservedAt = workspace.LastObservedAt;
        workspace.LastErrorCode = null;
        workspace.LastError = null;
        workspace.UpdatedAt = clock.GetUtcNow();
        operation.Stage = workspace.Stage;
        operation.Status = "completed";
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        operation.CompletedAt = operation.UpdatedAt = workspace.UpdatedAt;
        await SaveOwnedAsync(operation, ct);
    }

    private async Task DestroyAsync(CodingWorkspaceOperation operation, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.SingleAsync(x =>
            x.Id == operation.WorkspaceId && x.OrganizationId == operation.OrganizationId, ct);
        if (workspace.ProviderServerId is not null)
        {
            var account = await db.ProviderAccounts.SingleAsync(x =>
                x.Id == workspace.ProviderAccountId && x.OrganizationId == workspace.OrganizationId, ct);
            var client = await clients.CreateAsync(workspace.OrganizationId, workspace.ProviderAccountId, ct);
            var server = await client.FindServerAsync(ServerName(workspace.Id), ct);
            if (server is not null)
            {
                if (!MatchesWorkspace(server, workspace) || server.Id != workspace.ProviderServerId)
                    throw new WorkspaceOperationException("provider_identity_conflict",
                        "The recorded VPS identity no longer matches the provider server; no resource was deleted.");
                await client.DeleteServerAsync(workspace.ProviderServerId, ct);
            }
            else if (!account.Enabled)
            {
                // A missing account credential cannot establish that the previously recorded VPS
                // is gone, so do not mark a billable resource as deleted.
                throw new WorkspaceOperationException("provider_account_unavailable",
                    "The VPS could not be reconciled in its original provider account; its deletion is unconfirmed.");
            }
        }

        workspace.ProviderServerId = null;
        workspace.IpAddress = null;
        workspace.Stage = "destroyed";
        workspace.Connectivity = "unknown";
        workspace.UpdatedAt = clock.GetUtcNow();
        operation.Stage = workspace.Stage;
        operation.Status = "completed";
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        operation.CompletedAt = operation.UpdatedAt = workspace.UpdatedAt;
        await SaveOwnedAsync(operation, ct);
    }

    private async Task SaveOwnedAsync(CodingWorkspaceOperation operation, CancellationToken ct)
    {
        var owns = await db.CodingWorkspaceOperations.AsNoTracking().AnyAsync(x =>
            x.Id == operation.Id && x.Status == "running" &&
            x.OwnershipToken == operation.OwnershipToken && x.LeaseExpiresAt > clock.GetUtcNow(), ct);
        if (!owns) throw new WorkspaceOperationException("workspace_operation_lease_lost",
            "The workspace operation lease expired; the worker will reconcile the recorded provider identity before retrying.");
        await db.SaveChangesAsync(ct);
    }

    private async Task QueueReconciliationAsync(
        CodingWorkspaceOperation operation, CodingWorkspace workspace, CancellationToken ct)
    {
        workspace.Stage = operation.Stage = "reconciling";
        workspace.UpdatedAt = operation.UpdatedAt = clock.GetUtcNow();
        await SaveOwnedAsync(operation, ct);
        operation.Status = "queued";
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(CodingWorkspaceOperation operation, Exception error, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var current = await db.CodingWorkspaceOperations.SingleOrDefaultAsync(x =>
            x.Id == operation.Id && x.Status == "running" && x.OwnershipToken == operation.OwnershipToken, ct);
        if (current is null) return;
        var workspace = await db.CodingWorkspaces.SingleOrDefaultAsync(x =>
            x.Id == current.WorkspaceId && x.OrganizationId == current.OrganizationId, ct);
        var code = ErrorCode(error);
        var message = error is WorkspaceOperationException operationError
            ? operationError.Message
            : "Workspace provisioning failed. Inspect the selected provider account and quota before retrying.";
        current.Status = "failed";
        current.ErrorCode = code;
        current.ErrorMessage = workspace?.ProviderServerId is null
            ? message
            : $"{message} VPS {workspace.ProviderServerId} is retained; destroy or resume this workspace to manage it.";
        current.OwnershipToken = null;
        current.LeaseExpiresAt = null;
        current.CompletedAt = current.UpdatedAt = clock.GetUtcNow();
        if (workspace is not null)
        {
            workspace.Stage = "failed";
            workspace.LastErrorCode = code;
            workspace.LastError = current.ErrorMessage;
            workspace.UpdatedAt = current.UpdatedAt;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<CodingWorkspaceOperation>> GetExpiredOperationsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.CodingWorkspaceOperations.FromSqlInterpolated(
                $"SELECT * FROM coding_workspace_operations WHERE status = 'running' AND (lease_expires_at IS NULL OR lease_expires_at <= {now}) FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);
        }

        return (await db.CodingWorkspaceOperations.Where(x => x.Status == "running").ToListAsync(ct))
            .Where(x => x.LeaseExpiresAt is null || x.LeaseExpiresAt <= now).ToList();
    }

    private async Task<CodingWorkspaceOperation?> GetNextQueuedOperationAsync(CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.CodingWorkspaceOperations.FromSqlRaw(
                "SELECT * FROM coding_workspace_operations WHERE status = 'queued' ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1")
                .FirstOrDefaultAsync(ct);
        }

        return (await db.CodingWorkspaceOperations.Where(x => x.Status == "queued").ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
    }

    internal static bool MatchesWorkspace(ProviderServerInfo server, CodingWorkspace workspace)
    {
        if (!string.Equals(server.Name, ServerName(workspace.Id), StringComparison.Ordinal)) return false;
        return server.Labels is not null &&
            server.Labels.TryGetValue("workspace", out var id) && id == workspace.Id &&
            server.Labels.TryGetValue("organization", out var organization) && organization == workspace.OrganizationId &&
            server.Labels.TryGetValue("managed-by", out var manager) && manager == "minicloud";
    }

    private static string ServerName(string workspaceId) => $"minicloud-coding-{workspaceId}";

    private static string ErrorCode(Exception error) => error switch
    {
        WorkspaceOperationException workspaceError => workspaceError.Code,
        ProviderCatalogException providerError => providerError.Code,
        SecretStoreUnavailableException => "provider_account_unavailable",
        _ => "workspace_operation_failed"
    };

    private static bool IsDefinitiveCreateRejection(Exception error) =>
        error is HttpRequestException { StatusCode: { } status } &&
        (int)status is >= 400 and < 500 &&
        status is not HttpStatusCode.RequestTimeout and not HttpStatusCode.Conflict and not (HttpStatusCode)425;

    private sealed class WorkspaceOperationException(string code, string message, Exception? inner = null)
        : Exception(message, inner)
    {
        public string Code { get; } = code;
    }
}
