using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ProviderExpansionCatalogApiTests
{
    private const string OrganizationId = "org_provider_expansion";

    [Fact]
    public async Task DefaultConfigurationGatesExperimentalProvidersBeforeSecretsOrCloudDispatch()
    {
        await using var harness = await ApiHarness.CreateAsync(enableExperimental: false);
        using var client = harness.CreateAuthenticatedClient();

        var types = (await client.GetFromJsonAsync<ProviderTypeResponse[]>("/api/provider-types"))!;
        Assert.Equal(["digital-ocean", "hetzner", "vultr"], types.Select(t => t.Type).OrderBy(t => t).ToArray());
        Assert.All(types, t => Assert.Equal("qualified", t.QualificationState));

        foreach (var wireType in new[] { "aws", "azure", "gcp", "ovhcloud", "linode", "scaleway" })
        {
            var response = await client.PostAsJsonAsync(
                "/api/providers",
                new CreateProviderRequest(OrganizationId, $"Blocked {wireType}", wireType, "synthetic-blocked-credential"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            Assert.Equal("provider_type_unsupported", error!.Error.Code);
        }

        Assert.Empty(harness.Secrets.Values);
        Assert.Empty(harness.Cloud.Requests);
    }

    [Fact]
    public async Task EnabledExperimentalProvidersAdvertiseCapabilitiesAndCreateWriteOnlyAccountsAndPlans()
    {
        await using var harness = await ApiHarness.CreateAsync(enableExperimental: true);
        using var client = harness.CreateAuthenticatedClient();

        var types = (await client.GetFromJsonAsync<ProviderTypeResponse[]>("/api/provider-types"))!;
        Assert.Equal(9, types.Length);

        var byType = types.ToDictionary(t => t.Type, StringComparer.Ordinal);
        Assert.Equal("experimental", byType["aws"].QualificationState);
        Assert.True(byType["aws"].SupportsStorage);
        Assert.DoesNotContain("bare-metal", byType["aws"].Capabilities!.ComputeFamilies);
        Assert.False(byType["aws"].RequiresProjectId);

        Assert.Equal("experimental", byType["azure"].QualificationState);
        Assert.True(byType["azure"].SupportsStorage);
        Assert.True(byType["azure"].RequiresProjectId);

        Assert.Equal("experimental", byType["gcp"].QualificationState);
        Assert.True(byType["gcp"].SupportsStorage);
        Assert.True(byType["gcp"].RequiresProjectId);

        Assert.Equal("experimental", byType["ovhcloud"].QualificationState);
        Assert.True(byType["ovhcloud"].SupportsStorage);
        Assert.Contains("bare-metal", byType["ovhcloud"].Capabilities!.ComputeFamilies);
        Assert.True(byType["ovhcloud"].RequiresProjectId);

        Assert.Equal("experimental", byType["linode"].QualificationState);
        Assert.True(byType["linode"].SupportsStorage);
        Assert.False(byType["linode"].RequiresProjectId);

        Assert.Equal("experimental", byType["scaleway"].QualificationState);
        Assert.True(byType["scaleway"].SupportsStorage);
        Assert.True(byType["scaleway"].RequiresProjectId);

        var accountsToCreate = new (string WireType, string Name, string Credential, string? ProjectId, string Region, string Sku)[]
        {
            ("aws", "AWS Primary", """{"accessKeyId":"AKIATESTKEY","secretAccessKey":"synthetic-aws-secret"}""", "123456789012", "us-east-1", "t3.small"),
            ("azure", "Azure Primary", """{"subscriptionId":"00000000-0000-0000-0000-000000000001","resourceGroup":"rg-mc","accessToken":"synthetic-azure-bearer"}""", "00000000-0000-0000-0000-000000000001/rg-mc", "eastus", "Standard_B2s"),
            ("gcp", "GCP Primary", """{"projectId":"mc-gcp-proj","accessToken":"synthetic-gcp-bearer"}""", "mc-gcp-proj", "us-central1-a", "e2-small"),
            ("ovhcloud", "OVH Primary", """{"applicationKey":"appkey","applicationSecret":"synthetic-ovh-appsec","consumerKey":"synthetic-ovh-ckey","projectId":"ovh-proj-1"}""", "ovh-proj-1", "GRA11", "d2-4"),
            ("linode", "Linode Primary", "synthetic-linode-pat", null, "us-east", "g6-standard-1"),
            ("scaleway", "Scaleway Primary", """{"secretKey":"synthetic-scw-secret","projectId":"scw-proj-1"}""", "scw-proj-1", "fr-par-1", "DEV1-S")
        };

        foreach (var spec in accountsToCreate)
        {
            var createResponse = await client.PostAsJsonAsync(
                "/api/providers",
                new CreateProviderRequest(OrganizationId, spec.Name, spec.WireType, spec.Credential, spec.ProjectId));
            Assert.True(createResponse.IsSuccessStatusCode, $"{spec.WireType}: HTTP {(int)createResponse.StatusCode} {await createResponse.Content.ReadAsStringAsync()}");

            var rawAccount = await createResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic-", rawAccount, StringComparison.OrdinalIgnoreCase);

            var account = JsonSerializer.Deserialize<ProviderAccountResponse>(rawAccount, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(spec.WireType, account.ProviderType);
            Assert.Equal("valid", account.ValidationStatus);

            var planResponse = await client.PostAsJsonAsync(
                "/api/plans",
                new CreateProviderPlanRequest(OrganizationId, account.Id, spec.Region, spec.Sku, Name: $"{spec.WireType}-small"));
            Assert.True(planResponse.IsSuccessStatusCode, $"{spec.WireType} plan: HTTP {(int)planResponse.StatusCode} {await planResponse.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(6, harness.Secrets.Values.Count);
        Assert.NotEmpty(harness.Cloud.Requests);
        Assert.All(harness.Cloud.Requests, req => Assert.Equal(HttpMethod.Get, req.Method));

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var storedAccounts = await db.ProviderAccounts.AsNoTracking().ToListAsync();
        Assert.Equal(6, storedAccounts.Count);
        Assert.DoesNotContain("synthetic-", JsonSerializer.Serialize(storedAccounts), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedStructuredCredentialsFailBeforeSecretStorageOrHttpDispatch()
    {
        await using var harness = await ApiHarness.CreateAsync(enableExperimental: true);
        using var client = harness.CreateAuthenticatedClient();

        // AWS missing secretAccessKey
        var awsBad = await client.PostAsJsonAsync(
            "/api/providers",
            new CreateProviderRequest(OrganizationId, "Bad AWS", "aws", """{"accessKeyId":"AKIAONLY"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, awsBad.StatusCode);
        Assert.Equal("provider_credential_invalid", (await awsBad.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        // GCP mismatched projectId vs account.ProjectId
        var gcpMismatch = await client.PostAsJsonAsync(
            "/api/providers",
            new CreateProviderRequest(OrganizationId, "Mismatch GCP", "gcp", """{"projectId":"proj-a","accessToken":"tok"}""", "proj-b"));
        Assert.Equal(HttpStatusCode.BadRequest, gcpMismatch.StatusCode);
        Assert.Equal("provider_credential_invalid", (await gcpMismatch.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        Assert.Empty(harness.Secrets.Values);
        Assert.Empty(harness.Cloud.Requests);
    }

    private sealed class ApiHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ExpansionApiFactory _factory;
        private readonly string _token;

        private ApiHarness(SqliteConnection connection, ExpansionApiFactory factory, string token)
        {
            _connection = connection;
            _factory = factory;
            _token = token;
        }

        public FakeSecrets Secrets => _factory.Secrets;
        public FakeExpansionCloud Cloud => _factory.Cloud;
        public IServiceProvider Services => _factory.Services;

        public HttpClient CreateAuthenticatedClient()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return client;
        }

        public static async Task<ApiHarness> CreateAsync(bool enableExperimental)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new ExpansionApiFactory(connection, enableExperimental);
            factory.CreateClient().Dispose();

            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Provider expansion", Slug = "provider-expansion" });
            var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
            var key = tokens.Generate();
            db.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid().ToString("N"),
                OrganizationId = OrganizationId,
                Name = "Expansion test key",
                KeyPrefix = key.Prefix,
                KeyHash = tokens.Hash(key.Token),
                ScopesJson = JsonSerializer.Serialize(ApiKeyScopes.All),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            return new ApiHarness(connection, factory, key.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ExpansionApiFactory(SqliteConnection connection, bool enableExperimental) : WebApplicationFactory<Program>
    {
        public FakeSecrets Secrets { get; } = new();
        public FakeExpansionCloud Cloud { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Minicloud"] = "Data Source=:memory:",
                    ["Cors:AllowedOrigins:0"] = "https://dashboard.example.test",
                    ["OpenBao:EnvironmentPrefix"] = "provider-expansion-tests"
                };

                if (enableExperimental)
                {
                    values["Aws:Enabled"] = "true";
                    values["Aws:BaseUrl"] = "https://ec2.{region}.example.test/";
                    values["Azure:Enabled"] = "true";
                    values["Azure:BaseUrl"] = "https://management.azure.example.test/";
                    values["GoogleCloud:Enabled"] = "true";
                    values["GoogleCloud:BaseUrl"] = "https://compute.googleapis.example.test/";
                    values["Ovhcloud:Enabled"] = "true";
                    values["Ovhcloud:BaseUrl"] = "https://eu.api.ovh.example.test/1.0/";
                    values["Linode:Enabled"] = "true";
                    values["Linode:BaseUrl"] = "https://api.linode.example.test/v4/";
                    values["Scaleway:Enabled"] = "true";
                    values["Scaleway:BaseUrl"] = "https://api.scaleway.example.test/";
                }

                config.AddInMemoryCollection(values);
            });

            builder.ConfigureTestServices(services =>
            {
                services.UseSharedSqliteConnection(connection);
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore>(Secrets);
                services.AddHttpClient(ProviderAccountClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Cloud);
            });
        }
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            var path = OpenBaoSecretStore.ProviderPathFor("provider-expansion-tests", request.OrganizationId, request.ProviderAccountId!, request.CredentialGeneration!);
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            if (!Values.TryGetValue(reference.Path, out var value))
            {
                throw new SecretStoreUnavailableException("Missing test secret.");
            }
            return Task.FromResult(value);
        }

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            Values.TryRemove(reference.Path, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExpansionCloud : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, Uri? Uri)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.Method, request.RequestUri));
            Assert.Equal(HttpMethod.Get, request.Method);

            var host = request.RequestUri?.Host ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var json = host.Contains("ovh", StringComparison.OrdinalIgnoreCase) && path.Contains("/instance", StringComparison.OrdinalIgnoreCase)
                ? "[]"
                : """{"accountId":"123456789012","project_id":"ovh-proj-1","uid":1234,"username":"ops","servers":[],"droplets":[],"instances":[],"value":[],"items":{},"data":[]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
