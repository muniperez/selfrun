using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceRuntimeEvidenceTests
{
    [Fact]
    public void BootstrapHostIdentityMatchesItsPrivateKeyAndContainsNoToolInstallation()
    {
        var bootstrap = WorkspaceBootstrap.Generate();
        using var key = RSA.Create();
        key.ImportFromPem(bootstrap.HostPrivateKey);
        var blob = Convert.FromBase64String(bootstrap.HostPublicKey.Split(' ')[1]);
        Assert.Equal("SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), bootstrap.HostFingerprint);
        var config = WorkspaceBootstrap.CloudConfig(bootstrap, "ssh-ed25519 AAAAtest management");
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
        Assert.Throws<InvalidOperationException>(() => WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), key));
    }

    [Fact]
    public void BootstrapOverridesVendorPasswordExpiryWithoutEnablingPasswordLogin()
    {
        const string authorizedKey = "ssh-ed25519 AAAAtest management";
        var config = WorkspaceBootstrap.CloudConfig(WorkspaceBootstrap.Generate(), authorizedKey);
        using var document = JsonDocument.Parse(config["#cloud-config\n".Length..]);
        var payload = document.RootElement;
        Assert.False(payload.GetProperty("chpasswd").GetProperty("expire").GetBoolean());
        Assert.False(payload.GetProperty("ssh_pwauth").GetBoolean());
        var root = Assert.Single(payload.GetProperty("users").EnumerateArray());
        Assert.Equal("root", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("lock_passwd").GetBoolean());
        Assert.Equal(authorizedKey, Assert.Single(root.GetProperty("ssh_authorized_keys").EnumerateArray()).GetString());
        var sshPolicy = Assert.Single(payload.GetProperty("write_files").EnumerateArray()).GetProperty("content").GetString()!;
        Assert.Contains("PasswordAuthentication no\n", sshPolicy);
        Assert.Contains("KbdInteractiveAuthentication no\n", sshPolicy);
        Assert.Contains("PermitRootLogin prohibit-password\n", sshPolicy);
        Assert.DoesNotContain("UsePAM no", sshPolicy);
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
