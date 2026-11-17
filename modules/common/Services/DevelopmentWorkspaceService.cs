using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed class DevelopmentWorkspaceException(string code, string message, bool conflict = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Conflict { get; } = conflict;
}

public sealed class DevelopmentWorkspaceService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider clock,
    IOptions<DevelopmentWorkspaceOptions> options)
{
    private DevelopmentWorkspaceOptions Policy => options.Value;

    public async Task<DevelopmentWorkspace> CreateAsync(CreateDevelopmentWorkspaceRequest request, string actorId, CancellationToken ct)
    {
        EnsureEnabled();
        ValidateRequest(request);
        var existing = await db.DevelopmentWorkspaces.SingleOrDefaultAsync(
            x => x.OrganizationId == request.OrganizationId && x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.Name != request.Name.Trim() || existing.ImageId != request.ImageId
                || existing.Profile != request.Profile || existing.PlanId != request.PlanId
                || existing.VolumeSizeGb != request.VolumeSizeGb)
                throw Conflict("workspace_idempotency_conflict", "This idempotency key was already used with a different workspace request.");
            return existing;
        }

        if (!Policy.Images.TryGetValue(request.ImageId, out var image)
            || string.IsNullOrWhiteSpace(image.ProviderImageId) || !IsSha256(image.ManifestSha256)
            || image.Architecture != "linux/amd64")
            throw Invalid("workspace_image_invalid", "Select an enabled, registered linux/amd64 image with a verified manifest.");
        if (!Policy.Profiles.TryGetValue(request.Profile, out var profile)
            || profile.MemoryGiB is not (8 or 16) || profile.CpuCount <= 0
            || profile.ComputeHourlyRate <= 0 || !profile.AllowedPlanIds.Contains(request.PlanId, StringComparer.Ordinal))
            throw Invalid("workspace_profile_invalid", "The selected size profile is not configured for this plan.");

        var plan = await db.ProviderPlans.Include(x => x.ProviderAccount)
            .SingleOrDefaultAsync(x => x.Id == request.PlanId && x.OrganizationId == request.OrganizationId, ct);
        if (plan is null || plan.ProviderAccount.ProviderType != CloudProvider.Hetzner
            || !plan.Enabled || !plan.ProviderAccount.Enabled || plan.BuildPlatform != "linux/amd64")
            throw Invalid("workspace_plan_invalid", "Select an enabled Hetzner plan with linux/amd64 placement.");

        var now = clock.GetUtcNow();
        var orgCount = await db.DevelopmentWorkspaces.CountAsync(
            x => x.OrganizationId == request.OrganizationId && x.DesiredState == "running", ct);
        var activeCount = await db.DevelopmentWorkspaces.CountAsync(x => x.DesiredState == "running", ct);
        if (orgCount >= Policy.MaxWorkspacesPerOrganization || activeCount >= Policy.MaxActiveWorkspaces)
            throw Conflict("workspace_admission_limit", "Workspace admission is at the configured active-workspace limit.");

        var proposedMonthly = profile.ComputeHourlyRate * 720m + Policy.VolumeMonthlyRatePerGb * request.VolumeSizeGb;
        var currentMonthly = await EstimateCurrentSpendAsync(ct);
        if (Policy.MaxEstimatedMonthlySpend <= 0 || currentMonthly + proposedMonthly > Policy.MaxEstimatedMonthlySpend)
            throw Conflict("workspace_spend_limit", "The estimated monthly workspace spend exceeds the configured admission threshold.");

        var workspace = new DevelopmentWorkspace
        {
            Id = ids.NewId("workspace"),
            OrganizationId = request.OrganizationId,
            CreatedBy = actorId,
            Name = request.Name.Trim(),
            ImageId = request.ImageId,
            Profile = request.Profile,
            PlanId = plan.Id,
            ProviderAccountId = plan.ProviderAccountId,
            Region = plan.Region,
            Sku = plan.Sku,
            ProviderImage = image.ProviderImageId,
            VolumeSizeGb = request.VolumeSizeGb,
            IdempotencyKey = request.IdempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.DevelopmentWorkspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<DevelopmentWorkspace> StartAsync(string workspaceId, string organizationId, CancellationToken ct)
    {
        EnsureEnabled();
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        if (workspace.DesiredState == "running" && workspace.Phase is not ("blocked" or "recovering"))
            return workspace;
        if (workspace.Phase is "draining" or "deleting" or "recovering")
            throw Conflict("workspace_transition_in_progress", "The workspace must finish reconciliation before it can start.");

        await EnsureAdmissionAsync(workspace, ct);
        workspace.Generation++;
        workspace.DesiredState = "running";
        workspace.Phase = "provisioning";
        workspace.OperationId = ids.NewId("workspaceop");
        workspace.ErrorCode = null;
        workspace.ErrorMessage = null;
        workspace.ShutdownDeadline = null;
        workspace.ShutdownReason = null;
        workspace.QuiescedAt = null;
        workspace.QuiescedGeneration = null;
        workspace.Revision++;
        workspace.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<DevelopmentWorkspace> StopAsync(string workspaceId, string organizationId, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        if (workspace.DesiredState == "stopped" && workspace.Phase == "stopped") return workspace;
        if (workspace.Phase is "deleting" or "recovering")
            throw Conflict("workspace_transition_in_progress", "The workspace must finish reconciliation before it can stop.");

        workspace.DesiredState = "stopped";
        workspace.Phase = "draining";
        workspace.OperationId = ids.NewId("workspaceop");
        workspace.ShutdownDeadline = clock.GetUtcNow();
        workspace.ShutdownReason = "manual";
        workspace.ErrorCode = null;
        workspace.ErrorMessage = null;
        workspace.Revision++;
        workspace.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<DevelopmentWorkspace> CancelIdleStopAsync(string workspaceId, string organizationId, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        if (workspace.Phase != "draining" || workspace.ShutdownReason != "idle"
            || workspace.QuiescedGeneration == workspace.Generation)
            throw Conflict("workspace_shutdown_committed", "The pending shutdown can no longer be cancelled safely.");
        workspace.DesiredState = "running";
        workspace.Phase = "ready";
        workspace.ShutdownDeadline = null;
        workspace.ShutdownReason = null;
        workspace.LastActivityAt = clock.GetUtcNow();
        workspace.Revision++;
        workspace.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<DevelopmentWorkspaceTaskLease> RenewTaskLeaseAsync(
        string workspaceId, string organizationId, string taskId, CancellationToken ct)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128 || taskId.Any(char.IsControl))
            throw Invalid("workspace_task_id_invalid", "Task ID must contain 1 to 128 printable characters.");
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        if (workspace.Phase != "ready" || workspace.DesiredState != "running")
            throw Conflict("workspace_not_ready", "Tasks can be leased only while the current workspace generation is ready.");

        var now = clock.GetUtcNow();
        var lease = await db.DevelopmentWorkspaceTaskLeases.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.TaskId == taskId, ct);
        if (lease is not null && lease.Generation != workspace.Generation)
            throw Conflict("workspace_task_generation_stale", "Use a new task ID for a task admitted to a different workspace generation.");
        if (lease is null)
        {
            lease = new DevelopmentWorkspaceTaskLease
            {
                Id = ids.NewId("tasklease"),
                WorkspaceId = workspace.Id,
                TaskId = taskId,
                Generation = workspace.Generation,
                CreatedAt = now
            };
            db.DevelopmentWorkspaceTaskLeases.Add(lease);
        }
        lease.Generation = workspace.Generation;
        lease.Status = "active";
        lease.ExpiresAt = now.AddSeconds(Math.Clamp(Policy.TaskLeaseSeconds, 30, 900));
        lease.UpdatedAt = now;
        workspace.LastActivityAt = now;
        workspace.ShutdownDeadline = null;
        workspace.Revision++;
        workspace.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return lease;
    }

    public async Task<DevelopmentWorkspace> ScheduleIdleStopAsync(
        string workspaceId, string organizationId, DateTimeOffset deadline, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        if (!await IsIdleAsync(workspace, ct))
            throw Conflict("workspace_active", "Workspace activity or a registered task lease prevents idle shutdown.");
        workspace.DesiredState = "stopped";
        workspace.Phase = "draining";
        workspace.OperationId = ids.NewId("workspaceop");
        workspace.ShutdownDeadline = deadline;
        workspace.ShutdownReason = "idle";
        workspace.Revision++;
        workspace.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task<DevelopmentWorkspaceTaskLease> CompleteTaskAsync(
        string workspaceId, string organizationId, string taskId, bool cancelled, CancellationToken ct)
    {
        var workspace = await FindAsync(workspaceId, organizationId, ct);
        var lease = await db.DevelopmentWorkspaceTaskLeases.SingleOrDefaultAsync(
            x => x.WorkspaceId == workspaceId && x.TaskId == taskId && x.Generation == workspace.Generation, ct)
            ?? throw new DevelopmentWorkspaceException("workspace_task_not_found", "Task lease was not found.");
        lease.Status = cancelled ? "cancelled" : "completed";
        lease.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return lease;
    }

    public async Task<bool> IsIdleAsync(DevelopmentWorkspace workspace, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (workspace.DesiredState != "running" || workspace.Phase != "ready"
            || workspace.LastActivityAt is null
            || workspace.LastActivityAt > now.AddMinutes(-Math.Clamp(Policy.IdleGraceMinutes, 5, 1440)))
            return false;
        return !await db.DevelopmentWorkspaceTaskLeases.AnyAsync(x =>
            x.WorkspaceId == workspace.Id && x.Generation == workspace.Generation
            && x.Status == "active" && x.ExpiresAt > now, ct);
    }

    public async Task<IReadOnlyList<DevelopmentWorkspace>> ListAsync(string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<DevelopmentWorkspace?> GetAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workspaceId && x.OrganizationId == organizationId, ct);

    public DevelopmentWorkspaceResponse ToResponse(DevelopmentWorkspace workspace)
    {
        var profile = Policy.Profiles.GetValueOrDefault(workspace.Profile);
        return new(workspace.Id, workspace.OrganizationId, workspace.Name, workspace.ImageId, workspace.Profile,
            workspace.PlanId, workspace.ProviderAccountId, workspace.Region, workspace.Sku, workspace.VolumeSizeGb,
            workspace.DesiredState, workspace.Phase, workspace.Generation, workspace.ProviderServerId, workspace.ProviderVolumeId,
            workspace.ErrorCode, workspace.ErrorMessage, workspace.LastActivityAt, workspace.ShutdownDeadline, workspace.LastReadyAt,
            profile is null ? 0 : profile.ComputeHourlyRate * 720m,
            Policy.VolumeMonthlyRatePerGb * workspace.VolumeSizeGb, Policy.Currency, Policy.PricingUpdatedAt,
            workspace.Revision, workspace.CreatedAt, workspace.UpdatedAt);
    }

    private async Task EnsureAdmissionAsync(DevelopmentWorkspace workspace, CancellationToken ct)
    {
        var active = await db.DevelopmentWorkspaces.CountAsync(x => x.DesiredState == "running", ct);
        var orgActive = await db.DevelopmentWorkspaces.CountAsync(
            x => x.OrganizationId == workspace.OrganizationId && x.DesiredState == "running", ct);
        if (active >= Policy.MaxActiveWorkspaces || orgActive >= Policy.MaxWorkspacesPerOrganization)
            throw Conflict("workspace_admission_limit", "Workspace admission is at the configured active-workspace limit.");
        var spend = await EstimateCurrentSpendAsync(ct);
        if (spend > Policy.MaxEstimatedMonthlySpend)
            throw Conflict("workspace_spend_limit", "The estimated monthly workspace spend exceeds the configured admission threshold.");
    }

    private async Task<decimal> EstimateCurrentSpendAsync(CancellationToken ct)
    {
        var all = await db.DevelopmentWorkspaces.AsNoTracking()
            .Select(x => new { x.Profile, x.VolumeSizeGb, x.DesiredState }).ToListAsync(ct);
        return all.Sum(x => Policy.VolumeMonthlyRatePerGb * x.VolumeSizeGb
            + (x.DesiredState == "running" && Policy.Profiles.TryGetValue(x.Profile, out var profile)
                ? profile.ComputeHourlyRate * 720m : 0m));
    }

    private async Task<DevelopmentWorkspace> FindAsync(string id, string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, ct)
        ?? throw new DevelopmentWorkspaceException("workspace_not_found", "Workspace was not found.");

    private void EnsureEnabled()
    {
        if (!Policy.Enabled) throw new DevelopmentWorkspaceException(
            "workspaces_disabled", "Development workspaces are disabled by the operator.", true);
    }

    private void ValidateRequest(CreateDevelopmentWorkspaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationId)
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128
            || request.Name.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.ImageId) || request.ImageId.Length > 128
            || string.IsNullOrWhiteSpace(request.Profile) || request.Profile.Length > 32
            || string.IsNullOrWhiteSpace(request.PlanId) || request.PlanId.Length > 128
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
            || request.IdempotencyKey.Any(char.IsControl))
            throw Invalid("workspace_request_invalid", "Workspace name, image, profile, plan and idempotency key are required.");
        if (request.VolumeSizeGb is < 10 or > 10000)
            throw Invalid("workspace_volume_size_invalid", "Workspace volumes must be between 10 and 10,000 GB.");
        if (Policy.MaxActiveWorkspaces is < 1 or > 100 || Policy.MaxWorkspacesPerOrganization is < 1 or > 100
            || Policy.MaxEstimatedMonthlySpend <= 0 || Policy.VolumeMonthlyRatePerGb < 0)
            throw new DevelopmentWorkspaceException("workspace_policy_invalid", "Workspace admission and pricing policy is incomplete.");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static DevelopmentWorkspaceException Invalid(string code, string message) => new(code, message);
    private static DevelopmentWorkspaceException Conflict(string code, string message) => new(code, message, true);
}
