using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.DigitalOcean;
using Selfrun.Common.Providers.Hetzner;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Providers.Vultr;
using Selfrun.Common.Services;

namespace Selfrun.Tests;

public sealed class DatabaseNetworkLifecycleTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private SelfrunDbContext db = default!;
    private readonly NetworkProvider provider = new();
    private NetworkFactory factory = default!;
    private DatabaseNetworkService service = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<SelfrunDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_fixture", Name = "Fixture", Slug = "fixture" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "provider_fixture", OrganizationId = "org_fixture", Name = "Hetzner", ProviderType = CloudProvider.Hetzner,
            Revision = 2, Enabled = true, CredentialPath = "fixture", CredentialGeneration = "fixture" });
        db.ManagedDatabaseNetworks.Add(new ManagedDatabaseNetwork { Id = "dbnetwork_fixture", OrganizationId = "org_fixture", ProviderAccountId = "provider_fixture", ProviderAccountRevision = 1,
            Name = "fixture", Region = "nbg1", NetworkZone = "eu-central", ProviderNetworkId = "901", CreationAttempted = true, Status = "ready" });
        await db.SaveChangesAsync();
        factory = new(db, provider);
        service = new(db, null!, null!, factory, new IdGenerator(), TimeProvider.System);
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task RenamedAccountRefreshesOnlyAfterExactExistingNetworkProof()
    {
        Assert.True(await service.ReconcileAsync("dbnetwork_fixture", "org_fixture", default));
        Assert.Equal(2L, (await db.ManagedDatabaseNetworks.SingleAsync()).ProviderAccountRevision);
        Assert.Equal(0, provider.CreateCalls);
        Assert.All(provider.ReadIds, id => Assert.Equal("901", id));
    }

    [Fact]
    public async Task DisabledAccountCanDeleteItsVerifiedExistingNetwork()
    {
        (await db.ProviderAccounts.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        Assert.True(await service.DeleteAsync("dbnetwork_fixture", "org_fixture", default));
        Assert.Equal("deleted", (await db.ManagedDatabaseNetworks.SingleAsync()).Status);
        Assert.Equal(1, provider.DeleteCalls);
        Assert.All(factory.AllowDisabledCalls, value => Assert.True(value));
    }

    [Fact]
    public async Task NewCredentialsMissingPinnedResourceDoNotProveDeletion()
    {
        provider.Network = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("dbnetwork_fixture", "org_fixture", default));
        var retained = await db.ManagedDatabaseNetworks.SingleAsync();
        Assert.Equal("ready", retained.Status);
        Assert.Equal(1L, retained.ProviderAccountRevision);
        Assert.Equal("901", retained.ProviderNetworkId);
        Assert.Equal(0, provider.DeleteCalls);
    }

    [Fact]
    public async Task ChangedOwnershipLabelsCannotRefreshTheAccountPin()
    {
        provider.Network = provider.Network! with { Labels = new Dictionary<string, string> { ["selfrun-org"] = "org_other", ["selfrun-network"] = "dbnetwork_fixture" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconcileAsync("dbnetwork_fixture", "org_fixture", default));
        Assert.Equal(1L, (await db.ManagedDatabaseNetworks.SingleAsync()).ProviderAccountRevision);
    }

    [Fact]
    public async Task DisabledAccountCannotAttachAnotherServer()
    {
        db.Apps.Add(new SelfrunApp { Id = "app_fixture", OrganizationId = "org_fixture", Name = "App", Slug = "fixture", Database = DatabaseMode.None });
        db.ServerInstances.Add(new ServerInstance { Id = "server_fixture", OrganizationId = "org_fixture", AppId = "app_fixture", ProviderAccountId = "provider_fixture",
            Provider = "hetzner", ProviderServerId = "123", Region = "nbg1", Status = "active", BootstrapStatus = "ready" });
        (await db.ProviderAccounts.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync("dbnetwork_fixture", "org_fixture", "server_fixture", default));
        Assert.Empty(factory.AllowDisabledCalls);
    }

    [Fact]
    public async Task UnallocatedIntentCannotMoveToChangedAccountGeneration()
    {
        var local = await db.ManagedDatabaseNetworks.SingleAsync();
        local.ProviderNetworkId = null;
        local.CreationAttempted = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconcileAsync(local.Id, local.OrganizationId, default));
        Assert.Equal(0, provider.CreateCalls);
        Assert.Empty(factory.AllowDisabledCalls);
    }

    private sealed class NetworkFactory(SelfrunDbContext db, NetworkProvider provider) : ProviderAccountClientFactory(db, null!, null!,
        Options.Create(new HetznerOptions()), Options.Create(new DigitalOceanOptions()), Options.Create(new VultrOptions()), NullLoggerFactory.Instance)
    {
        public List<bool> AllowDisabledCalls { get; } = [];
        public override Task<IVpsProviderClient> CreateAsync(string org, string id, CancellationToken ct, bool allowDisabled = true)
        {
            AllowDisabledCalls.Add(allowDisabled);
            return Task.FromResult<IVpsProviderClient>(provider);
        }
    }

    private sealed class NetworkProvider : IVpsProviderClient, IPrivateNetworkProvider
    {
        public ProviderPrivateNetwork? Network { get; set; } = new("901", "selfrun-dbnetwork-fixture", "10.72.0.0/16", "eu-central",
            new Dictionary<string, string> { ["selfrun-org"] = "org_fixture", ["selfrun-network"] = "dbnetwork_fixture" }, []);
        public List<string> ReadIds { get; } = [];
        public int CreateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Task<string> GetRegionNetworkZoneAsync(string region, CancellationToken ct) => Task.FromResult("eu-central");
        public Task<ProviderPrivateNetwork?> FindPrivateNetworkAsync(string name, CancellationToken ct) => Task.FromResult(Network);
        public Task<ProviderPrivateNetwork?> GetPrivateNetworkAsync(string id, CancellationToken ct) { ReadIds.Add(id); return Task.FromResult(Network); }
        public Task<ProviderPrivateNetwork> CreatePrivateNetworkAsync(string name, string range, string subnet, string zone, IReadOnlyDictionary<string, string> labels, CancellationToken ct) { CreateCalls++; throw new InvalidOperationException("Allocation forbidden in lifecycle tests."); }
        public Task<ProviderPrivateAddress> AttachPrivateNetworkAsync(string network, string server, CancellationToken ct) => throw new InvalidOperationException();
        public Task DetachPrivateNetworkAsync(string network, string server, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeletePrivateNetworkAsync(string id, CancellationToken ct) { DeleteCalls++; Network = null; return Task.CompletedTask; }
        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<ProviderServerStatus> GetServerStatusAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task DeleteServerAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException();
    }
}
