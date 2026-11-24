using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ProviderLifecycleAndReconciliationContractTests
{
    [Fact]
    public async Task LifecycleAdapterAllocatesReconcilesAndDeletesWithOwnershipVerification()
    {
        CreateServerRequest? captured = null;
        ProviderServerInfo? remote = null;
        var client = new SyntheticVpsProviderClient(
            createServer: req =>
            {
                captured = req;
                remote = new ProviderServerInfo("srv-101", req.Name, "203.0.113.42", "running", Labels: req.Labels);
                return Task.FromResult(remote);
            },
            findServer: () => Task.FromResult(remote),
            getStatus: id => Task.FromResult(remote is not null && remote.Id == id
                ? new ProviderServerStatus(id, "running", remote.Ipv4Address, IsRunning: true)
                : new ProviderServerStatus(id, "not_found", null, IsRunning: false)),
            deleteServer: () =>
            {
                remote = null;
                return Task.CompletedTask;
            });

        var adapter = new VpsProviderLifecycleAdapter(
            client,
            BuiltInProviderDescriptors.Hetzner,
            DefaultProviderErrorClassifier.Instance);

        var ownership = new ProviderOwnershipEvidence("acme", "orders", "production", "org_1", "acc_1", "app_1");
        var createRequest = new CreateServerRequest(
            ownership.ManagedServerName,
            "cx23",
            "nbg1",
            ["ssh-key-1"],
            "#cloud-config\n",
            ownership.ToLabels(),
            ownership.ToTags(),
            Image: "ubuntu-24.04");

        var allocation = await adapter.AllocateServerAsync(new ProviderAllocationRequest(
            createRequest,
            IdempotencyKey: "idem-123",
            Ownership: ownership,
            ComputeFamily: ProviderComputeFamily.VirtualServer,
            OrganizationId: "org_1",
            ProviderAccountId: "acc_1"));

        Assert.Equal(ProviderAllocationState.Allocated, allocation.State);
        Assert.Equal("srv-101", allocation.Server.Id);
        Assert.NotNull(captured);
        Assert.Equal("ubuntu-24.04", captured.Image);
        Assert.NotNull(captured.Labels);
        Assert.Equal("minicloud", captured.Labels!["managed-by"]);
        Assert.Equal("acme", captured.Labels!["customer"]);

        // Calling AllocateServerAsync again reconciles the existing owned server without creating a second one.
        var existingReused = await adapter.AllocateServerAsync(new ProviderAllocationRequest(
            createRequest,
            IdempotencyKey: "idem-123",
            Ownership: ownership));
        Assert.Equal("srv-101", existingReused.Server.Id);

        var reconciled = await adapter.ReconcileServerAsync(new ProviderReconciliationQuery(
            ownership.ManagedServerName,
            ownership,
            KnownProviderServerId: "srv-101"));
        Assert.Equal(ProviderReconciliationState.Matched, reconciled.State);
        Assert.Equal("srv-101", reconciled.Server?.Id);

        // Tampered labels must fail closed as OwnershipMismatch on label-verified providers.
        remote = remote! with { Labels = new Dictionary<string, string> { ["managed-by"] = "external" } };
        var mismatch = await adapter.ReconcileServerAsync(new ProviderReconciliationQuery(
            ownership.ManagedServerName,
            ownership));
        Assert.Equal(ProviderReconciliationState.OwnershipMismatch, mismatch.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.AllocateServerAsync(new ProviderAllocationRequest(createRequest, "idem-124", ownership)));

        // Unsupported compute family (e.g., BareMetal on Hetzner) is rejected before dispatch.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.AllocateServerAsync(new ProviderAllocationRequest(createRequest, "idem-125", ownership, ProviderComputeFamily.BareMetal)));

        // Cleanup deletes and reports AlreadyAbsent when server is already gone.
        var deleted = await adapter.DeleteServerWithVerificationAsync("srv-101", ownership);
        Assert.Equal(ProviderCleanupState.Deleted, deleted.State);

        var alreadyAbsent = await adapter.DeleteServerWithVerificationAsync("srv-101", ownership);
        Assert.Equal(ProviderCleanupState.AlreadyAbsent, alreadyAbsent.State);
    }

    [Fact]
    public void LifecycleAdapterClassifiesDefiniteRejectionsAndAmbiguousPartialAllocations()
    {
        var adapter = new VpsProviderLifecycleAdapter(
            new SyntheticVpsProviderClient(),
            BuiltInProviderDescriptors.DigitalOcean,
            DefaultProviderErrorClassifier.Instance);

        var quotaClass = adapter.ClassifyError(
            new ProviderApiException("quota exceeded", HttpStatusCode.UnprocessableEntity, category: ProviderErrorCategory.QuotaExceeded),
            ProviderOperationSafety.NonIdempotentMutation);
        Assert.Equal(ProviderErrorCategory.QuotaExceeded, quotaClass.Category);
        Assert.Equal(ProviderErrorDisposition.DefiniteRejection, quotaClass.Disposition);

        var partialClass = adapter.ClassifyError(
            new ProviderApiException(
                "Droplet created but project assignment failed.",
                HttpStatusCode.Forbidden,
                providerWireType: "digital-ocean",
                category: ProviderErrorCategory.PartialAllocationAmbiguous,
                isAmbiguousOutcome: true),
            ProviderOperationSafety.NonIdempotentMutation);
        Assert.Equal(ProviderErrorCategory.PartialAllocationAmbiguous, partialClass.Category);
        Assert.Equal(ProviderErrorDisposition.AmbiguousOutcome, partialClass.Disposition);

        var transportClass = adapter.ClassifyError(
            new HttpRequestException("Connection reset by peer."),
            ProviderOperationSafety.NonIdempotentMutation);
        Assert.Equal(ProviderErrorCategory.TransientProviderError, transportClass.Category);
        Assert.Equal(ProviderErrorDisposition.AmbiguousOutcome, transportClass.Disposition);
    }

    [Fact]
    public async Task AsyncOperationClientAndBootstrapReadinessContractsTrackLongRunningProvisioning()
    {
        var opClient = new SyntheticAsyncOperationClient(
        [
            new ProviderOperationStatus("op-42", ProviderOperationState.Pending, ProgressPercent: 0),
            new ProviderOperationStatus("op-42", ProviderOperationState.Running, ProgressPercent: 60),
            new ProviderOperationStatus("op-42", ProviderOperationState.Succeeded, ProgressPercent: 100, ResourceId: "bm-99")
        ]);

        var first = await opClient.GetOperationStatusAsync("op-42");
        Assert.False(first.IsTerminal);
        var second = await opClient.GetOperationStatusAsync("op-42");
        Assert.False(second.IsTerminal);
        var third = await opClient.GetOperationStatusAsync("op-42");
        Assert.True(third.IsTerminal);
        Assert.Equal(ProviderOperationState.Succeeded, third.State);
        Assert.Equal("bm-99", third.ResourceId);

        var pendingBootstrap = new ProviderBootstrapReadiness(
            ProviderReportsRunning: true,
            HasPublicAddress: true,
            SshPortReachable: true,
            HostKeyVerified: false,
            CloudInitCompleted: false,
            EstimatedProvisioningLeadTime: TimeSpan.FromMinutes(25));
        Assert.False(pendingBootstrap.IsReadyForDeployment);

        var readyBootstrap = pendingBootstrap with { HostKeyVerified = true, CloudInitCompleted = true };
        Assert.True(readyBootstrap.IsReadyForDeployment);
    }

    [Fact]
    public async Task CredentialRotationUsesScopeClientToRejectCrossAccountRepurposing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options;
        await using var db = new MinicloudDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Organizations.Add(new Organization { Id = "org_scope", Name = "Scope Org", Slug = "scope-org" });
        await db.SaveChangesAsync();

        var secrets = new InMemorySecretStore();
        var ids = new FixedIdGenerator();
        var scopeFactory = new ScopeAwareAccountClientFactory(db, secrets, token => token switch
        {
            "token-scope-a-v1" => new ProviderScopeIdentity("hetzner", "project", "proj-A"),
            "token-scope-a-v2" => new ProviderScopeIdentity("hetzner", "project", "proj-A"),
            "token-scope-b" => new ProviderScopeIdentity("hetzner", "project", "proj-B"),
            _ => throw new ProviderApiException("unauthorized", HttpStatusCode.Unauthorized)
        });

        var service = new ProviderAccountService(
            db, secrets, scopeFactory, ids, TimeProvider.System,
            Options.Create(new OpenBaoSecretStoreOptions { EnvironmentPrefix = "test" }));

        var account = await service.CreateAsync(
            new CreateProviderRequest("org_scope", "Scoped account", "hetzner", "token-scope-a-v1"),
            CancellationToken.None);
        var originalPath = account.CredentialPath;
        var originalRevision = account.Revision;

        // Rotating to a credential from a DIFFERENT cloud project/account must fail and preserve original reference.
        var crossScopeError = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            service.ReplaceCredentialsAsync(account, new ReplaceProviderCredentialRequest("token-scope-b", account.Revision), CancellationToken.None));
        Assert.Equal("provider_scope_unverified", crossScopeError.Code);
        Assert.Equal(originalPath, account.CredentialPath);
        Assert.Equal(originalRevision, account.Revision);

        // Rotating to a new credential in the SAME cloud project/account succeeds.
        await service.ReplaceCredentialsAsync(account, new ReplaceProviderCredentialRequest("token-scope-a-v2", account.Revision), CancellationToken.None);
        Assert.NotEqual(originalPath, account.CredentialPath);
        Assert.Equal(originalRevision + 1, account.Revision);
    }

    private sealed class SyntheticAsyncOperationClient(IEnumerable<ProviderOperationStatus> sequence) : IVpsProviderAsyncOperationClient
    {
        private readonly Queue<ProviderOperationStatus> _queue = new(sequence);
        public Task<ProviderOperationStatus> GetOperationStatusAsync(string operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_queue.Dequeue());
    }

    private sealed class ScopeAwareAccountClientFactory(
        MinicloudDbContext db,
        ISecretStore secrets,
        Func<string, ProviderScopeIdentity> resolveScope)
        : ProviderAccountClientFactory(db, secrets, null!, Options.Create(new HetznerOptions()),
            Options.Create(new DigitalOceanOptions()), Options.Create(new VultrOptions()), NullLoggerFactory.Instance)
    {
        public override IVpsProviderClient CreateCandidate(ProviderAccount account, string credential) =>
            new ScopeAwareClient(resolveScope(credential));
    }

    private sealed class ScopeAwareClient(ProviderScopeIdentity scope) : IVpsProviderClient, IVpsProviderScopeClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default) => Task.FromResult(scope);
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken ct = default) => Task.FromResult<ProviderServerInfo?>(null);
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteServerAsync(string serverId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProviderSshKeyInfo>>([]);
        public Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            var path = OpenBaoSecretStore.ProviderPathFor("test", request.OrganizationId, request.ProviderAccountId!, request.CredentialGeneration!);
            _values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference(OpenBaoSecretStore.ProviderName, path, "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct)
        {
            if (!_values.TryGetValue(reference.Path, out var value))
            {
                throw new SecretStoreUnavailableException("Not found");
            }
            return Task.FromResult(value);
        }

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct)
        {
            _values.Remove(reference.Path);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        private int _counter;
        public string NewId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _counter)}";
    }
}

internal sealed class SyntheticVpsProviderClient(
    Func<CreateServerRequest, Task<ProviderServerInfo>>? createServer = null,
    Func<Task<ProviderServerInfo?>>? findServer = null,
    Func<string, Task<ProviderServerStatus>>? getStatus = null,
    Func<Task>? deleteServer = null,
    CloudProvider provider = CloudProvider.Hetzner) : IVpsProviderClient
{
    public CloudProvider Provider => provider;

    public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken ct = default) =>
        createServer is not null
            ? createServer(request)
            : Task.FromResult(new ProviderServerInfo("synthetic-srv", request.Name, "203.0.113.10", "running", Labels: request.Labels));

    public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken ct = default) =>
        findServer is not null ? findServer() : Task.FromResult<ProviderServerInfo?>(null);

    public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken ct = default) =>
        getStatus is not null
            ? getStatus(serverId)
            : Task.FromResult(new ProviderServerStatus(serverId, "running", "203.0.113.10", IsRunning: true));

    public Task DeleteServerAsync(string serverId, CancellationToken ct = default) =>
        deleteServer is not null ? deleteServer() : Task.CompletedTask;

    public Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderSshKeyInfo>>([]);

    public Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken ct = default) =>
        Task.FromResult(new ProviderSshKeyInfo("ssh-1", name, publicKey));
}
