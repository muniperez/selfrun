using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceRuntimeEvidenceTests
{
    [Theory]
    [InlineData("""{"ok":false,"state":"private bearer","reason":"/private/key","diagnostic":{"stage":"private bearer","failureType":"/private/key","tool":"private bearer","toolAction":"/private/key","exitCode":99999999999,"errno":-1},"password":"private bearer"}""")]
    [InlineData("""{"ok":false,"state":{},"reason":[],"diagnostic":{"stage":false,"failureType":{},"tool":[],"exitCode":"13","errno":{}}}""")]
    [InlineData("""{"ok":false,"diagnostic":"private bearer"}""")]
    [InlineData("null")]
    public void RejectionDiagnosticsNeverRetainUnknownTextOrUnboundedNumericFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rejection = WorkspaceRuntimeRejection.Capture("git-save", document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal("unrecognized_runtime_rejection", rejection.ReasonCode);
        Assert.Equal("Unknown", rejection.State);
        Assert.Equal("unknown", rejection.Stage);
        Assert.Equal("unknown", rejection.FailureType);
        Assert.Null(rejection.Tool);
        Assert.Null(rejection.ToolAction);
        Assert.Null(rejection.ExitCode);
        Assert.Null(rejection.Errno);
        var serialized = JsonSerializer.Serialize(rejection);
        Assert.DoesNotContain("private", serialized);
        Assert.True(serialized.Length < 1024);
    }

    [Fact]
    public void RejectionDiagnosticsSupportLegacyRuntimeAndKnownT3TableWithoutCopyingRuntimeText()
    {
        using var document = JsonDocument.Parse("""
            {"ok":false,"state":"Blocked","reason":"T3 has active/pending work or an unknown status in orchestration_v2_projection_runtime_requests"}
            """);
        var rejection = WorkspaceRuntimeRejection.Capture("git-save", document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal("t3_work_not_idle_runtime_requests", rejection.ReasonCode);
        Assert.Equal("unknown", rejection.Stage);
        Assert.Equal("Blocked", rejection.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"activityAt":null}""")]
    public void RuntimeWithoutRecordedActivityHasNoActivityTimestamp(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(DevelopmentWorkspaceProcessor.ActivityTimestamp(document.RootElement));
    }

    [Fact]
    public void RuntimeActivityPreservesNumericTimestamp()
    {
        using var document = JsonDocument.Parse("""{"activityAt":1730000000.125}""");
        Assert.Equal(1730000000.125, DevelopmentWorkspaceProcessor.ActivityTimestamp(document.RootElement));
    }

    [Theory]
    [InlineData("""{"activityAt":"1730000000"}""")]
    [InlineData("""{"activityAt":false}""")]
    [InlineData("""{"activityAt":{}}""")]
    public void MalformedRuntimeActivityRemainsRejected(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => DevelopmentWorkspaceProcessor.ActivityTimestamp(document.RootElement));
    }

    [Fact]
    public void BootstrapHostIdentityMatchesItsPrivateKeyAndContainsNoToolInstallation()
    {
        var bootstrap = WorkspaceBootstrap.Generate();
        using var key = RSA.Create();
        key.ImportFromPem(bootstrap.HostPrivateKey);
        var blob = Convert.FromBase64String(bootstrap.HostPublicKey.Split(' ')[1]);
        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), bootstrap.HostFingerprint);
        var config = WorkspaceBootstrap.CloudConfig(bootstrap, "ssh-ed25519 AAAAtest management", "minicloud-dev");
        Assert.StartsWith("#cloud-config\n", config);
        using var payload = JsonDocument.Parse(config["#cloud-config\n".Length..]);
        Assert.Equal(bootstrap.HostPrivateKey, payload.RootElement.GetProperty("ssh_keys").GetProperty("rsa_private").GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("ssh_genkeytypes").GetArrayLength());
        Assert.False(payload.RootElement.TryGetProperty("packages", out _));
        Assert.DoesNotContain(bootstrap.EnrollmentToken, config);
    }

    [Theory]
    [InlineData("ssh-ed25519 key\ninjected")]
    [InlineData("")]
    [InlineData("invalid-key")]
    public void BootstrapRejectsUnregisteredOrMultilineManagementKeys(string key)
    {
        Assert.Throws<InvalidOperationException>(() => WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), key, "minicloud-dev"));
    }

    [Fact]
    public void BootstrapOverridesVendorPasswordExpiryWithoutEnablingPasswordLogin()
    {
        const string authorizedKey = "ssh-ed25519 AAAAtest management";
        var config = WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), authorizedKey, "minicloud-dev");
        using var document = JsonDocument.Parse(config["#cloud-config\n".Length..]);
        var payload = document.RootElement;
        Assert.False(payload.GetProperty("chpasswd").GetProperty("expire").GetBoolean());
        Assert.False(payload.GetProperty("ssh_pwauth").GetBoolean());
        var root = Assert.Single(payload.GetProperty("users").EnumerateArray());
        Assert.Equal("root", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("lock_passwd").GetBoolean());
        Assert.Equal(authorizedKey, Assert.Single(root.GetProperty("ssh_authorized_keys").EnumerateArray()).GetString());
        var sshPolicy = Assert.Single(payload.GetProperty("write_files").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == "/etc/ssh/sshd_config.d/90-minicloud-workspace.conf").GetProperty("content").GetString()!;
        Assert.Contains("PasswordAuthentication no\n", sshPolicy);
        Assert.Contains("KbdInteractiveAuthentication no\n", sshPolicy);
        Assert.Contains("PermitRootLogin prohibit-password\n", sshPolicy);
        Assert.DoesNotContain("UsePAM no", sshPolicy);
    }

    [Theory]
    [InlineData("minicloud-dev")]
    [InlineData("Equipe São Paulo")]
    [InlineData("Team \"Debug\"")]
    public void BootstrapPublishesWorkspaceNameAsT3DisplayLabel(string name)
    {
        var config = WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), "ssh-ed25519 AAAAtest management", name);
        using var document = JsonDocument.Parse(config["#cloud-config\n".Length..]);
        var file = Assert.Single(document.RootElement.GetProperty("write_files").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == "/etc/machine-info");
        Assert.Equal("root:root", file.GetProperty("owner").GetString());
        Assert.Equal("0644", file.GetProperty("permissions").GetString());
        Assert.Equal($"PRETTY_HOSTNAME=\"{name}\"\n", file.GetProperty("content").GetString());
        Assert.False(document.RootElement.TryGetProperty("hostname", out _));
    }

    [Fact]
    public void BootstrapRejectsMultilineWorkspaceNames()
    {
        var bootstrap = WorkspaceBootstrap.Generate();
        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceBootstrap.CloudConfig(bootstrap, "ssh-ed25519 AAAAtest management", "Team\nOTHER=value"));
    }

    [Fact]
    public void BootstrapInstallsDeviceAuthLauncherForTheManagedCodexBinary()
    {
        var config = WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), "ssh-ed25519 AAAAtest management", "minicloud-dev");
        using var document = JsonDocument.Parse(config["#cloud-config\n".Length..]);
        var payload = document.RootElement;
        var launcher = Assert.Single(payload.GetProperty("write_files").EnumerateArray(), file =>
            file.GetProperty("path").GetString() == "/opt/minicloud-workspace/codex-device-auth");
        Assert.Equal("root:root", launcher.GetProperty("owner").GetString());
        Assert.Equal("0755", launcher.GetProperty("permissions").GetString());
        Assert.Contains("set -- login --device-auth", launcher.GetProperty("content").GetString());
        Assert.Contains("readlink /usr/local/bin/codex", payload.GetProperty("runcmd")[0][2].GetString());
    }

    [Theory]
    [InlineData("idle", true)]
    [InlineData("completed", true)]
    [InlineData("cancelled", true)]
    [InlineData("running", false)]
    [InlineData("waiting", false)]
    [InlineData("future-status", false)]
    public void T3ReadinessUsesVerifiedThreadStatusContract(string status, bool expected)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, snapshotSequence = 7,
            threads = new[] { new { status, activeRunId = (string?)null, pendingRuntimeRequest = (object?)null } },
            archivedThreads = Array.Empty<object>()
        }));
        Assert.Equal(expected, T3WorkspaceEvidence.IsIdle(document.RootElement));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"snapshotSequence":7,"threads":[],"archivedThreads":[{"status":"running","activeRunId":"run","pendingRuntimeRequest":null}]}""")]
    [InlineData("""{"schemaVersion":1,"snapshotSequence":7,"threads":[{"status":"idle","activeRunId":null,"pendingRuntimeRequest":{"kind":"approval"}}],"archivedThreads":[]}""")]
    [InlineData("""{"schemaVersion":1,"snapshotSequence":7,"threads":[{"status":"idle","activeRunId":null,"pendingRuntimeRequest":null,"pendingBackgroundTasks":[{}]}],"archivedThreads":[]}""")]
    [InlineData("""{"schemaVersion":1,"snapshotSequence":7,"threads":[{"status":"idle","activeRunId":null}],"archivedThreads":[]}""")]
    [InlineData("""{"threads":[],"archivedThreads":[]}""")]
    public void PendingWorkArchivedRunsAndUnknownSnapshotsNeverAuthorizeShutdown(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(T3WorkspaceEvidence.IsIdle(document.RootElement));
    }
}
