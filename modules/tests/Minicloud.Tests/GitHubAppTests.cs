using System.Net;
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
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class GitHubAppTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new($"Data Source=github-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
    private Factory factory = default!;
    private HttpClient owner = default!;
    private const string Org = "org_github";

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new Factory(connection);
        factory.CreateClient().Dispose();
        await InDb(async db =>
        {
            await db.Database.EnsureCreatedAsync();
            db.Organizations.Add(new() { Id = Org, Name = "GitHub", Slug = "github" });
            db.Organizations.Add(new() { Id = "foreign", Name = "Foreign", Slug = "foreign" });
            await db.SaveChangesAsync();
        });
        owner = await Browser("owner", Org, OrganizationRole.Owner);
    }

    [Fact]
    public async Task SetupRequiresAuthorizedBrowserAdminAndCanonicalUrlsAndRequestsLeastPrivilege()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/github-apps/setup", Request())).StatusCode);
        using var developer = await Browser("developer", Org, OrganizationRole.Developer);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.PostAsJsonAsync("/api/github-apps/setup", Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/github-apps/setup", Request() with { OrganizationId = "foreign" })).StatusCode);
        using var csrfMissing = await Browser("no_csrf", Org, OrganizationRole.Admin);
        csrfMissing.DefaultRequestHeaders.Remove("X-Minicloud-CSRF");
        Assert.Equal(HttpStatusCode.Forbidden, (await csrfMissing.PostAsJsonAsync("/api/github-apps/setup", Request())).StatusCode);
        owner.DefaultRequestHeaders.Host = "attacker.example";
        var setup = await Begin();
        var manifest = setup.Manifest;
        Assert.Equal("https://console.example.test", manifest.GetProperty("url").GetString());
        Assert.StartsWith("https://console.example.test/settings/github/callback?", manifest.GetProperty("redirect_url").GetString());
        Assert.StartsWith("https://api.example.test/api/github-apps/", manifest.GetProperty("hook_attributes").GetProperty("url").GetString());
        Assert.False(manifest.GetProperty("public").GetBoolean());
        var permissions = manifest.GetProperty("default_permissions");
        Assert.Equal(3, permissions.EnumerateObject().Count());
        Assert.Equal("write", permissions.GetProperty("contents").GetString());
        Assert.Equal("write", permissions.GetProperty("pull_requests").GetString());
        Assert.Equal("read", permissions.GetProperty("metadata").GetString());
        Assert.Empty(manifest.GetProperty("default_events").EnumerateArray());
        Assert.Equal(0, factory.GitHub.Conversions);
    }

    [Fact]
    public async Task OrganizationManifestTargetsTheExplicitOwner()
    {
        var response = await owner.PostAsJsonAsync("/api/github-apps/setup", Request() with { OwnerType = "Organization" });
        var body = await Success<Setup>(response);
        Assert.StartsWith("https://github.com/organizations/fixture-owner/settings/apps/new?state=", body.PostUrl);
    }

    [Fact]
    public async Task CallbackRejectsIdentityStateExpiryAndCodeSubstitutionWithoutConverting()
    {
        var setup = await Begin();
        using var otherAdmin = await Browser("other", Org, OrganizationRole.Admin);
        await Error(await otherAdmin.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_state_invalid");
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup) with { State = new string('a', 64) }), "github_state_invalid");
        await InDb(async db => { (await db.GitHubAppConnections.SingleAsync()).SetupExpiresAtTicks = DateTimeOffset.UtcNow.AddSeconds(-1).UtcTicks; await db.SaveChangesAsync(); });
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_state_invalid");
        Assert.Equal(0, factory.GitHub.Conversions);
    }

    [Fact]
    public async Task ConversionIsSingleUseIdempotentAndCredentialsNeverLeaveOpenBao()
    {
        var setup = await Begin();
        var first = await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)));
        Assert.Equal("app_created", first.Status);
        Assert.Null(first.InstallationId);
        await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)));
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup) with { Code = "different" }), "github_callback_replayed");
        Assert.Equal(1, factory.GitHub.Conversions);
        var status = await owner.GetStringAsync("/api/github-apps?organizationId=" + Org);
        await InDb(async db =>
        {
            var stored = await db.GitHubAppConnections.SingleAsync();
            Assert.StartsWith($"env/github-tests/orgs/{Org}/integrations/{stored.Id}/credentials/", stored.SecretPath);
            Assert.DoesNotContain(State(setup.PostUrl), JsonSerializer.Serialize(stored));
            Assert.DoesNotContain("synthetic-webhook", JsonSerializer.Serialize(stored));
            Assert.DoesNotContain("PRIVATE KEY", JsonSerializer.Serialize(stored));
        });
        Assert.DoesNotContain("secret", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("state", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE KEY", status);
        Assert.Single(factory.Secrets.Values);
    }

    [Fact]
    public async Task StoredConversionCanRecoverAfterAmbiguousSecretWriteWithoutReusingCode()
    {
        var setup = await Begin();
        factory.Secrets.FailAfterWrite = true;
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_conversion_uncertain");
        factory.Secrets.FailAfterWrite = false;
        var recovered = await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)));
        Assert.Equal("app_created", recovered.Status);
        Assert.Equal(1, factory.GitHub.Conversions);
    }

    [Fact]
    public async Task WrongAppOwnerAndAmbiguousConversionCannotBeRetriedAutomatically()
    {
        var setup = await Begin();
        factory.GitHub.WrongOwner = true;
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_conversion_uncertain");
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_conversion_uncertain");
        Assert.Equal(1, factory.GitHub.Conversions);
        Assert.Empty(factory.Secrets.Values);
    }

    [Fact]
    public async Task InstallationIsVerifiedServerSideAndRepositoryListingIsTenantScoped()
    {
        var setup = await Created();
        var link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        var request = new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900);
        factory.GitHub.WrongInstallation = true;
        await Error(await owner.PostAsJsonAsync("/api/github-apps/installation", request), "github_installation_mismatch");
        factory.GitHub.WrongInstallation = false;
        var connected = await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/installation", request));
        Assert.Equal("connected", connected.Status);
        await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/installation", request));
        await Error(await owner.PostAsJsonAsync("/api/github-apps/installation", request with { InstallationId = 901 }), "github_callback_replayed");
        var repos = await owner.GetFromJsonAsync<GitHubRepository[]>($"/api/github-apps/{setup.ConnectionId}/repositories");
        Assert.Equal("fixture-owner/private-repo", Assert.Single(repos!).FullName);
        using var foreign = await Browser("foreign", "foreign", OrganizationRole.Owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync($"/api/github-apps/{setup.ConnectionId}/repositories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync("/api/github-apps?organizationId=" + Org)).StatusCode);
        Assert.True(factory.GitHub.RevokedTokens > 0);
    }

    [Fact]
    public async Task SignedWebhooksAreIdempotentAndRevocationAndDisconnectFailClosed()
    {
        var setup = await Connected();
        var body = """{"action":"deleted","installation":{"id":900}}""";
        using var anonymous = factory.CreateClient();
        await Error(await Hook(anonymous, setup.ConnectionId, body, "delivery-1", "sha256=" + new string('0', 64)), "github_signature_invalid");
        Assert.Equal(HttpStatusCode.NoContent, (await Hook(anonymous, setup.ConnectionId, body, "delivery-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Hook(anonymous, setup.ConnectionId, body, "delivery-1")).StatusCode);
        Assert.Equal("revoked", (await Status()).Status);
        await InDb(async db => { Assert.Single(await db.GitHubWebhookReceipts.ToListAsync()); Assert.Equal("[]", (await db.GitHubAppConnections.SingleAsync()).RepositoriesJson); });
        factory.GitHub.Deleted = true;
        await Error(await owner.GetAsync($"/api/github-apps/{setup.ConnectionId}/repositories"), "github_access_revoked");
        await Success<GitHubAppStatus>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/disconnect", null));
        factory.GitHub.Deleted = false;
        Assert.Equal(HttpStatusCode.NoContent, (await Hook(anonymous, setup.ConnectionId,
            """{"action":"unsuspend","installation":{"id":900}}""", "delivery-2")).StatusCode);
        Assert.Equal("disconnected", (await Status()).Status);
        await Error(await owner.GetAsync($"/api/github-apps/{setup.ConnectionId}/repositories"), "github_disconnected");
        var reconnected = await Success<GitHubAppStatus>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/reconnect", null));
        Assert.Equal("connected", reconnected.Status);
        Assert.Equal(1, factory.GitHub.Conversions);
    }

    [Fact]
    public async Task DisconnectInvalidatesCallbacksAndExplicitRemovalRetainsDurableIntentOnFailure()
    {
        var setup = await Created();
        var link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        await Success<GitHubAppStatus>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/disconnect", null));
        await Error(await owner.PostAsJsonAsync("/api/github-apps/installation",
            new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900)), "github_state_invalid");
        factory.Secrets.FailDelete = true;
        await Error(await owner.DeleteAsync($"/api/github-apps/{setup.ConnectionId}"), "github_secrets_unavailable");
        Assert.Equal("removing", (await Status()).Status);
        Assert.Single(factory.Secrets.Values);
        factory.Secrets.FailDelete = false;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/github-apps/{setup.ConnectionId}")).StatusCode);
        Assert.Empty(factory.Secrets.Values);
        Assert.Equal("not_configured", (await Status()).Status);
        Assert.Equal(0, factory.GitHub.AppDeletes);
    }

    [Fact]
    public async Task InFlightReconciliationCannotResurrectADisconnectedConnection()
    {
        var setup = await Connected();
        factory.GitHub.DuringVerification = async () =>
        {
            await Success<GitHubAppStatus>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/disconnect", null));
        };
        await Error(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/reconcile", null), "github_connection_changed");
        Assert.Equal("disconnected", (await Status()).Status);
    }

    [Theory]
    [InlineData(true, false, "github_installation_mismatch")]
    [InlineData(false, true, "github_permissions_changed")]
    public async Task InstallationRejectsAnotherAppAndUnexpectedPermissions(bool wrongApp, bool extraPermission, string code)
    {
        var setup = await Created();
        var link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        factory.GitHub.WrongApp = wrongApp; factory.GitHub.ExtraPermission = extraPermission;
        await Error(await owner.PostAsJsonAsync("/api/github-apps/installation",
            new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900)), code);
        Assert.NotEqual("connected", (await Status()).Status);
    }

    [Fact]
    public async Task InstallationStateExpiresAndRepositoryChangesAndSuspensionClearAccess()
    {
        var setup = await Created();
        var link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        await InDb(async db => { (await db.GitHubAppConnections.SingleAsync()).InstallationExpiresAtTicks = 1; await db.SaveChangesAsync(); });
        await Error(await owner.PostAsJsonAsync("/api/github-apps/installation",
            new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900)), "github_state_invalid");
        link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/installation",
            new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900)));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await Hook(anonymous, setup.ConnectionId,
            """{"action":"removed","installation":{"id":900}}""", "repo-change", eventName: "installation_repositories")).StatusCode);
        Assert.Equal("repositories_unverified", (await Status()).Status);
        await InDb(async db => Assert.Equal("[]", (await db.GitHubAppConnections.SingleAsync()).RepositoriesJson));
        factory.GitHub.Suspended = true;
        var repositories = await owner.GetFromJsonAsync<GitHubRepository[]>($"/api/github-apps/{setup.ConnectionId}/repositories");
        Assert.Empty(repositories!);
        Assert.Equal("suspended", (await Status()).Status);
    }

    [Fact]
    public async Task AppCreationCannotResurrectSetupDisconnectedDuringConversion()
    {
        var setup = await Begin();
        factory.GitHub.DuringConversion = async () =>
        {
            await Success<GitHubAppStatus>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/disconnect", null));
        };
        await Error(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup)), "github_connection_changed");
        Assert.Equal("disconnected", (await Status()).Status);
        Assert.Single(factory.Secrets.Values);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/github-apps/{setup.ConnectionId}")).StatusCode);
        Assert.Empty(factory.Secrets.Values);
    }

    private static GitHubAppSetupRequest Request() => new(Org, "fixture-owner", "User");
    private async Task<GitHubAppStatus> Status() => (await owner.GetFromJsonAsync<GitHubAppStatus>("/api/github-apps?organizationId=" + Org))!;
    private Task<Setup> Begin() => BeginCore();
    private async Task<Setup> BeginCore() => await Success<Setup>(await owner.PostAsJsonAsync("/api/github-apps/setup", Request()));
    private async Task<Setup> Created() { var setup = await Begin(); await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/complete", Complete(setup))); return setup; }
    private async Task<Setup> Connected()
    {
        var setup = await Created();
        var link = await Success<GitHubAppInstallationUrl>(await owner.PostAsync($"/api/github-apps/{setup.ConnectionId}/installation-url", null));
        await Success<GitHubAppStatus>(await owner.PostAsJsonAsync("/api/github-apps/installation", new GitHubAppInstallRequest(setup.ConnectionId, State(link.Url), 900)));
        return setup;
    }
    private static GitHubAppCompleteRequest Complete(Setup setup) => new(setup.ConnectionId, State(setup.PostUrl), "oneusecode");
    private static string State(string url) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();
    private static async Task<T> Success<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task Error(HttpResponseMessage response, string code)
    {
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
    }
    private async Task InDb(Func<MinicloudDbContext, Task> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<MinicloudDbContext>());
    }
    private async Task<HttpClient> Browser(string id, string org, OrganizationRole role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        db.Users.Add(new() { Id = id, KratosIdentityId = "identity-" + id, Email = id + "@example.test" });
        db.OrganizationMembers.Add(new() { Id = "member-" + id, OrganizationId = org, UserId = id, Role = role });
        await db.SaveChangesAsync();
        var session = await scope.ServiceProvider.GetRequiredService<BrowserSessionService>().CreateAsync(id, "identity-" + id, "session-" + id, default);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "__Host-minicloud-session=" + session.Cookie);
        client.DefaultRequestHeaders.Add("Origin", "https://console.example.test");
        client.DefaultRequestHeaders.Add("X-Minicloud-CSRF", session.CsrfToken);
        return client;
    }
    private static Task<HttpResponseMessage> Hook(HttpClient client, string id, string body, string delivery, string? signature = null, string eventName = "installation")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/github-apps/{id}/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-Hub-Signature-256", signature ?? "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("synthetic-webhook"), Encoding.UTF8.GetBytes(body))));
        return client.SendAsync(request);
    }
    public async Task DisposeAsync() { owner.Dispose(); await factory.DisposeAsync(); await connection.DisposeAsync(); }
    private sealed record Setup(string ConnectionId, string PostUrl, JsonElement Manifest);

    private sealed class Factory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public MemorySecrets Secrets { get; } = new();
        public GitHubTransport GitHub { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:", ["Console:BaseUrl"] = "https://console.example.test",
                ["GitHub:ApiUrl"] = "https://api.example.test", ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["OpenBao:EnvironmentPrefix"] = "github-tests"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection.ConnectionString));
                services.RemoveAll<ISecretStore>(); services.AddSingleton<ISecretStore>(Secrets);
                services.RemoveAll<IKratosSessionValidator>(); services.AddSingleton<IKratosSessionValidator>(new Kratos());
                services.AddHttpClient<GitHubAppClient>().ConfigurePrimaryHttpMessageHandler(() => GitHub);
            });
        }
    }
    private sealed class Kratos : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken ct) => Task.FromResult(true);
        public Task RevokeSessionAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailAfterWrite, FailDelete;
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            var path = OpenBaoSecretStore.IntegrationPathFor("github-tests", request.OrganizationId, request.IntegrationId!, request.CredentialGeneration!);
            Values[path] = request.Value;
            if (FailAfterWrite) throw new SecretStoreUnavailableException("synthetic-store-outage");
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) =>
            Values.TryGetValue(reference.Path, out var value) ? Task.FromResult(value) : throw new SecretStoreUnavailableException("synthetic-store-outage");
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct)
        {
            if (FailDelete) throw new SecretStoreUnavailableException("synthetic-store-outage");
            Values.Remove(reference.Path); return Task.CompletedTask;
        }
    }
    private sealed class GitHubTransport : HttpMessageHandler
    {
        private readonly string pem = RSA.Create(2048).ExportRSAPrivateKeyPem();
        public int Conversions, RevokedTokens, AppDeletes;
        public bool WrongOwner, WrongInstallation, Deleted, WrongApp, ExtraPermission, Suspended;
        public Func<Task>? DuringVerification, DuringConversion;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            object body;
            if (path.Contains("/conversions"))
            {
                Conversions++;
                if (DuringConversion is not null) { var action = DuringConversion; DuringConversion = null; await action(); }
                body = new { id = 42, slug = "fixture-app", owner = new { id = 12, login = WrongOwner ? "attacker" : "fixture-owner", type = "User" },
                    pem, webhook_secret = "synthetic-webhook", client_id = "synthetic-client", client_secret = "synthetic-client-secret" };
            }
            else if (path == "/app/installations/900")
            {
                if (DuringVerification is not null) { var action = DuringVerification; DuringVerification = null; await action(); }
                if (Deleted) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var permissions = new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" };
                if (ExtraPermission) permissions["workflows"] = "write";
                body = new { id = 900, app_id = WrongApp ? 99 : 42, account = new { id = WrongInstallation ? 99 : 12, login = "fixture-owner", type = "User" },
                    suspended_at = Suspended ? "2026-01-01T00:00:00Z" : null, permissions };
            }
            else if (path.EndsWith("/access_tokens"))
            {
                using var tokenRequest = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Single(tokenRequest.RootElement.GetProperty("permissions").EnumerateObject());
                Assert.Equal("read", tokenRequest.RootElement.GetProperty("permissions").GetProperty("metadata").GetString());
                body = new { token = "synthetic-installation-token" };
            }
            else if (path == "/installation/repositories") body = new { repositories = new[] { new { id = 10, full_name = "fixture-owner/private-repo", @private = true, owner = new { id = 12 } } } };
            else if (path == "/installation/token") { RevokedTokens++; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            else { if (request.Method == HttpMethod.Delete) AppDeletes++; throw new InvalidOperationException("Unexpected GitHub request."); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
