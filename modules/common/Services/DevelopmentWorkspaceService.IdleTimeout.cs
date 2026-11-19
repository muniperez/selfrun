using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    private const int MinIdleTimeoutMinutes = 1;
    private const int MaxIdleTimeoutMinutes = 7 * 24 * 60;

    public Task<DevelopmentWorkspace> UpdateIdleTimeoutAsync(string workspaceId, string organizationId,
        int? idleTimeoutMinutes, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateIdleTimeout(idleTimeoutMinutes);
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var now = Now;
            var timeoutMinutes = EffectiveIdleTimeout(idleTimeoutMinutes);
            var changed = workspace.IdleTimeoutMinutes != idleTimeoutMinutes;
            foreach (var vm in workspace.Vms)
            {
                if (!CanCancelIdleStop(vm) || vm.ShutdownDeadline is null || HasIdleElapsed(vm, timeoutMinutes, now))
                    continue;
                var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId, ct);
                if (operation.Kind == "stop" && operation.Status == "pending" && operation.Attempts == 0
                    && operation.WorkspaceId == workspaceId && operation.VmId == vm.Id && operation.Generation == vm.Generation)
                {
                    CancelUnclaimedIdleStop(vm, operation);
                    changed = true;
                }
            }
            if (changed)
            {
                workspace.IdleTimeoutMinutes = idleTimeoutMinutes;
                Touch(workspace);
            }
            return workspace;
        }, ct);

    public async Task<bool> IsIdleAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        var idleTimeout = await db.DevelopmentWorkspaces.AsNoTracking()
            .Where(x => x.Id == vm.WorkspaceId && x.OrganizationId == vm.OrganizationId)
            .Select(x => x.IdleTimeoutMinutes).SingleAsync(ct);
        return await IsIdleAsync(vm, EffectiveIdleTimeout(idleTimeout), ct);
    }

    private async Task<bool> IsIdleAsync(DevelopmentWorkspaceVm vm, int timeoutMinutes, CancellationToken ct)
    {
        var now = Now;
        return vm.DesiredState == "running" && vm.Phase == "ready" && HasIdleElapsed(vm, timeoutMinutes, now)
            && HasFreshHeartbeat(vm, now) && !await HasUnresolvedTasksAsync(vm, ct);
    }

    private int EffectiveIdleTimeout(int? idleTimeoutMinutes) => idleTimeoutMinutes ?? Policy.IdleGraceMinutes;

    private static bool HasIdleElapsed(DevelopmentWorkspaceVm vm, int timeoutMinutes, DateTimeOffset now) =>
        vm.LastActivityAt is { } activity && now - activity >= TimeSpan.FromMinutes(timeoutMinutes);

    private bool HasFreshHeartbeat(DevelopmentWorkspaceVm vm, DateTimeOffset now) =>
        vm.LastHeartbeatAt >= now.AddSeconds(-Policy.HeartbeatTimeoutSeconds);

    private static void ValidateIdleTimeout(int? idleTimeoutMinutes)
    {
        if (idleTimeoutMinutes is < MinIdleTimeoutMinutes or > MaxIdleTimeoutMinutes)
            throw Invalid("workspace_idle_timeout_invalid", $"Idle timeout must be a whole number from {MinIdleTimeoutMinutes} to {MaxIdleTimeoutMinutes} minutes.");
    }
}
