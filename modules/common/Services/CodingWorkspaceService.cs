using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class CodingWorkspaceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CodingWorkspaceCreation(CodingWorkspace Workspace, CodingWorkspaceOperation Operation, bool Resumed);

public sealed class CodingWorkspaceService(MinicloudDbContext db, ProviderCatalogService catalog, IIdGenerator ids, TimeProvider clock)
{
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    public const string T3Version = CodingWorkspaceVersions.T3;
    public const string BootstrapRevision = CodingWorkspaceVersions.BootstrapRevision;

    public async Task<CodingWorkspaceCreation> CreateAsync(CreateCodingWorkspaceRequest request, CancellationToken ct)
    {
        var name = NormalizeName(request.Name);
        var repository = NormalizeRepository(request.Repository);
        if (!string.Equals(request.Agent, "codex", StringComparison.OrdinalIgnoreCase))
            throw new CodingWorkspaceException("agent_unsupported", "The only supported coding workspace agent is 'codex'.");

        ProviderPlan plan;
        try { plan = await catalog.ResolveAsync(request.OrganizationId, request.Plan, ct); }
        catch (ProviderCatalogException ex) { throw new CodingWorkspaceException(ex.Code, ex.Message); }
        if (!plan.Enabled || !plan.ProviderAccount.Enabled)
            throw new CodingWorkspaceException("plan_unavailable", "The selected plan or provider account is disabled.");
        if (plan.ProviderAccount.ProviderType != Minicloud.Common.Providers.CloudProvider.Hetzner ||
            !string.Equals(plan.Image, "ubuntu-24.04", StringComparison.Ordinal))
            throw new CodingWorkspaceException("workspace_image_unsupported",
                "Coding workspaces currently support only Hetzner plans using the ubuntu-24.04 image.");

        var key = await db.ProviderSshKeys.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.SshKeyId && x.OrganizationId == request.OrganizationId &&
            x.ProviderAccountId == plan.ProviderAccountId, ct);
        if (key is null || key.RegistrationState != "ready" || string.IsNullOrWhiteSpace(key.ProviderKeyId))
            throw new CodingWorkspaceException("ssh_key_unavailable",
                "Select a registered SSH key for the provider account used by this plan.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.CodingWorkspaces.SingleOrDefaultAsync(x =>
            x.OrganizationId == request.OrganizationId && x.NormalizedName == name.Normalized, ct);
        if (existing is not null)
        {
            if (existing.PlanId != plan.Id || existing.Repository != repository ||
                existing.ProviderSshKeyId != key.Id || existing.Agent != "codex")
                throw new CodingWorkspaceException("workspace_configuration_conflict",
                    "A workspace with this name already exists with different settings. Use its existing configuration or choose another name.");
            if (existing.Stage is "destroying" or "destroyed")
                throw new CodingWorkspaceException("workspace_destroying", "This workspace is being destroyed or has been destroyed. Choose another name.");

            var existingOperation = (await db.CodingWorkspaceOperations
                .Where(x => x.OrganizationId == existing.OrganizationId && x.WorkspaceId == existing.Id)
                .ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (existingOperation is not null)
            {
                if (existingOperation.Status == "failed")
                {
                    existingOperation.Status = "queued";
                    existingOperation.ErrorCode = null;
                    existingOperation.ErrorMessage = null;
                    existingOperation.CompletedAt = null;
                    existingOperation.Stage = existing.ProviderServerId is null ? "requested" : "bootstrapping";
                    existing.Stage = existingOperation.Stage;
                    existing.LastErrorCode = null;
                    existing.LastError = null;
                    existing.UpdatedAt = clock.GetUtcNow();
                    existingOperation.UpdatedAt = existing.UpdatedAt;
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return new(existing, existingOperation, true);
            }
        }

        var now = clock.GetUtcNow();
        var workspace = existing ?? new CodingWorkspace
        {
            Id = ids.NewId("workspace"),
            OrganizationId = request.OrganizationId,
            Name = name.Display,
            NormalizedName = name.Normalized,
            PlanId = plan.Id,
            PlanName = plan.Name,
            ProviderAccountId = plan.ProviderAccountId,
            Provider = plan.ProviderAccount.ProviderType.ToString().ToLowerInvariant(),
            Region = plan.Region,
            Sku = plan.Sku,
            Image = plan.Image,
            ProviderSshKeyId = key.Id,
            SshPublicKey = key.PublicKey,
            SshFingerprint = key.Fingerprint,
            Repository = repository,
            Agent = "codex",
            AgentVersion = "pending",
            RequestedAgentVersion = CodingWorkspaceVersions.Codex,
            T3Version = "pending",
            RequestedT3Version = T3Version,
            WorkspaceUser = "coding",
            BootstrapRevision = BootstrapRevision,
            Stage = "requested",
            Connectivity = "unknown",
            CreatedAt = now,
            UpdatedAt = now
        };
        if (existing is null) db.CodingWorkspaces.Add(workspace);
        var operation = new CodingWorkspaceOperation
        {
            Id = ids.NewId("workspaceop"),
            OrganizationId = request.OrganizationId,
            WorkspaceId = workspace.Id,
            Kind = "provision",
            Status = "queued",
            Stage = "requested",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CodingWorkspaceOperations.Add(operation);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var raced = await db.CodingWorkspaces.SingleOrDefaultAsync(
                x => x.OrganizationId == request.OrganizationId && x.NormalizedName == name.Normalized, ct);
            if (raced is null) throw;
            if (raced.PlanId != plan.Id || raced.Repository != repository ||
                raced.ProviderSshKeyId != key.Id || raced.Agent != "codex")
                throw new CodingWorkspaceException("workspace_configuration_conflict",
                    "A workspace with this name was created concurrently with different settings. Use its saved configuration or choose another name.");
            var operations = await db.CodingWorkspaceOperations.Where(x =>
                x.OrganizationId == raced.OrganizationId && x.WorkspaceId == raced.Id).ToListAsync(ct);
            var concurrentOperation = operations.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (concurrentOperation is not null)
                return new(raced, concurrentOperation, true);
            throw;
        }
        await transaction.CommitAsync(ct);
        return new(workspace, operation, false);
    }

    public Task<List<CodingWorkspace>> ListAsync(string organizationId, CancellationToken ct) =>
        db.CodingWorkspaces.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.NormalizedName).ToListAsync(ct);

    public Task<CodingWorkspace?> GetAsync(string organizationId, string workspaceId, CancellationToken ct) =>
        db.CodingWorkspaces.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == workspaceId, ct);

    public Task<CodingWorkspaceOperation?> GetOperationAsync(string organizationId, string operationId, CancellationToken ct) =>
        db.CodingWorkspaceOperations.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == operationId, ct);

    public async Task<CodingWorkspace> RecordConnectionObservationAsync(
        string organizationId, string workspaceId, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == workspaceId, ct)
            ?? throw new CodingWorkspaceException("workspace_not_found", "Coding workspace was not found.");
        if (workspace.Stage is "destroying" or "destroyed" or "failed")
            throw new CodingWorkspaceException("workspace_not_ready", "The workspace cannot accept a connection observation in its current stage.");
        var now = clock.GetUtcNow();
        workspace.T3Health = "healthy";
        workspace.T3ObservedAt = now;
        workspace.Connectivity = "tunnel";
        workspace.LastObservedAt = now;
        workspace.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<CodingWorkspaceOperation> QueueDestroyAsync(string organizationId, string workspaceId, CancellationToken ct)
    {
        var workspace = await db.CodingWorkspaces.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == workspaceId, ct)
            ?? throw new CodingWorkspaceException("workspace_not_found", "Coding workspace was not found.");
        if (workspace.Stage == "destroyed")
            throw new CodingWorkspaceException("workspace_destroyed", "This coding workspace has already been destroyed.");
        if (await db.CodingWorkspaceOperations.AnyAsync(x => x.OrganizationId == organizationId &&
                x.WorkspaceId == workspaceId && (x.Status == "queued" || x.Status == "running"), ct))
            throw new CodingWorkspaceException("workspace_operation_in_progress", "A workspace operation is already in progress.");

        var now = clock.GetUtcNow();
        var operation = new CodingWorkspaceOperation
        {
            Id = ids.NewId("workspaceop"),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            Kind = "destroy",
            Status = "queued",
            Stage = "requested",
            CreatedAt = now,
            UpdatedAt = now
        };
        workspace.Stage = "destroying";
        workspace.UpdatedAt = now;
        db.CodingWorkspaceOperations.Add(operation);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw new CodingWorkspaceException("workspace_operation_in_progress", "Another workspace operation was accepted concurrently.");
        }
        return operation;
    }

    public async Task<CodingWorkspaceOperation> StartAuthenticationAsync(
        string organizationId, string workspaceId, string provider, string requestedBy, CancellationToken ct)
    {
        provider = NormalizeAuthProvider(provider);
        var workspace = await db.CodingWorkspaces.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == workspaceId, ct)
            ?? throw new CodingWorkspaceException("workspace_not_found", "Coding workspace was not found.");
        if (workspace.Stage is not ("awaiting-auth" or "ready"))
            throw new CodingWorkspaceException("workspace_not_ready", "Workspace bootstrap must reach awaiting-auth before provider authorization can start.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var active = await db.CodingWorkspaceOperations.Where(x =>
            x.OrganizationId == organizationId && x.WorkspaceId == workspaceId &&
            (x.Status == "running" || x.Status == "queued")).ToListAsync(ct);
        if (active.Any(x => x.Status == "queued" || x.LeaseExpiresAt is not null && x.LeaseExpiresAt > now))
            throw new CodingWorkspaceException("workspace_operation_in_progress", "Another workspace operation is already in progress.");
        foreach (var expired in active)
        {
            expired.Status = "failed";
            expired.ErrorCode = "authorization_interrupted";
            expired.ErrorMessage = "The authorization session expired before completion. Retry authentication explicitly.";
            expired.OwnershipToken = null;
            expired.LeaseExpiresAt = null;
            expired.CompletedAt = expired.UpdatedAt = now;
        }

        var operation = new CodingWorkspaceOperation
        {
            Id = ids.NewId("workspaceop"),
            OrganizationId = organizationId,
            WorkspaceId = workspaceId,
            Kind = $"auth-{provider}",
            RequestedBy = requestedBy,
            Status = "running",
            Stage = "awaiting-auth",
            AttemptCount = 1,
            OwnershipToken = Guid.NewGuid().ToString("N"),
            LeaseExpiresAt = now.AddMinutes(20),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.CodingWorkspaceOperations.Add(operation);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw new CodingWorkspaceException("workspace_operation_in_progress", "Another workspace operation was accepted concurrently.");
        }
        await transaction.CommitAsync(ct);
        return operation;
    }

    public async Task<CodingWorkspaceOperation> CompleteAuthenticationAsync(
        string organizationId,
        string workspaceId,
        string provider,
        CompleteCodingWorkspaceAuthRequest request,
        string completedBy,
        CancellationToken ct)
    {
        provider = NormalizeAuthProvider(provider);
        if (request.RepositoryReady && (!request.Authenticated || provider != "github"))
            throw new CodingWorkspaceException("workspace_auth_result_invalid", "Repository readiness can only be recorded after successful GitHub authentication.");
        var operation = await db.CodingWorkspaceOperations.SingleOrDefaultAsync(x =>
            x.Id == request.OperationId && x.OrganizationId == organizationId && x.WorkspaceId == workspaceId, ct)
            ?? throw new CodingWorkspaceException("workspace_operation_not_found", "Workspace authentication operation was not found.");
        if (operation.Kind != $"auth-{provider}" || operation.Status != "running" ||
            operation.RequestedBy != completedBy ||
            operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= clock.GetUtcNow())
            throw new CodingWorkspaceException("workspace_operation_expired", "Workspace authentication operation is expired or belongs to another CLI session. Start a new authentication attempt.");

        var workspace = await db.CodingWorkspaces.SingleAsync(
            x => x.Id == workspaceId && x.OrganizationId == organizationId, ct);
        var now = clock.GetUtcNow();
        if (request.Authenticated && (request.AgentVersion != workspace.RequestedAgentVersion ||
                                      request.T3Version != workspace.RequestedT3Version))
            throw new CodingWorkspaceException("workspace_version_mismatch", "Reported tool versions do not match the pinned workspace versions.");
        if (request.Authenticated)
        {
            workspace.AgentVersion = request.AgentVersion!;
            workspace.T3Version = request.T3Version!;
            workspace.BootstrapStatus = "ready";
            workspace.BootstrapObservedAt = now;
        }
        if (provider == "codex")
            workspace.CodexAuthentication = request.Authenticated ? "authenticated" : "failed";
        else
        {
            workspace.GitHubAuthentication = request.Authenticated ? "authenticated" : "failed";
            workspace.RepositoryStatus = request.RepositoryReady ? "ready" : request.Authenticated ? "blocked" : "unknown";
            workspace.RepositoryObservedAt = request.RepositoryReady || request.Authenticated ? now : null;
        }
        if (provider == "codex") workspace.CodexAuthenticationObservedAt = now;
        else workspace.GitHubAuthenticationObservedAt = now;
        workspace.UpdatedAt = now;
        operation.Status = request.Authenticated ? "completed" : "failed";
        operation.Stage = request.Authenticated ? "completed" : "failed";
        operation.ErrorCode = request.Authenticated ? null : "provider_authentication_failed";
        operation.ErrorMessage = request.Authenticated ? null : "Provider authentication did not complete. Retry the authorization flow.";
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        operation.CompletedAt = operation.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return operation;
    }

    public static CodingWorkspaceResponse ToResponse(CodingWorkspace workspace) => new(
        workspace.Id, workspace.OrganizationId, workspace.Name, workspace.PlanName, workspace.Provider,
        workspace.ProviderAccountId, workspace.ProviderSshKeyId, workspace.SshFingerprint, workspace.Region, workspace.Sku,
        workspace.ProviderServerId, workspace.IpAddress, workspace.AllocationReconciliationRequired,
        workspace.Agent, workspace.AgentVersion,
        workspace.RequestedAgentVersion, workspace.T3Version, workspace.RequestedT3Version,
        workspace.Repository, workspace.Stage, workspace.ProviderStatus, workspace.BootstrapStatus,
        workspace.T3Health, workspace.CodexAuthentication, workspace.GitHubAuthentication,
        workspace.RepositoryStatus, workspace.Connectivity,
        workspace.CreatedAt, workspace.UpdatedAt, workspace.LastObservedAt, workspace.ProviderObservedAt,
        workspace.BootstrapObservedAt, workspace.T3ObservedAt, workspace.CodexAuthenticationObservedAt,
        workspace.GitHubAuthenticationObservedAt,
        workspace.RepositoryObservedAt,
        workspace.LastErrorCode, workspace.LastError);

    public static CodingWorkspaceOperationResponse ToResponse(CodingWorkspaceOperation operation) => new(
        operation.Id, operation.WorkspaceId, operation.Kind, operation.Status, operation.Stage,
        operation.CreatedAt, operation.UpdatedAt, operation.CompletedAt, operation.ErrorCode, operation.ErrorMessage);

    private static (string Display, string Normalized) NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 || value.Any(char.IsControl))
            throw new CodingWorkspaceException("workspace_name_invalid", "Workspace name must be nonempty, at most 128 characters, and contain no control characters.");
        try
        {
            var display = value.Trim().Normalize(NormalizationForm.FormC);
            return (display, display.ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new CodingWorkspaceException("workspace_name_invalid", "Workspace name contains invalid Unicode.");
        }
    }

    private static string NormalizeRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255 || !RepositoryPattern.IsMatch(value))
            throw new CodingWorkspaceException("repository_invalid", "Repository must use OWNER/REPO format.");
        var parts = value.Split('/');
        if (parts.Any(part => part is "." or ".."))
            throw new CodingWorkspaceException("repository_invalid", "Repository owner and name cannot be path components.");
        return value;
    }

    private static string NormalizeAuthProvider(string provider)
    {
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase)) return "codex";
        if (string.Equals(provider, "github", StringComparison.OrdinalIgnoreCase)) return "github";
        throw new CodingWorkspaceException("auth_provider_unsupported", "Supported authentication providers are 'codex' and 'github'.");
    }
}
