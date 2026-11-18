using System.Text.Json;

namespace Minicloud.Common.Services;

public sealed partial class GitHubAppClient
{
    public async Task<WorkspaceGitCredential> IssueWorkspaceCredentialAsync(
        GitHubAppCredentials app, long installationId, string origin, CancellationToken ct)
    {
        if (!WorkspaceGitAccessService.IsCanonicalRepository(origin) || installationId <= 0) throw Denied();
        var uri = new Uri(origin);
        var fullName = uri.AbsolutePath[1..^4];
        var repositoryName = fullName.Split('/')[1];
        var jwt = Jwt(app);
        using var installationDocument = await SendAsync(HttpMethod.Get, $"app/installations/{installationId}", jwt, null, ct);
        var installation = installationDocument.RootElement;
        var account = installation.GetProperty("account");
        if (installation.GetProperty("id").GetInt64() != installationId
            || installation.GetProperty("app_id").GetInt64() != app.AppId
            || account.GetProperty("id").GetInt64() != app.OwnerId
            || !string.Equals(account.GetProperty("login").GetString(), app.OwnerLogin, StringComparison.OrdinalIgnoreCase)
            || account.GetProperty("type").GetString() != app.OwnerType
            || !fullName.StartsWith(app.OwnerLogin + "/", StringComparison.OrdinalIgnoreCase)
            || (installation.TryGetProperty("suspended_at", out var suspended) && suspended.ValueKind != JsonValueKind.Null)
            || !installation.GetProperty("permissions").TryGetProperty("contents", out var contents)
            || contents.GetString() != "write")
            throw Denied();

        // A selected-name metadata token verifies current installation access without any all-repository token.
        using var metadataDocument = await SendAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt,
            new { repositories = new[] { repositoryName }, permissions = new { metadata = "read" } }, ct);
        var metadataToken = metadataDocument.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(metadataToken)) throw Unavailable();
        long repositoryId;
        try
        {
            using var repositoryDocument = await SendAsync(HttpMethod.Get, "repos/" + fullName, metadataToken, null, ct);
            var repository = repositoryDocument.RootElement;
            repositoryId = repository.GetProperty("id").GetInt64();
            if (repositoryId <= 0 || repository.GetProperty("owner").GetProperty("id").GetInt64() != app.OwnerId
                || repository.GetProperty("full_name").GetString() != fullName
                || repository.GetProperty("clone_url").GetString() != origin)
                throw Denied();
        }
        finally
        {
            await RevokeWorkspaceCredentialAsync(metadataToken, CancellationToken.None);
        }

        var issuedAt = clock.GetUtcNow();
        using var tokenDocument = await SendAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt,
            new { repository_ids = new[] { repositoryId }, permissions = new { contents = "write" } }, ct);
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        try
        {
            var root = tokenDocument.RootElement;
            var permissions = root.GetProperty("permissions");
            if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)
                || !root.GetProperty("expires_at").TryGetDateTimeOffset(out var expires)
                || expires <= clock.GetUtcNow() || expires > issuedAt.AddHours(1)
                || !permissions.TryGetProperty("contents", out var granted) || granted.GetString() != "write"
                || permissions.EnumerateObject().Any(x => x.Name is not ("contents" or "metadata"))
                || (permissions.TryGetProperty("metadata", out var metadata) && metadata.GetString() != "read"))
                throw Denied();
            if (root.TryGetProperty("repositories", out var repositories)
                && (repositories.GetArrayLength() != 1 || repositories[0].GetProperty("id").GetInt64() != repositoryId))
                throw Denied();
            return new(origin, "x-access-token", token, expires.ToUnixTimeSeconds());
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(token)) await RevokeWorkspaceCredentialAsync(token, CancellationToken.None);
            throw;
        }
    }

    public async Task RevokeWorkspaceCredentialAsync(string token, CancellationToken ct)
    {
        // This exact endpoint rejects expired/already-revoked tokens with 401; other failures are not proof of revocation.
        using var revoked = await SendAsync(HttpMethod.Delete, "installation/token", token, null, ct, tokenRevocation: true);
    }

    private static GitHubAppException Denied() =>
        new("workspace_git_access_denied", "Selected repository GitHub App access is unavailable.", 403);
}
