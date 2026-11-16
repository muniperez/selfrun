using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed partial class DevelopmentWorkspaceService(
    MinicloudDbContext db, IIdGenerator ids, TimeProvider clock, IOptions<DevelopmentWorkspaceOptions> options,
    MaintenanceParticipant? maintenanceParticipant = null)
{
    private static readonly SemaphoreSlim AdmissionGate = new(1, 1);
    private readonly MaintenanceParticipant maintenance = maintenanceParticipant ?? MaintenanceParticipant.CreateLocal("worker", clock);
    private DevelopmentWorkspaceOptions Policy => options.Value;
    private DateTimeOffset Now => clock.GetUtcNow();

    public Task<DevelopmentWorkspace> CreateAsync(CreateDevelopmentWorkspaceRequest request, string actorId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateText(request.OrganizationId, 128); ValidateText(request.Name, 128);
            ValidateText(request.IdempotencyKey, 128); ValidateText(actorId, 256);
            if (request.VolumeSizeGb is < 10 or > 10000) throw Invalid("workspace_volume_size_invalid", "Volume size must be 10–10,000 GB.");
            var digest = Digest(request with { Name = request.Name.Trim() });
            var existing = await db.DevelopmentWorkspaces.Include(x => x.Vms).ThenInclude(x => x.TaskLeases).SingleOrDefaultAsync(
                x => x.OrganizationId == request.OrganizationId && x.IdempotencyKey == request.IdempotencyKey, ct);
            if (existing is not null)
            {
                if (existing.RequestDigest != digest) throw Conflict("workspace_idempotency_conflict", "Idempotency key already has a different request.");
                return existing;
            }
            if (!await db.Organizations.AnyAsync(x => x.Id == request.OrganizationId, ct))
                throw Invalid("workspace_organization_invalid", "Organization was not found.");
            if (await db.DevelopmentWorkspaces.CountAsync(x => x.OrganizationId == request.OrganizationId, ct) >= Policy.MaxWorkspacesPerOrganization)
                throw Conflict("workspace_admission_limit", "Organization workspace limit reached.");
            var selection = await SelectAsync(request.OrganizationId, request.ImageId, request.Profile, request.PlanId, ct);
            var workspace = new DevelopmentWorkspace
            {
                Id = ids.NewId("workspace"), OrganizationId = request.OrganizationId, CreatedBy = actorId,
                Name = request.Name.Trim(), ProviderAccountId = selection.Plan.ProviderAccountId, Region = selection.Plan.Region,
                VolumeSizeGb = request.VolumeSizeGb, StorageMonthlyRate = Policy.VolumeMonthlyRatePerGb * request.VolumeSizeGb,
                Currency = Policy.Currency, PricingUpdatedAt = Policy.PricingUpdatedAt,
                IdempotencyKey = request.IdempotencyKey, RequestDigest = digest, CreatedAt = Now, UpdatedAt = Now
            };
            await CheckSpendAsync(workspace.OrganizationId, workspace.StorageMonthlyRate, ct);
            var primary = NewVm(workspace, DevelopmentWorkspaceVmKinds.Primary, "primary", digest);
            ApplySelection(primary, selection, request.ImageId, request.Profile);
            workspace.PrimaryVmId = primary.Id;
            workspace.Vms.Add(primary);
            db.DevelopmentWorkspaces.Add(workspace);
            return workspace;
        }, ct);

    public Task<DevelopmentWorkspace> StartAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, null, null, StartCoreAsync, ct);

    public Task<DevelopmentWorkspace> StartVmAsync(string workspaceId, string organizationId, string vmId, long generation, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, generation, StartCoreAsync, ct);

    public Task<DevelopmentWorkspace> StopAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, null, null, (w, v, token) => StopCoreAsync(w, v, "manual", Now, token), ct);

    public Task<DevelopmentWorkspace> StopVmAsync(string workspaceId, string organizationId, string vmId, long generation, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, generation, (w, v, token) => StopCoreAsync(w, v, "manual", Now, token), ct);

    public Task<DevelopmentWorkspace> StopAllAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            foreach (var vm in workspace.Vms) await StopCoreAsync(workspace, vm, "manual", Now, ct);
            Touch(workspace);
            return workspace;
        }, ct);

    public Task<DevelopmentWorkspace> LaunchEphemeralAsync(string workspaceId, string organizationId,
        LaunchEphemeralDevelopmentWorkspaceVmRequest request, string actorId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            ValidateText(request.IdempotencyKey, 128);
            ValidateGitRequest(request);
            var digest = Digest(request);
            var existing = workspace.Vms.SingleOrDefault(x => x.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.RequestDigest != digest) throw Conflict("workspace_idempotency_conflict", "Idempotency key already has a different launch request.");
                return workspace;
            }
            var credentialRef = RepositoryCredential(ApprovedRepository(organizationId, request.Repository, request.BaseRef), organizationId);
            if (request.RepositoryCredentialRef is not null && request.RepositoryCredentialRef != credentialRef)
                throw Invalid("workspace_git_credential_invalid", "Repository credentials must match the organization's explicitly approved reference.");
            var selection = await SelectAsync(organizationId, request.ImageId, request.Profile, request.PlanId, ct);
            EnsurePlacement(workspace, selection.Plan);
            var vm = NewVm(workspace, DevelopmentWorkspaceVmKinds.Ephemeral, request.IdempotencyKey, digest);
            ApplySelection(vm, selection, request.ImageId, request.Profile);
            vm.Repository = request.Repository;
            vm.BaseRef = request.BaseRef;
            vm.BaseCommit = request.BaseCommit.ToLowerInvariant();
            vm.WorkBranch = $"minicloud/{vm.Id}/g1";
            vm.PushAuthorized = request.PushAuthorized;
            vm.RepositoryCredentialRef = credentialRef;
            vm.IgnoredDataAcknowledgedAt = Now;
            vm.GitStatus = "base_verification_pending";
            workspace.Vms.Add(vm);
            db.DevelopmentWorkspaceVms.Add(vm);
            await StartCoreAsync(workspace, vm, ct);
            Touch(workspace);
            return workspace;
        }, ct);

    public Task<DevelopmentWorkspace> ResizeAsync(string workspaceId, string organizationId, string? vmId,
        DevelopmentWorkspaceResizeRequest request, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, request.Generation, async (workspace, vm, token) =>
        {
            var selection = await SelectAsync(organizationId, request.ImageId, request.Profile, request.PlanId, token);
            EnsurePlacement(workspace, selection.Plan);
            vm.NextImageId = request.ImageId; vm.NextProfile = request.Profile; vm.NextPlanId = request.PlanId;
            Touch(vm);
        }, ct);

    public Task<DevelopmentWorkspace> CancelIdleStopAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, null, null, CancelIdleCoreAsync, ct);

    public Task<DevelopmentWorkspace> CancelIdleStopVmAsync(string workspaceId, string organizationId, string vmId, long generation, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, generation, CancelIdleCoreAsync, ct);

    private async Task CancelIdleCoreAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (vm.Phase is not ("ready" or "draining") || vm.ShutdownReason != "idle"
            || vm.OperationId is null || vm.QuiescedGeneration == vm.Generation)
            throw Conflict("workspace_shutdown_committed", "Only an uncommitted idle stop can be cancelled.");
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId, ct);
        // A claimed worker may already be freezing writes. Do not race an in-flight remote command.
        if (operation.Attempts > 0) throw Conflict("workspace_shutdown_committed", "Shutdown has already been claimed for reconciliation.");
        operation.Status = "cancelled"; operation.Revision++; operation.UpdatedAt = Now;
        vm.DesiredState = "running"; vm.Phase = "ready"; vm.OperationId = null;
        vm.ShutdownReason = null; vm.ShutdownDeadline = null; vm.LastActivityAt = Now;
        Touch(vm);
    }

    public Task<DevelopmentWorkspace> DiscardAsync(string workspaceId, string organizationId, string vmId,
        DevelopmentWorkspaceDiscardRequest request, string actorId, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, request.Generation, async (workspace, vm, token) =>
        {
            if (vm.Kind != DevelopmentWorkspaceVmKinds.Ephemeral || !request.DiscardLocalData || !request.AcknowledgeIgnoredDataLoss)
                throw Invalid("workspace_discard_invalid", "Explicit acknowledgement is required to discard ephemeral data.");
            if (vm.Phase == "stopped") throw Conflict("workspace_not_running", "VM has no live generation to discard.");
            ValidateText(actorId, 256);
            await StopCoreAsync(workspace, vm, "discard", Now, token);
            vm.DiscardGeneration = vm.Generation; vm.DiscardOperationId = vm.OperationId;
            vm.DiscardRequestedBy = actorId; vm.DiscardRequestedAt = Now;
            Touch(vm);
        }, ct);

    public Task<DevelopmentWorkspaceTaskLease> RenewTaskLeaseAsync(string workspaceId, string organizationId,
        string vmId, long generation, string taskId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateText(taskId, 128);
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var vm = Vm(workspace, vmId, generation);
            if (vm.Phase != "ready" || vm.DesiredState != "running")
                throw Conflict("workspace_not_ready", "Tasks require a ready VM with open admission.");
            var lease = await db.DevelopmentWorkspaceTaskLeases.SingleOrDefaultAsync(
                x => x.VmId == vm.Id && x.Generation == generation && x.TaskId == taskId, ct);
            if (lease?.Status is "completed" or "cancelled")
                throw Conflict("workspace_task_completed", "A completed task cannot be reopened.");
            await CancelScheduledIdleForActivityAsync(workspace, vm, ct);
            if (lease is null)
            {
                lease = new() { Id = ids.NewId("tasklease"), WorkspaceId = workspace.Id, VmId = vm.Id,
                    TaskId = taskId, Generation = generation, CreatedAt = Now };
                db.DevelopmentWorkspaceTaskLeases.Add(lease);
            }
            lease.ExpiresAt = Now.AddSeconds(Policy.TaskLeaseSeconds); lease.UpdatedAt = Now;
            vm.LastActivityAt = Now; Touch(vm); Touch(workspace);
            return lease;
        }, ct);

    public Task<DevelopmentWorkspaceTaskLease> CompleteTaskAsync(string workspaceId, string organizationId,
        string vmId, long generation, string taskId, bool cancelled, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var vm = Vm(workspace, vmId, generation);
            var lease = await db.DevelopmentWorkspaceTaskLeases.SingleOrDefaultAsync(
                x => x.VmId == vm.Id && x.Generation == generation && x.TaskId == taskId, ct)
                ?? throw Invalid("workspace_task_not_found", "Task lease was not found.");
            if (lease.Status == "active") lease.Status = cancelled ? "cancelled" : "completed";
            if (vm.DesiredState == "running" && vm.Phase == "ready")
                await CancelScheduledIdleForActivityAsync(workspace, vm, ct);
            lease.UpdatedAt = Now; vm.LastActivityAt = Now; Touch(vm); Touch(workspace);
            return lease;
        }, ct);

    public Task<DevelopmentWorkspace> ActivityAsync(string workspaceId, string organizationId, string vmId, long generation, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, generation, async (workspace, vm, token) =>
        {
            if (vm.DesiredState != "running" || vm.Phase != "ready")
            {
                if (!await CanRecordRecoveryActivityAsync(vm, token))
                    throw Conflict("workspace_not_ready", "Activity cannot reopen closed task admission.");
            }
            else
                await CancelScheduledIdleForActivityAsync(workspace, vm, token);
            vm.LastActivityAt = Now; Touch(vm);
        }, ct);

    public async Task<bool> CanRecordRecoveryActivityAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (vm.DesiredState != "stopped" || !vm.GatewayReady || vm.QuiescedGeneration == vm.Generation || vm.OperationId is null
            || vm.LastHeartbeatAt is null || vm.LastHeartbeatAt < Now.AddSeconds(-Policy.HeartbeatTimeoutSeconds))
            return false;
        var operation = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == vm.OperationId && x.WorkspaceId == vm.WorkspaceId && x.VmId == vm.Id && x.Generation == vm.Generation
            && x.Kind == "stop" && (x.Status == "pending" || x.Status == "running"), ct);
        if (operation is null
            || !(vm.Phase == "draining" && operation.Step is "pending" or "wait-for-tasks"
                || vm.Phase == "awaiting_git_save" && operation.Step == "wait-for-save" && vm.ErrorCode == "workspace_git_save_required"))
            return false;
        var routes = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().Where(x =>
            x.OrganizationId == vm.OrganizationId && x.WorkspaceId == vm.WorkspaceId && x.VmId == vm.Id
            && x.Generation == vm.Generation && x.Service == "t3" && x.RevokedAt == null).ToListAsync(ct);
        return routes.Any(x => x.ExpiresAt > Now);
    }

    private Task CancelScheduledIdleForActivityAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, CancellationToken ct) =>
        vm.ShutdownReason == "idle" && vm.OperationId is not null
            ? CancelIdleCoreAsync(workspace, vm, ct) : Task.CompletedTask;

    public Task<DevelopmentWorkspace> ScheduleIdleStopAsync(string workspaceId, string organizationId, string vmId,
        long generation, DateTimeOffset deadline, CancellationToken ct) =>
        MutateAsync(workspaceId, organizationId, vmId, generation, async (workspace, vm, token) =>
        {
            if (!(await maintenance.CheckAsync(db, requireOpen: true, token)).IsAdmitted)
                throw Conflict("workspace_maintenance_fenced", "Maintenance authority is not open for automatic idle-stop admission.");
            if (deadline < Now || !await IsIdleAsync(vm, token))
                throw Conflict("workspace_active", "Activity, missing heartbeat or unresolved tasks prevent idle shutdown.");
            await StopCoreAsync(workspace, vm, "idle", deadline, token);
        }, ct);

    public async Task<bool> IsIdleAsync(DevelopmentWorkspaceVm vm, CancellationToken ct) =>
        vm.DesiredState == "running" && vm.Phase == "ready" && vm.LastActivityAt is not null
        && vm.LastActivityAt <= Now.AddMinutes(-Policy.IdleGraceMinutes)
        && vm.LastHeartbeatAt >= Now.AddSeconds(-Policy.HeartbeatTimeoutSeconds)
        && !await HasUnresolvedTasksAsync(vm, ct);

    public async Task<bool> HasUnresolvedTasksAsync(DevelopmentWorkspaceVm vm, CancellationToken ct) =>
        await db.DevelopmentWorkspaceTaskLeases.AnyAsync(x => x.VmId == vm.Id && x.Generation == vm.Generation && x.Status == "active", ct);

    public async Task<IReadOnlyList<DevelopmentWorkspace>> ListAsync(string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.AsNoTracking().Include(x => x.Vms).ThenInclude(x => x.TaskLeases).Where(x => x.OrganizationId == organizationId).OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<DevelopmentWorkspace?> GetAsync(string workspaceId, string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.AsNoTracking().Include(x => x.Vms).ThenInclude(x => x.TaskLeases).SingleOrDefaultAsync(x => x.Id == workspaceId && x.OrganizationId == organizationId, ct);

    public DevelopmentWorkspaceResponse ToResponse(DevelopmentWorkspace workspace)
    {
        var primary = workspace.Vms.Single(x => x.Id == workspace.PrimaryVmId && x.Kind == DevelopmentWorkspaceVmKinds.Primary);
        return new(workspace.Id, workspace.OrganizationId, workspace.Name, primary.ImageId, primary.Profile,
            primary.PlanId, workspace.ProviderAccountId, workspace.Region, primary.Sku, workspace.VolumeSizeGb,
            primary.DesiredState, primary.Phase, primary.Generation, primary.ProviderServerId, workspace.ProviderVolumeId,
            primary.ErrorCode, primary.ErrorMessage, primary.LastActivityAt, primary.ShutdownDeadline, primary.LastReadyAt,
            workspace.Vms.Where(x => x.ComputeReserved).Sum(x => x.ComputeHourlyRate * 720m), workspace.StorageMonthlyRate,
            workspace.Currency, workspace.PricingUpdatedAt, workspace.Revision, workspace.CreatedAt, workspace.UpdatedAt,
            workspace.Vms.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(ToVmResponse).ToArray(), workspace.PrimaryVmId, workspace.VolumeStatus);
    }

    public DevelopmentWorkspaceVmResponse ToVmResponse(DevelopmentWorkspaceVm vm) =>
        new(vm.Id, vm.Kind, vm.Generation, vm.DesiredState, vm.Phase, vm.ImageId, vm.Profile, vm.PlanId,
            vm.ProviderAccountId, vm.Region, vm.Sku, vm.ProviderServerId, vm.ErrorCode, vm.ErrorMessage,
            vm.LastActivityAt, vm.ShutdownDeadline, vm.LastReadyAt, vm.Repository, vm.BaseRef, vm.BaseCommit, vm.WorkBranch,
            vm.PushAuthorized, vm.IgnoredDataAcknowledgedAt is not null, vm.GitStatus, vm.FinalHead, vm.VerifiedRemoteRef,
            vm.VerifiedRemoteCommit, vm.NextImageId, vm.NextProfile, vm.NextPlanId, vm.ComputeHourlyRate,
            vm.ComputeReserved ? vm.ComputeHourlyRate * 720m : 0, vm.ComputeReserved, vm.Revision,
            vm.TaskLeases.Where(x => x.Generation == vm.Generation).Select(x =>
                new DevelopmentWorkspaceTaskLeaseResponse(x.VmId, x.TaskId, x.Generation, x.Status, x.ExpiresAt)).ToArray(), vm.StartAfterStop);

    private async Task StartCoreAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (vm.DesiredState == "running") return;
        if (vm.Kind == DevelopmentWorkspaceVmKinds.Ephemeral)
            vm.RepositoryCredentialRef = RepositoryCredential(ApprovedRepository(workspace.OrganizationId, vm.Repository, vm.BaseRef),
                workspace.OrganizationId);
        if (vm.Phase is "draining" or "deleting" or "awaiting_git_save")
        {
            var next = await SelectAsync(workspace.OrganizationId, vm.NextImageId ?? vm.ImageId,
                vm.NextProfile ?? vm.Profile, vm.NextPlanId ?? vm.PlanId, ct);
            EnsurePlacement(workspace, next.Plan);
            vm.StartAfterStop = true; vm.StartRequestedAt = Now; Touch(vm);
            return;
        }
        if (vm.Phase != "stopped" || vm.ComputeReserved || vm.ProviderServerId is not null
            || vm.ProviderFirewallId is not null || vm.ProviderIpId is not null)
            throw Conflict("workspace_transition_in_progress", "Previous generation must finish reconciliation before starting.");
        var gitRecovery = await SavedGitSourceAsync(vm, ct);
        var imageId = vm.NextImageId ?? vm.ImageId;
        var profile = vm.NextProfile ?? vm.Profile;
        var selection = await SelectAsync(workspace.OrganizationId, imageId, profile, vm.NextPlanId ?? vm.PlanId, ct);
        EnsurePlacement(workspace, selection.Plan);
        await CheckAdmissionAsync(workspace, selection.Profile.ComputeHourlyRate, ct);
        ApplySelection(vm, selection, imageId, profile);
        if (gitRecovery is not null) vm.BaseCommit = gitRecovery.Commit;
        vm.NextImageId = null; vm.NextProfile = null; vm.NextPlanId = null;
        vm.Generation++; vm.ComputeReserved = true; vm.DesiredState = "running"; vm.Phase = "provisioning";
        vm.StartAfterStop = false; vm.StartRequestedAt = Now;
        vm.EnrollmentTokenDigest = null; vm.EnrollmentTokenExpiresAt = null; vm.EnrolledAt = null;
        vm.RuntimeTokenHash = null; vm.RuntimeTokenExpiresAt = null; vm.GatewayReady = false;
        vm.SshBootstrapSecretRef = null; vm.RuntimeCredentialRef = null; vm.SshHostKeyGeneration = null;
        vm.SshHostKey = null; vm.SshHostKeyFingerprint = null; vm.LastHeartbeatAt = null;
        vm.ShutdownDeadline = null; vm.ShutdownReason = null; vm.QuiescedAt = null; vm.QuiescedGeneration = null;
        vm.QuiescedOperationId = null; vm.QuiescenceReceipt = null; vm.GitSavedGeneration = null;
        vm.GitSavedOperationId = null; vm.GitSavedAt = null; vm.GitSaveReceipt = null;
        vm.DiscardGeneration = null; vm.DiscardOperationId = null; vm.DiscardRequestedBy = null; vm.DiscardRequestedAt = null;
        vm.BaseVerifiedAt = null; vm.BaseVerifiedGeneration = null; vm.FinalHead = null;
        vm.VerifiedRemoteRef = null; vm.VerifiedRemoteCommit = null; vm.LastActivityAt = null;
        vm.ErrorCode = null; vm.ErrorMessage = null;
        if (vm.Kind == DevelopmentWorkspaceVmKinds.Ephemeral)
        {
            vm.WorkBranch = $"minicloud/{vm.Id}/g{vm.Generation}";
            vm.GitStatus = "base_verification_pending";
        }
        if (selection.Plan.ProvisioningLockedAt is null)
        {
            selection.Plan.ProvisioningLockedAt = Now; selection.Plan.Revision++; selection.Plan.UpdatedAt = Now;
        }
        AddOperation(workspace, vm, "start", gitRecovery);
        Touch(vm);
    }

    private async Task StopCoreAsync(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, string reason, DateTimeOffset deadline, CancellationToken ct)
    {
        if (reason != "idle") { vm.StartAfterStop = false; Touch(vm); }
        if (vm.Phase == "stopped") return;
        if (vm.DesiredState == "stopped")
        {
            if (vm.OperationId is not null)
            {
                var existing = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId, ct);
                if (existing.Kind == "stop" && existing.Status == "blocked")
                {
                    existing.Status = "pending"; existing.LeaseExpiresAt = null; existing.LeaseToken = null;
                    existing.Revision++; existing.UpdatedAt = Now;
                }
            }
            return;
        }
        if (vm.OperationId is not null)
        {
            var previous = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId, ct);
            if (reason == "idle" && vm.ShutdownReason == "idle" && previous.Kind == "stop"
                && previous.Status == "pending" && previous.Attempts == 0)
                return;
            if (previous.Status is "pending" or "running")
            {
                if (previous.LeaseExpiresAt > Now)
                    throw Conflict("workspace_transition_in_progress", "Wait for the in-flight provider operation before stopping.");
                previous.Status = "superseded"; previous.Revision++; previous.UpdatedAt = Now;
            }
        }
        if (reason != "idle") { vm.DesiredState = "stopped"; vm.Phase = "draining"; }
        vm.ShutdownReason = reason; vm.ShutdownDeadline = deadline;
        AddOperation(workspace, vm, "stop");
        Touch(vm);
    }

    private void AddOperation(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, string kind,
        DevelopmentWorkspaceGitRecoverySource? gitRecovery = null)
    {
        var operation = new DevelopmentWorkspaceOperation
        {
            Id = ids.NewId("workspaceop"), WorkspaceId = workspace.Id, VmId = vm.Id, Generation = vm.Generation,
            Kind = kind, PlanId = vm.PlanId, ProviderAccountId = vm.ProviderAccountId, Region = vm.Region, Sku = vm.Sku,
            // Older workers must not discard unknown recovery evidence and bootstrap the wrong source.
            Step = gitRecovery is null ? "pending" : "git-recovery-pending",
            ImageId = vm.ImageId, ProviderImage = vm.ProviderImage, ImageManifestSha256 = vm.ImageManifestSha256,
            ComputeHourlyRate = vm.ComputeHourlyRate, CreatedAt = Now, UpdatedAt = Now,
            ProviderEvidenceJson = gitRecovery is null ? null
                : JsonSerializer.Serialize(new { gitRecovery }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        db.DevelopmentWorkspaceOperations.Add(operation); vm.OperationId = operation.Id;
        if (kind == "start") vm.AllocationOperationId = operation.Id;
    }

    private async Task CheckAdmissionAsync(DevelopmentWorkspace workspace, decimal rate, CancellationToken ct)
    {
        var reserved = await db.DevelopmentWorkspaceVms.Where(x => x.ComputeReserved)
            .Select(x => new { x.OrganizationId, x.WorkspaceId }).ToListAsync(ct);
        if (reserved.Count >= Policy.MaxActiveVms
            || (!reserved.Any(x => x.WorkspaceId == workspace.Id)
                && reserved.Select(x => x.WorkspaceId).Distinct().Count() >= Policy.MaxActiveWorkspaces)
            || reserved.Count(x => x.OrganizationId == workspace.OrganizationId) >= Policy.MaxVmsPerOrganization
            || reserved.Count(x => x.WorkspaceId == workspace.Id) >= Policy.MaxVmsPerWorkspace)
            throw Conflict("workspace_admission_limit", "Configured VM concurrency limit reached.");
        await CheckSpendAsync(workspace.OrganizationId, rate * 720m, ct);
    }

    private async Task CheckSpendAsync(string organizationId, decimal additional, CancellationToken ct)
    {
        var storage = await db.DevelopmentWorkspaces.Select(x => new { x.OrganizationId, Rate = x.StorageMonthlyRate }).ToListAsync(ct);
        var compute = await db.DevelopmentWorkspaceVms.Where(x => x.ComputeReserved)
            .Select(x => new { x.OrganizationId, Rate = x.ComputeHourlyRate }).ToListAsync(ct);
        var total = storage.Sum(x => x.Rate) + compute.Sum(x => x.Rate * 720m);
        var org = storage.Where(x => x.OrganizationId == organizationId).Sum(x => x.Rate)
            + compute.Where(x => x.OrganizationId == organizationId).Sum(x => x.Rate * 720m);
        if (total + additional > Policy.MaxEstimatedMonthlySpend || org + additional > Policy.MaxEstimatedMonthlySpendPerOrganization)
            throw Conflict("workspace_spend_limit", "Estimated compute plus retained storage exceeds the configured spend cap.");
    }

    private async Task<Selection> SelectAsync(string organizationId, string imageId, string profileId, string planId, CancellationToken ct)
    {
        ValidateText(imageId, 128); ValidateText(profileId, 32); ValidateText(planId, 128);
        if (!Policy.Images.TryGetValue(imageId, out var image) || !EligibleImage(image))
            throw Invalid("workspace_image_invalid", "Image must be explicitly enabled and runtime-qualified with an amd64 manifest.");
        if (!Policy.Profiles.TryGetValue(profileId, out var profile) || !EligibleProfile(profile, planId))
            throw Invalid("workspace_profile_invalid", "Profile is not approved for this immutable plan.");
        var plan = await db.ProviderPlans.Include(x => x.ProviderAccount).SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == planId, ct);
        if (plan is null || !EligiblePlan(organizationId, image, profile, new SelectionPlan(
                plan.Id, plan.Name, plan.OrganizationId, plan.ProviderAccountId, plan.Region, plan.Sku, plan.BuildPlatform,
                plan.Enabled, plan.ProviderAccount.Name, plan.ProviderAccount.OrganizationId,
                plan.ProviderAccount.Enabled, plan.ProviderAccount.ProviderType)))
            throw Invalid("workspace_plan_invalid", "Plan, image, account, region, SKU and architecture must match the explicit policy.");
        return new(plan, profile, image);
    }

    private static void EnsurePlacement(DevelopmentWorkspace workspace, ProviderPlan plan)
    {
        if (!MatchesPlacement(workspace, plan.ProviderAccountId, plan.Region))
            throw Invalid("workspace_placement_invalid", "All workspace generations must use the retained volume's account and region.");
    }

    private static void ApplySelection(DevelopmentWorkspaceVm vm, Selection selection, string imageId, string profile)
    {
        vm.ImageId = imageId; vm.Profile = profile; vm.PlanId = selection.Plan.Id;
        vm.ProviderAccountId = selection.Plan.ProviderAccountId; vm.Region = selection.Plan.Region; vm.Sku = selection.Plan.Sku;
        vm.ProviderImage = selection.Image.ProviderImageId; vm.ImageManifestSha256 = selection.Image.ManifestSha256;
        vm.ComputeHourlyRate = selection.Profile.ComputeHourlyRate; vm.CpuCount = selection.Profile.CpuCount; vm.MemoryGiB = selection.Profile.MemoryGiB;
    }

    private DevelopmentWorkspaceVm NewVm(DevelopmentWorkspace workspace, string kind, string key, string digest) => new()
    {
        Id = ids.NewId("workspacevm"), WorkspaceId = workspace.Id, OrganizationId = workspace.OrganizationId, Kind = kind,
        IdempotencyKey = key, RequestDigest = digest, CreatedAt = Now, UpdatedAt = Now
    };

    private Task<DevelopmentWorkspace> MutateAsync(string workspaceId, string organizationId, string? vmId, long? generation,
        Func<DevelopmentWorkspace, DevelopmentWorkspaceVm, CancellationToken, Task> action, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            await action(workspace, Vm(workspace, vmId ?? workspace.PrimaryVmId, generation), ct);
            Touch(workspace);
            return workspace;
        }, ct);

    private async Task<DevelopmentWorkspace> FindAsync(string id, string organizationId, CancellationToken ct) =>
        await db.DevelopmentWorkspaces.Include(x => x.Vms).ThenInclude(x => x.TaskLeases).SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == organizationId, ct)
        ?? throw Invalid("workspace_not_found", "Workspace was not found.");

    private static DevelopmentWorkspaceVm Vm(DevelopmentWorkspace workspace, string vmId, long? generation)
    {
        var vm = workspace.Vms.SingleOrDefault(x => x.Id == vmId) ?? throw Invalid("workspace_vm_not_found", "VM was not found.");
        if (generation.HasValue && vm.Generation != generation) throw Conflict("workspace_generation_stale", "VM generation has changed.");
        return vm;
    }

    private async Task<T> AtomicAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        EnsureEnabled();
        await AdmissionGate.WaitAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Cross-process admission is serialized globally, including organization and storage reservations.
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(11020261117)", ct);
            db.ChangeTracker.Clear();
            var result = await action();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw Conflict("workspace_revision_conflict", "Workspace changed concurrently; reload and retry.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw Conflict("workspace_reservation_conflict", "Workspace name or reservation conflicts with another request.");
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
        finally { AdmissionGate.Release(); }
    }

    private void EnsureEnabled()
    {
        if (!Policy.Enabled) throw Conflict("workspaces_disabled", "Development workspaces are disabled by the operator.");
        if (Policy.MaxActiveWorkspaces < 1 || Policy.MaxActiveVms < 1 || Policy.MaxWorkspacesPerOrganization < 1 || Policy.MaxVmsPerWorkspace < 1
            || Policy.MaxVmsPerOrganization < 1 || Policy.MaxEstimatedMonthlySpend <= 0
            || Policy.MaxEstimatedMonthlySpendPerOrganization <= 0 || Policy.VolumeMonthlyRatePerGb <= 0
            || Policy.MaxPricingAgeDays < 1 || Policy.PricingUpdatedAt is null || Policy.PricingUpdatedAt > Now
            || Policy.PricingUpdatedAt < Now.AddDays(-Policy.MaxPricingAgeDays) || Policy.Currency.Length != 3
            || Policy.IdleGraceMinutes < 1 || Policy.TaskLeaseSeconds is < 30 or > 900
            || Policy.HeartbeatTimeoutSeconds is < 15 or > 900 || Policy.OperationLeaseSeconds is < 30 or > 900)
            throw Invalid("workspace_policy_invalid", "Explicit quota, current pricing and lease policy are required.");
    }

    private static void ValidateGitRequest(LaunchEphemeralDevelopmentWorkspaceVmRequest request)
    {
        ValidateText(request.Repository, 2048); ValidateText(request.BaseRef, 256);
        if (!Uri.TryCreate(request.Repository, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.IsLoopback || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns
            || !request.PushAuthorized || !request.AcknowledgeIgnoredDataLoss || !Sha(request.BaseCommit, 40)
            || request.BaseRef.StartsWith('-') || request.BaseRef.Contains("..") || request.BaseRef.Any(char.IsWhiteSpace))
            throw Invalid("workspace_git_policy_invalid", "Use an HTTPS repository, explicit ref/commit, push authority and ignored-data acknowledgement.");
        if (request.RepositoryCredentialRef is not null)
        {
            ValidateText(request.RepositoryCredentialRef, 512);
        }
    }

    private string? RepositoryCredential(DevelopmentWorkspaceRepositoryPolicy policy, string organizationId)
    {
        if (policy.GitHubConnectionId is not null)
        {
            if (policy.Credential is not null || !policy.AllowPush
                || !WorkspaceGitAccessService.IsCanonicalRepository(policy.Repository)
                || !db.GitHubAppConnections.AsNoTracking().Any(x => x.Id == policy.GitHubConnectionId
                    && x.OrganizationId == organizationId && x.Status == "connected"
                    && x.AppId > 0 && x.InstallationId > 0 && x.LastVerifiedAt != null))
                throw Invalid("workspace_git_credential_invalid", "Repository requires an explicitly selected connected organization GitHub App.");
            return JsonSerializer.Serialize(new StoredSecretReference("github-app", policy.GitHubConnectionId, policy.Repository));
        }
        if (policy.Credential is null) return null;
        var path = policy.Credential.Path.Split('/');
        if (policy.Credential.Provider != "openbao" || policy.Credential.Path.Contains("..", StringComparison.Ordinal)
            || path.Length != 8 || path.Any(string.IsNullOrWhiteSpace)
            || path[0] != "env" || path[2] != "orgs" || path[3] != Uri.EscapeDataString(organizationId)
            || path[4] != "integrations" || path[6] != "credentials"
            || !path[5].StartsWith("workspace-repository-", StringComparison.Ordinal)
            || path[5].Length == "workspace-repository-".Length
            || path[5].Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_'))
            || policy.CredentialExpiresAt is null || policy.CredentialExpiresAt <= Now
            || policy.CredentialExpiresAt > Now.AddHours(1))
            throw Invalid("workspace_git_credential_invalid", "Repository credential must use an organization-owned workspace-repository integration and expire within one hour.");
        return JsonSerializer.Serialize(policy.Credential);
    }

    private DevelopmentWorkspaceRepositoryPolicy ApprovedRepository(string organizationId, string? repository, string? baseRef)
    {
        var matches = Policy.Repositories.Where(x => x.OrganizationId == organizationId && x.Repository == repository
            && x.AllowedBaseRefs.Contains(baseRef, StringComparer.Ordinal) && x.AllowPush).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw Invalid("workspace_repository_forbidden", "Repository and base ref must have exactly one explicit organization push policy.");
    }

    private void Touch(DevelopmentWorkspace workspace) { workspace.Revision++; workspace.UpdatedAt = Now; }
    private void Touch(DevelopmentWorkspaceVm vm) { vm.Revision++; vm.UpdatedAt = Now; }
    private static string Digest<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static bool Sha(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static void ValidateText(string? value, int length)
    {
        if (!ValidText(value, length))
            throw Invalid("workspace_request_invalid", "Required value is empty, oversized or contains control characters.");
    }
    private static bool ValidText(string? value, int length) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);
    private static DevelopmentWorkspaceException Invalid(string code, string message) => new(code, message);
    private static DevelopmentWorkspaceException Conflict(string code, string message) => new(code, message, true);
    private sealed record Selection(ProviderPlan Plan, DevelopmentWorkspaceProfile Profile, DevelopmentWorkspaceImage Image);
}
