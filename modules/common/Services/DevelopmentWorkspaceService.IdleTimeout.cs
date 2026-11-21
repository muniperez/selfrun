using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed partial class DevelopmentWorkspaceService
{
    private const int MinIdleTimeoutMinutes = 1;
    private const int MaxIdleTimeoutMinutes = 7 * 24 * 60;

    public Task<DevelopmentWorkspace> UpdateIdleTimeoutAsync(string workspaceId, string organizationId,
        int? idleTimeoutMinutes, CancellationToken ct) =>
        UpdateIdleTimeoutAsync(workspaceId, organizationId, idleTimeoutMinutes, keepRunning: false, ct);

    public Task<DevelopmentWorkspace> UpdateIdleTimeoutAsync(string workspaceId, string organizationId,
        int? idleTimeoutMinutes, bool keepRunning, CancellationToken ct) =>
        AtomicAsync(async () =>
        {
            ValidateIdleTimeout(idleTimeoutMinutes);
            var workspace = await FindAsync(workspaceId, organizationId, ct);
            var now = Now;
            var timeoutMinutes = EffectiveIdleTimeout(idleTimeoutMinutes, keepRunning);
            var changed = workspace.IdleTimeoutMinutes != idleTimeoutMinutes || workspace.KeepRunning != keepRunning;
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
                workspace.KeepRunning = keepRunning;
                Touch(workspace);
            }
            return workspace;
        }, ct);

    public async Task<bool> IsIdleAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        var policy = await db.DevelopmentWorkspaces.AsNoTracking()
            .Where(x => x.Id == vm.WorkspaceId && x.OrganizationId == vm.OrganizationId)
            .Select(x => new { x.IdleTimeoutMinutes, x.KeepRunning }).SingleAsync(ct);
        return await IsIdleAsync(vm, EffectiveIdleTimeout(policy.IdleTimeoutMinutes, policy.KeepRunning), ct);
    }

    private async Task<bool> IsIdleAsync(DevelopmentWorkspaceVm vm, int? timeoutMinutes, CancellationToken ct)
    {
        var now = Now;
        return vm.DesiredState == "running" && ReportedPhase(vm) == "ready" && HasIdleElapsed(vm, timeoutMinutes, now)
            && HasFreshHeartbeat(vm, now) && !await HasUnresolvedTasksAsync(vm, ct);
    }

    // Null means Keep running: no inactivity timeout applies, so automatic idle stop never starts.
    private int? EffectiveIdleTimeout(DevelopmentWorkspace workspace) =>
        EffectiveIdleTimeout(workspace.IdleTimeoutMinutes, workspace.KeepRunning);

    private int? EffectiveIdleTimeout(int? idleTimeoutMinutes, bool keepRunning) =>
        keepRunning ? null : idleTimeoutMinutes ?? Policy.IdleGraceMinutes;

    private static bool HasIdleElapsed(DevelopmentWorkspaceVm vm, int? timeoutMinutes, DateTimeOffset now) =>
        timeoutMinutes is { } timeout && vm.LastActivityAt is { } activity && now - activity >= TimeSpan.FromMinutes(timeout);

    private bool HasFreshHeartbeat(DevelopmentWorkspaceVm vm, DateTimeOffset now) =>
        vm.LastHeartbeatAt >= now.AddSeconds(-Policy.HeartbeatTimeoutSeconds);

    private static void ValidateIdleTimeout(int? idleTimeoutMinutes)
    {
        if (idleTimeoutMinutes is < MinIdleTimeoutMinutes or > MaxIdleTimeoutMinutes)
            throw Invalid("workspace_idle_timeout_invalid", $"Idle timeout must be a whole number from {MinIdleTimeoutMinutes} to {MaxIdleTimeoutMinutes} minutes.");
    }
}
