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
    public const string T3Version = "0.1.9";
    public const string BootstrapRevision = "coding-workspace-v1";

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
            T3Version = T3Version,
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
        await db.SaveChangesAsync(ct);
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
        await db.SaveChangesAsync(ct);
        return operation;
    }

    public static CodingWorkspaceResponse ToResponse(CodingWorkspace workspace) => new(
        workspace.Id, workspace.OrganizationId, workspace.Name, workspace.PlanName, workspace.Provider,
        workspace.ProviderServerId, workspace.IpAddress, workspace.Agent, workspace.AgentVersion,
        workspace.T3Version, workspace.Repository, workspace.Stage, workspace.Connectivity,
        workspace.CreatedAt, workspace.UpdatedAt, workspace.LastObservedAt,
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
        return value;
    }
}
