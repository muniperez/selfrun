using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Databases;
using Minicloud.Worker.Ssh;
using static Minicloud.Worker.Tests.Pipeline.ServerHostKeyEnrollmentTests;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class ServerHostKeyPinSourceTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public ServerHostKeyPinSourceTests(WorkerSqliteTestHarness harness) => _harness = harness;

    [Fact]
    public async Task RecycledAddressNeverUsesAnotherAppsEnrollment()
    {
        var first = await SeedAsync("old", "192.0.2.10", "active", "SHA256:first");
        var second = await SeedAsync("new", "192.0.2.10", "active", "SHA256:second");
        var source = new ServerHostKeyPinSource(_harness.CreateScopeFactory());
        await RefusesAsync(first);
        await RefusesAsync(second);

        await using var db = _harness.CreateDbContext();
        var old = await db.ServerInstances.SingleAsync(s => s.Id == first.Id);
        old.Status = "provisioning";
        await db.SaveChangesAsync();
        await RefusesAsync(first);
        Assert.Equal(second.HostKeyFingerprint, await source.GetPinnedFingerprintAsync(
            new SshTarget(second.Id, second.IpAddress!), default));

        var current = await db.ServerInstances.SingleAsync(s => s.Id == second.Id);
        current.Status = "provisioning";
        old.Status = "active";
        await db.SaveChangesAsync();
        await RefusesAsync(second);
        Assert.Equal(first.HostKeyFingerprint, await source.GetPinnedFingerprintAsync(
            new SshTarget(first.Id, first.IpAddress!), default));

        async Task RefusesAsync(ServerInstance selected)
        {
            var error = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => source.GetPinnedFingerprintAsync(
                new SshTarget(selected.Id, selected.IpAddress!), default));
            Assert.Equal("ssh_host_key_untrusted", error.FailureCode);
        }
    }

    [Theory]
    [InlineData("active", null)]
    [InlineData("active", "192.0.2.11")]
    [InlineData("provisioning", "192.0.2.12")]
    public async Task InvalidRegistrationCannotFallBackToOperatorPins(string status, string? registeredIp)
    {
        var server = await SeedAsync(Guid.NewGuid().ToString("N"), registeredIp, status, null);
        var directory = CreateFixtureDirectory("registration-");
        try
        {
            var file = Path.Combine(directory.FullName, "host-pins");
            await File.WriteAllTextAsync(file, "192.0.2.12 SHA256:operator\n");
            var client = new SshDeploymentClient(new SshDeploymentOptions { HostKeyPinsFile = file },
                pinSource: new ServerHostKeyPinSource(_harness.CreateScopeFactory()));
            var error = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
                new SshTarget(server.Id, "192.0.2.12"), "unused", "unused", "true", (_, _) => Task.CompletedTask, default));
            Assert.Equal("ssh_host_key_untrusted", error.FailureCode);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MissingRegistrationAndChangedEnrollmentFailClosed()
    {
        var server = await SeedAsync("changed", "192.0.2.13", "active", "SHA256:current");
        var source = new ServerHostKeyPinSource(_harness.CreateScopeFactory());
        foreach (var target in new[] {
            new SshTarget("missing", "192.0.2.13"),
            new SshTarget(server.Id, "192.0.2.13", "SHA256:previous")
        })
        {
            var error = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => source.GetPinnedFingerprintAsync(target, default));
            Assert.Equal("ssh_host_key_untrusted", error.FailureCode);
        }
    }

    [SshdFact]
    public async Task SelectedRecordPinIsEnforcedOnCommandAndSftpConnections()
    {
        await using var sshd = await LocalSshd.StartAsync();
        await sshd.WriteAsync("banner", "");
        await sshd.ListenAsync();
        var selected = await SeedAsync("selected", "127.0.0.1", "active", sshd.Fingerprint);
        var stale = await SeedAsync("stale", "127.0.0.1", "provisioning", "SHA256:stale");
        var pins = await sshd.WriteAsync("host-pins", $"127.0.0.1 {sshd.Fingerprint}\n");
        var client = new SshDeploymentClient(new SshDeploymentOptions { Port = sshd.Port, HostKeyPinsFile = pins },
            pinSource: new ServerHostKeyPinSource(_harness.CreateScopeFactory()));
        var target = new SshTarget(selected.Id, "127.0.0.1", selected.HostKeyFingerprint);
        Assert.Equal(0, await client.ExecuteCommandAsync(target, Environment.UserName, sshd.ClientPrivateKey,
            "true", (_, _) => Task.CompletedTask, default));
        var inactive = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
            new SshTarget(stale.Id, "127.0.0.1"), Environment.UserName, sshd.ClientPrivateKey, "true",
            (_, _) => Task.CompletedTask, default));
        Assert.Equal("ssh_host_key_untrusted", inactive.FailureCode);

        await using (var db = _harness.CreateDbContext())
        {
            var record = await db.ServerInstances.SingleAsync(s => s.Id == selected.Id);
            record.HostKeyFingerprint = "SHA256:different";
            await db.SaveChangesAsync();
        }
        var mismatchTarget = target with { ExpectedFingerprint = null };
        var mismatch = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteCommandAsync(
            mismatchTarget, Environment.UserName, sshd.ClientPrivateKey, "true", (_, _) => Task.CompletedTask, default));
        Assert.Equal("ssh_host_key_mismatch", mismatch.FailureCode);
        var opened = false;
        var transfer = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => client.ExecuteDeploymentAsync(
            new SshDeploymentRequest(mismatchTarget, Environment.UserName, sshd.ClientPrivateKey, "/unused",
                "{}", "#!/bin/sh\nexit 0", [new SshDeploymentArtifactUpload("fixture", "fixture.zip", _ =>
                {
                    opened = true;
                    return Task.FromResult<Stream>(new MemoryStream([1]));
                })], TimeSpan.FromSeconds(10)), (_, _) => Task.CompletedTask, default));
        Assert.Equal("ssh_host_key_mismatch", transfer.FailureCode);
        Assert.False(opened);
    }

    [Theory]
    [InlineData("192.0.2.20", true)]
    [InlineData("192.0.2.21", true)]
    [InlineData(null, true)]
    [InlineData("192.0.2.20", false)]
    public async Task DatabaseTargetVerifiesSelectedProviderIdentityBeforeSsh(string? providerIp, bool running)
    {
        var provider = new StatusProvider(new ProviderServerStatus("provider-server", "running", providerIp, IsRunning: running));
        var factory = new StatusFactory(provider);
        var server = new ServerInstance { Id = "selected", OrganizationId = "synthetic-org", ProviderAccountId = "selected-account",
            Provider = "hetzner", ProviderServerId = "provider-server", Status = "active",
            IpAddress = "192.0.2.20", HostKeyFingerprint = "SHA256:selected" };
        var processor = new DatabaseOperationProcessor(null!, null!, null!, null!, null!, null!,
            new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()),
            Options.Create(new DatabasePlatformOptions()), null!, providerClients: factory);
        if (running && providerIp == server.IpAddress)
        {
            Assert.Equal(new SshTarget(server.Id, server.IpAddress, server.HostKeyFingerprint),
                await processor.ResolveSshTargetAsync(server, default));
        }
        else
        {
            var error = await Assert.ThrowsAsync<SshHostKeyTrustException>(() => processor.ResolveSshTargetAsync(server, default));
            Assert.Equal("ssh_host_key_untrusted", error.FailureCode);
        }
        Assert.Equal(("synthetic-org", "selected-account"), factory.SelectedAccount);
        Assert.Equal("provider-server", provider.SelectedServer);
    }

    private async Task<ServerInstance> SeedAsync(string name, string? ip, string status, string? fingerprint)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pin-" + name);
        var server = new ServerInstance { Id = "server-" + name, OrganizationId = org.Id, AppId = app.Id,
            Provider = "hetzner", ProviderServerId = "provider-" + name, Region = "synthetic", IpAddress = ip, Status = status,
            BootstrapStatus = status == "active" ? "ready" : "pending",
            HostKeyFingerprint = fingerprint, CreatedAt = DateTimeOffset.UtcNow };
        await using var db = _harness.CreateDbContext();
        db.ServerInstances.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    internal sealed class StatusFactory(IVpsProviderClient provider) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public (string, string)? SelectedAccount { get; private set; }
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            SelectedAccount = (organizationId, providerAccountId);
            return Task.FromResult(provider);
        }
    }

    internal sealed class StatusProvider(ProviderServerStatus status) : IVpsProviderClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public string? SelectedServer { get; private set; }
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
        {
            SelectedServer = serverId;
            return Task.FromResult(status);
        }
        public Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No discovery in this fixture.");
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No provisioning in this fixture.");
        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No deletion in this fixture.");
    }
}
