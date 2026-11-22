using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace WorkspaceReconciliation;

public sealed class Recovery(MinicloudDbContext db, ISecretStore secrets, string environmentPrefix, HetznerAbsence provider)
{
    private const string TombstoneStep = "operator-provider-absence-confirmed";
    private static bool Terminal(DevelopmentWorkspaceOperation op) =>
        op.Status is "completed" or "superseded" or "cancelled" or "failed";

    public async Task<string> RunAsync(RecoveryScope scope, bool apply, CancellationToken ct)
    {
        scope.Validate();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same transaction lock as legacy workspace admission and worker claims.
        if (!await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(11020261117) AS \"Value\"").SingleAsync(ct))
            throw new RecoveryRefused("Legacy workspace admission is busy; stop all legacy writers before retrying.");
        await db.Database.ExecuteSqlRawAsync("SET LOCAL idle_in_transaction_session_timeout = '120s'", ct);

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (!applied.SetEquals(db.Database.GetMigrations()))
            throw new RecoveryRefused("Database history does not exactly match the workspace-bearing build; this tool does not migrate it.");

        // Also fence writers that do not use the admission lock (e.g. route publication and provider credential rotation).
        await db.Database.ExecuteSqlRawAsync("""
            LOCK TABLE provider_accounts, development_workspaces, development_workspace_vms,
              development_workspace_operations, development_workspace_task_leases, development_workspace_filesystem_jobs,
              development_workspace_gateway_routes, development_workspace_gateway_sessions, development_workspace_vm_resource_samples
              IN SHARE ROW EXCLUSIVE MODE NOWAIT
            """, ct);

        var workspace = await db.DevelopmentWorkspaces.Include(x => x.Vms).ThenInclude(x => x.TaskLeases)
            .SingleOrDefaultAsync(x => x.Id == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId, ct);
        if (workspace is null || workspace.PrimaryVmId != scope.VmId || workspace.ProviderAccountId != scope.ProviderAccountId
            || workspace.Region != scope.Region || workspace.VolumeSizeGb != scope.VolumeSizeGb)
            throw new RecoveryRefused("Workspace, organization, account, placement or volume size did not match.");
        var vm = workspace.Vms.SingleOrDefault(x => x.Id == scope.VmId);
        if (vm is null || vm.Kind != "primary" || vm.Generation != scope.Generation
            || vm.ProviderAccountId != scope.ProviderAccountId || vm.Region != scope.Region
            || vm.AllocationOperationId != scope.AllocationOperationId)
            throw new RecoveryRefused("Primary VM generation, allocation or placement did not match.");

        var ops = await db.DevelopmentWorkspaceOperations.Where(x => x.WorkspaceId == workspace.Id).ToListAsync(ct);
        var stop = ops.SingleOrDefault(x => IsScoped(x, scope, scope.StopOperationId, "stop"));
        if (stop is null || !ops.Any(x => IsScoped(x, scope, scope.AllocationOperationId, "start") && x.Status == "completed"))
            throw new RecoveryRefused("The recorded stop and completed allocation operations did not match.");
        var vmIds = workspace.Vms.Select(x => x.Id).ToArray();
        var routes = await db.DevelopmentWorkspaceGatewayRoutes
            .Where(x => x.WorkspaceId == workspace.Id || vmIds.Contains(x.VmId)).ToListAsync(ct);
        var sessions = await db.DevelopmentWorkspaceGatewaySessions
            .Where(x => x.WorkspaceId == workspace.Id || vmIds.Contains(x.VmId)).ToListAsync(ct);
        if (routes.Select(x => (x.OrganizationId, x.WorkspaceId, x.VmId))
            .Concat(sessions.Select(x => (x.OrganizationId, x.WorkspaceId, x.VmId)))
            .Any(x => x.OrganizationId != workspace.OrganizationId || x.WorkspaceId != workspace.Id || !vmIds.Contains(x.VmId)))
            throw new RecoveryRefused("A gateway record is not scoped to this organization and workspace.");
        var taskLeases = workspace.Vms.SelectMany(x => x.TaskLeases).ToList();
        if (taskLeases.Any(x => x.Status == "active") ||
            await db.DevelopmentWorkspaceFilesystemJobs.AnyAsync(x => x.WorkspaceId == workspace.Id
                && (x.State == "queued" || x.State == "running"), ct))
            throw new RecoveryRefused("Active task leases or filesystem work block recovery, even if a task lease has expired.");

        var prior = await db.DevelopmentWorkspaceOperations.SingleOrDefaultAsync(x => x.Id == scope.RecoveryOperationId, ct);
        if (workspace.VolumeStatus == "deleted" || prior is not null)
        {
            if (workspace.VolumeStatus != "deleted" || workspace.ProviderVolumeId is not null || workspace.VolumeAttachedVmId is not null
                || workspace.VolumeFilesystemUuid is not null || workspace.StorageMonthlyRate != 0
                || vm.OperationId != scope.RecoveryOperationId || stop.Status != "superseded"
                || prior is null || !IsScoped(prior, scope, scope.RecoveryOperationId, "delete-workspace")
                || prior.Status != "completed" || prior.Step != TombstoneStep || prior.ProviderResourceId != scope.VolumeId
                || prior.ProviderEvidenceJson is null || JsonSerializer.Deserialize<Tombstone>(prior.ProviderEvidenceJson, RecoveryScope.Json)?.Scope != scope
                || workspace.Vms.Any(x => x.Phase != "deleted" || !Offline(x) || !NoResources(x)
                    || x.SshBootstrapSecretRef is not null || x.RuntimeTokenHash is not null || x.EnrollmentTokenDigest is not null)
                || ops.Any(x => !Terminal(x)) || routes.Count != 0 || sessions.Count != 0 || taskLeases.Count != 0)
                throw new RecoveryRefused("Existing recovery ID or retired state does not match this exact tombstone.");
            return "Already reconciled: exact terminal tombstone verified; no changes made.";
        }

        if (workspace.VolumeStatus is not ("mounted" or "retained") || workspace.ProviderVolumeId != scope.VolumeId
            || (workspace.VolumeAttachedVmId is not null && workspace.VolumeAttachedVmId != vm.Id)
            || vm.ProviderServerId != scope.ServerId || vm.ProviderIpId != scope.PrimaryIpId || vm.ProviderFirewallId != scope.FirewallId
            || vm.OperationId != stop.Id || vm.DesiredState != "stopped" || vm.StartAfterStop
            || vm.Phase is not ("draining" or "blocked") || stop.Step is not ("wait-for-tasks" or "gateway-drain" or "quiesce")
            || stop.Status is not ("pending" or "running" or "blocked"))
            throw new RecoveryRefused("Only the exact recorded primary generation and resources in the legacy drain can be reconciled.");

        if (workspace.Vms.Any(x => x != vm && (x.Kind != "ephemeral" || x.ProviderAccountId != scope.ProviderAccountId
                || x.Phase is not ("stopped" or "deleted") || !Offline(x) || !NoResources(x)))
            || workspace.Vms.Any(x => x.RepositoryCredentialRef is not null || x.TaskSecretReferencesJson is not null)
            || ops.Any(x => x != stop && !Terminal(x)))
            throw new RecoveryRefused("A sibling, credential or another operation requires normal cleanup first.");

        var dbNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (ops.Any(x => x.LeaseExpiresAt > dbNow || (x.LeaseToken is not null && x.LeaseExpiresAt is null)))
            throw new RecoveryRefused("A current or ambiguous worker lease blocks recovery.");

        var account = await db.ProviderAccounts.SingleOrDefaultAsync(x => x.Id == scope.ProviderAccountId
            && x.OrganizationId == scope.OrganizationId, ct);
        if (account is null || account.ProviderType != CloudProvider.Hetzner || account.CredentialStoreProvider != OpenBaoSecretStore.ProviderName
            || account.CredentialGeneration != scope.CredentialGeneration || string.IsNullOrWhiteSpace(account.CredentialVersion)
            || account.CredentialPath != OpenBaoSecretStore.ProviderPathFor(
                environmentPrefix, scope.OrganizationId, scope.ProviderAccountId, scope.CredentialGeneration))
            throw new RecoveryRefused("The recorded Hetzner account or its pinned OpenBao credential did not match.");

        var workspaceSecrets = WorkspaceSecrets(workspace, ops, routes.Select(x => x.T3CredentialReference));
        var token = await secrets.GetAsync(new(account.CredentialStoreProvider, account.CredentialPath, account.CredentialVersion), ct);
        await provider.VerifyAsync(scope, token, ct);
        if (!apply) return "Check passed: exact scope and project absence verified; no changes made.";

        var verifiedAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        // Use the existing identity trigger's deleting -> deleted transition, without publishing an intermediate state.
        workspace.VolumeStatus = "deleting";
        await db.SaveChangesAsync(ct);
        workspace.VolumeStatus = "deleted";
        workspace.ProviderVolumeId = workspace.VolumeFilesystemUuid = workspace.VolumeAttachedVmId = null;
        workspace.StorageMonthlyRate = 0;
        workspace.UpdatedAt = verifiedAt;
        workspace.Revision++;

        stop.Status = "superseded";
        stop.LeaseToken = stop.LeaseOwner = null;
        stop.LeaseExpiresAt = null;
        stop.UpdatedAt = verifiedAt;
        stop.Revision++;
        vm.OperationId = scope.RecoveryOperationId;
        foreach (var machine in workspace.Vms)
        {
            machine.Phase = "deleted";
            machine.ComputeReserved = machine.GatewayReady = false;
            machine.ProviderServerId = machine.ProviderIpId = machine.ProviderFirewallId = machine.ProviderActionId = machine.IpAddress = null;
            machine.RuntimeCredentialRef = machine.SshBootstrapSecretRef = machine.EnrollmentTokenDigest = machine.RuntimeTokenHash = null;
            machine.EnrollmentTokenExpiresAt = machine.RuntimeTokenExpiresAt = null;
            machine.ErrorCode = machine.ErrorMessage = null;
            machine.UpdatedAt = verifiedAt;
            machine.Revision++;
        }
        db.DevelopmentWorkspaceOperations.Add(new()
        {
            Id = scope.RecoveryOperationId, WorkspaceId = workspace.Id, VmId = vm.Id, Generation = vm.Generation,
            Kind = "delete-workspace", Status = "completed", Step = TombstoneStep,
            PlanId = vm.PlanId, ProviderAccountId = vm.ProviderAccountId, Region = vm.Region, Sku = vm.Sku,
            ImageId = vm.ImageId, ProviderImage = vm.ProviderImage, ImageManifestSha256 = vm.ImageManifestSha256,
            ComputeHourlyRate = vm.ComputeHourlyRate, ProviderResourceId = scope.VolumeId,
            ProviderEvidenceJson = JsonSerializer.Serialize(new Tombstone(scope, verifiedAt), RecoveryScope.Json),
            CreatedAt = verifiedAt, UpdatedAt = verifiedAt
        });
        db.DevelopmentWorkspaceGatewayRoutes.RemoveRange(routes);
        db.DevelopmentWorkspaceGatewaySessions.RemoveRange(sessions);
        db.DevelopmentWorkspaceTaskLeases.RemoveRange(taskLeases);
        await db.DevelopmentWorkspaceVmResourceSamples.Where(x => x.WorkspaceId == workspace.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SET CONSTRAINTS ALL IMMEDIATE", ct);
        foreach (var reference in workspaceSecrets)
            await secrets.DeleteAsync(reference, ct);
        await tx.CommitAsync(ct);
        return "Reconciled: legacy workspace tombstone committed.";
    }

    private static bool IsScoped(DevelopmentWorkspaceOperation op, RecoveryScope s, string id, string kind) =>
        op.Id == id && op.Kind == kind && op.WorkspaceId == s.WorkspaceId && op.VmId == s.VmId
        && op.Generation == s.Generation && op.ProviderAccountId == s.ProviderAccountId && op.Region == s.Region;

    private static bool Offline(DevelopmentWorkspaceVm vm) =>
        vm.DesiredState == "stopped" && !vm.ComputeReserved && !vm.StartAfterStop && !vm.GatewayReady;

    private static bool NoResources(DevelopmentWorkspaceVm vm) =>
        vm.ProviderServerId is null && vm.ProviderIpId is null && vm.ProviderFirewallId is null
        && vm.RuntimeCredentialRef is null;

    private IEnumerable<StoredSecretReference> WorkspaceSecrets(DevelopmentWorkspace workspace,
        IEnumerable<DevelopmentWorkspaceOperation> ops, IEnumerable<string?> routeReferences)
    {
        var generations = ops.Where(x => x.Kind == "start").Select(x => (x.VmId, x.Generation))
            .Concat(workspace.Vms.Select(x => (VmId: x.Id, x.Generation))).Distinct();
        var expected = (from generation in generations
                        where generation.Generation > 0
                        from name in new[] { "bootstrap", "t3", "connect" }
                        select new StoredSecretReference(OpenBaoSecretStore.ProviderName,
                            OpenBaoSecretStore.IntegrationPathFor(environmentPrefix, workspace.OrganizationId, "workspace-vm-" + generation.VmId,
                                generation.Generation.ToString(CultureInfo.InvariantCulture) + "-" + name), null))
            .ToDictionary(x => x.Path, StringComparer.Ordinal);
        var references = routeReferences.Concat(workspace.Vms.SelectMany(x => new[] { x.SshBootstrapSecretRef, x.RuntimeCredentialRef }));
        foreach (var raw in references.OfType<string>())
        {
            var reference = JsonSerializer.Deserialize<StoredSecretReference>(raw, RecoveryScope.Json);
            if (reference is null || reference.Provider != OpenBaoSecretStore.ProviderName || !expected.ContainsKey(reference.Path))
                throw new RecoveryRefused("A workspace secret reference is not scoped to this workspace's generations.");
        }
        return expected.Values;
    }

    private sealed record Tombstone(RecoveryScope Scope, DateTimeOffset ProviderAbsenceVerifiedAt);
}
