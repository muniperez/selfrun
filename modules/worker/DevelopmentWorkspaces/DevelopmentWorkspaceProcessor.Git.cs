using System.Collections.Concurrent;
using System.Text.Json;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor
{
    private static readonly ConcurrentDictionary<(string VmId, long Generation), DateTimeOffset> RepositoryRenewals = new();

    private async Task RefreshRepositoryCredentialAsync(DevelopmentWorkspaceVm vm, CancellationToken ct, bool force = false)
    {
        if (vm.Kind != "ephemeral" || vm.RepositoryCredentialRef is null) return;
        if (!policy.Value.Repositories.Any(x => x.OrganizationId == vm.OrganizationId && x.Repository == vm.Repository &&
            x.AllowPush && x.AllowedBaseRefs.Contains(vm.BaseRef, StringComparer.Ordinal)))
            throw new DevelopmentWorkspaceException("workspace_git_access_denied", "Selected repository authority is no longer approved.");
        var identity = (vm.Id, vm.Generation);
        if (!force && RepositoryRenewals.TryGetValue(identity, out var renewal) && renewal > Now) return;
        RepositoryRenewals[identity] = Now.AddMinutes(1);
        var credential = await gitAccess.IssueAsync(vm.OrganizationId, vm.Repository!,
            vm.RepositoryCredentialRef, ct);
        var expires = DateTimeOffset.FromUnixTimeSeconds(credential.ExpiresAtUnixSeconds);
        if (credential.Origin != vm.Repository || expires <= Now || expires > Now.AddHours(1))
            throw new InvalidOperationException("Selected Git authority has no valid bounded issuer expiry.");
        await runtime.StageRepositoryCredentialAsync(Target(vm), JsonSerializer.SerializeToElement(new
        {
            origin = credential.Origin, username = credential.Username, password = credential.Password,
            expiresAt = credential.ExpiresAtUnixSeconds
        }, Json), ct);
        RepositoryRenewals[identity] = expires > Now.AddMinutes(11) ? expires.AddMinutes(-10) : Now.AddSeconds(15);
    }
}
