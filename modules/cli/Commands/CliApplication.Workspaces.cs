using System.Globalization;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunWorkspacesAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        if (action is not ("list" or "create" or "status" or "start" or "stop" or "cancel-idle-stop"
            or "resize-next-start" or "vms" or "launch" or "vm-status" or "vm-start" or "vm-stop"
            or "vm-cancel-idle-stop" or "vm-discard" or "vm-resize-next-start" or "stop-all"
            or "task-renew" or "task-complete"))
            throw WorkspaceUsage("Expected list, create, status, start, stop, cancel-idle-stop, resize-next-start, vms, launch, vm-status, vm-start, vm-stop, vm-cancel-idle-stop, vm-discard, vm-resize-next-start, stop-all, task-renew or task-complete. See workspaces --help.");

        string Required(string option) => GetOption(args, option) is { Length: > 0 } value
            ? value : throw WorkspaceUsage($"Required: {option} <value>.");
        long Generation() => long.TryParse(Required("--generation"), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw WorkspaceUsage("--generation must identify the current VM generation (non-negative integer).");
        void Acknowledge(string flag)
        {
            if (!args.Contains(flag)) throw WorkspaceUsage($"Explicit {flag} is required.");
        }
        DevelopmentWorkspaceResizeRequest Resize() => new(Generation(), Required("--image"), Required("--profile"), Required("--plan"));

        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null && me.Organizations.Count == 1) organizationId = me.Organizations[0].Id;
        if (organizationId is null || !me.Organizations.Any(x => x.Id == organizationId))
            throw WorkspaceUsage("Select an accessible organization with --organization <id>.");

        if (action == "list")
        {
            var items = await _apiClient.GetDevelopmentWorkspacesAsync(organizationId, ct);
            foreach (var item in items) WriteWorkspace(item);
            if (items.Count == 0) _console.WriteLine("No development workspaces.");
            return CliExitCodes.Success;
        }
        if (action == "create")
        {
            if (!int.TryParse(Required("--volume-gb"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size is < 10 or > 10000)
                throw WorkspaceUsage("--volume-gb must be between 10 and 10000.");
            var key = GetOption(args, "--idempotency-key") ?? Guid.NewGuid().ToString("N");
            _console.WriteLine($"Create idempotency key: {key}. Reuse this key when retrying the same request.");
            WriteWorkspace(await _apiClient.CreateDevelopmentWorkspaceAsync(new CreateDevelopmentWorkspaceRequest(
                organizationId, Required("--name"), Required("--image"), Required("--profile"), Required("--plan"), size, key), ct));
            _console.WriteLine("Workspace created stopped; start is explicit.");
            return CliExitCodes.Success;
        }

        var id = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(id) || id.StartsWith("--", StringComparison.Ordinal))
            throw WorkspaceUsage($"Usage: minicloud workspaces {action} <workspace-id> [options]");
        if (action == "vms" || action == "vm-status")
        {
            var vms = await _apiClient.GetWorkspaceVmsAsync(id, organizationId, ct);
            if (action == "vm-status") vms = vms.Where(x => x.Id == Required("--vm")).ToArray();
            if (vms.Count == 0 && action == "vm-status") throw WorkspaceUsage("VM not found in selected workspace.");
            foreach (var vm in vms) WriteWorkspaceVm(vm);
            return CliExitCodes.Success;
        }
        if (action.StartsWith("task-", StringComparison.Ordinal))
        {
            var vm = Required("--vm");
            var task = Required("--task");
            var generation = Generation();
            var complete = action == "task-complete";
            object body = complete
                ? new CompleteDevelopmentWorkspaceTaskRequest(generation, args.Contains("--cancelled"))
                : new DevelopmentWorkspaceGenerationRequest(generation);
            var lease = await _apiClient.WorkspaceTaskAsync(id, vm, organizationId, task, complete, body, ct);
            _console.WriteLine($"Task {lease.TaskId}: {lease.Status}; VM {lease.VmId}, generation {lease.Generation}; lease expires {lease.ExpiresAt:O}");
            return CliExitCodes.Success;
        }
        if (action == "launch")
        {
            Acknowledge("--push-authorized");
            Acknowledge("--acknowledge-ignored-data-loss");
            var request = new LaunchEphemeralDevelopmentWorkspaceVmRequest(
                Required("--image"), Required("--profile"), Required("--plan"), Required("--repository"),
                Required("--base-ref"), Required("--base-commit"), true, true,
                GetOption(args, "--idempotency-key") ?? Guid.NewGuid().ToString("N"), GetOption(args, "--repository-credential-ref"));
            _console.WriteLine($"Launch idempotency key: {request.IdempotencyKey}. Reuse this key when retrying the same request.");
            WriteWorkspace(await _apiClient.WorkspaceOperationAsync(id, organizationId, "vms/ephemeral", request, ct));
            _console.WriteLine($"Launch idempotency key: {request.IdempotencyKey}. Git-only root: ignored/untracked data is not retained. Launch does not push, open a PR, or merge.");
            return CliExitCodes.Success;
        }
        if (action.StartsWith("vm-", StringComparison.Ordinal))
        {
            var vm = Required("--vm");
            var operation = action["vm-".Length..];
            object body;
            if (operation == "discard")
            {
                Acknowledge("--discard-local-data");
                Acknowledge("--acknowledge-ignored-data-loss");
                body = new DevelopmentWorkspaceDiscardRequest(Generation(), true, true);
                _console.WriteLine($"IRREVERSIBLE discard requested for VM {vm}, generation {Generation()}. Local ignored/untracked/unpushed data will be lost.");
            }
            else body = operation == "resize-next-start" ? Resize() : new DevelopmentWorkspaceGenerationRequest(Generation());
            WriteWorkspace(await _apiClient.WorkspaceOperationAsync(id, organizationId,
                $"vms/{Uri.EscapeDataString(vm)}/{(operation == "resize-next-start" ? "resize" : operation)}", body, ct));
            return CliExitCodes.Success;
        }
        if (action == "stop-all")
        {
            var current = await _apiClient.GetDevelopmentWorkspaceAsync(id, organizationId, ct);
            _console.WriteLine("Stopping the following VMs; Git-save guards apply independently and may block deletion:");
            foreach (var vm in current.Vms ?? []) WriteWorkspaceVm(vm);
        }
        var workspace = action == "status"
            ? await _apiClient.GetDevelopmentWorkspaceAsync(id, organizationId, ct)
            : action == "resize-next-start"
                ? await _apiClient.WorkspaceOperationAsync(id, organizationId, "resize", Resize(), ct)
                : await _apiClient.MutateDevelopmentWorkspaceAsync(id, organizationId, action, ct);
        WriteWorkspace(workspace);
        return CliExitCodes.Success;
    }

    private static CliCommandException WorkspaceUsage(string message) => new(CliExitCodes.ValidationError, message);

    private void WriteWorkspace(DevelopmentWorkspaceResponse workspace)
    {
        _console.WriteLine($"Workspace: {workspace.Name} ({workspace.Id})");
        _console.WriteLine($"Primary retained storage: {workspace.VolumeSizeGb} GB; {workspace.VolumeStatus ?? "status unavailable"}; storage charges continue while stopped.");
        _console.WriteLine($"Estimated storage: {workspace.EstimatedMonthlyStorage:F2} {workspace.Currency}/month; rates {(workspace.PricingUpdatedAt?.ToString("O") ?? "timestamp unavailable")}");
        foreach (var vm in workspace.Vms ?? []) WriteWorkspaceVm(vm);
        if (workspace.ErrorCode is not null) _console.WriteLine($"Blocked: {workspace.ErrorCode}: {workspace.ErrorMessage}");
    }

    private void WriteWorkspaceVm(DevelopmentWorkspaceVmResponse vm)
    {
        _console.WriteLine($"VM {vm.Id}: {vm.Kind}; {vm.DesiredState}/{vm.Phase}; generation={vm.Generation}; {vm.Profile}/{vm.Sku}/{vm.Region}");
        _console.WriteLine(vm.Kind == "primary" ? "Data: retained primary volume." : "Data: independent Git-only root; ignored/untracked/unpushed data can be lost. No primary volume access.");
        _console.WriteLine($"Readiness stage: {vm.Phase}; estimated compute {vm.EstimatedMonthlyCompute:F2}/month ({vm.ComputeHourlyRate:F4}/hour); reserved={vm.ComputeReserved}");
        if (vm.ShutdownDeadline is not null) _console.WriteLine($"Idle stop deadline: {vm.ShutdownDeadline:O}");
        if (vm.LastActivityAt is not null) _console.WriteLine($"Last activity: {vm.LastActivityAt:O}");
        if (vm.NextProfile is not null) _console.WriteLine($"Next start: {vm.NextProfile}/{vm.NextPlanId}/{vm.NextImageId}; no live CPU/RAM resize.");
        if (vm.StartAfterStop) _console.WriteLine("Authorized start queued after the current generation is safely stopped.");
        if (vm.Repository is not null) _console.WriteLine($"Git: {vm.Repository} {vm.BaseRef}@{vm.BaseCommit}; branch={vm.WorkBranch}; save={vm.GitStatus}; remote={vm.VerifiedRemoteRef}@{vm.VerifiedRemoteCommit}");
        foreach (var lease in vm.TaskLeases ?? [])
            _console.WriteLine($"Task {lease.TaskId}: {lease.Status}; generation {lease.Generation}; lease expires {lease.ExpiresAt:O}");
        if (vm.TaskLeases is null || vm.TaskLeases.Count == 0) _console.WriteLine("No task leases reported.");
        if (vm.Phase == "awaiting_git_save")
            _console.WriteLine("Awaiting Git save: resolve the reported remote/authentication/conflict guard, then retry stop; use explicit generation-bound discard only if local data loss is intended.");
        if (vm.ErrorCode is not null) _console.WriteLine($"Blocked: {vm.ErrorCode}: {vm.ErrorMessage}");
    }
}
