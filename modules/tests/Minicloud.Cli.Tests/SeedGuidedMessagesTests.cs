// SPDX-License-Identifier: MPL-2.0

using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Seed;

namespace Minicloud.Tests;

public sealed class SeedGuidedMessagesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        RepoRoot(),
        ".local",
        "artifacts",
        "seed-guided-messages-" + Guid.NewGuid().ToString("N"));

    public SeedGuidedMessagesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task GuidedRecoveryExport_AcceptRecordsExportAndSkipsPromptOnRerun()
    {
        var recoveryDir = Path.Combine(_root, "private-recovery");
        var (app, console) = CreateApp("y");
        CliApplication.GuidedRecoveryExport? recorded = null;
        var exportCalls = 0;

        var firstExit = await app.CompleteGuidedRecoveryExportAsync(
            recorded,
            recoveryDir,
            installationId: "inst-1",
            setupTokenRequired: true,
            includeSetupToken =>
            {
                exportCalls++;
                Assert.True(includeSetupToken);
                Directory.CreateDirectory(recoveryDir);
                return Task.FromResult(CliExitCodes.Success);
            },
            (updated, overwrite) =>
            {
                Assert.False(overwrite);
                recorded = updated;
            });

        Assert.Equal(CliExitCodes.Success, firstExit);
        Assert.Equal(1, exportCalls);
        Assert.Equal(new CliApplication.GuidedRecoveryExport("inst-1", SetupTokenExported: true), recorded);
        Assert.Contains("Keep an independent encrypted copy of " + recoveryDir, console.Output);
        Assert.DoesNotContain("Rerun seed to export recovery material", console.Output);

        // Rerun with recorded export skips prompt even when no console input is queued.
        var (rerunApp, rerunConsole) = CreateApp();
        var rerunExit = await rerunApp.CompleteGuidedRecoveryExportAsync(
            recorded,
            recoveryDir,
            installationId: "inst-1",
            setupTokenRequired: true,
            _ =>
            {
                exportCalls++;
                return Task.FromResult(CliExitCodes.Success);
            },
            (_, _) => throw new InvalidOperationException("Should not re-record already exported recovery state."));

        Assert.Equal(CliExitCodes.Success, rerunExit);
        Assert.Equal(1, exportCalls);
        Assert.DoesNotContain("Export private recovery material", rerunConsole.Output);
        Assert.DoesNotContain("Rerun seed to export recovery material", rerunConsole.Output);
        Assert.Contains("Private recovery material is already exported at " + recoveryDir, rerunConsole.Output);

        // If the operator moved private-recovery off disk after recording export, rerun still does not re-prompt.
        Directory.Delete(recoveryDir, recursive: true);
        var (movedApp, movedConsole) = CreateApp();
        var movedExit = await movedApp.CompleteGuidedRecoveryExportAsync(
            recorded,
            recoveryDir,
            installationId: "inst-1",
            setupTokenRequired: true,
            _ => throw new InvalidOperationException("Should not export again after recorded completion."),
            (_, _) => throw new InvalidOperationException("Should not overwrite already exported record."));

        Assert.Equal(CliExitCodes.Success, movedExit);
        Assert.DoesNotContain("Export private recovery material", movedConsole.Output);
        Assert.DoesNotContain("Rerun seed to export recovery material", movedConsole.Output);
        Assert.Contains("Private recovery material was already exported from this guided state.", movedConsole.Output);
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    public async Task GuidedRecoveryExport_DeclineBeforeExportShowsRerunWarning(string answer)
    {
        var recoveryDir = Path.Combine(_root, "private-recovery-" + (answer == "" ? "empty" : answer));
        var (app, console) = CreateApp(answer);
        var exportCalled = false;

        var exit = await app.CompleteGuidedRecoveryExportAsync(
            recorded: null,
            recoveryDir,
            installationId: "inst-2",
            setupTokenRequired: false,
            _ =>
            {
                exportCalled = true;
                return Task.FromResult(CliExitCodes.Success);
            },
            (_, _) => throw new InvalidOperationException("Declined export must not record completion."));

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.False(exportCalled);
        Assert.Contains("Export private recovery material to this workstation now? [y/N]: ", console.Output);
        Assert.Contains("Rerun seed to export recovery material; do not leave your only copy on the server.", console.Output);
    }

    [Fact]
    public async Task GuidedRecoveryExport_DetectsPreexistingCompleteRecoveryDirectoryWithoutMarker()
    {
        var recoveryDir = Path.Combine(_root, "legacy-recovery");
        PopulateCompleteRecoveryDirectory(recoveryDir, "inst-legacy", setupTokenFile: "setup-token-2026");

        Assert.True(CliApplication.IsGuidedRecoveryExported(null, recoveryDir, "inst-legacy", setupTokenRequired: false));
        Assert.True(CliApplication.IsGuidedRecoveryExported(null, recoveryDir, "inst-legacy", setupTokenRequired: true));
        Assert.False(CliApplication.IsGuidedRecoveryExported(null, recoveryDir, "inst-other", setupTokenRequired: false));

        var (app, console) = CreateApp();
        var exit = await app.CompleteGuidedRecoveryExportAsync(
            recorded: null,
            recoveryDir,
            installationId: "inst-legacy",
            setupTokenRequired: true,
            _ => throw new InvalidOperationException("Complete existing export directory should not trigger re-export."),
            (_, _) => throw new InvalidOperationException("Should not write marker when skipping prompt."));

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.DoesNotContain("Export private recovery material", console.Output);
        Assert.DoesNotContain("Rerun seed to export recovery material", console.Output);
        Assert.Contains("Private recovery material is already exported at " + recoveryDir, console.Output);
    }

    [Fact]
    public async Task GuidedRecoveryExport_PromptsOnlyForPendingSetupTokenWhenCoreAlreadyExported()
    {
        var recoveryDir = Path.Combine(_root, "setup-token-only");
        var recorded = new CliApplication.GuidedRecoveryExport("inst-3", SetupTokenExported: false);

        var (declineApp, declineConsole) = CreateApp("n");
        var declineExit = await declineApp.CompleteGuidedRecoveryExportAsync(
            recorded,
            recoveryDir,
            installationId: "inst-3",
            setupTokenRequired: true,
            _ => throw new InvalidOperationException("Declined setup-token export should not execute."),
            (_, _) => throw new InvalidOperationException("Declined setup-token export should not record."));

        Assert.Equal(CliExitCodes.Success, declineExit);
        Assert.Contains("one-time setup token has not been exported yet", declineConsole.Output);
        Assert.Contains("Setup token was not exported.", declineConsole.Output);
        Assert.DoesNotContain("do not leave your only copy on the server", declineConsole.Output);

        var (acceptApp, _) = CreateApp("y");
        CliApplication.GuidedRecoveryExport? updatedRecord = null;
        var overwriteUsed = false;
        var acceptExit = await acceptApp.CompleteGuidedRecoveryExportAsync(
            recorded,
            recoveryDir,
            installationId: "inst-3",
            setupTokenRequired: true,
            includeSetupToken =>
            {
                Assert.True(includeSetupToken);
                return Task.FromResult(CliExitCodes.Success);
            },
            (updated, overwrite) =>
            {
                updatedRecord = updated;
                overwriteUsed = overwrite;
            });

        Assert.Equal(CliExitCodes.Success, acceptExit);
        Assert.True(overwriteUsed);
        Assert.Equal(new CliApplication.GuidedRecoveryExport("inst-3", SetupTokenExported: true), updatedRecord);
    }

    [Fact]
    public void PostCreateMessages_DistinguishStartingServerFromInterruptedDispatchWithoutId()
    {
        var createdWithoutIpv4 = new SeedResource("vultr-srv-123", "minicloud-seed-1", "ewr", "vc2-2c-4gb", "ubuntu-24-04", Address: null);

        var lookupKnown = CliApplication.FormatPostCreateLookupMessage(createdWithoutIpv4);
        Assert.Contains("Server vultr-srv-123 is still starting", lookupKnown);
        Assert.Contains("public IPv4", lookupKnown);
        Assert.Contains("No creation will be retried.", lookupKnown);
        Assert.DoesNotContain("dispatched or interrupted", lookupKnown);
        Assert.DoesNotContain("Reconcile the original operation", lookupKnown);

        var awaitingIpv4 = CliApplication.FormatAwaitingIpv4Message("vultr-srv-123");
        Assert.Contains("Server vultr-srv-123 is still starting", awaitingIpv4);
        Assert.Contains("public IPv4 is not ready yet", awaitingIpv4);
        Assert.Contains("Rerun the same seed command", awaitingIpv4);
        Assert.DoesNotContain("dispatched or interrupted", awaitingIpv4);

        var missingKnown = CliApplication.FormatMissingInventoryMessage(createdWithoutIpv4);
        Assert.Contains("Server vultr-srv-123 is not visible in provider inventory yet", missingKnown);
        Assert.Contains("rerun the same seed command", missingKnown);

        var lookupInterrupted = CliApplication.FormatPostCreateLookupMessage(null);
        Assert.Contains("without a recorded server ID", lookupInterrupted);
        Assert.Contains("interrupted", lookupInterrupted);
        Assert.Contains("no creation will be retried", lookupInterrupted);

        var missingInterrupted = CliApplication.FormatMissingInventoryMessage(null);
        Assert.Contains("No uniquely identified server is visible yet", missingInterrupted);
        Assert.Contains("Reconcile the original operation in your authenticated provider console", missingInterrupted);
    }

    private (CliApplication App, TestConsole Console) CreateApp(params string[] inputs)
    {
        var envDir = Path.Combine(_root, "env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(envDir);
        var console = new TestConsole(inputs);
        var environment = CliEnvironment.ForTests("https://api.example", envDir);
        var tokens = new TokenStore(environment);
        var client = new MinicloudApiClient(environment, tokens);
        return (new CliApplication(console, environment, tokens, client), console);
    }

    private static void PopulateCompleteRecoveryDirectory(string recoveryDir, string installationId, string? setupTokenFile)
    {
        Directory.CreateDirectory(recoveryDir);
        File.WriteAllText(Path.Combine(recoveryDir, "openbao-recovery.json"), "{}");
        File.WriteAllText(Path.Combine(recoveryDir, "openbao-root-token"), "token");
        File.WriteAllText(Path.Combine(recoveryDir, "openbao-ca.key"), "key");
        File.WriteAllText(Path.Combine(recoveryDir, "openbao-ca.crt"), "cert");
        var metadata = setupTokenFile is null
            ? JsonSerializer.Serialize(new { installationId })
            : JsonSerializer.Serialize(new { installationId, setupTokenFile });
        File.WriteAllText(Path.Combine(recoveryDir, "installation.json"), metadata);
        if (setupTokenFile is not null)
        {
            File.WriteAllText(Path.Combine(recoveryDir, setupTokenFile), "one-time-token");
        }
    }

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "README.md")))
            current = current.Parent;
        return current?.FullName ?? Directory.GetCurrentDirectory();
    }

    private sealed class TestConsole(params string[] inputs) : IConsole
    {
        private readonly Queue<string> _inputs = new(inputs);
        private readonly List<string> _writes = [];

        public bool SupportsAnsi => false;
        public int WindowWidth => 80;
        public string Output => string.Join("", _writes);

        public void Write(string message) => _writes.Add(message);
        public void WriteLine(string message = "") => _writes.Add(message + "\n");
        public void WriteError(string message) => _writes.Add(message + "\n");
        public string? ReadLine() => _inputs.Count > 0 ? _inputs.Dequeue() : null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
