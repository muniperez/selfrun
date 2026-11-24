using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ProviderExtensionApiContractTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ExtensionApiFactory _factory = default!;
    private string _token = default!;
    private const string OrganizationId = "org_provider_ext";

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new ExtensionApiFactory(_connection);
        _factory.CreateClient().Dispose();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Provider Ext Org", Slug = "provider-ext-org" });
        var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var generated = tokens.Generate();
        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid().ToString("N"),
            OrganizationId = OrganizationId,
            Name = "Ext test key",
            KeyPrefix = generated.Prefix,
            KeyHash = tokens.Hash(generated.Token),
            ScopesJson = JsonSerializer.Serialize(ApiKeyScopes.All),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        _token = generated.Token;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ProviderTypesEndpointReturnsCapabilityMetadataAndHidesDraftGatedProviders()
    {
        using var client = Client();
        var types = (await client.GetFromJsonAsync<ProviderTypeResponse[]>("/api/provider-types"))!;

        // Only the three qualified providers are advertised; draft-gated providers (aws, ovh, etc.) are hidden.
        Assert.Equal(["digital-ocean", "hetzner", "vultr"], types.Select(x => x.Type).OrderBy(x => x).ToArray());
        Assert.All(types, t =>
        {
            Assert.Equal("qualified", t.QualificationState);
            Assert.NotNull(t.Capabilities);
            Assert.Equal(["virtual-server"], t.Capabilities!.ComputeFamilies);
            Assert.Contains("linux/amd64", t.Capabilities.SupportedArchitectures);
            Assert.Contains("linux/arm64", t.Capabilities.SupportedArchitectures);
            Assert.Equal("region", t.Capabilities.LocationGranularity);
            Assert.True(t.Capabilities.SupportsCloudInit);
            Assert.True(t.Capabilities.SupportsSshKeyInventory);
        });

        var hetzner = types.Single(x => x.Type == "hetzner");
        Assert.True(hetzner.SupportsStorage);
        Assert.True(hetzner.Capabilities!.SupportsDurableVolumes);
        Assert.True(hetzner.Capabilities.SupportsVolumeResize);
        Assert.True(hetzner.Capabilities.SupportsPrivateNetworks);

        var digitalOcean = types.Single(x => x.Type == "digital-ocean");
        Assert.False(digitalOcean.SupportsStorage);
        Assert.False(digitalOcean.Capabilities!.SupportsDurableVolumes);
        Assert.False(digitalOcean.Capabilities.SupportsPrivateNetworks);

        var vultr = types.Single(x => x.Type == "vultr");
        Assert.False(vultr.SupportsStorage);
        Assert.Equal(2284, vultr.DefaultOsId);
        Assert.False(vultr.Capabilities!.SupportsDurableVolumes);
    }

    [Theory]
    [InlineData("aws")]
    [InlineData("azure")]
    [InlineData("gcp")]
    [InlineData("ovh")]
    [InlineData("linode")]
    [InlineData("scaleway")]
    public async Task DraftGatedPlannedProvidersCannotBeCreatedWithoutQualification(string wireType)
    {
        using var client = Client();
        var response = await client.PostAsJsonAsync("/api/providers",
            new CreateProviderRequest(OrganizationId, $"Planned {wireType}", wireType, "synthetic-secret"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("provider_type_unsupported", body);
        Assert.DoesNotContain("synthetic-secret", body);
        Assert.Empty(_factory.Secrets.Values);
        Assert.Empty(_factory.Cloud.Requests);
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private sealed class ExtensionApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public FakeSecrets Secrets { get; } = new();
        public FakeCloud Cloud { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:",
                ["OpenBao:EnvironmentPrefix"] = "provider-ext-tests"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.UseSharedSqliteConnection(connection);
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(Secrets);
                services.RemoveAll<IKratosSessionValidator>();
                services.AddSingleton<IKratosSessionValidator>(new FakeKratos());
                services.AddHttpClient(ProviderAccountClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Cloud);

                // Register synthetic DraftGated entries for planned providers to verify they stay hidden and rejected.
                foreach (var (wire, display) in new[]
                {
                    ("aws", "AWS EC2"),
                    ("azure", "Azure VMs"),
                    ("gcp", "GCP Compute Engine"),
                    ("ovh", "OVHcloud"),
                    ("linode", "Linode"),
                    ("scaleway", "Scaleway")
                })
                {
                    services.AddVpsProvider(new DraftGatedRegistration(wire, display));
                }
            });
        }
    }

    private sealed class DraftGatedRegistration(string wireType, string displayName) : IVpsProviderRegistration
    {
        public ProviderDescriptor Descriptor { get; } = new(
            WireType: wireType,
            Aliases: [],
            DisplayName: displayName,
            BuiltInProvider: null,
            QualificationState: ProviderQualificationState.DraftGated,
            Capabilities: new ProviderCapabilities(
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64", "linux/arm64"],
                ProviderLocationGranularity.Region,
                true, true, true, false, false, true, true, true, true, true, true, true, true, true, true),
            Placement: new ProviderPlacementContract(
                ProviderLocationGranularity.Region,
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64", "linux/arm64"],
                ProviderImageFormat.SlugOrId),
            Credentials: new ProviderCredentialContract(
                ProviderCredentialFormat.ApiToken,
                ProviderScopeBoundaryKind.Project,
                [], [], true),
            RetryPolicy: ProviderRetryPolicy.Default);

        public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
        public IVpsProviderClient CreateClient(ProviderClientCreationContext context) => throw new NotSupportedException("Gated provider must not be instantiated.");
    }

    private sealed class FakeKratos : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            var path = OpenBaoSecretStore.ProviderPathFor("provider-ext-tests", request.OrganizationId, request.ProviderAccountId!, request.CredentialGeneration!);
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(Values[reference.Path]);
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            Values.TryRemove(reference.Path, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCloud : HttpMessageHandler
    {
        public ConcurrentQueue<Uri?> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { servers = Array.Empty<object>() }) });
        }
    }
}
