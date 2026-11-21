// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Minicloud.Cli.Seed;

namespace Minicloud.Cli.Commands;

public sealed partial class CliApplication
{
    private sealed record GuidedSession(SeedPortableConfig Config, string KeyPath, bool UseAgent,
        string KeyFingerprint, string? SourceSha256, string? HetznerCredentialScope);
    private sealed record GuidedDispatch(string Image, SeedVerifiedRelease Release);

    private async Task<int> RunGuidedSeedAsync(string? stateDirectory, CancellationToken ct)
    {
        try
        {
            SeedGuard.Require(!_console.IsInputRedirected,
                "Guided seed requires a terminal. For automation, see minicloud help seed.");
            var home = Path.GetFullPath(stateDirectory ?? Path.Combine(_environment.ConfigHome, "seed-guided"));
            SeedFiles.Directory(home);
            _console.WriteLine("Protected seed state: " + home + ". Preserve it and the original SSH key to resume.");
            var lockPath = Path.Combine(home, "guided.lock");
            if (!File.Exists(lockPath))
                using (SeedFiles.Create(lockPath)) { }
            SeedFiles.Check(lockPath);
            using var seedLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sessionPath = Path.Combine(home, "session.json");
            var archive = Path.Combine(home, "source.tar.gz");
            var dispatchPath = Path.Combine(home, "create-dispatched.json");
            var resourcePath = Path.Combine(home, "resource.json");
            var targetPath = Path.Combine(home, "target.json");
            var reports = Path.Combine(home, "reports");
            SeedFiles.Directory(reports);
            var reportPath = Path.Combine(reports, "seed-report.json");
            string? credential = null;
            var session = ReadGuided<GuidedSession>(sessionPath);
            var dispatch = ReadGuided<GuidedDispatch>(dispatchPath);
            var resource = ReadGuided<SeedResource>(resourcePath);
            var target = ReadGuided<SeedPortableTarget>(targetPath);
            SeedGuard.Require((session is not null || dispatch is null)
                && (dispatch is not null || resource is null && target is null),
                "Protected seed state is incomplete. Reconcile the original operation; do not start a replacement.");
            if (session is null)
            {
                var mode = SeedAnswer("Server source: type 'signed' for an exact published release or 'local' for embedded TEST source: ");
                SeedGuard.Require(mode is "signed" or "local", "Choose signed or local explicitly.");
                var tag = mode == "signed" ? SeedAnswer("Exact published compatible server tag (server-v...): ") : SeedLocalSource.EmbeddedTag;
                SeedGuard.Require(tag is not null, "This executable does not carry TEST source. Use a matching candidate or the documented source publish command. No trust fallback.");
                var selection = new SeedSelection(
                    SeedAnswer("Provider (hetzner, digitalocean, vultr): "),
                    SeedAnswer("Region ID from your provider's catalog: "),
                    SeedAnswer("Server size ID from your provider's catalog: "),
                    SeedAnswer("Dashboard hostname (the API uses api.<dashboard hostname>): "),
                    SeedAnswer("Separate application DNS suffix: "), tag);
                selection.Validate();
                var architecture = SeedAnswer("Server architecture (linux/amd64 or linux/arm64): ");
                SeedGuard.Require(architecture is "linux/amd64" or "linux/arm64", "Select the server architecture explicitly.");
                var key = Path.GetFullPath(SeedAnswer("Absolute path to your owner-only OpenSSH private key: "));
                var useAgent = SeedConfirm("Use your independently unlocked SSH agent for this key? [y/N]: ", "y");
                var keyFingerprint = SeedUserKey.Fingerprint(await SeedUserKey.ReadAsync(key, useAgent, ct));
                string? project = null;
                if (selection.Provider == "hetzner")
                    project = SeedAnswer("Hetzner project identifier you independently verified in the provider console: ");
                var digest = mode == "local" ? SeedLocalSource.PrepareEmbedded(archive, tag) : null;
                credential = SeedFiles.Credential(null, _console);
                var seedId = Guid.NewGuid().ToString("N");
                using var provider = new SeedProvider(selection.Provider, credential);
                var scope = selection.Provider == "hetzner" ? provider.CredentialScope(seedId) : null;
                var account = await provider.GetAccountIdentityAsync(ct);
                var config = new SeedPortableConfig
                {
                    SchemaVersion = 1, SeedId = seedId, Provider = selection.Provider,
                    ProviderAccount = project is not null ? "hetzner-project:" + project : account,
                    Region = selection.Region, Size = selection.Size, Domain = selection.Domain, AppDomain = selection.AppDomain,
                    ServerVersion = tag, Architecture = architecture
                };
                config.Validate();
                session = new(config, key, useAgent, keyFingerprint, digest, scope);
                SeedGuard.Require(!JsonSerializer.Serialize(session, SeedJournal.Json).Contains(credential, StringComparison.Ordinal),
                    "A configuration value contains the supplied credential. Never enter a credential in another prompt.");
                WriteGuided(sessionPath, session);
            }

            var saved = session;
            var configForRun = saved.Config;
            configForRun.Validate();
            SeedGuard.Require(configForRun is { ProviderCredential: null, Target: null, InitialCreationIntent: null },
                "Unexpected data in guided state. Preserve the original protected inputs.");
            var localSource = saved.SourceSha256 is not null;
            if (localSource)
            {
                SeedGuard.Require(File.Exists(archive), "Original source archive is missing. Restore the protected archive; never substitute different bytes.");
                SeedGuard.Require(SeedLocalSource.PrepareEmbedded(archive, configForRun.ServerVersion) == saved.SourceSha256,
                    "Original embedded source archive hash changed. Restore the approved bytes.");
                _console.WriteLine($"TEST source {configForRun.ServerVersion}, SHA256 {saved.SourceSha256}.");
                _console.WriteLine("This explicitly selected source is not a signed release. Review its provenance; the VPS will build it natively.");
            }
            else _console.WriteLine($"Signed release selected: {configForRun.ServerVersion}. Missing or incompatible assets never fall back to source.");
            _console.WriteLine($"Seed ID: {configForRun.SeedId}; provider scope: {configForRun.ProviderAccount}.");

            async Task<int> StepAsync(string action, bool initial = false, string? approval = null, bool setupToken = false)
            {
                var args = new List<string> { action, "--config", sessionPath, "--report", reportPath, "--ssh-key", saved.KeyPath };
                if (saved.UseAgent) args.Add("--use-ssh-agent");
                if (initial) args.Add("--new-seed");
                if (localSource && action is "plan" or "apply")
                    args.AddRange(["--local-source", archive, "--source-sha256", saved.SourceSha256!]);
                if (approval is not null) args.AddRange(["--approve", approval]);
                if (action == "export") args.AddRange(["--recovery-dir", Path.Combine(home, "private-recovery")]);
                if (setupToken) args.Add("--setup-token");
                var config = initial ? configForRun with { InitialCreationIntent = SeedPortableConfig.CreationIntent } : configForRun;
                return await RunPortableSeedAsync(args.ToArray(), ct, new(config, saved.KeyFingerprint,
                    initial ? credential : null, localSource, saved.HetznerCredentialScope, dispatch?.Release,
                    initial ? plan =>
                    {
                        var approved = plan.Plan!;
                        dispatch = new(approved.Quote.Image, new(approved.ReleaseDigest, approved.ReleaseSource));
                        WriteGuided(dispatchPath, dispatch);
                    } : null,
                    initial ? created =>
                    {
                        WriteGuided(resourcePath, created);
                        resource = created;
                    } : null));
            }

            if (dispatch is null)
            {
                credential ??= SeedFiles.Credential(null, _console);
                var planned = await StepAsync("plan", initial: true);
                if (planned != 0) return ExplainGuidedExit(planned, reportPath);
                var plan = ReadGuided<SeedPortableReport>(reportPath)!;
                SeedGuard.Require(plan.Classification == "new_seed" && plan.Approval is not null,
                    "This seed did not resolve to a new authorized target. Do not create a replacement.");
                _console.WriteLine("Review the exact plan above. The VPS remains billable during setup, pauses and interruptions.");
                if (!SeedConfirm("Only if you are the sole authorized creator with no unresolved prior request, type 'create' to approve this plan (Enter to pause): ", "create"))
                    return 10;
                var applied = await StepAsync("apply", initial: true, approval: plan.Approval);
                if (applied != 10 || dispatch is null) return ExplainGuidedExit(applied, reportPath);
            }

            if (target is null)
            {
                // Once dispatch is durable, inventory can recover the original target but can never authorize another create.
                if (resource?.Address is null)
                {
                    _console.WriteLine("Creation was dispatched or interrupted. Reconcile the original operation in your authenticated provider console. No creation will be retried.");
                    credential ??= SeedFiles.Credential(null, _console);
                    using var provider = new SeedProvider(configForRun.Provider, credential);
                    RequireGuidedCredentialScope(configForRun, provider, saved.HetznerCredentialScope);
                    var account = await provider.GetAccountIdentityAsync(ct);
                    SeedGuard.Require(configForRun.Provider == "hetzner" || account == configForRun.ProviderAccount,
                        "Authorized provider account differs from the original account.");
                    var candidate = await provider.FindServerAsync(configForRun.SeedId, ct);
                    if (candidate?.Address is null)
                    {
                        _console.WriteLine("No uniquely identified server with a public IPv4 is visible yet. Reconcile with provider support; inventory absence never authorizes creation.");
                        return 10;
                    }
                    var expected = configForRun.State();
                    expected.Plan = expected.Plan with { Quote = expected.Plan.Quote with { Image = dispatch.Image } };
                    expected.Server = resource;
                    SeedLifecycle.Match(expected, candidate);
                    resource = candidate;
                }
                _console.WriteLine($"Original server ID: {resource.Id}; public IPv4: {resource.Address}.");
                PrintSeedDns(configForRun, resource.Address);
                _console.WriteLine("Confirm that server in the authenticated provider console. In its console (not over SSH), run:");
                _console.WriteLine("  ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub");
                var fingerprint = SeedAnswer("Paste only its independent SHA256:... fingerprint (Enter to pause): ", allowEmpty: true);
                if (fingerprint.Length == 0) return 10;
                configForRun = configForRun with { Target = new(resource.Id, resource.Address, fingerprint, resource.Image, resource.PrimaryIpId) };
            }
            else configForRun = configForRun with { Target = target };
            configForRun.Validate();

            var continuation = await StepAsync("plan");
            if (continuation != 0) return ExplainGuidedExit(continuation, reportPath);
            if (target is null) WriteGuided(targetPath, configForRun.Target!);
            var observed = ReadGuided<SeedPortableReport>(reportPath)!;
            if (target is not null) PrintSeedDns(configForRun, target.Ipv4!);
            if (observed.Classification == "continue_partial")
            {
                if (!SeedConfirm("Review the pinned-target plan above. Type 'install' to approve it (Enter to pause): ", "install"))
                    return 10;
                continuation = await StepAsync("apply", approval: observed.Approval);
                if (continuation is not (0 or 10)) return ExplainGuidedExit(continuation, reportPath);
                observed = ReadGuided<SeedPortableReport>(reportPath)!;
            }
            if (observed.Classification is "upgrade_required" or "reconciliation_required")
            {
                _console.WriteError(string.Join(" ", observed.NextActions));
                return SeedPortable.ExitCode(observed);
            }
            if (observed.Observation.Health != "ready")
            {
                _console.WriteLine("Public HTTPS/readiness is not verified. Finish DNS and rerun the same seed command. The original host remains billable.");
                return SeedPortable.ExitCode(observed);
            }

            var setup = observed.Observation.Browser == "pending";
            if (setup) _console.WriteLine($"Open https://{configForRun.Domain}/setup and enter the one-time token from the protected export.");
            if (SeedConfirm("Export private recovery material to this workstation now? [y/N]: ", "y"))
            {
                var exported = await StepAsync("export", setupToken: setup);
                if (exported != 0) return exported;
                _console.WriteLine("Keep an independent encrypted copy of " + Path.Combine(home, "private-recovery") + ". Never share the setup token or recovery secrets.");
            }
            else _console.WriteLine("Rerun seed to export recovery material; do not leave your only copy on the server.");
            if (setup || observed.Observation.Browser == "ready")
            {
                _console.WriteLine("After creating your operator account, sign in at https://" + configForRun.Domain + "/, or connect this CLI (use its path if not on PATH):");
                _console.WriteLine($"  minicloud instance add my-cloud https://{configForRun.Domain}");
                _console.WriteLine("  minicloud login");
            }
            else _console.WriteLine("Browser setup state is not verified. Check the original host and rerun seed; never reinitialize it.");
            return SeedPortable.ExitCode(observed);
        }
        catch (CliCommandException ex)
        {
            _console.WriteError(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _console.WriteError("Seed interrupted. Rerun with the same protected state. Never retry an uncertain create.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {
            _console.WriteError("Could not read, lock or write protected seed state or input. Preserve the state and reconcile any dispatched creation; no diagnostics containing inputs were logged.");
        }
        return CliExitCodes.ValidationError;
    }

    private static void RequireGuidedCredentialScope(SeedPortableConfig config, SeedProvider provider, string? expectedScope)
    {
        if (config.Provider != "hetzner") return;
        SeedGuard.Require(expectedScope is not null,
            "This guided state has no original Hetzner credential binding. Preserve it and reconcile with the provider; never retry an uncertain create.");
        SeedGuard.Require(provider.CredentialScope(config.SeedId) == expectedScope,
            "Hetzner credential differs from the original guided seed. Use the original token; no request was sent to Hetzner.");
    }

    private string SeedAnswer(string prompt, bool allowEmpty = false)
    {
        _console.Write(prompt);
        var answer = _console.ReadLine()?.Trim();
        SeedGuard.Require(answer is not null && (allowEmpty || answer.Length > 0),
            "Input was not supplied. Rerun the same seed command to continue from saved state.");
        return answer;
    }

    private bool SeedConfirm(string prompt, string affirmative)
    {
        var answer = SeedAnswer(prompt, allowEmpty: true);
        SeedGuard.Require(answer == affirmative || answer is "" or "n", "No approval recorded; use the exact confirmation shown or Enter to pause.");
        return answer == affirmative;
    }

    private void PrintSeedDns(SeedPortableConfig config, string address)
    {
        _console.WriteLine($"DNS: set {config.Domain}. A {address} and api.{config.Domain}. A {address}.");
        _console.WriteLine("Remove conflicting A/AAAA records. Do not point the application wildcard at this control-plane VPS.");
    }

    private int ExplainGuidedExit(int code, string reportPath)
    {
        if (code is 10 or 11 or 12 && ReadGuided<SeedPortableReport>(reportPath) is { } report)
            _console.WriteLine(string.Join(" ", report.NextActions));
        return code;
    }

    private static T? ReadGuided<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        SeedFiles.Check(path);
        SeedGuard.Require(new FileInfo(path).Length is > 0 and < 1048576, "Invalid guided seed state size.");
        using var document = SeedPortable.ReadDocument(File.ReadAllText(path));
        return document.RootElement.Deserialize<T>(SeedJournal.Json) ?? throw new JsonException();
    }

    private static void WriteGuided<T>(string path, T value)
    {
        var pending = path + ".pending";
        SeedFiles.WriteNew(pending, JsonSerializer.SerializeToUtf8Bytes(value, SeedJournal.Json));
        SeedJournal.PublishDurably(pending, path, overwrite: false);
    }
}
