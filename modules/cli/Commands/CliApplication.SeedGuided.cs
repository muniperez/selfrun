// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Selfrun.Cli.Seed;

namespace Selfrun.Cli.Commands;

public sealed partial class CliApplication
{
    private sealed record GuidedSession(SeedPortableConfig Config, string KeyPath, bool UseAgent,
        string KeyFingerprint, string? SourceSha256, string? HetznerCredentialScope);
    private sealed record GuidedDispatch(string Image, SeedVerifiedRelease Release, bool EnrollSsh = false);
    internal sealed record GuidedRecoveryExport(string? InstallationId, bool SetupTokenExported);

    private async Task<int> RunGuidedSeedAsync(string? stateDirectory, CancellationToken ct)
    {
        try
        {
            SeedGuard.Require(!_console.IsInputRedirected,
                "Guided seed requires a terminal. For automation, see selfrun help seed.");
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
            var enrollmentPath = Path.Combine(home, "ssh-enrollment.secret");
            var recoveryExportPath = Path.Combine(home, "recovery-exported.json");
            var recoveryDir = Path.Combine(home, "private-recovery");
            var reports = Path.Combine(home, "reports");
            SeedFiles.Directory(reports);
            var reportPath = Path.Combine(reports, "seed-report.json");
            string? credential = null;
            string? verifiedSourceDigest = null;
            var session = ReadGuided<GuidedSession>(sessionPath);
            var dispatch = ReadGuided<GuidedDispatch>(dispatchPath);
            var resource = ReadGuided<SeedResource>(resourcePath);
            var target = ReadGuided<SeedPortableTarget>(targetPath);
            var recoveryExport = ReadGuided<GuidedRecoveryExport>(recoveryExportPath);
            SeedGuard.Require((session is not null || dispatch is null)
                && (dispatch is not null || resource is null && target is null && recoveryExport is null),
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
                string? project = null;
                if (selection.Provider == "hetzner")
                    project = SeedAnswer("Hetzner project identifier you independently verified in the provider console: ");
                if (mode == "local")
                    verifiedSourceDigest = SeedLocalSource.PrepareEmbedded(archive, tag, allowCreate: true);
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
                var key = Path.Combine(home, $"operator-{seedId}_ed25519");
                var keyFingerprint = await SeedOperatorKeys.ManagedIdentityAsync(key, ct);
                session = new(config, key, false, keyFingerprint, verifiedSourceDigest, scope);
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
                verifiedSourceDigest ??= SeedLocalSource.PrepareEmbedded(archive, configForRun.ServerVersion, allowCreate: false);
                SeedGuard.Require(verifiedSourceDigest == saved.SourceSha256,
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
                if (action == "export") args.AddRange(["--recovery-dir", recoveryDir]);
                if (setupToken) args.Add("--setup-token");
                var config = initial ? configForRun with { InitialCreationIntent = SeedPortableConfig.CreationIntent } : configForRun;
                return await RunPortableSeedAsync(args.ToArray(), ct, new(config, saved.KeyFingerprint,
                    initial ? credential : null, localSource, saved.HetznerCredentialScope, dispatch?.Release,
                    initial ? plan =>
                    {
                        var approved = plan.Plan!;
                        var secret = File.Exists(enrollmentPath) ? SeedEnrollment.ReadSecret(enrollmentPath)
                            : SeedEnrollment.CreateSecret(enrollmentPath);
                        try
                        {
                            var userData = SeedEnrollment.UserData(config.SeedId, secret);
                            var intent = new GuidedDispatch(approved.Quote.Image, new(approved.ReleaseDigest, approved.ReleaseSource), EnrollSsh: true);
                            WriteGuided(dispatchPath, intent);
                            dispatch = intent;
                            return userData;
                        }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
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
                    _console.WriteLine(FormatPostCreateLookupMessage(resource));
                    credential ??= SeedFiles.Credential(null, _console);
                    using var provider = new SeedProvider(configForRun.Provider, credential);
                    RequireGuidedCredentialScope(configForRun, provider, saved.HetznerCredentialScope);
                    var account = await provider.GetAccountIdentityAsync(ct);
                    SeedGuard.Require(configForRun.Provider == "hetzner" || account == configForRun.ProviderAccount,
                        "Authorized provider account differs from the original account.");
                    var candidate = await provider.FindServerAsync(configForRun.SeedId, ct);
                    if (candidate is null)
                    {
                        _console.WriteLine(FormatMissingInventoryMessage(resource));
                        return 10;
                    }
                    var expected = configForRun.State();
                    expected.Plan = expected.Plan with { Quote = expected.Plan.Quote with { Image = dispatch.Image } };
                    expected.Server = resource;
                    SeedLifecycle.Match(expected, candidate);
                    if (candidate != resource) WriteGuided(resourcePath, candidate, overwrite: resource is not null);
                    resource = candidate;
                }
                if (resource.Address is null)
                {
                    _console.WriteLine(FormatAwaitingIpv4Message(resource.Id));
                    return 10;
                }
                _console.WriteLine($"Original server ID: {resource.Id}; public IPv4: {resource.Address}.");
                PrintSeedDns(configForRun, resource.Address);
                string? fingerprint;
                if (dispatch.EnrollSsh)
                {
                    _console.WriteLine("Waiting for the original server's first-boot SSH proof.");
                    var secret = SeedEnrollment.ReadSecret(enrollmentPath);
                    try { fingerprint = await SeedEnrollment.VerifyAsync(configForRun.SeedId, resource.Address, secret, home, ct); }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
                    if (fingerprint is null)
                    {
                        _console.WriteLine("No valid SSH enrollment proof received. No authenticated SSH was attempted. Rerun the same seed after first boot; if this persists, inspect cloud-init through the provider console. Do not recreate the server.");
                        return 10;
                    }
                }
                else
                {
                    _console.WriteLine("This older seed was created without automatic enrollment. In its authenticated provider console (not over SSH), run:");
                    _console.WriteLine("  ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub");
                    fingerprint = SeedAnswer("Paste only its independent SHA256:... fingerprint (Enter to pause): ", allowEmpty: true);
                    if (fingerprint.Length == 0) return 10;
                }
                var verifiedTarget = new SeedPortableTarget(resource.Id, resource.Address, fingerprint, resource.Image, resource.PrimaryIpId);
                configForRun = configForRun with { Target = verifiedTarget };
                if (dispatch.EnrollSsh)
                {
                    configForRun.Validate();
                    WriteGuided(targetPath, verifiedTarget);
                    target = verifiedTarget;
                }
            }
            else
            {
                configForRun = configForRun with { Target = target };
                PrintSeedDns(configForRun, target.Ipv4!);
            }
            configForRun.Validate();
            if (dispatch.EnrollSsh && File.Exists(enrollmentPath))
            {
                SeedFiles.Check(enrollmentPath);
                File.Delete(enrollmentPath);
            }

            var continuation = await StepAsync("plan");
            if (continuation != 0) return ExplainGuidedExit(continuation, reportPath);
            if (target is null) WriteGuided(targetPath, configForRun.Target!);
            var observed = ReadGuided<SeedPortableReport>(reportPath)!;
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
            var exportExit = await CompleteGuidedRecoveryExportAsync(
                recoveryExport,
                recoveryDir,
                observed.Observation.InstallationId,
                setup,
                includeSetupToken => StepAsync("export", setupToken: includeSetupToken),
                (updated, overwrite) =>
                {
                    WriteGuided(recoveryExportPath, updated, overwrite: overwrite);
                    recoveryExport = updated;
                });
            if (exportExit != CliExitCodes.Success) return exportExit;
            if (setup || observed.Observation.Browser == "ready")
            {
                _console.WriteLine("After creating your operator account, sign in at https://" + configForRun.Domain + "/, or connect this CLI (use its path if not on PATH):");
                _console.WriteLine($"  selfrun instance add my-cloud https://{configForRun.Domain}");
                _console.WriteLine("  selfrun login");
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

    internal static string FormatPostCreateLookupMessage(SeedResource? resource) =>
        resource is null
            ? "Creation was dispatched without a recorded server ID (the previous run may have been interrupted). Checking provider inventory for the original server; no creation will be retried."
            : $"Server {resource.Id} is still starting; checking provider inventory for its public IPv4. No creation will be retried.";

    internal static string FormatMissingInventoryMessage(SeedResource? resource) =>
        resource is null
            ? "No uniquely identified server is visible yet. Reconcile the original operation in your authenticated provider console or with provider support; inventory absence never authorizes creation."
            : $"Server {resource.Id} is not visible in provider inventory yet. Wait a moment and rerun the same seed command, or reconcile in your authenticated provider console; inventory absence never authorizes creation.";

    internal static string FormatAwaitingIpv4Message(string serverId) =>
        $"Server {serverId} is still starting; public IPv4 is not ready yet. Rerun the same seed command to continue; billing continues.";

    internal static string FormatAlreadyExportedRecoveryMessage(string recoveryDir, bool directoryExists) =>
        directoryExists
            ? $"Private recovery material is already exported at {recoveryDir}. Keep an independent encrypted copy; never share the setup token or recovery secrets."
            : "Private recovery material was already exported from this guided state. Keep your independent encrypted copy; never share the setup token or recovery secrets.";

    internal static string FormatDeclinedRecoveryExportMessage(bool setupTokenOnly, string recoveryDir) =>
        setupTokenOnly
            ? $"Setup token was not exported. Rerun seed to export it to {recoveryDir}; private recovery material was already exported."
            : "Rerun seed to export recovery material; do not leave your only copy on the server.";

    internal async Task<int> CompleteGuidedRecoveryExportAsync(
        GuidedRecoveryExport? recorded,
        string recoveryDir,
        string? installationId,
        bool setupTokenRequired,
        Func<bool, Task<int>> exportAsync,
        Action<GuidedRecoveryExport, bool> recordExport)
    {
        var coreExported = IsGuidedRecoveryExported(recorded, recoveryDir, installationId, setupTokenRequired: false);
        if (coreExported && (!setupTokenRequired || IsGuidedRecoveryExported(recorded, recoveryDir, installationId, setupTokenRequired: true)))
        {
            _console.WriteLine(FormatAlreadyExportedRecoveryMessage(recoveryDir, Directory.Exists(recoveryDir)));
            return CliExitCodes.Success;
        }

        var setupTokenOnly = coreExported && setupTokenRequired;
        var prompt = setupTokenOnly
            ? "Recovery material is already exported, but the one-time setup token has not been exported yet. Export it to this workstation now? [y/N]: "
            : "Export private recovery material to this workstation now? [y/N]: ";
        if (SeedConfirm(prompt, "y"))
        {
            var exported = await exportAsync(setupTokenRequired);
            if (exported != CliExitCodes.Success) return exported;
            var updated = new GuidedRecoveryExport(
                installationId ?? recorded?.InstallationId,
                setupTokenRequired || (recorded?.SetupTokenExported == true));
            recordExport(updated, recorded is not null);
            _console.WriteLine("Keep an independent encrypted copy of " + recoveryDir + ". Never share the setup token or recovery secrets.");
        }
        else
        {
            _console.WriteLine(FormatDeclinedRecoveryExportMessage(setupTokenOnly, recoveryDir));
        }

        return CliExitCodes.Success;
    }

    internal static bool IsGuidedRecoveryExported(
        GuidedRecoveryExport? recorded, string recoveryDir, string? installationId, bool setupTokenRequired)
    {
        if (recorded is not null
            && (installationId is null || recorded.InstallationId == installationId)
            && (!setupTokenRequired || recorded.SetupTokenExported))
        {
            return true;
        }

        return HasCompleteRecoveryExportDirectory(recoveryDir, installationId, setupTokenRequired);
    }

    private static bool HasCompleteRecoveryExportDirectory(string recoveryDir, string? installationId, bool setupTokenRequired)
    {
        if (!Directory.Exists(recoveryDir)) return false;
        try
        {
            if (File.GetAttributes(recoveryDir).HasFlag(FileAttributes.ReparsePoint)) return false;
            string[] requiredFiles =
            [
                "openbao-recovery.json",
                "openbao-root-token",
                "openbao-ca.key",
                "openbao-ca.crt",
                "installation.json"
            ];
            foreach (var name in requiredFiles)
            {
                var path = Path.Combine(recoveryDir, name);
                if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }

            using var metadata = SeedPortable.ReadDocument(File.ReadAllText(Path.Combine(recoveryDir, "installation.json")));
            var root = metadata.RootElement;
            if (installationId is not null
                && (!root.TryGetProperty("installationId", out var id) || id.GetString() != installationId))
            {
                return false;
            }

            if (setupTokenRequired)
            {
                if (!root.TryGetProperty("setupTokenFile", out var tokenFile)) return false;
                var tokenName = tokenFile.GetString();
                if (string.IsNullOrWhiteSpace(tokenName)
                    || tokenName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
                {
                    return false;
                }

                var tokenPath = Path.Combine(recoveryDir, tokenName);
                if (!File.Exists(tokenPath) || File.GetAttributes(tokenPath).HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CliCommandException)
        {
            return false;
        }
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

    private static void WriteGuided<T>(string path, T value, bool overwrite = false)
    {
        var pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        SeedFiles.WriteNew(pending, JsonSerializer.SerializeToUtf8Bytes(value, SeedJournal.Json));
        SeedJournal.PublishDurably(pending, path, overwrite);
    }
}
