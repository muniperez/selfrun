using System.Globalization;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunWorkspacesAsync(string[] args, CancellationToken ct)
    {
        var action = args.FirstOrDefault();
        if (action is not ("list" or "create" or "status" or "start" or "stop" or "cancel-idle-stop"))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud workspaces <list|create|status|start|stop|cancel-idle-stop> [options]");

        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null && me.Organizations.Count == 1) organizationId = me.Organizations[0].Id;
        if (organizationId is null || !me.Organizations.Any(x => x.Id == organizationId))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Select an accessible organization with --organization <id>.");

        if (action == "list")
        {
            var items = await _apiClient.GetDevelopmentWorkspacesAsync(organizationId, ct);
            foreach (var item in items)
                _console.WriteLine($"{item.Id}\t{item.Name}\t{item.Profile}\t{item.DesiredState}/{item.Phase}\tgeneration={item.Generation}");
            if (items.Count == 0) _console.WriteLine("No development workspaces.");
            return CliExitCodes.Success;
        }

        if (action == "create")
        {
            var name = GetOption(args, "--name");
            var image = GetOption(args, "--image");
            var profile = GetOption(args, "--profile");
            var plan = GetOption(args, "--plan");
            var volume = GetOption(args, "--volume-gb");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(image)
                || string.IsNullOrWhiteSpace(profile) || string.IsNullOrWhiteSpace(plan)
                || !int.TryParse(volume, NumberStyles.None, CultureInfo.InvariantCulture, out var volumeGb))
                throw new CliCommandException(CliExitCodes.ValidationError,
                    "Usage: minicloud workspaces create --name <name> --image <registered-image> --profile Standard|Heavy --plan <plan-id> --volume-gb <10-10000> [--organization <id>] [--idempotency-key <key>]");
            var key = GetOption(args, "--idempotency-key") ?? Guid.NewGuid().ToString("N");
            var created = await _apiClient.CreateDevelopmentWorkspaceAsync(new CreateDevelopmentWorkspaceRequest(
                organizationId, name, image, profile, plan, volumeGb, key), ct);
            WriteWorkspace(created);
            _console.WriteLine($"Create idempotency key: {key}");
            return CliExitCodes.Success;
        }

        var id = args.Skip(1).FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(id))
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"Usage: minicloud workspaces {action} <workspace-id> --organization <id>");
        var workspace = action == "status"
            ? await _apiClient.GetDevelopmentWorkspaceAsync(id, organizationId, ct)
            : await _apiClient.MutateDevelopmentWorkspaceAsync(id, organizationId, action, ct);
        WriteWorkspace(workspace);
        return CliExitCodes.Success;
    }

    private void WriteWorkspace(DevelopmentWorkspaceResponse workspace)
    {
        _console.WriteLine($"Workspace: {workspace.Name} ({workspace.Id})");
        _console.WriteLine($"State: {workspace.DesiredState} / {workspace.Phase}; generation {workspace.Generation}");
        _console.WriteLine($"Placement: {workspace.Profile} / {workspace.Sku} / {workspace.Region}");
        _console.WriteLine($"Persistent storage: {workspace.VolumeSizeGb} GB (retained; charges may continue while compute is stopped)");
        _console.WriteLine($"Estimated monthly cost: {workspace.EstimatedMonthlyCompute + workspace.EstimatedMonthlyStorage:F2} {workspace.Currency}"
            + (workspace.PricingUpdatedAt is null ? " (operator rate timestamp unavailable)" : $" (rates as of {workspace.PricingUpdatedAt:O})"));
        if (workspace.ShutdownDeadline is not null) _console.WriteLine($"Shutdown deadline: {workspace.ShutdownDeadline:O}");
        if (workspace.ErrorCode is not null) _console.WriteLine($"Blocked: {workspace.ErrorCode}: {workspace.ErrorMessage}");
    }
}
