using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    public Task<DevelopmentWorkspaceOperation?> ClaimOperationAsync(string workerId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateText(workerId, 128);
            if (!await maintenance.CanClaimAsync(db, ct)) return null;
            var candidates = await db.DevelopmentWorkspaceOperations
                .Where(x => x.Status == "pending" || x.Status == "running")
                .ToListAsync(ct);
            foreach (var operation in candidates.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Attempts)
                         .ThenBy(x => x.CreatedAt).ThenBy(x => x.Id))
            {
                if (operation.LeaseExpiresAt > Now) continue;
                var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == operation.VmId, ct);
                if (vm.Generation != operation.Generation || vm.OperationId != operation.Id)
                {
                    operation.Status = "superseded"; operation.Revision++; operation.UpdatedAt = Now;
                    continue;
                }
                if (operation.Kind == "stop" && vm.ShutdownDeadline > Now) continue;
                if (operation.Kind == "stop" && vm.ShutdownReason == "idle" && operation.Attempts == 0)
                {
                    if (vm.ShutdownDeadline is null || vm.Phase is not ("ready" or "draining")
                        || vm.LastHeartbeatAt is null || vm.LastHeartbeatAt < Now.AddSeconds(-Policy.HeartbeatTimeoutSeconds)
                        || vm.LastActivityAt is null)
                        continue;
                    var workspace = await FindAsync(vm.WorkspaceId, vm.OrganizationId, ct);
                    if (vm.LastActivityAt > Now.AddMinutes(-Policy.IdleGraceMinutes) || await HasUnresolvedTasksAsync(vm, ct))
                    {
                        await CancelIdleCoreAsync(workspace, vm, ct);
                        Touch(workspace);
                        continue;
                    }
                    vm.DesiredState = "stopped"; vm.Phase = "draining";
                    Touch(vm); Touch(workspace);
                }
                operation.Status = "running"; operation.LeaseOwner = workerId;
                operation.LeaseToken = ids.NewId("workspacelease");
                operation.LeaseExpiresAt = Now.AddSeconds(Policy.OperationLeaseSeconds);
                operation.Attempts++; operation.Revision++; operation.UpdatedAt = Now;
                return operation;
            }
            return null;
        }, ct);

    public Task<DevelopmentWorkspaceOperation> RenewOperationLeaseAsync(string operationId, string leaseToken, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var (operation, _) = await RequireClaimAsync(operationId, leaseToken, ct);
            operation.LeaseExpiresAt = Now.AddSeconds(Policy.OperationLeaseSeconds);
            operation.Revision++; operation.UpdatedAt = Now;
            return operation;
        }, ct);

    // Worker-only: invoke after checking the actual runtime/provider, never bind this to user input.
    // The delegate must perform database changes only; external calls happen before this fenced commit.
    public Task<DevelopmentWorkspaceOperation> UpdateClaimedOperationAsync(string operationId, string leaseToken,
        Action<DevelopmentWorkspaceOperation, DevelopmentWorkspaceVm, DevelopmentWorkspace> update, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var (operation, vm) = await RequireClaimAsync(operationId, leaseToken, ct);
            var workspace = await FindAsync(vm.WorkspaceId, vm.OrganizationId, ct);
            update(operation, vm, workspace);
            if (vm.Generation != operation.Generation || vm.OperationId != operation.Id)
                throw Conflict("workspace_generation_stale", "Operation no longer targets this generation.");
            if (vm.Kind == DevelopmentWorkspaceVmKinds.Ephemeral && vm.Phase is "attaching" or "mounting" or "detaching")
                throw Conflict("workspace_vm_isolation", "Ephemeral VMs cannot enter retained-storage phases.");
            operation.Revision++; operation.UpdatedAt = Now; Touch(vm); Touch(workspace);
            return operation;
        }, ct);

    private async Task<(DevelopmentWorkspaceOperation Operation, DevelopmentWorkspaceVm Vm)> RequireClaimAsync(
        string operationId, string leaseToken, CancellationToken ct)
    {
        ValidateText(leaseToken, 256);
        var operation = await db.DevelopmentWorkspaceOperations.SingleOrDefaultAsync(x => x.Id == operationId, ct)
            ?? throw Invalid("workspace_operation_not_found", "Operation was not found.");
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == operation.VmId, ct);
        if (operation.Status != "running" || operation.LeaseToken != leaseToken || operation.LeaseExpiresAt <= Now
            || operation.LeaseExpiresAt is null || operation.Generation != vm.Generation || vm.OperationId != operation.Id)
            throw Conflict("workspace_operation_stale", "Worker lease or generation is no longer current.");
        return (operation, vm);
    }

    public async Task<bool> CanDeleteComputeAsync(DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation operation, CancellationToken ct)
    {
        if (operation.VmId != vm.Id || operation.Generation != vm.Generation || vm.OperationId != operation.Id
            || operation.Status != "running" || operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= Now
            || operation.Kind != "stop" || vm.DesiredState != "stopped" || vm.LastHeartbeatAt is null
            || vm.LastHeartbeatAt < Now.AddSeconds(-Policy.HeartbeatTimeoutSeconds)
            || vm.QuiescedGeneration != vm.Generation || vm.QuiescedOperationId != operation.Id
            || vm.QuiescedAt is null || string.IsNullOrWhiteSpace(vm.QuiescenceReceipt)
            || await HasUnresolvedTasksAsync(vm, ct))
            return false;
        if (vm.Kind == DevelopmentWorkspaceVmKinds.Primary) return true;
        var discard = vm.DiscardGeneration == vm.Generation && vm.DiscardOperationId == operation.Id
            && vm.DiscardRequestedAt is not null && !string.IsNullOrWhiteSpace(vm.DiscardRequestedBy);
        var gitSaved = vm.GitSavedGeneration == vm.Generation && vm.GitSavedOperationId == operation.Id
            && vm.GitSavedAt >= vm.QuiescedAt && !string.IsNullOrWhiteSpace(vm.GitSaveReceipt)
            && Sha(vm.FinalHead, 40) && vm.FinalHead == vm.VerifiedRemoteCommit
            && vm.VerifiedRemoteRef == $"refs/heads/{vm.WorkBranch}" && vm.GitStatus == "saved";
        return discard || gitSaved;
    }
}
