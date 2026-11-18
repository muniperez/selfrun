using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Minicloud.Api.Auth;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppDeletionServiceTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, true, false, false, true)]
    public async Task ChecksAllOwnershipBeforeCleanup(bool bound, bool allowLegacy, bool runtimeOnly, bool environmentOnly, bool namedPlan)
    {
        await using var harness = new SqliteTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        var application = new MinicloudApplication { Id = "application", OrganizationId = "org", Name = "Demo", Slug = "demo", NormalizedSlug = "demo" };
        db.Applications.Add(application);
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Test",
            ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner, CredentialPath = "synthetic/path", CredentialGeneration = "test" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
            Name = "Test", NormalizedName = "TEST", Region = "test", Sku = "test" });
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Minicloud.slnx"))) repository = repository.Parent;
        var root = Path.Combine(repository?.FullName ?? throw new InvalidOperationException("Repository root not found."),
            ".local", "artifacts", "deletion-tests", Guid.NewGuid().ToString("N"));
        var artifacts = new DeploymentArtifactStorageService(Options.Create(new DeploymentArtifactStorageOptions { RootPath = root }), TimeProvider.System);
        try
        {
            foreach (var id in new[] { "production", "staging" })
            {
                var accountId = id == "production" || bound ? "account" : null;
                db.Apps.Add(new MinicloudApp { Id = id, ApplicationId = application.Id, EnvironmentName = id, OrganizationId = "org", Name = id, Slug = id, PlanId = namedPlan ? "plan" : null });
                if (!runtimeOnly)
                    db.ServerInstances.Add(new ServerInstance { Id = id, AppId = id, OrganizationId = "org", ProviderAccountId = accountId,
                        Provider = "hetzner", ProviderServerId = id, Region = "test", Status = "ready", BootstrapStatus = "ready" });
                db.RuntimeServers.Add(new RuntimeServer { Id = id, AppId = id, OrganizationId = "org", ProviderAccountId = accountId, Provider = "hetzner", ProviderServerId = id });
                db.RuntimeTokens.Add(new RuntimeToken { Id = id, RuntimeServerId = id, TokenPrefix = id, TokenHash = id });
                db.RegistrySessions.Add(new RegistrySession { Id = id, AppId = id, OrganizationId = "org", SubjectId = "synthetic", CredentialHash = id, Revision = 7 });
                db.Deployments.Add(new Deployment { Id = id, AppId = id, OrganizationId = "org", Status = DeploymentStatus.Queued });
                db.AppServiceSecrets.Add(new AppServiceSecret { Id = id, AppId = id, OrganizationId = "org", ServiceName = "web", Name = "TEST",
                    StoreProvider = "test", StorePath = "synthetic/path", Status = "active" });
                db.AppTelemetrySettings.Add(new AppTelemetrySettings { Id = id, AppId = id, OrganizationId = "org", TenantId = id, Enabled = true });
                db.TelemetryIngestionCredentials.Add(new TelemetryIngestionCredential { Id = id, AppId = id, OrganizationId = "org",
                    ServiceName = "web", Name = "test", KeyPrefix = id, KeyHash = id });
                var artifact = new DeploymentArtifact { Id = id, AppId = id, OrganizationId = "org", ServiceName = "web", FileName = "service.zip",
                    ContentType = "application/zip", ManifestJson = "{}", StoragePath = artifacts.CreateStoragePath("org", id, id) };
                db.DeploymentArtifacts.Add(artifact);
                Directory.CreateDirectory(Path.GetDirectoryName(artifacts.AbsolutePath(artifact))!);
                await File.WriteAllTextAsync(artifacts.AbsolutePath(artifact), "synthetic artifact contents");
            }
            await db.SaveChangesAsync();
            var ids = new IdGenerator();
            var auth = new ControlPlaneAuthorization(db);
            var audit = new AuditEventService(db, ids, TimeProvider.System);
            var store = new Store();
            var service = new AppDeletionService(db, auth, new AppServiceSecretService(db, store, ids, TimeProvider.System),
                new TelemetryService(db, ids, TimeProvider.System, new TelemetryTokenService(), new DeploymentServiceSetService(), auth, audit, new Hosting()),
                audit, artifacts, Options.Create(new ProviderCatalogOptions { AllowLegacyConfiguration = allowLegacy }));
            var principal = new ControlPlanePrincipal("api_key", null, "key", "org", new HashSet<string> { ApiKeyScopes.AppsWrite });
            var result = environmentOnly
                ? await service.RequestEnvironmentDeletionAsync(principal, application.Id, "staging", "staging", default)
                : await service.RequestApplicationDeletionAsync(principal, application.Id, "demo", default);
            var blocked = !bound && (!allowLegacy || namedPlan);
            if (blocked)
            {
                Assert.Equal(409, result?.Status);
                Assert.Equal(AppDeletionErrors.ImportRequired, result?.Code);
                Assert.Contains("staging", result?.Message);
                Assert.Contains("import-legacy", result?.Message);
                Assert.Equal(0, store.Deletes);
                Assert.Empty(await db.AuditEvents.ToListAsync());
                Assert.Empty(await db.TelemetryCleanupJobs.ToListAsync());
            }
            else
            {
                Assert.Null(result);
                Assert.Equal(2, store.Deletes);
            }
            db.ChangeTracker.Clear();
            Assert.All(await db.Apps.ToListAsync(), x => Assert.Equal(blocked ? null : "queued", x.DeletionStatus));
            Assert.Equal(blocked ? null : "queued", (await db.Applications.SingleAsync()).DeletionStatus);
            Assert.All(await db.Deployments.ToListAsync(), x => Assert.Equal(blocked ? DeploymentStatus.Queued : DeploymentStatus.Canceled, x.Status));
            Assert.All(await db.AppServiceSecrets.ToListAsync(), x => Assert.Equal(blocked, x.DeletedAt is null));
            Assert.All(await db.RuntimeTokens.ToListAsync(), x => Assert.Equal(blocked, x.RevokedAt is null));
            Assert.All(await db.RegistrySessions.ToListAsync(), x => { Assert.Equal(blocked, x.RevokedAt is null); Assert.Equal(blocked ? 7 : 8, x.Revision); });
            Assert.All(await db.TelemetryIngestionCredentials.ToListAsync(), x => Assert.Equal(blocked, x.RevokedAt is null));
            Assert.All(await db.AppTelemetrySettings.ToListAsync(), x => { Assert.Equal(blocked, x.Enabled); Assert.Equal(!blocked, x.Deleting); Assert.Equal(blocked, x.DeletedAt is null); });
            foreach (var artifact in await db.DeploymentArtifacts.ToListAsync())
            {
                Assert.Equal(blocked, File.Exists(artifacts.AbsolutePath(artifact)));
                if (blocked) Assert.Equal("synthetic artifact contents", await File.ReadAllTextAsync(artifacts.AbsolutePath(artifact)));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("other", "demo", false, 404)]
    [InlineData("org", "wrong", false, 400)]
    [InlineData("org", "demo", true, 409)]
    [InlineData("org", "demo", false, 0)]
    public async Task EnforcesScopeConfirmationAndActiveDeployment(string organizationId, string confirmation, bool active, int expected)
    {
        await using var harness = new SqliteTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        var app = new MinicloudApp { Id = "app", OrganizationId = "org", Name = "Demo", Slug = "demo" };
        var application = new MinicloudApplication { Id = "application", OrganizationId = "org", Name = "Demo", Slug = "demo", NormalizedSlug = "demo", DefaultEnvironmentId = "app" };
        app.ApplicationId = application.Id;
        app.EnvironmentName = "production";
        db.Applications.Add(application);
        db.Apps.Add(app);
        db.Apps.Add(new MinicloudApp { Id = "branch", ApplicationId = application.Id, EnvironmentName = "staging", OrganizationId = "org", Name = "Branch", Slug = "branch", ParentAppId = "app" });
        db.Deployments.Add(new Deployment { Id = "dep", AppId = "app", OrganizationId = "org", Status = active ? DeploymentStatus.Deploying : DeploymentStatus.Queued });
        await db.SaveChangesAsync();
        var ids = new IdGenerator();
        var auth = new ControlPlaneAuthorization(db);
        var audit = new AuditEventService(db, ids, TimeProvider.System);
        var store = new Store();
        var secrets = new AppServiceSecretService(db, store, ids, TimeProvider.System);
        var telemetry = new TelemetryService(db, ids, TimeProvider.System, new TelemetryTokenService(), new DeploymentServiceSetService(), auth, audit, new Hosting());
        var service = new AppDeletionService(db, auth, secrets, telemetry, audit, new DeploymentArtifactStorageService(Options.Create(new DeploymentArtifactStorageOptions()), TimeProvider.System), Options.Create(new ProviderCatalogOptions()));
        var principal = new ControlPlanePrincipal("api_key", null, "key", organizationId, new HashSet<string> { ApiKeyScopes.AppsWrite, ApiKeyScopes.DeploymentsCreate });
        var result = await service.RequestApplicationDeletionAsync(principal, application.Id, confirmation, default);
        Assert.Equal(expected, result?.Status ?? 0);
        if (expected == 0)
        {
            Assert.All(await db.Apps.ToListAsync(), x => Assert.Equal("queued", x.DeletionStatus));
            Assert.Equal(DeploymentStatus.Canceled, (await db.Deployments.SingleAsync()).Status);
            Assert.False(await auth.CanAccessAppAsync(principal.ToActorIdentity(), "app", OrganizationAction.CreateDeployments, default));
            Assert.Single(await db.AuditEvents.ToListAsync());
            Assert.Null(await service.RequestApplicationDeletionAsync(principal, application.Id, confirmation, default));
            Assert.Single(await db.AuditEvents.ToListAsync());
        }
        else
        {
            Assert.All(await db.Apps.ToListAsync(), x => Assert.Null(x.DeletionStatus));
            Assert.Empty(await db.AuditEvents.ToListAsync());
        }
    }

    private sealed class Store : ISecretStore
    {
        public int Deletes { get; private set; }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) { Deletes++; return Task.CompletedTask; }
    }
    private sealed class Hosting : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
