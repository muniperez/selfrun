extern alias WorkerAssembly;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Services;
using WorkerAssembly::Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Tests;

public sealed class DevelopmentWorkspaceApiTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private WorkspaceApiFactory factory = default!;
    private string token = default!;
    private string otherToken = default!;
    private const string Root = "/api/development-workspaces";
    private static CreateDevelopmentWorkspaceRequest Request() => new("org", "API fixture", "image", "standard", "plan", 20, "create");

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new WorkspaceApiFactory(connection);
        factory.CreateClient().Dispose();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.AddRange(new Organization { Id = "org", Name = "Org", Slug = "org" },
            new Organization { Id = "other", Name = "Other", Slug = "other" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "sensitive/provider", CredentialGeneration = "synthetic" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
            Name = "Standard", NormalizedName = "standard", Region = "nbg1", Sku = "cx-test" });
        var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        token = AddKey(db, tokens, "org", ApiKeyScopes.All);
        otherToken = AddKey(db, tokens, "other", ApiKeyScopes.All);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SpoofedActorAndOrganizationHeadersNeverAuthorizePublicRequests()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Organization-Id", "org");
        client.DefaultRequestHeaders.Add("X-Actor-Id", "admin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Root}?organizationId=org")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Root, Request())).StatusCode);
        await AssertNoCompute();
    }

    [Fact]
    public async Task OtherOrganizationCannotReadOrMutateAnyWorkspaceVmOrTask()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var vm = workspace.Vms!.Single();
        using var other = Client(otherToken);
        var prefix = $"{Root}/{workspace.Id}";
        foreach (var url in new[] { $"{Root}?organizationId=org", $"{prefix}?organizationId=org", $"{prefix}/vms?organizationId=org" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
        foreach (var action in new[] { "start", "stop", "stop-all", "cancel-idle-stop" })
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"{prefix}/{action}?organizationId=org", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync(Root, Request())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{prefix}?organizationId=other")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.DeleteAsync($"{prefix}?organizationId=org&confirmPermanentDeletion=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.DeleteAsync($"{prefix}?organizationId=other&confirmPermanentDeletion=true")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{prefix}/vms/{vm.Id}/discard?organizationId=org",
            new DevelopmentWorkspaceDiscardRequest(0, true, true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"{prefix}/vms/{vm.Id}/tasks/task/lease?organizationId=org",
            new DevelopmentWorkspaceGenerationRequest(0))).StatusCode);
        await AssertNoCompute();
    }

    [Fact]
    public async Task PermanentDeleteRequiresConfirmationAndReleasesCapacityOnlyAfterWorkerCompletion()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        var prefix = $"{Root}/{workspace.Id}";
        var url = $"{prefix}?organizationId=org";
        var confirmed = $"{url}&confirmPermanentDeletion=true";
        using var stopped = await client.PostAsync($"{prefix}/stop?organizationId=org", null);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.Equal("stopped", (await stopped.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);

        foreach (var unconfirmed in new[] { url, $"{url}&confirmPermanentDeletion=false" })
        {
            using var refused = await client.DeleteAsync(unconfirmed);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("workspace_delete_confirmation_required",
                (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        }
        await AssertNoCompute();

        // Model a retained disk from a previous run without starting a real VM.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var retained = await db.DevelopmentWorkspaces.SingleAsync();
            retained.ProviderVolumeId = "201";
            retained.VolumeStatus = "retained";
            await db.SaveChangesAsync();
        }
        factory.Cloud.WorkspaceId = workspace.Id;
        using var queued = await client.DeleteAsync(confirmed);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal("deleting", (await queued.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);
        var replacementRequest = Request() with { IdempotencyKey = "replacement" };
        using var tooSoon = await client.PostAsJsonAsync(Root, replacementRequest);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal(0, factory.Cloud.Deletions);

        for (var hop = 0; hop < 12; hop++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceProcessor>().ProcessNextAsync(default))
                break;
        }

        using var deleted = await client.DeleteAsync(confirmed);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("deleted", (await deleted.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Phase);
        Assert.Equal(1, factory.Cloud.Deletions);
        Assert.Empty((await client.GetFromJsonAsync<DevelopmentWorkspaceResponse[]>($"{Root}?organizationId=org"))!);
        using var replacement = await client.PostAsJsonAsync(Root, replacementRequest);
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        Assert.NotEqual(workspace.Id, (await replacement.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!.Id);
    }

    [Fact]
    public async Task RepeatedGetAndVmListingNeverAllocateAndResponsesRedactRuntimeCredentials()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}?organizationId=org")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/{workspace.Id}?organizationId=org")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Root}/{workspace.Id}/vms?organizationId=org")).StatusCode);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var vm = await db.DevelopmentWorkspaceVms.SingleAsync();
            vm.EnrollmentTokenDigest = "sensitive-enrollment"; vm.RuntimeTokenHash = "sensitive-runtime";
            vm.RepositoryCredentialRef = "sensitive-repository"; vm.TaskSecretReferencesJson = "sensitive-task";
            await db.SaveChangesAsync();
        }
        var raw = await client.GetStringAsync($"{Root}/{workspace.Id}?organizationId=org");
        Assert.DoesNotContain("sensitive-", raw);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        await AssertNoCompute();
    }

    [Fact]
    public async Task DisabledMutationsAndStaleGenerationFailClosed()
    {
        using var client = Client(token);
        var workspace = await Create(client);
        var vm = workspace.Vms!.Single();
        var stale = await client.PostAsJsonAsync($"{Root}/{workspace.Id}/vms/{vm.Id}/start?organizationId=org",
            new DevelopmentWorkspaceGenerationRequest(999));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("workspace_generation_stale", (await stale.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        factory.Policy.Enabled = false;
        var disabled = await client.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
        Assert.Equal("workspaces_disabled", (await disabled.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        await AssertNoCompute();
    }

    [Fact]
    public async Task RuntimeEnrollmentConsumesOnlyTheGenerationBoundOneTimeCredential()
    {
        var (request, enrollmentToken) = await Enrollment();
        using var runtime = Client(enrollmentToken);
        var accepted = await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request);
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var machine = await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().DevelopmentWorkspaceVms.SingleAsync();
        Assert.NotNull(machine.EnrolledAt);
        Assert.Null(machine.EnrollmentTokenDigest);
        Assert.Null(machine.EnrollmentTokenExpiresAt);
        Assert.False(machine.GatewayReady);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("kind")]
    [InlineData("manifest")]
    [InlineData("receipt")]
    public async Task ForgedRuntimeEvidenceCannotConsumeTheLegitimateEnrollment(string forged)
    {
        var (request, enrollmentToken) = await Enrollment();
        var invalid = forged switch
        {
            "generation" => request with { Generation = request.Generation + 1 },
            "kind" => request with { Kind = "ephemeral" },
            "manifest" => request with { ImageManifestSha256 = new('c', 64) },
            _ => request with { BootstrapReceipt = JsonSerializer.SerializeToElement(new { type = "UserSuppliedProof" }) }
        };
        using var runtime = Client(enrollmentToken);
        Assert.Equal(HttpStatusCode.Conflict, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await runtime.PostAsJsonAsync($"{Root}/runtime/enroll", request)).StatusCode);
    }

    private async Task<(Minicloud.Api.Controllers.DevelopmentWorkspaceEnrollmentRequest Request, string Token)> Enrollment()
    {
        using var owner = Client(token);
        var workspace = await Create(owner);
        var started = await owner.PostAsync($"{Root}/{workspace.Id}/start?organizationId=org", null);
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        await using var scope = factory.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<DevelopmentWorkspaceService>();
        var operation = await lifecycle.ClaimOperationAsync("enrollment-fixture", default);
        Assert.NotNull(operation);
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var machine = await db.DevelopmentWorkspaceVms.SingleAsync();
        var credential = new string('b', 64);
        machine.ProviderServerId = "301"; machine.SshHostKeyGeneration = machine.Generation;
        machine.EnrollmentTokenDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        machine.EnrollmentTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        var receipt = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, type = "BootstrapReady", vmId = machine.Id,
            generation = machine.Generation, kind = machine.Kind, operationId = operation.Id
        });
        return (new(1, machine.Id, machine.Generation, machine.Kind, operation.Id, new('a', 64), receipt), credential);
    }

    private async Task AssertNoCompute()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await db.DevelopmentWorkspaceOperations.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceVms.Where(x => x.ComputeReserved).ToListAsync());
        Assert.Empty(await db.ServerInstances.ToListAsync());
    }

    private static async Task<DevelopmentWorkspaceResponse> Create(HttpClient client)
    {
        var result = await client.PostAsJsonAsync(Root, Request());
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        return (await result.Content.ReadFromJsonAsync<DevelopmentWorkspaceResponse>())!;
    }
    private HttpClient Client(string value)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
        return client;
    }
    private static string AddKey(MinicloudDbContext db, IApiKeyTokenService tokens, string org, IEnumerable<string> scopes)
    {
        var value = tokens.Generate();
        db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid().ToString("N"), OrganizationId = org, Name = "Fixture",
            KeyPrefix = value.Prefix, KeyHash = tokens.Hash(value.Token), ScopesJson = JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        return value.Token;
    }
    public async Task DisposeAsync() { await factory.DisposeAsync(); await connection.DisposeAsync(); }

    private sealed class WorkspaceApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public VolumeTransport Cloud { get; } = new();
        public DevelopmentWorkspaceOptions Policy { get; } = new()
        {
            Enabled = true, MaxWorkspacesPerOrganization = 1,
            MaxEstimatedMonthlySpend = 1000, MaxEstimatedMonthlySpendPerOrganization = 1000,
            VolumeMonthlyRatePerGb = .1m, PricingUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Profiles = new()
            {
                ["standard"] = new() { CpuCount = 4, MemoryGiB = 8, ComputeHourlyRate = .1m,
                    AllowedPlanIds = ["plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["cx-test"] }
            },
            Images = new()
            {
                ["image"] = new() { Enabled = true, RuntimeQualified = true, QualifiedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    ProviderAccountId = "account", AllowedRegions = ["nbg1"], ProviderImageId = "123", ManifestSha256 = new('a', 64) }
            }
        };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
                services.AddSingleton<IOptions<DevelopmentWorkspaceOptions>>(Options.Create(Policy));
                services.AddDevelopmentWorkspaceWorker(new ConfigurationBuilder().Build());
                // Drive the real processor explicitly so tests control the queue and share SQLite safely.
                services.Remove(services.Single(x => x.ServiceType == typeof(IHostedService)
                    && x.ImplementationType == typeof(DevelopmentWorkspaceWorker)));
                services.RemoveAll<ProviderAccountClientFactory>();
                services.AddSingleton<ProviderAccountClientFactory>(new VolumeProviderFactory(Cloud));
            });
        }
    }

    private sealed class VolumeProviderFactory(VolumeTransport cloud)
        : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, NullLoggerFactory.Instance)
    {
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId,
            CancellationToken ct, bool allowDisabled = true)
        {
            Assert.Equal("org", organizationId);
            Assert.Equal("account", providerAccountId);
            return Task.FromResult<IVpsProviderClient>(new HetznerClient(new HttpClient(cloud, disposeHandler: false),
                new HetznerOptions { ApiToken = "synthetic", BaseUrl = "https://provider.test/v1/" }));
        }
    }

    private sealed class VolumeTransport : HttpMessageHandler
    {
        public string? WorkspaceId { get; set; }
        public int Deletions { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var volume = new
            {
                id = 201, name = "fixture-volume", size = 20, format = "ext4", server = (long?)null,
                location = new { name = "nbg1" }, linux_device = "/dev/disk/by-id/fixture",
                labels = new Dictionary<string, string>
                {
                    ["managed-by"] = "minicloud-workspaces", ["organization"] = "org",
                    ["workspace"] = WorkspaceId!, ["kind"] = "primary"
                }
            };
            if (request.RequestUri!.AbsolutePath == "/v1/volumes")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = JsonContent.Create(new { volumes = Deletions == 0 ? new[] { volume } : [] }) });
            }
            Assert.Equal("/v1/volumes/201", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal(0, Deletions++);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Deletions > 0
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { volume }) });
        }
    }
}
