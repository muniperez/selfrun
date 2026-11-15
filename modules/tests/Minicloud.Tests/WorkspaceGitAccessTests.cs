using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class WorkspaceGitAccessTests : IAsyncLifetime
{
    private const string Origin = "https://github.com/fixture-owner/private-repo.git";
    private const string SecretToken = "synthetic-workspace-token";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly Clock clock = new();
    private readonly SecretStore secrets = new();
    private readonly GitHubHandler handler = new();
    private readonly DevelopmentWorkspaceOptions policy = new()
    {
        Repositories = [new() { OrganizationId = "org", Repository = Origin, AllowPush = true,
            GitHubConnectionId = "connection", AllowedBaseRefs = ["refs/heads/main"] }]
    };
    private MinicloudDbContext db = default!;
    private HttpClient http = default!;
    private GitHubAppConnection Connection => db.GitHubAppConnections.Local.Single();
    private string Authority => JsonSerializer.Serialize(new StoredSecretReference("github-app", "connection", Origin));
    private WorkspaceGitAccessService Service => new(db, secrets, new(http, clock), clock, Options.Create(policy));

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.AddRange(new Organization { Id = "org", Name = "Org", Slug = "org" },
            new Organization { Id = "other", Name = "Other", Slug = "other" });
        db.GitHubAppConnections.Add(new()
        {
            Id = "connection", OrganizationId = "org", Status = "connected", AppId = 42, OwnerId = 7,
            OwnerLogin = "fixture-owner", OwnerType = "Organization", InstallationId = 900,
            LastVerifiedAt = clock.GetUtcNow(), SecretProvider = "openbao",
            SecretPath = "env/test/orgs/org/integrations/connection/credentials/generation", SecretVersion = "1"
        });
        await db.SaveChangesAsync();
        using var rsa = RSA.Create(2048);
        secrets.Value = JsonSerializer.Serialize(new GitHubAppCredentials(42, "fixture", 7, "fixture-owner",
            "Organization", rsa.ExportRSAPrivateKeyPem(), "synthetic-webhook", "synthetic-client", "synthetic-secret"));
        handler.ExpiresAt = clock.GetUtcNow().AddHours(1);
        http = new(handler);
    }

    [Fact]
    public async Task FreshAuthorizationMintsOnlySelectedRepositoryContentsWriteAndReturnsActualExpiry()
    {
        var result = await Service.IssueAsync("org", Origin, Authority, default);
        Assert.Equal(Origin, result.Origin);
        Assert.Equal("x-access-token", result.Username);
        Assert.Equal(SecretToken, result.Password);
        Assert.Equal(handler.ExpiresAt.ToUnixTimeSeconds(), result.ExpiresAtUnixSeconds);
        Assert.DoesNotContain(SecretToken, result.ToString());
        Assert.Equal(new[] { "installation", "metadata-token", "repository", "revoke-metadata", "write-token" }, handler.Calls);
        using var metadata = JsonDocument.Parse(handler.MetadataBody!);
        Assert.Equal("private-repo", Assert.Single(metadata.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
        Assert.Equal("read", metadata.RootElement.GetProperty("permissions").GetProperty("metadata").GetString());
        Assert.Single(metadata.RootElement.GetProperty("permissions").EnumerateObject());
        using var write = JsonDocument.Parse(handler.WriteBody!);
        Assert.Equal(123, Assert.Single(write.RootElement.GetProperty("repository_ids").EnumerateArray()).GetInt64());
        Assert.False(write.RootElement.TryGetProperty("repositories", out _));
        Assert.Equal("write", write.RootElement.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Single(write.RootElement.GetProperty("permissions").EnumerateObject());
        Assert.DoesNotContain(SecretToken, JsonSerializer.Serialize(await db.GitHubAppConnections.SingleAsync()));
    }

    [Theory]
    [InlineData("wrong-org")]
    [InlineData("disconnected")]
    [InlineData("unverified")]
    [InlineData("wrong-reference")]
    [InlineData("wrong-origin")]
    [InlineData("wrong-provider")]
    [InlineData("not-opted-in")]
    [InlineData("push-disabled")]
    [InlineData("foreign-credentials")]
    public async Task AuthorityAndExplicitOrganizationPolicyCannotBeSubstituted(string mutation)
    {
        var authority = Authority;
        switch (mutation)
        {
            case "wrong-org": Connection.OrganizationId = "other"; break;
            case "disconnected": Connection.Status = "disconnected"; break;
            case "unverified": Connection.LastVerifiedAt = null; break;
            case "wrong-reference": authority = JsonSerializer.Serialize(new StoredSecretReference("github-app", "foreign", Origin)); break;
            case "wrong-origin": authority = JsonSerializer.Serialize(new StoredSecretReference("github-app", "connection", Origin.Replace("private-repo", "other"))); break;
            case "wrong-provider": authority = JsonSerializer.Serialize(new StoredSecretReference("other", "connection", Origin)); break;
            case "not-opted-in": policy.Repositories[0].GitHubConnectionId = null; break;
            case "push-disabled": policy.Repositories[0].AllowPush = false; break;
            case "foreign-credentials": Connection.OwnerId = 8; break;
        }
        await db.SaveChangesAsync();
        await Denied(() => Service.IssueAsync("org", Origin, authority, default));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("suspended")]
    [InlineData("permissions")]
    [InlineData("wrong-installation")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-owner")]
    public async Task CurrentGitHubAuthorizationIsVerifiedBeforeWriteTokenMint(string failure)
    {
        handler.Failure = failure;
        await Denied(() => Service.IssueAsync("org", Origin, Authority, default));
        Assert.DoesNotContain("write-token", handler.Calls);
        if (failure is "wrong-repository" or "wrong-owner") Assert.Contains("revoke-metadata", handler.Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(3601, false)]
    [InlineData(3600, true)]
    public async Task UsesActualGitHubExpiryAndRejectsExpiredOrLongLivedTokens(int seconds, bool allowed)
    {
        handler.ExpiresAt = clock.GetUtcNow().AddSeconds(seconds);
        if (allowed)
            Assert.Equal(handler.ExpiresAt.ToUnixTimeSeconds(), (await Service.IssueAsync("org", Origin, Authority, default)).ExpiresAtUnixSeconds);
        else
        {
            await Denied(() => Service.IssueAsync("org", Origin, Authority, default));
            Assert.Contains("revoke-write", handler.Calls);
        }
    }

    [Theory]
    [InlineData("broader-token")]
    [InlineData("wrong-token-repository")]
    [InlineData("malformed")]
    [InlineData("http-error")]
    [InlineData("transport-error")]
    [InlineData("secret-store-error")]
    public async Task SensitiveErrorsAreRedactedAndBroaderReturnedTokensRejected(string failure)
    {
        handler.Failure = failure;
        secrets.Fail = failure == "secret-store-error";
        var error = await Denied(() => Service.IssueAsync("org", Origin, Authority, default));
        Assert.DoesNotContain(SecretToken, error.ToString());
        Assert.Null(error.InnerException);
        if (failure is "broader-token" or "wrong-token-repository") Assert.Contains("revoke-write", handler.Calls);
    }

    [Fact]
    public async Task DisconnectDuringIssuanceRevokesNewCredentialAndStopsRenewal()
    {
        handler.BeforeWriteResponse = async () => { Connection.Status = "disconnected"; await db.SaveChangesAsync(); };
        await Denied(() => Service.IssueAsync("org", Origin, Authority, default));
        Assert.Contains("revoke-write", handler.Calls);
        var calls = handler.Calls.Count;
        await Denied(() => Service.IssueAsync("org", Origin, Authority, default));
        Assert.Equal(calls, handler.Calls.Count);
    }

    [Fact]
    public async Task OpenBaoRetainsExactSelectedReferenceNamespaceAndExpiryGuards()
    {
        var selected = policy.Repositories[0];
        selected.GitHubConnectionId = null;
        selected.Credential = new("openbao", "env/test/orgs/org/integrations/workspace-repository-selected/credentials/generation", "1");
        selected.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(30);
        secrets.Value = JsonSerializer.Serialize(new { origin = Origin, username = "x-access-token", password = SecretToken,
            expiresAt = selected.CredentialExpiresAt.Value.ToUnixTimeSeconds() });
        var authority = JsonSerializer.Serialize(selected.Credential);
        Assert.Equal(SecretToken, (await Service.IssueAsync("org", Origin, authority, default)).Password);
        var reads = secrets.Reads;
        await Denied(() => Service.IssueAsync("org", Origin, authority.Replace("selected", "other"), default));
        Assert.Equal(reads, secrets.Reads);
        selected.CredentialExpiresAt = clock.GetUtcNow().AddHours(2);
        await Denied(() => Service.IssueAsync("org", Origin, authority, default));
        selected.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(-1);
        await Denied(() => Service.IssueAsync("org", Origin, authority, default));
        selected.CredentialExpiresAt = clock.GetUtcNow().AddMinutes(30);
        selected.Credential = selected.Credential with { Path = selected.Credential.Path.Replace("/orgs/org/", "/orgs/other/") };
        await Denied(() => Service.IssueAsync("org", Origin, JsonSerializer.Serialize(selected.Credential), default));
        Assert.Empty(handler.Calls);
    }

    private static async Task<DevelopmentWorkspaceException> Denied(Func<Task<WorkspaceGitCredential>> action)
    {
        var error = await Assert.ThrowsAsync<DevelopmentWorkspaceException>(action);
        Assert.Equal("workspace_git_access_denied", error.Code);
        return error;
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-15T10:00:00Z");
    }

    private sealed class SecretStore : ISecretStore
    {
        public string Value { get; set; } = "";
        public bool Fail { get; set; }
        public int Reads { get; private set; }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct)
        {
            Reads++;
            if (Fail) throw new SecretStoreUnavailableException(SecretToken);
            return Task.FromResult(Value);
        }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? MetadataBody { get; private set; }
        public string? WriteBody { get; private set; }
        public string? Failure { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public Func<Task>? BeforeWriteResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var path = request.RequestUri.AbsolutePath;
            if (Failure == "transport-error") throw new HttpRequestException(SecretToken);
            if (Failure == "http-error") return new(HttpStatusCode.Forbidden) { Content = new StringContent(SecretToken) };
            if (Failure == "malformed") return Json(new { error = SecretToken });
            if (request.Method == HttpMethod.Get && path == "/app/installations/900")
            {
                Calls.Add("installation");
                return Json(new { id = Failure == "wrong-installation" ? 901 : 900, app_id = 42,
                    account = new { id = 7, login = "fixture-owner", type = "Organization" },
                    suspended_at = Failure == "suspended" ? "2026-11-15T00:00:00Z" : null,
                    permissions = new { contents = Failure == "permissions" ? "read" : "write", pull_requests = "write", metadata = "read" } });
            }
            if (request.Method == HttpMethod.Post && path == "/app/installations/900/access_tokens")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("repositories", out _))
                {
                    Calls.Add("metadata-token"); MetadataBody = body;
                    if (Failure == "removed") return new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(SecretToken) };
                    return Json(new { token = "synthetic-metadata-token" });
                }
                Calls.Add("write-token"); WriteBody = body;
                if (BeforeWriteResponse is not null) await BeforeWriteResponse();
                return Json(new { token = SecretToken, expires_at = ExpiresAt,
                    permissions = Failure == "broader-token"
                        ? new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write" }
                        : new Dictionary<string, string> { ["contents"] = "write", ["metadata"] = "read" },
                    repositories = new[] { new { id = Failure == "wrong-token-repository" ? 999 : 123 } } });
            }
            if (request.Method == HttpMethod.Get && path == "/repos/fixture-owner/private-repo")
            {
                Calls.Add("repository");
                Assert.Equal("synthetic-metadata-token", request.Headers.Authorization?.Parameter);
                return Json(new { id = 123, full_name = "fixture-owner/private-repo",
                    clone_url = Failure == "wrong-repository" ? Origin.Replace("private-repo", "other") : Origin,
                    owner = new { id = Failure == "wrong-owner" ? 8 : 7 } });
            }
            if (request.Method == HttpMethod.Delete && path == "/installation/token")
            {
                Calls.Add(request.Headers.Authorization?.Parameter == SecretToken ? "revoke-write" : "revoke-metadata");
                return new(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected mocked GitHub request.");
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
