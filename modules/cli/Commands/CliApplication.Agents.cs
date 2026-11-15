using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private sealed record LocalSshIdentity(string Fingerprint, string Label, string? PrivateKeyPath);
    internal const string RequestedCodexVersion = "0.169.0";
    internal const string RequestedT3Version = "0.1.9";

    private async Task<int> RunAgentsAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud agents create <name> --plan <plan> --agent codex --repo OWNER/REPO | list | status <name> | open <name> [--no-browser] | destroy <name> [--yes]");

        return args[0] switch
        {
            "create" => await CreateCodingWorkspaceAsync(args.Skip(1).ToArray(), ct),
            "list" => await ListCodingWorkspacesAsync(args, ct),
            "status" => await ShowCodingWorkspaceStatusAsync(args, ct),
            "open" => await OpenCodingWorkspaceAsync(args, ct),
            "destroy" => await DestroyCodingWorkspaceAsync(args, ct),
            "auth" => await AuthenticateCodingWorkspaceAsync(args, ct),
            _ => throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud agents create <name> --plan <plan> --agent codex --repo OWNER/REPO | list | status <name> | open <name> [--no-browser] | destroy <name> [--yes]")
        };
    }

    private async Task<int> CreateCodingWorkspaceAsync(string[] args, CancellationToken ct)
    {
        var json = IsAgentJsonMode(args);
        var name = args.FirstOrDefault();
        var planName = GetOption(args, "--plan");
        var agent = GetOption(args, "--agent");
        var repository = GetOption(args, "--repo");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(planName) ||
            string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(repository))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud agents create <name> --plan <plan> --agent codex --repo OWNER/REPO [--organization <id>] [--ssh-key <provider-key-id>] [--ssh-identity <private-key-path>]");
        if (!string.Equals(agent, "codex", StringComparison.OrdinalIgnoreCase))
            throw new CliCommandException(CliExitCodes.ValidationError, "The only supported coding workspace agent is 'codex'.");

        var organizationId = await ResolveAgentsOrganizationAsync(args, ct);
        var plan = (await _apiClient.GetPlansAsync(organizationId, ct))
            .SingleOrDefault(item => string.Equals(item.Name, planName, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Plan '{planName}' was not found in organization '{organizationId}'.");
        var providerKeys = await _apiClient.GetProviderSshKeysAsync(plan.ProviderAccountId, organizationId, ct);
        var localIdentities = GetLocalSshIdentities();
        var matches = providerKeys
            .SelectMany(key => localIdentities.Where(identity => identity.Fingerprint == key.Fingerprint)
                .Select(identity => (Key: key, Identity: identity)))
            .ToArray();

        var selectedKeyId = GetOption(args, "--ssh-key");
        var identityPath = GetOption(args, "--ssh-identity");
        if (identityPath is not null)
        {
            var identity = ReadExplicitIdentity(identityPath);
            matches = matches.Where(match => match.Identity.Fingerprint == identity.Fingerprint)
                .Select(match => (match.Key, identity)).ToArray();
        }

        var selected = selectedKeyId is null
            ? SelectLocalSshKey(matches)
            : matches.FirstOrDefault(match => match.Key.Id == selectedKeyId);
        if (selected is null || selected.Value.Key is null)
            throw new CliCommandException(CliExitCodes.ValidationError,
                selectedKeyId is null
                    ? "No registered provider key matches a usable local SSH key or SSH-agent identity. Provide --ssh-key and --ssh-identity, or register a matching public key with the provider account."
                    : $"Provider SSH key '{selectedKeyId}' has no matching local SSH identity. Minicloud will not allocate a VPS without usable user access.");

        var choice = selected.Value;
        var response = await _apiClient.CreateCodingWorkspaceAsync(new CreateCodingWorkspaceRequest(
            organizationId, name, planName, agent, repository, choice.Key.Id), ct);
        if (json)
            WriteAgentJson(new { type = "operation", response.Workspace, response.Operation, response.Resumed });
        else
        {
            _console.WriteLine($"{(response.Resumed ? "Resumed" : "Created")} coding workspace {response.Workspace.Name} ({response.Workspace.Id}).");
            _console.WriteLine($"Operation {response.Operation.Id}: {response.Operation.Status}/{response.Operation.Stage}");
            _console.WriteLine($"SSH identity: {choice.Identity.Label} ({choice.Identity.Fingerprint})");
        }
        var lastStage = response.Operation.Stage;
        var operation = response.Operation;
        while (operation.Status is "queued" or "running")
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            operation = await _apiClient.GetCodingWorkspaceOperationAsync(operation.Id, ct);
            if (operation.Stage != lastStage)
            {
                if (json) WriteAgentJson(new { type = "progress", workspaceId = response.Workspace.Id, operation.Stage });
                else _console.WriteLine($"Provisioning: {operation.Stage}");
                lastStage = operation.Stage;
            }
        }
        if (json)
        {
            var workspace = (await _apiClient.GetCodingWorkspacesAsync(organizationId, ct))
                .Single(item => item.Id == response.Workspace.Id);
            WriteAgentJson(new { type = operation.Status == "completed" ? "completed" : "failed", workspace, operation });
        }
        else
        {
            _console.WriteLine($"Operation {operation.Id}: {operation.Status}/{operation.Stage}");
        }
        if (operation.Status != "completed")
        {
            if (!json && operation.ErrorCode is not null)
                _console.WriteError($"Workspace operation failed ({operation.ErrorCode}): {operation.ErrorMessage}");
            return CliExitCodes.ValidationError;
        }
        return CliExitCodes.Success;
    }

    private async Task<int> ListCodingWorkspacesAsync(string[] args, CancellationToken ct)
    {
        var organizationId = await ResolveAgentsOrganizationAsync(args, ct);
        var workspaces = await _apiClient.GetCodingWorkspacesAsync(organizationId, ct);
        if (IsAgentJsonMode(args))
        {
            WriteAgentJson(workspaces);
            return CliExitCodes.Success;
        }
        foreach (var workspace in workspaces)
            _console.WriteLine($"{workspace.Name}\t{workspace.Id}\t{workspace.Stage}\t{workspace.ProviderStatus}\t{workspace.IpAddress ?? "-"}");
        if (workspaces.Count == 0) _console.WriteLine("No coding workspaces configured.");
        return CliExitCodes.Success;
    }

    private async Task<int> ShowCodingWorkspaceStatusAsync(string[] args, CancellationToken ct)
    {
        var name = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name))
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud agents status <name> [--organization <id>]");
        var workspace = await FindCodingWorkspaceAsync(args, name, ct);
        if (IsAgentJsonMode(args))
        {
            WriteAgentJson(workspace);
            return CliExitCodes.Success;
        }
        _console.WriteLine($"Workspace: {workspace.Name} ({workspace.Id})");
        _console.WriteLine($"Operation stage: {workspace.Stage}; provider: {workspace.ProviderStatus} ({workspace.ProviderObservedAt?.ToString("O") ?? "not observed"})");
        if (workspace.AllocationReconciliationRequired)
            _console.WriteError("Provider allocation has an ambiguous result; the worker is reconciling and will not issue another create request.");
        _console.WriteLine($"Connectivity: {workspace.Connectivity} ({workspace.LastObservedAt?.ToString("O") ?? "not observed"})");
        _console.WriteLine($"Bootstrap: {workspace.BootstrapStatus} ({workspace.BootstrapObservedAt?.ToString("O") ?? "not observed"})");
        _console.WriteLine($"T3: {workspace.T3Health}; installed={workspace.T3Version}; requested={workspace.RequestedT3Version} ({workspace.T3ObservedAt?.ToString("O") ?? "not observed"})");
        _console.WriteLine($"Codex: auth={workspace.CodexAuthentication}; installed={workspace.AgentVersion}; requested={workspace.RequestedAgentVersion} (observed {workspace.CodexAuthenticationObservedAt?.ToString("O") ?? "not observed"})");
        _console.WriteLine($"GitHub: auth={workspace.GitHubAuthentication} (observed {workspace.GitHubAuthenticationObservedAt?.ToString("O") ?? "not observed"}); repository={workspace.RepositoryStatus} ({workspace.RepositoryObservedAt?.ToString("O") ?? "not observed"})");
        if (workspace.LastErrorCode is not null)
            _console.WriteError($"Last error ({workspace.LastErrorCode}): {workspace.LastError}");
        return CliExitCodes.Success;
    }

    private async Task<int> OpenCodingWorkspaceAsync(string[] args, CancellationToken ct)
    {
        if (IsAgentJsonMode(args))
            throw new CliCommandException(CliExitCodes.ValidationError, "JSON output is not available for the interactive tunnel command.");
        var name = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name))
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud agents open <name> [--organization <id>] [--no-browser]");
        var workspace = await FindCodingWorkspaceAsync(args, name, ct);
        if (workspace.IpAddress is null || workspace.Stage is "requested" or "allocating")
            throw new CliCommandException(CliExitCodes.ValidationError, "The workspace VPS is not ready yet. Check `minicloud agents status` and retry.");
        if (workspace.Stage is "failed" or "destroyed" or "destroying")
            throw new CliCommandException(CliExitCodes.ValidationError, $"Workspace stage '{workspace.Stage}' does not permit opening a tunnel.");
        if (!System.Net.IPAddress.TryParse(workspace.IpAddress, out _))
            throw new CliCommandException(CliExitCodes.ValidationError, "The workspace returned an invalid VPS address; refusing to connect.");
        if (workspace.RequestedAgentVersion != RequestedCodexVersion || workspace.RequestedT3Version != RequestedT3Version)
            throw new CliCommandException(CliExitCodes.ValidationError, "The workspace requests tool versions this CLI has not qualified.");

        var identities = GetLocalSshIdentities().Where(identity => identity.Fingerprint == workspace.SshFingerprint).ToArray();
        var explicitIdentity = GetOption(args, "--ssh-identity");
        var identity = explicitIdentity is null
            ? identities.FirstOrDefault()
            : ReadExplicitIdentity(explicitIdentity);
        if (identity is null || identity.Fingerprint != workspace.SshFingerprint)
            throw new CliCommandException(CliExitCodes.ValidationError,
                "No usable local SSH identity matches this workspace. Specify --ssh-identity or load the matching key into ssh-agent.");

        if (await RunRemoteSshCommandAsync(workspace, identity, "true", ct) != 0)
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "SSH host verification or authentication failed.");
        var pairingOutput = await RunRemoteSshCommandCaptureAsync(workspace, identity,
            "export PATH=/home/coding/.local/bin:/usr/local/bin:/usr/bin:/bin; cloud-init status --wait && t3 pair --ttl 5m --label 'Minicloud CLI'",
            ct);
        var pairingLine = pairingOutput.Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, "\\u001b\\[[0-9;]*[A-Za-z]", "").Trim())
            .FirstOrDefault(line => line.StartsWith("Pairing URL:", StringComparison.Ordinal));
        if (pairingLine is null ||
            !Uri.TryCreate(pairingLine["Pairing URL:".Length..].Trim(), UriKind.Absolute, out var pairingUrl))
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                "T3 did not return a valid pairing URL. Check `minicloud agents status` and retry.");

        var knownHosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "minicloud", "known_hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(knownHosts)!);
        Process? tunnel = null;
        var port = 0;
        for (var attempt = 0; attempt < 5 && tunnel is null; attempt++)
        {
            port = FindAvailablePort();
            var startInfo = new ProcessStartInfo("ssh")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-L");
            startInfo.ArgumentList.Add($"127.0.0.1:{port}:127.0.0.1:3773");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ExitOnForwardFailure=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("StrictHostKeyChecking=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add($"UserKnownHostsFile={knownHosts}");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ServerAliveInterval=30");
            if (identity.PrivateKeyPath is not null)
            {
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(identity.PrivateKeyPath);
            }
            startInfo.ArgumentList.Add($"coding@{workspace.IpAddress}");

            var candidate = new Process { StartInfo = startInfo };
            try
            {
                if (!candidate.Start())
                    throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "Could not start the SSH tunnel.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                candidate.Dispose();
                throw new CliCommandException(CliExitCodes.ValidationError, "OpenSSH client was not found. Install ssh and retry.");
            }

            var errorOutput = candidate.StandardError.ReadToEndAsync();
            try
            {
                for (var waitAttempt = 0; waitAttempt < 20; waitAttempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (candidate.HasExited) break;
                    if (await IsPortOpenAsync(port, ct))
                    {
                        tunnel = candidate;
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                }
            }
            catch
            {
                if (!candidate.HasExited)
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync(CancellationToken.None);
                }
                candidate.Dispose();
                throw;
            }

            if (tunnel is null)
            {
                if (!candidate.HasExited)
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync(CancellationToken.None);
                }
                var error = await errorOutput;
                candidate.Dispose();
                if (error.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("Cannot assign requested address", StringComparison.OrdinalIgnoreCase))
                    continue;
                throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                    "SSH tunnel failed to start. Verify the approved host key, local SSH identity, and VPS connectivity.");
            }
        }
        if (tunnel is null)
            throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                "Could not find an available local port for the SSH tunnel.");

        try
        {
            await _apiClient.RecordCodingWorkspaceConnectionAsync(workspace.Id, ct);
            var localPairingUrl = RewritePairingUrlForTunnel(pairingUrl, workspace.IpAddress, port);
            _console.WriteLine("T3 service responded and the tunnel is active. Complete browser pairing to connect; the tunnel stays open until interrupted.");
            if (!args.Contains("--no-browser", StringComparer.Ordinal))
            {
                try { Process.Start(new ProcessStartInfo(localPairingUrl.ToString()) { UseShellExecute = true }); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _console.WriteError($"Could not open a browser automatically. Visit {localPairingUrl} while this command is running.");
                }
            }
            else
            {
                _console.WriteLine($"Pairing URL (expires in 5 minutes): {localPairingUrl}");
            }
            await tunnel.WaitForExitAsync(ct);
            return CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CliExitCodes.Success;
        }
        finally
        {
            if (!tunnel.HasExited)
            {
                tunnel.Kill(entireProcessTree: true);
                await tunnel.WaitForExitAsync(CancellationToken.None);
            }
            tunnel.Dispose();
        }
    }

    private async Task<int> DestroyCodingWorkspaceAsync(string[] args, CancellationToken ct)
    {
        var name = args.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name))
            throw new CliCommandException(CliExitCodes.ValidationError, "Usage: minicloud agents destroy <name> [--organization <id>] [--yes]");
        var workspace = await FindCodingWorkspaceAsync(args, name, ct);
        _console.WriteLine($"This permanently deletes VPS {workspace.ProviderServerId ?? "(not allocated)"} and all workspace data on its server disk.");
        if (!args.Contains("--yes", StringComparer.Ordinal))
        {
            if (IsAgentJsonMode(args))
                throw new CliCommandException(CliExitCodes.ValidationError, "JSON destroy requires --yes.");
            if (Console.IsInputRedirected)
                throw new CliCommandException(CliExitCodes.ValidationError, "Noninteractive destroy requires --yes.");
            _console.Write("Type the workspace name to confirm: ");
            if (!string.Equals(_console.ReadLine(), workspace.Name, StringComparison.Ordinal))
                throw new CliCommandException(CliExitCodes.ValidationError, "Workspace destroy was not confirmed.");
        }
        var operation = await _apiClient.DestroyCodingWorkspaceAsync(workspace.Id, ct);
        if (IsAgentJsonMode(args)) WriteAgentJson(operation);
        else _console.WriteLine($"Destroy operation {operation.Id}: {operation.Status}/{operation.Stage}");
        return CliExitCodes.Success;
    }

    private async Task<int> AuthenticateCodingWorkspaceAsync(string[] args, CancellationToken ct)
    {
        if (IsAgentJsonMode(args))
            throw new CliCommandException(CliExitCodes.ValidationError, "JSON output is not available for interactive provider authorization.");
        var name = args.ElementAtOrDefault(1);
        var provider = GetOption(args, "--provider");
        if (string.IsNullOrWhiteSpace(name) || provider is not ("codex" or "github"))
            throw new CliCommandException(CliExitCodes.ValidationError,
                "Usage: minicloud agents auth <name> --provider codex|github [--organization <id>] [--ssh-identity <private-key-path>]");

        var workspace = await FindCodingWorkspaceAsync(args, name, ct);
        if (workspace.RequestedAgentVersion != RequestedCodexVersion || workspace.RequestedT3Version != RequestedT3Version)
            throw new CliCommandException(CliExitCodes.ValidationError, "The workspace requests tool versions this CLI has not qualified.");
        var identity = GetLocalSshIdentities().FirstOrDefault(candidate => candidate.Fingerprint == workspace.SshFingerprint);
        var identityPath = GetOption(args, "--ssh-identity");
        if (identityPath is not null) identity = ReadExplicitIdentity(identityPath);
        if (identity is null || identity.Fingerprint != workspace.SshFingerprint)
            throw new CliCommandException(CliExitCodes.ValidationError,
                "No usable local SSH identity matches this workspace. Specify --ssh-identity or load the matching key into ssh-agent.");

        var operation = await _apiClient.StartCodingWorkspaceAuthAsync(
            workspace.Id, new StartCodingWorkspaceAuthRequest(provider), ct);
        var versionCheck = $"export PATH=/home/coding/.local/bin:/usr/local/bin:/usr/bin:/bin; " +
            $"test \"$(codex --version | awk '{{print $NF}}')\" = '{workspace.RequestedAgentVersion}' && " +
            $"test \"$(t3 --version | awk '{{print $NF}}')\" = '{workspace.RequestedT3Version}' && cloud-init status --wait";
        var remoteAuth = provider == "codex"
            ? $"{versionCheck} && codex login --device-auth && codex login status && chmod 700 /home/coding/.codex && chmod 600 /home/coding/.codex/auth.json"
            : $"{versionCheck} && BROWSER=echo gh auth login --web --hostname github.com --git-protocol https --scopes repo,read:org && gh auth status --hostname github.com && chmod 700 /home/coding/.config/gh && chmod 600 /home/coding/.config/gh/hosts.yml";
        int authExitCode;
        try
        {
            authExitCode = await RunRemoteSshCommandAsync(workspace, identity, remoteAuth, ct);
            var repositoryReady = false;
            if (authExitCode == 0 && provider == "github")
            {
                var repoExitCode = await RunRemoteSshCommandAsync(workspace, identity, BuildRepositoryPreparationCommand(workspace.Repository), ct);
                repositoryReady = repoExitCode == 0;
            }
            await _apiClient.CompleteCodingWorkspaceAuthAsync(workspace.Id, provider,
                new CompleteCodingWorkspaceAuthRequest(operation.Operation.Id, authExitCode == 0, repositoryReady,
                    authExitCode == 0 ? workspace.RequestedAgentVersion : null,
                    authExitCode == 0 ? workspace.RequestedT3Version : null), ct);
            if (authExitCode != 0)
                throw new CliCommandException(CliExitCodes.ValidationError,
                    $"{provider} authorization did not complete. Retry the browser authorization with `minicloud agents auth {workspace.Name} --provider {provider}`.");
            if (provider == "github" && !repositoryReady)
                throw new CliCommandException(CliExitCodes.ValidationError,
                    "GitHub authorization succeeded, but repository access, write permission, or clone validation failed. Check the workspace status and retry.");
            _console.WriteLine($"{provider} authorization completed for workspace {workspace.Name}.");
            return CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static string BuildRepositoryPreparationCommand(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository) ||
            !System.Text.RegularExpressions.Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            repository.Split('/').Any(part => part is "." or ".."))
            throw new CliCommandException(CliExitCodes.ValidationError, "Workspace repository identity is invalid; refusing to run a remote clone command.");
        var project = repository.Split('/')[1];
        var destination = $"/home/coding/repos/{project}";
        var expectedHttps = $"https://github.com/{repository}.git";
        var expectedSsh = $"git@github.com:{repository}.git";
        return $"set -eu; gh auth setup-git; " +
            $"test \"$(gh api repos/{repository} --jq '.permissions.push')\" = true || {{ echo 'GitHub token needs repository write access.' >&2; exit 41; }}; " +
            $"if [ -d '{destination}/.git' ]; then " +
            $"origin=$(git -C '{destination}' remote get-url origin); " +
            $"case \"$origin\" in '{expectedHttps}'|'{expectedSsh}') ;; *) echo 'Existing clone has a different origin; refusing to reuse it.' >&2; exit 42 ;; esac; " +
            $"elif [ -e '{destination}' ]; then echo 'Repository destination exists but is not a git clone.' >&2; exit 43; " +
            $"else git clone -- '{expectedHttps}' '{destination}'; fi";
    }

    private static bool IsAgentJsonMode(string[] args) =>
        args.Contains("--json", StringComparer.Ordinal);

    private void WriteAgentJson<T>(T value) =>
        _console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private async Task<int> RunRemoteSshCommandAsync(
        CodingWorkspaceResponse workspace, LocalSshIdentity identity, string command, CancellationToken ct)
    {
        var knownHosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "minicloud", "known_hosts");
        Directory.CreateDirectory(Path.GetDirectoryName(knownHosts)!);
        var startInfo = new ProcessStartInfo("ssh") { UseShellExecute = false };
        startInfo.ArgumentList.Add("-tt");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("StrictHostKeyChecking=ask");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add($"UserKnownHostsFile={knownHosts}");
        if (identity.PrivateKeyPath is not null)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(identity.PrivateKeyPath);
        }

        startInfo.ArgumentList.Add($"coding@{workspace.IpAddress}");
        startInfo.ArgumentList.Add(command);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "Could not start the remote SSH authorization command.");
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "OpenSSH client was not found. Install ssh and retry.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private async Task<string> RunRemoteSshCommandCaptureAsync(
        CodingWorkspaceResponse workspace, LocalSshIdentity identity, string command, CancellationToken ct)
    {
        var knownHosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "minicloud", "known_hosts");
        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-tt");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("StrictHostKeyChecking=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add($"UserKnownHostsFile={knownHosts}");
        if (identity.PrivateKeyPath is not null)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(identity.PrivateKeyPath);
        }
        startInfo.ArgumentList.Add($"coding@{workspace.IpAddress}");
        startInfo.ArgumentList.Add(command);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable, "Could not start the T3 pairing command.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await stdout + Environment.NewLine + await stderr;
            if (process.ExitCode != 0)
                throw new CliCommandException(CliExitCodes.NetworkOrApiUnavailable,
                    "T3 pairing command failed. Check the T3 service and workspace status.");
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliCommandException(CliExitCodes.ValidationError, "OpenSSH client was not found. Install ssh and retry.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    internal static Uri RewritePairingUrlForTunnel(Uri pairingUrl, string serverAddress, int localPort)
    {
        var hostMatches = Uri.CheckHostName(serverAddress) != UriHostNameType.Unknown &&
            (string.Equals(pairingUrl.Host, serverAddress, StringComparison.OrdinalIgnoreCase) ||
             System.Net.IPAddress.TryParse(pairingUrl.Host, out var address) && System.Net.IPAddress.IsLoopback(address) ||
             string.Equals(pairingUrl.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (pairingUrl.Scheme != "http" || !hostMatches || localPort is < 1 or > 65535)
            throw new CliCommandException(CliExitCodes.ValidationError, "T3 pairing URL host or scheme is not valid for this workspace.");
        var builder = new UriBuilder(pairingUrl)
        {
            Scheme = "http",
            Host = "127.0.0.1",
            Port = localPort
        };
        return builder.Uri;
    }

    private async Task<string> ResolveAgentsOrganizationAsync(string[] args, CancellationToken ct)
    {
        var me = await _apiClient.GetMeAsync(ct);
        var organizationId = GetOption(args, "--organization");
        if (organizationId is null)
        {
            if (me.Organizations.Count != 1)
                throw new CliCommandException(CliExitCodes.ValidationError, "Select an organization with --organization <id>.");
            organizationId = me.Organizations[0].Id;
        }
        if (!me.Organizations.Any(item => item.Id == organizationId))
            throw new CliCommandException(CliExitCodes.AuthError, "The selected organization is not available to this token.");
        return organizationId;
    }

    private async Task<CodingWorkspaceResponse> FindCodingWorkspaceAsync(string[] args, string name, CancellationToken ct)
    {
        var organizationId = await ResolveAgentsOrganizationAsync(args, ct);
        var workspaces = await _apiClient.GetCodingWorkspacesAsync(organizationId, ct);
        return workspaces.SingleOrDefault(workspace => workspace.Id == name || workspace.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliCommandException(CliExitCodes.ValidationError, $"Coding workspace '{name}' was not found.");
    }

    private (ProviderSshKeyResponse Key, LocalSshIdentity Identity)? SelectLocalSshKey(
        (ProviderSshKeyResponse Key, LocalSshIdentity Identity)[] matches)
    {
        if (matches.Length == 0) return null;
        if (matches.Length == 1) return matches[0];
        if (Console.IsInputRedirected)
            throw new CliCommandException(CliExitCodes.ValidationError, "Multiple local SSH identities match provider keys. Select one with --ssh-key <provider-key-id>.");
        for (var index = 0; index < matches.Length; index++)
            _console.WriteLine($"{index + 1}. {matches[index].Key.Name} [{matches[index].Key.Id}] {matches[index].Identity.Fingerprint}");
        _console.Write("Select an SSH key: ");
        var input = _console.ReadLine();
        if (!int.TryParse(input, out var selected) || selected < 1 || selected > matches.Length)
            throw new CliCommandException(CliExitCodes.ValidationError, "No valid SSH key was selected.");
        return matches[selected - 1];
    }

    private static LocalSshIdentity ReadExplicitIdentity(string path)
    {
        var fullPath = Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path);
        if (!File.Exists(fullPath))
            throw new CliCommandException(CliExitCodes.ValidationError, $"SSH private key '{path}' was not found.");
        var publicPath = fullPath + ".pub";
        if (!File.Exists(publicPath))
            throw new CliCommandException(CliExitCodes.ValidationError,
                $"SSH private key '{path}' has no matching .pub file. Use a matching key in ssh-agent instead.");
        var publicKey = ReadSshPublicKeyFile(publicPath);
        return new LocalSshIdentity(SshPublicKeyFingerprint(publicKey.PublicKey),
            Path.GetFileName(fullPath), fullPath);
    }

    private static LocalSshIdentity[] GetLocalSshIdentities()
    {
        var identities = new List<LocalSshIdentity>();
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        try
        {
            if (Directory.Exists(sshDirectory))
            {
                foreach (var publicPath in Directory.EnumerateFiles(sshDirectory, "*.pub"))
                {
                    try
                    {
                        var key = ReadSshPublicKeyFile(publicPath);
                        var privatePath = publicPath[..^4];
                        if (File.Exists(privatePath))
                            identities.Add(new(SshPublicKeyFingerprint(key.PublicKey),
                                Path.GetFileName(privatePath), privatePath));
                    }
                    catch (CliCommandException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-add", "-L")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                if (process.ExitCode == 0)
                {
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            var key = ParseSshPublicKey(line, "ssh-agent identity");
                            identities.Add(new(SshPublicKeyFingerprint(key.PublicKey),
                                key.Comment ?? "ssh-agent", null));
                        }
                        catch (CliCommandException) { }
                    }
                }
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        return identities
            .GroupBy(identity => identity.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(identity => identity.PrivateKeyPath is not null).First())
            .ToArray();
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
            return true;
        }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
