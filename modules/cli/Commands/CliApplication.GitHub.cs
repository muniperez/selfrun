// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private async Task<int> RunGitHubAsync(string[] args, CancellationToken ct)
    {
        var command = args.FirstOrDefault();
        if (command is not ("setup" or "status" or "repositories" or "disconnect" or "reconnect" or "reconcile"))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud github setup|status|repositories|disconnect|reconnect|reconcile [--organization <id>] [--no-browser]");
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null && me.Organizations.Count == 1) organizationId = me.Organizations[0].Id;
        if (organizationId is null || !me.Organizations.Any(x => x.Id == organizationId))
            throw new CliCommandException(CliExitCodes.ValidationError, "Select an available organization with --organization <id>.");
        if (command == "setup")
        {
            var link = await _apiClient.GetGitHubSetupUrlAsync(organizationId, ct);
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0)
                throw new CliCommandException(CliExitCodes.ValidationError, "The server returned an invalid setup URL.");
            _console.WriteLine("Open this URL and sign in as an organization owner or administrator:");
            _console.WriteLine(link.Url);
            if (!args.Contains("--no-browser", StringComparer.Ordinal)) TryOpenBrowser(link.Url);
            _console.WriteLine("Create the App, then install it. Run 'minicloud github status' to inspect the result.");
            return CliExitCodes.Success;
        }
        var status = await _apiClient.GetGitHubStatusAsync(organizationId, ct);
        if (command == "repositories")
        {
            if (status.ConnectionId is null)
                throw new CliCommandException(CliExitCodes.ValidationError, "Run 'minicloud github setup' first.");
            var repositories = await _apiClient.GetGitHubRepositoriesAsync(status.ConnectionId, ct);
            foreach (var repository in repositories) _console.WriteLine(repository.FullName + (repository.Private ? " (private)" : ""));
            _console.WriteLine($"Verified repositories: {repositories.Count}");
            return CliExitCodes.Success;
        }
        if (command != "status")
        {
            if (status.ConnectionId is null)
                throw new CliCommandException(CliExitCodes.ValidationError, "No GitHub connection is configured.");
            status = await _apiClient.MutateGitHubAsync(status.ConnectionId, command!, ct);
        }
        if (args.Contains("--json", StringComparer.Ordinal))
            _console.WriteLine(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        else
        {
            _console.WriteLine("GitHub: " + status.Status);
            if (status.AppSlug is not null) _console.WriteLine("App: https://github.com/apps/" + status.AppSlug);
            if (status.OwnerLogin is not null) _console.WriteLine("Owner: " + status.OwnerLogin);
            if (status.FailureCode is not null) _console.WriteLine("Action required: " + status.FailureCode + ". Inspect GitHub settings and reconcile.");
        }
        return CliExitCodes.Success;
    }
}
