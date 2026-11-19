using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    public Task<DevelopmentWorkspace> DeleteAsync(string workspaceId, string organizationId, bool confirmed, string actorId, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var workspace = await FindAsync(workspaceId, organizationId, ct, allowDeletion: true);
            if (!confirmed)
                throw Invalid("workspace_delete_confirmation_required", "Confirm permanent deletion of the workspace and its retained files.");
            ValidateText(actorId, 256);
            var primary = Vm(workspace, workspace.PrimaryVmId, null);
            if (workspace.VolumeStatus == "deleted") return workspace;
            if (workspace.VolumeStatus == "deleting")
            {
                var existing = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == primary.OperationId, ct);
                if (existing.Kind == "delete-workspace" && existing.Status == "failed")
                {
                    existing.Status = "pending"; existing.UpdatedAt = Now; existing.Revision++;
                    primary.Phase = "deleting"; Touch(primary); Touch(workspace);
                }
                return workspace;
            }

            if (workspace.Vms.Any(vm => vm.Phase != "stopped" || vm.DesiredState != "stopped" || vm.ComputeReserved
                    || vm.StartAfterStop || vm.GatewayReady || vm.ProviderServerId is not null || vm.ProviderIpId is not null
                    || vm.ProviderFirewallId is not null || vm.RuntimeCredentialRef is not null)
                || workspace.VolumeAttachedVmId is not null
                || await db.DevelopmentWorkspaceOperations.AnyAsync(x => x.WorkspaceId == workspaceId
                    && (x.Status == "running" || x.Status == "pending"), ct))
                throw Conflict("workspace_delete_requires_stop", "Stop all workspace VMs and wait for cleanup to finish before permanently deleting the workspace.");
            if (primary.ProviderAccountId != workspace.ProviderAccountId)
                throw Conflict("workspace_delete_account_mismatch", "The primary VM and workspace provider accounts differ. Contact an operator to reconcile them.");

            var operation = AddOperation(workspace, primary, "delete-workspace");
            operation.ProviderResourceId = workspace.ProviderVolumeId;
            operation.ProviderEvidenceJson = JsonSerializer.Serialize(new { requestedBy = actorId, requestedAt = Now });
            workspace.VolumeStatus = "deleting";
            primary.Phase = "deleting"; Touch(primary); Touch(workspace);
            return workspace;
        }, ct);

    public Task<DevelopmentWorkspaceOperation> CompleteDeletionAsync(string operationId, string leaseToken, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            var (operation, primary) = await RequireClaimAsync(operationId, leaseToken, ct);
            var workspace = await FindAsync(primary.WorkspaceId, primary.OrganizationId, ct, allowDeletion: true);
            if (operation.Kind != "delete-workspace" || workspace.VolumeStatus != "deleting" || workspace.PrimaryVmId != primary.Id
                || (operation.ProviderResourceId is not null && operation.Step != "delete-verified-volume"))
                throw Conflict("workspace_operation_stale", "Operation no longer targets this workspace deletion.");

            db.DevelopmentWorkspaceGatewayRoutes.RemoveRange(await db.DevelopmentWorkspaceGatewayRoutes
                .Where(x => x.WorkspaceId == workspace.Id && x.OrganizationId == workspace.OrganizationId).ToListAsync(ct));
            db.DevelopmentWorkspaceGatewaySessions.RemoveRange(await db.DevelopmentWorkspaceGatewaySessions
                .Where(x => x.WorkspaceId == workspace.Id && x.OrganizationId == workspace.OrganizationId).ToListAsync(ct));
            foreach (var vm in workspace.Vms)
            {
                db.DevelopmentWorkspaceTaskLeases.RemoveRange(vm.TaskLeases);
                vm.Phase = "deleted"; vm.GatewayReady = false; vm.SshBootstrapSecretRef = null; vm.RepositoryCredentialRef = null;
                vm.EnrollmentTokenDigest = null; vm.EnrollmentTokenExpiresAt = null;
                vm.RuntimeTokenHash = null; vm.RuntimeTokenExpiresAt = null;
                vm.ErrorCode = null; vm.ErrorMessage = null;
                Touch(vm);
            }
            workspace.VolumeStatus = "deleted"; workspace.ProviderVolumeId = null; workspace.VolumeFilesystemUuid = null;
            workspace.StorageMonthlyRate = 0;
            operation.Status = "completed"; operation.Step = "provider-absence-confirmed";
            operation.ErrorCode = null; operation.ErrorMessage = null;
            operation.LeaseToken = null; operation.LeaseOwner = null; operation.LeaseExpiresAt = null;
            operation.UpdatedAt = Now; operation.Revision++;
            Touch(workspace);
            return operation;
        }, ct);
}
