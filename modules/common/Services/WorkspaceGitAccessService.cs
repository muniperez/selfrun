using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;

namespace Minicloud.Common.Services;

public sealed class WorkspaceGitCredential(string origin, string username, string password, long expiresAtUnixSeconds)
{
    public string Origin { get; } = origin;
    public string Username { get; } = username;
    public string Password { get; } = password;
    public long ExpiresAtUnixSeconds { get; } = expiresAtUnixSeconds;
    public override string ToString() => "WorkspaceGitCredential { [REDACTED] }";
}

public sealed class WorkspaceGitAccessService(MinicloudDbContext db, ISecretStore secrets, GitHubAppClient github,
    TimeProvider clock, IOptions<DevelopmentWorkspaceOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsCanonicalRepository(string repository) =>
        Regex.IsMatch(repository, @"\Ahttps://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]+\.git\z",
            RegexOptions.CultureInvariant) && !repository.Contains("/..", StringComparison.Ordinal);

    public async Task<WorkspaceGitCredential> IssueAsync(string organizationId, string canonicalRepoUrl,
        string serializedAuthorityRef, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(canonicalRepoUrl, UriKind.Absolute, out var origin) || origin.Scheme != "https"
                || !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query)
                || !string.IsNullOrEmpty(origin.Fragment)) throw Denied();
            var policies = options.Value.Repositories.Where(x => x.OrganizationId == organizationId
                && x.Repository == canonicalRepoUrl && x.AllowPush).ToArray();
            if (policies.Length != 1) throw Denied();
            var policy = policies[0];
            var reference = JsonSerializer.Deserialize<StoredSecretReference>(serializedAuthorityRef, JsonOptions) ?? throw Denied();
            if (string.IsNullOrWhiteSpace(reference.Path)) throw Denied();
            if (reference.Provider == "openbao")
                return await ReadSelectedSecretAsync(policy, organizationId, canonicalRepoUrl, reference, ct);
            if (reference.Provider != "github-app" || !IsCanonicalRepository(canonicalRepoUrl) || policy.Credential is not null
                || string.IsNullOrWhiteSpace(policy.GitHubConnectionId)
                || reference.Path != policy.GitHubConnectionId || reference.Version != canonicalRepoUrl)
                throw Denied();
            var connection = await db.GitHubAppConnections.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == reference.Path && x.OrganizationId == organizationId && x.Status == "connected", ct) ?? throw Denied();
            if (connection.AppId is not > 0 || connection.InstallationId is not > 0 || connection.OwnerId is not > 0
                || connection.LastVerifiedAt is null || connection.SecretProvider != "openbao"
                || string.IsNullOrWhiteSpace(connection.SecretPath) || string.IsNullOrWhiteSpace(connection.SecretVersion))
                throw Denied();
            var app = JsonSerializer.Deserialize<GitHubAppCredentials>(await secrets.GetAsync(
                new(connection.SecretProvider, connection.SecretPath, connection.SecretVersion), ct), JsonOptions) ?? throw Denied();
            if (app.AppId != connection.AppId || app.OwnerId != connection.OwnerId
                || app.OwnerLogin != connection.OwnerLogin || app.OwnerType != connection.OwnerType)
                throw Denied();
            var credential = await github.IssueWorkspaceCredentialAsync(app, connection.InstallationId.Value, canonicalRepoUrl, ct);
            if (!await db.GitHubAppConnections.AsNoTracking().AnyAsync(x => x.Id == connection.Id
                && x.OrganizationId == organizationId && x.Status == "connected" && x.Revision == connection.Revision, ct))
            {
                await github.RevokeWorkspaceCredentialAsync(credential.Password, ct);
                throw Denied();
            }
            return credential;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is GitHubAppException or SecretStoreUnavailableException or JsonException
            or CryptographicException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            // Upstream errors and secret-store exceptions may contain credentials; do not retain them as inner exceptions.
            throw Denied();
        }
    }

    private async Task<WorkspaceGitCredential> ReadSelectedSecretAsync(DevelopmentWorkspaceRepositoryPolicy policy,
        string organizationId, string origin, StoredSecretReference reference, CancellationToken ct)
    {
        if (policy.GitHubConnectionId is not null || policy.Credential != reference
            || policy.CredentialExpiresAt is null || policy.CredentialExpiresAt <= clock.GetUtcNow()
            || policy.CredentialExpiresAt > clock.GetUtcNow().AddHours(1))
            throw Denied();
        var path = reference.Path.Split('/');
        if (path.Length != 8 || path.Any(string.IsNullOrWhiteSpace) || reference.Path.Contains("..", StringComparison.Ordinal)
            || path[0] != "env" || path[2] != "orgs" || path[3] != Uri.EscapeDataString(organizationId)
            || path[4] != "integrations" || path[6] != "credentials"
            || !path[5].StartsWith("workspace-repository-", StringComparison.Ordinal)
            || path[5].Length == "workspace-repository-".Length
            || path[5].Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw Denied();
        using var document = JsonDocument.Parse(await secrets.GetAsync(reference, ct));
        var value = document.RootElement;
        var expiry = value.GetProperty("expiresAt").GetInt64();
        var username = value.GetProperty("username").GetString();
        var password = value.GetProperty("password").GetString();
        if (value.GetProperty("origin").GetString() != origin || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password) || username.Any(char.IsControl) || password.Any(char.IsControl)
            || expiry <= clock.GetUtcNow().ToUnixTimeSeconds()
            || expiry > policy.CredentialExpiresAt.Value.ToUnixTimeSeconds())
            throw Denied();
        return new(origin, username, password, expiry);
    }

    private static DevelopmentWorkspaceException Denied() =>
        new("workspace_git_access_denied", "Selected repository Git credentials are unavailable or no longer authorized.");
}
