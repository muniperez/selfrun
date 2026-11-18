using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor
{
    private static readonly ConcurrentDictionary<(string VmId, long Generation), DateTimeOffset> RepositoryRenewals = new();
    private static readonly ConcurrentDictionary<(string VmId, long Generation), SemaphoreSlim> RepositoryCredentialLocks = new();

    private async Task RefreshRepositoryCredentialAsync(DevelopmentWorkspaceVm vm, CancellationToken ct, bool force = false)
    {
        if (vm.Kind != "ephemeral" || vm.RepositoryCredentialRef is null) return;
        var gate = RepositoryCredentialLocks.GetOrAdd((vm.Id, vm.Generation), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // A monitor selected before stop admission must not issue a token after final retirement.
            if (!force && !await db.DevelopmentWorkspaceVms.AsNoTracking().AnyAsync(x =>
                x.Id == vm.Id && x.Generation == vm.Generation && x.Phase == "ready" && x.DesiredState == "running", ct))
                return;
            await RotateRepositoryCredentialAsync(vm, ct, force);
        }
        finally { gate.Release(); }
    }

    private async Task RotateRepositoryCredentialAsync(DevelopmentWorkspaceVm vm, CancellationToken ct, bool force)
    {
        if (!policy.Value.Repositories.Any(x => x.OrganizationId == vm.OrganizationId && x.Repository == vm.Repository &&
            x.AllowPush && x.AllowedBaseRefs.Contains(vm.BaseRef, StringComparer.Ordinal)))
            throw new DevelopmentWorkspaceException("workspace_git_access_denied", "Selected repository authority is no longer approved.");
        var identity = (vm.Id, vm.Generation);
        if (!force && RepositoryRenewals.TryGetValue(identity, out var renewal) && renewal > Now) return;
        RepositoryRenewals.TryRemove(identity, out _);
        var previous = await ReadRepositoryCredentialAsync(vm, ct);
        var credential = await gitAccess.IssueAsync(vm.OrganizationId, vm.Repository!,
            vm.RepositoryCredentialRef!, ct);
        try
        {
            var expires = DateTimeOffset.FromUnixTimeSeconds(credential.ExpiresAtUnixSeconds);
            if (credential.Origin != vm.Repository || expires <= Now || expires > Now.AddHours(1))
                throw new InvalidOperationException("Selected Git authority has no valid bounded issuer expiry.");
            if (previous is not null)
                await gitAccess.RevokeAsync(vm.OrganizationId, vm.Repository!, vm.RepositoryCredentialRef!, previous, ct);
            await runtime.StageRepositoryCredentialAsync(Target(vm), JsonSerializer.SerializeToElement(new
            {
                origin = credential.Origin, username = credential.Username, password = credential.Password,
                expiresAt = credential.ExpiresAtUnixSeconds
            }, Json), ct);
            RepositoryRenewals[identity] = expires > Now.AddMinutes(11) ? expires.AddMinutes(-10) : Now.AddSeconds(15);
        }
        catch
        {
            await gitAccess.RevokeAsync(vm.OrganizationId, vm.Repository!, vm.RepositoryCredentialRef!, credential, CancellationToken.None);
            throw;
        }
    }

    private async Task<WorkspaceGitCredential?> ReadRepositoryCredentialAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        var value = await runtime.ReadRepositoryCredentialAsync(Target(vm), ct);
        if (value is null) return null;
        return new(value.Value.GetProperty("origin").GetString()!,
            value.Value.GetProperty("username").GetString()!, value.Value.GetProperty("password").GetString()!,
            value.Value.GetProperty("expiresAt").GetInt64());
    }

    private async Task RevokeRepositoryCredentialAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (vm.Kind != "ephemeral" || vm.RepositoryCredentialRef is null) return;
        var gate = RepositoryCredentialLocks.GetOrAdd((vm.Id, vm.Generation), _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var credential = await ReadRepositoryCredentialAsync(vm, ct)
                ?? throw new InvalidOperationException("Repository credential retirement cannot be confirmed without readback.");
            await gitAccess.RevokeAsync(vm.OrganizationId, vm.Repository!, vm.RepositoryCredentialRef, credential, ct);
            RepositoryRenewals.TryRemove((vm.Id, vm.Generation), out _);
        }
        finally { gate.Release(); }
    }
}
