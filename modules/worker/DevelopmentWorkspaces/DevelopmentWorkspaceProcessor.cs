using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessor(
    MinicloudDbContext db, DevelopmentWorkspaceService lifecycle, ProviderAccountClientFactory providers,
    ISecretStore secrets, IWorkspaceRuntimeTransport runtime, IWorkspaceTunnelManager tunnels,
    IOptions<DevelopmentWorkspaceWorkerOptions> options,
    IOptions<DeploymentPipelineOptions> pipeline, IIdGenerator ids, TimeProvider clock, MaintenanceParticipant maintenance,
    WorkspaceGitAccessService gitAccess, IOptions<DevelopmentWorkspaceOptions> policy,
    ILogger<DevelopmentWorkspaceProcessor>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTimeOffset Now => clock.GetUtcNow();
    private sealed class Evidence
    {
        public bool VolumeAttempted { get; set; }
        public bool NewVolume { get; set; }
        public bool FirewallAttempted { get; set; }
        public bool AllocationAttempted { get; set; }
        public List<WorkspaceCloudAction> Actions { get; set; } = [];
        public DateTimeOffset? GatewayRevokedAt { get; set; }
        public string? ReceiptId { get; set; }
        public string? EnvironmentId { get; set; }
        public bool DeleteCommitted { get; set; }
        public bool RepositoryCredentialRevoked { get; set; }
        public long? T3SnapshotSequence { get; set; }
        public WorkspaceRuntimeRejection? LastRuntimeRejection { get; set; }
        public WorkspaceRuntimeRejection? LastRuntimeRecoveryRejection { get; set; }
        public DevelopmentWorkspaceGitRecoverySource? GitRecovery { get; set; }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var operation = await lifecycle.ClaimOperationAsync("workspace-" + Environment.ProcessId, ct);
        if (operation is null)
        {
            var queued = await db.DevelopmentWorkspaceVms.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Phase == "stopped" && x.StartAfterStop, ct);
            if (queued is null) return false;
            await lifecycle.StartVmAsync(queued.WorkspaceId, queued.OrganizationId, queued.Id, queued.Generation, ct);
            return true;
        }
        var token = operation.LeaseToken!;
        Evidence? operationEvidence = null;
        try
        {
            var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == operation.VmId, ct);
            var workspace = await db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == operation.WorkspaceId, ct);
            if (operation.Kind == "delete-workspace" && (operation.ProviderAccountId != workspace.ProviderAccountId
                || vm.ProviderAccountId != operation.ProviderAccountId || vm.OrganizationId != workspace.OrganizationId
                || operation.ProviderResourceId != workspace.ProviderVolumeId || workspace.VolumeStatus != "deleting"))
                throw DeleteBlocked("workspace_delete_identity_mismatch", "The recorded workspace provider account or volume changed. Contact an operator; no volume was deleted.");
            var client = await providers.CreateAsync(vm.OrganizationId, operation.ProviderAccountId, ct, allowDisabled: operation.Kind != "start" || vm.ProviderServerId is not null);
            if (client is not IDevelopmentWorkspaceProvider cloud) throw new InvalidOperationException("Workspace provider must be Hetzner.");
            var evidence = operationEvidence = JsonSerializer.Deserialize<Evidence>(operation.ProviderEvidenceJson ?? "{}", Json)!;
            if (operation.Kind == "start") await StartStepAsync(cloud, workspace, vm, operation, evidence, ct);
            else if (operation.Kind == "stop") await StopStepAsync(cloud, workspace, vm, operation, evidence, ct);
            else if (operation.Kind == "delete-workspace") await DeleteStepAsync(cloud, workspace, vm, operation, ct);
            else throw new InvalidOperationException("Unsupported workspace operation.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DevelopmentWorkspaceException error) when (error.Code is "workspace_operation_stale" or "workspace_generation_stale" or "workspace_revision_conflict") { }
        catch (Exception error)
        {
            logger?.LogError(
                "Workspace operation {OperationId} ({OperationKind}) failed at step {Step}; exception type {ExceptionType}; HTTP status {HttpStatusCode}. Reconciliation is required.",
                operation.Id, operation.Kind, operation.Step, error.GetType().Name,
                error is HttpRequestException { StatusCode: { } status } ? (int?)status : null);
            try
            {
                await lifecycle.UpdateClaimedOperationAsync(operation.Id, token, (op, vm, _) =>
                {
                    if (op.Kind == "delete-workspace")
                    {
                        RecordDeleteFailure(op, vm, error);
                        return;
                    }
                    var recoveryError = error is DevelopmentWorkspaceException { Code:
                        "workspace_git_recovery_source_changed" or "workspace_git_recovery_source_unavailable" } recovery
                        ? recovery.Code : null;
                    vm.Phase = "blocked"; vm.GatewayReady = false;
                    vm.ErrorCode = recoveryError ?? "workspace_reconciliation_required";
                    vm.ErrorMessage = recoveryError is null
                        ? "Provider/runtime state is unconfirmed. Resources remain reserved and billed; inspect the operation before retrying."
                        : "The saved Git remote source changed or could not be verified. Recovery remains reserved and billed; no original-base fallback was performed.";
                    if (operationEvidence?.LastRuntimeRejection is { } rejection)
                        vm.ErrorMessage += " Last recorded rejection: " + rejection.Summary;
                    op.ErrorCode = vm.ErrorCode; op.ErrorMessage = vm.ErrorMessage;
                    Release(op);
                }, ct);
            }
            catch (DevelopmentWorkspaceException) { }
        }
        return true;
    }

    private Task SaveAsync(DevelopmentWorkspaceOperation op, Evidence evidence, string step,
        Action<DevelopmentWorkspaceOperation, DevelopmentWorkspaceVm, DevelopmentWorkspace>? update, CancellationToken ct, bool release = true) =>
        lifecycle.UpdateClaimedOperationAsync(op.Id, op.LeaseToken!, (fresh, vm, workspace) =>
        {
            fresh.Step = step; fresh.ProviderEvidenceJson = JsonSerializer.Serialize(evidence, Json);
            vm.ErrorCode = null; vm.ErrorMessage = null; fresh.ErrorCode = null; fresh.ErrorMessage = null;
            update?.Invoke(fresh, vm, workspace);
            if (release && fresh.Status != "completed") Release(fresh);
        }, ct);

    private static void Release(DevelopmentWorkspaceOperation operation)
    {
        operation.Status = "pending"; operation.LeaseOwner = null; operation.LeaseToken = null; operation.LeaseExpiresAt = null;
    }

    private static Dictionary<string, string> VolumeLabels(DevelopmentWorkspace workspace) => new()
    {
        ["managed-by"] = "minicloud-workspaces", ["organization"] = workspace.OrganizationId,
        ["workspace"] = workspace.Id, ["kind"] = "primary"
    };

    private static Dictionary<string, string> ComputeLabels(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm) =>
        new(VolumeLabels(workspace))
        {
            ["kind"] = vm.Kind, ["vm"] = vm.Id, ["generation"] = WorkspaceBootstrap.Generation(vm.Generation),
            ["operation"] = vm.AllocationOperationId ?? throw new InvalidOperationException("Allocation identity is missing.")
        };

    private static WorkspaceRuntimeTarget Target(DevelopmentWorkspaceVm vm) =>
        vm.SshHostKeyGeneration == vm.Generation && vm.IpAddress is not null && vm.SshHostKeyFingerprint is not null
            ? new(vm.Id, vm.Generation, vm.Kind, vm.IpAddress, vm.SshHostKeyFingerprint)
            : throw new InvalidOperationException("Generation-bound workspace SSH identity is unavailable.");

    private static JsonElement Result(JsonElement response)
    {
        if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Workspace supervisor refused the action.");
        return response.GetProperty("result").Clone();
    }

    private async Task<bool> WaitActionsAsync(IDevelopmentWorkspaceProvider cloud, Evidence evidence, CancellationToken ct)
    {
        if (evidence.Actions.Count == 0) return true;
        var pending = evidence.Actions[0];
        var current = await cloud.GetWorkspaceActionAsync(pending.Resource, pending.Id, ct);
        if (current.Status == "error") throw new InvalidOperationException("Workspace provider action failed.");
        if (current.Status == "success") evidence.Actions.RemoveAt(0);
        return evidence.Actions.Count == 0;
    }

    private async Task StartStepAsync(IDevelopmentWorkspaceProvider cloud, DevelopmentWorkspace workspace,
        DevelopmentWorkspaceVm vm, DevelopmentWorkspaceOperation op, Evidence evidence, CancellationToken ct)
    {
        if (evidence.GitRecovery is { } source &&
            (vm.Kind != "ephemeral" || source.Repository != vm.Repository || source.Commit != vm.BaseCommit
             || source.Generation != vm.Generation - 1
             || vm.WorkBranch != $"minicloud/{vm.Id}/g{vm.Generation}"
             || source.RemoteRef != $"refs/heads/minicloud/{vm.Id}/g{source.Generation}"))
            throw new InvalidOperationException("Pinned saved Git source differs from the generation being started.");
        if (op.Step == "git-recovery-pending" && evidence.GitRecovery is null)
            throw new InvalidOperationException("Saved Git recovery evidence is missing.");
        switch (op.Step)
        {
            case "pending":
            case "git-recovery-pending":
                var image = await cloud.GetWorkspaceImageAsync(op.ProviderImage, ct);
                if (image is null || image.Status != "available" || image.Type != "snapshot" || image.Architecture != "x86" || image.OsFlavor != "ubuntu")
                    throw new InvalidOperationException("The exact qualified Ubuntu amd64 workspace snapshot is unavailable.");
                await SaveAsync(op, evidence, "bootstrap-secret", null, ct); break;
            case "bootstrap-secret":
                var bootstrap = WorkspaceBootstrap.Generate();
                var reference = await StoreAsync(vm, "bootstrap", JsonSerializer.Serialize(bootstrap, Json), ct);
                await SaveAsync(op, evidence, "volume", (_, fresh, _) =>
                {
                    fresh.SshBootstrapSecretRef = JsonSerializer.Serialize(reference, Json);
                    fresh.SshHostKey = bootstrap.HostPublicKey; fresh.SshHostKeyFingerprint = bootstrap.HostFingerprint;
                    fresh.SshHostKeyGeneration = fresh.Generation;
                    fresh.EnrollmentTokenDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bootstrap.EnrollmentToken)));
                    fresh.EnrollmentTokenExpiresAt = Now.AddMinutes(30);
                }, ct); break;
            case "volume":
                if (vm.Kind == DevelopmentWorkspaceVmKinds.Ephemeral) { await SaveAsync(op, evidence, "firewall", null, ct); break; }
                WorkspaceCloudVolume? volume = workspace.ProviderVolumeId is not null ? await cloud.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId, ct) : null;
                if (workspace.ProviderVolumeId is not null && volume is null) throw new InvalidOperationException("Retained volume is missing; never replace it automatically.");
                if (volume is null)
                {
                    var found = await cloud.FindWorkspaceVolumesAsync(VolumeLabels(workspace), ct);
                    if (found.Count > 1) throw new InvalidOperationException("Workspace volume ownership is ambiguous.");
                    volume = found.SingleOrDefault();
                }
                if (volume is null)
                {
                    if (evidence.VolumeAttempted) throw new InvalidOperationException("Volume allocation outcome is unknown.");
                    evidence.VolumeAttempted = true;
                    await SaveAsync(op, evidence, "volume", null, ct, release: false);
                    var labels = VolumeLabels(workspace); labels["created-for"] = op.Id;
                    var allocation = await cloud.CreateWorkspaceVolumeAsync("mc-" + workspace.Id.Replace('_', '-'), workspace.VolumeSizeGb, workspace.Region, labels, ct);
                    volume = allocation.Volume;
                    if (allocation.Action is not null) evidence.Actions.Add(allocation.Action);
                    evidence.Actions.AddRange(allocation.NextActions);
                }
                HetznerClient.RequireWorkspaceOwnership(volume.Labels, VolumeLabels(workspace));
                if (volume.Region != workspace.Region || volume.SizeGb != workspace.VolumeSizeGb || volume.ServerId is not null)
                    throw new InvalidOperationException("Retained volume placement or writer is unconfirmed.");
                evidence.NewVolume = workspace.VolumeFilesystemUuid is null &&
                    volume.Labels.TryGetValue("created-for", out var creator) && creator == op.Id;
                if (workspace.VolumeFilesystemUuid is null && !evidence.NewVolume)
                    throw new InvalidOperationException("Existing volume filesystem ownership must be reconciled, not initialized.");
                var volumeId = volume.Id;
                await SaveAsync(op, evidence, "volume-ready", (_, _, fresh) =>
                { fresh.ProviderVolumeId = volumeId; fresh.VolumeStatus = "creating"; }, ct); break;
            case "volume-ready":
                if (!await WaitActionsAsync(cloud, evidence, ct)) { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                await SaveAsync(op, evidence, "firewall", (_, _, fresh) => fresh.VolumeStatus = "retained", ct); break;
            case "firewall":
                var firewallMatches = await cloud.FindWorkspaceFirewallsAsync(ComputeLabels(workspace, vm), ct);
                if (firewallMatches.Count > 1) throw new InvalidOperationException("Workspace firewall ownership is ambiguous.");
                var firewall = firewallMatches.SingleOrDefault();
                if (firewall is null)
                {
                    if (evidence.FirewallAttempted) throw new InvalidOperationException("Firewall allocation outcome is unknown.");
                    evidence.FirewallAttempted = true;
                    await SaveAsync(op, evidence, op.Step, null, ct, release: false);
                    firewall = await cloud.CreateWorkspaceFirewallAsync("mc-" + vm.Id.Replace('_', '-') + "-g" + vm.Generation,
                        options.Value.ManagementAddresses, ComputeLabels(workspace, vm), ct);
                }
                var firewallId = firewall.Id;
                await SaveAsync(op, evidence, "allocate", (_, fresh, _) => fresh.ProviderFirewallId = firewallId, ct); break;
            case "allocate":
                var matches = await cloud.FindWorkspaceServersAsync(ComputeLabels(workspace, vm), ct);
                if (matches.Count > 1) throw new InvalidOperationException("Workspace compute allocation is ambiguous.");
                var server = matches.SingleOrDefault();
                if (server is null)
                {
                    if (evidence.AllocationAttempted) throw new InvalidOperationException("Compute allocation outcome is unknown; no new VM was created.");
                    var savedBootstrap = JsonSerializer.Deserialize<WorkspaceBootstrapSecret>(await SecretAsync(vm.SshBootstrapSecretRef!, ct), Json)!;
                    var userData = WorkspaceBootstrap.CloudConfig(savedBootstrap, pipeline.Value.SshPublicKey);
                    evidence.AllocationAttempted = true;
                    await SaveAsync(op, evidence, op.Step, null, ct, release: false);
                    var allocated = await cloud.CreateWorkspaceServerAsync(new("mc-" + vm.Id.Replace('_', '-') + "-g" + vm.Generation,
                        op.Sku, op.Region, op.ProviderImage, userData, vm.ProviderFirewallId!, ComputeLabels(workspace, vm)), ct);
                    server = allocated.Server; evidence.Actions.Add(allocated.Action);
                }
                var captured = server;
                await SaveAsync(op, evidence, "server-ready", (_, fresh, _) =>
                { fresh.ProviderServerId = captured.Id; fresh.ProviderIpId = captured.PrimaryIpId; fresh.IpAddress = captured.Ipv4; fresh.Phase = "provisioning"; }, ct); break;
            case "server-ready":
                if (!await WaitActionsAsync(cloud, evidence, ct)) { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                var running = await cloud.GetWorkspaceServerAsync(vm.ProviderServerId!, ct) ?? throw new InvalidOperationException("Allocated workspace VM disappeared.");
                HetznerClient.RequireWorkspaceOwnership(running.Labels, ComputeLabels(workspace, vm));
                if (running.Sku != op.Sku || running.Region != op.Region || running.ImageId != op.ProviderImage)
                    throw new InvalidOperationException("Provider placement differs from the immutable workspace reservation.");
                if (running.Status != "running" || !running.FirewallsApplied || !running.FirewallIds!.Contains(vm.ProviderFirewallId!))
                { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                await cloud.SecureWorkspacePrimaryIpAsync(running, ComputeLabels(workspace, vm), ct);
                await SaveAsync(op, evidence, vm.Kind == "primary" ? "attach" : "bootstrap", (_, fresh, _) =>
                { fresh.ProviderIpId = running.PrimaryIpId; fresh.IpAddress = running.Ipv4; }, ct); break;
            case "attach":
                if (workspace.VolumeAttachedVmId is not null && workspace.VolumeAttachedVmId != vm.Id)
                    throw new InvalidOperationException("A previous primary writer is not fenced.");
                var attach = await cloud.AttachWorkspaceVolumeAsync(workspace.ProviderVolumeId!, vm.ProviderServerId!, VolumeLabels(workspace), ct);
                if (attach is not null) evidence.Actions.Add(attach);
                await SaveAsync(op, evidence, "mount", (_, fresh, parent) =>
                { fresh.Phase = "attaching"; parent.VolumeAttachedVmId = fresh.Id; parent.VolumeStatus = "attaching"; }, ct); break;
            case "mount":
                if (!await WaitActionsAsync(cloud, evidence, ct)) { await SaveAsync(op, evidence, op.Step, null, ct); break; }
                var attached = await cloud.GetWorkspaceVolumeAsync(workspace.ProviderVolumeId!, ct) ?? throw new InvalidOperationException("Retained volume disappeared.");
                HetznerClient.RequireWorkspaceOwnership(attached.Labels, VolumeLabels(workspace));
                if (attached.ServerId != vm.ProviderServerId || attached.Filesystem != "ext4")
                    throw new InvalidOperationException("Retained volume attachment or filesystem is unconfirmed.");
                var probe = Result(await runtime.VolumeProbeAsync(Target(vm), attached.Id, ct));
                var uuid = probe.GetProperty("filesystemUuid").GetString()!;
                if (string.IsNullOrWhiteSpace(uuid) || workspace.VolumeFilesystemUuid is not null && workspace.VolumeFilesystemUuid != uuid)
                    throw new InvalidOperationException("Retained volume filesystem UUID differs from its recorded identity.");
                await SaveAsync(op, evidence, "bootstrap", (_, fresh, parent) =>
                { parent.VolumeFilesystemUuid = uuid; fresh.Phase = "bootstrapping"; }, ct); break;
            case "bootstrap":
                if (evidence.GitRecovery is { } recovery)
                {
                    await RefreshRepositoryCredentialAsync(vm, ct, force: true);
                    await runtime.VerifyGitRecoverySourceAsync(Target(vm), recovery, ct);
                }
                if (vm.EnrolledAt is not null)
                {
                    var recovered = Result(await runtime.CallAsync(Target(vm), op.Id, "status", null, ct));
                    if (recovered.GetProperty("phase").GetString() != "Ready")
                        throw new InvalidOperationException("Accepted enrollment has no ready runtime.");
                    await SaveAsync(op, evidence, "session", null, ct);
                    break;
                }
                var identity = JsonSerializer.Deserialize<WorkspaceBootstrapSecret>(await SecretAsync(vm.SshBootstrapSecretRef!, ct), Json)!;
                if (evidence.GitRecovery is null) await RefreshRepositoryCredentialAsync(vm, ct, force: true);
                var endpoint = new Uri(new Uri(options.Value.ApiBaseUrl.TrimEnd('/') + "/"), "api/development-workspaces/runtime/enroll");
                if (endpoint.Scheme != "https") throw new InvalidOperationException("VM enrollment requires public authenticated HTTPS.");
                Result(await runtime.BootstrapAsync(Target(vm), RuntimeConfig(workspace, vm, evidence), new
                {
                    url = endpoint.AbsoluteUri, token = identity.EnrollmentToken, vmId = vm.Id, generation = vm.Generation,
                    operationId = op.Id, expiresAt = vm.EnrollmentTokenExpiresAt!.Value.ToUnixTimeSeconds()
                }, ct));
                await SaveAsync(op, evidence, "session", (_, fresh, parent) =>
                {
                    if (fresh.EnrolledAt is null) throw new InvalidOperationException("The one-time enrollment was not durably accepted.");
                    parent.VolumeStatus = fresh.Kind == "primary" ? "mounted" : parent.VolumeStatus;
                    if (fresh.Kind == "ephemeral") { fresh.BaseVerifiedGeneration = fresh.Generation; fresh.BaseVerifiedAt = Now; }
                }, ct); break;
            case "session":
                await RefreshRuntimeAsync(workspace, vm, op.Id, ct);
                await SaveAsync(op, evidence, "ready", (fresh, machine, _) =>
                {
                    machine.Phase = "ready"; machine.GatewayReady = true; machine.LastReadyAt = Now; machine.LastHeartbeatAt = Now;
                    machine.LastActivityAt = Now; fresh.Status = "completed";
                    fresh.LeaseOwner = null; fresh.LeaseToken = null; fresh.LeaseExpiresAt = null;
                }, ct); break;
            default: throw new InvalidOperationException("Unknown workspace start stage.");
        }
    }

    private object RuntimeConfig(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm, Evidence evidence)
    {
        var config = new Dictionary<string, object>
        {
            ["vmId"] = vm.Id, ["generation"] = vm.Generation, ["kind"] = vm.Kind,
            ["uid"] = 2000, ["gid"] = 2000, ["workspace"] = "/workspace"
        };
        config["observedTerminalInputs"] = options.Value.ObservedTerminalInputs;
        var units = options.Value.PreviewServices.Values.Select(x => x.SupervisedUnit).Where(x => x is not null).ToArray();
        if (units.Any(unit => !System.Text.RegularExpressions.Regex.IsMatch(unit!, "^minicloud-preview-[a-z0-9-]{1,24}\\.service$")))
            throw new InvalidOperationException("Preview supervision requires an explicitly registered Minicloud preview unit.");
        config["infrastructureUnits"] = new[] { "minicloud-t3.service" }.Concat(units!).ToArray();
        config["stopUnits"] = new[] { "minicloud-t3.service" }.Concat(units!).ToArray();
        if (vm.Kind == "primary")
        {
            config["volume"] = new
            {
                device = "/dev/disk/by-id/scsi-0HC_Volume_" + workspace.ProviderVolumeId,
                providerDeviceId = "scsi-0HC_Volume_" + workspace.ProviderVolumeId,
                providerVolumeId = workspace.ProviderVolumeId, filesystemUuid = workspace.VolumeFilesystemUuid,
                initializeOwner = evidence.NewVolume,
                owner = new { organizationId = workspace.OrganizationId, workspaceId = workspace.Id,
                    providerAccountId = workspace.ProviderAccountId, location = workspace.Region, providerVolumeId = workspace.ProviderVolumeId }
            };
            if (options.Value.BackupRecipient is not null)
                config["backup"] = new { transport = "management-ssh-stream", recipient = options.Value.BackupRecipient,
                    keyRecoveryId = options.Value.BackupRecoveryId, destinationIdentity = options.Value.BackupDestinationIdentity,
                    retentionDays = options.Value.BackupRetentionDays };
        }
        else
        {
            config["ignoredDataAcknowledged"] = vm.IgnoredDataAcknowledgedAt is not null;
            config["repositories"] = new[] { new { path = "/workspace/repos/project", origin = vm.Repository,
                baseSha = vm.BaseCommit, branch = vm.WorkBranch, remoteRef = "refs/heads/" + vm.WorkBranch,
                defaultBranches = new[] { "main", "master" }, pushAuthorized = vm.PushAuthorized } };
        }
        return config;
    }

    private Task<string> SecretAsync(string reference, CancellationToken ct) =>
        secrets.GetAsync(JsonSerializer.Deserialize<StoredSecretReference>(reference, Json)
            ?? throw new InvalidOperationException("Workspace secret reference is invalid."), ct);

    private Task<StoredSecretReference> StoreAsync(DevelopmentWorkspaceVm vm, string name, string value, CancellationToken ct) =>
        secrets.PutAsync(new(vm.OrganizationId, "", "", name, value)
        { IntegrationId = "workspace-vm-" + vm.Id, CredentialGeneration = WorkspaceBootstrap.Generation(vm.Generation) + "-" + name }, ct);
}
