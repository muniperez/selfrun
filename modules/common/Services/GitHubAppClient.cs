using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Common.Contracts;

namespace Minicloud.Common.Services;

public sealed class GitHubAppException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record GitHubAppCredentials(long AppId, string Slug, long OwnerId, string OwnerLogin,
    string OwnerType, string Pem, string WebhookSecret, string ClientId, string ClientSecret);
public sealed record VerifiedGitHubInstallation(long Id, bool Suspended, IReadOnlyList<GitHubRepository> Repositories);

public sealed class GitHubAppClient(HttpClient client, TimeProvider clock)
{
    public async Task<GitHubAppCredentials> ConvertAsync(string code, CancellationToken ct)
    {
        using var document = await SendAsync(HttpMethod.Post, "app-manifests/" + Uri.EscapeDataString(code) + "/conversions", null, null, ct);
        var root = document.RootElement;
        var owner = root.GetProperty("owner");
        var credentials = new GitHubAppCredentials(root.GetProperty("id").GetInt64(), root.GetProperty("slug").GetString()!,
            owner.GetProperty("id").GetInt64(), owner.GetProperty("login").GetString()!, owner.GetProperty("type").GetString()!,
            root.GetProperty("pem").GetString()!, root.GetProperty("webhook_secret").GetString()!,
            root.GetProperty("client_id").GetString()!, root.GetProperty("client_secret").GetString()!);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(credentials.Pem);
        if (credentials.AppId <= 0 || credentials.OwnerId <= 0 || rsa.KeySize < 2048 ||
            string.IsNullOrEmpty(credentials.WebhookSecret) || string.IsNullOrEmpty(credentials.ClientSecret) ||
            !System.Text.RegularExpressions.Regex.IsMatch(credentials.Slug, "^[a-zA-Z0-9-]{1,100}$"))
            throw Unavailable();
        return credentials;
    }

    public async Task<VerifiedGitHubInstallation> VerifyInstallationAsync(
        GitHubAppCredentials app, long installationId, CancellationToken ct)
    {
        var jwt = Jwt(app);
        using var document = await SendAsync(HttpMethod.Get, $"app/installations/{installationId}", jwt, null, ct);
        var installation = document.RootElement;
        var account = installation.GetProperty("account");
        if (installation.GetProperty("id").GetInt64() != installationId ||
            installation.GetProperty("app_id").GetInt64() != app.AppId ||
            account.GetProperty("id").GetInt64() != app.OwnerId ||
            !string.Equals(account.GetProperty("login").GetString(), app.OwnerLogin, StringComparison.OrdinalIgnoreCase) ||
            account.GetProperty("type").GetString() != app.OwnerType)
            throw new GitHubAppException("github_installation_mismatch", "The installation does not belong to this App and intended owner.", 403);
        if (installation.TryGetProperty("suspended_at", out var suspended) && suspended.ValueKind != JsonValueKind.Null)
            return new(installationId, true, []);
        var permissions = installation.GetProperty("permissions");
        if (!permissions.TryGetProperty("contents", out var contents) || contents.GetString() != "write" ||
            !permissions.TryGetProperty("pull_requests", out var pulls) || pulls.GetString() != "write" ||
            permissions.EnumerateObject().Any(x => x.Name is not ("contents" or "pull_requests" or "metadata")))
            throw new GitHubAppException("github_permissions_changed", "Grant only Contents and Pull requests read/write and mandatory Metadata read access.");

        using var tokenDocument = await SendAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt,
            new { permissions = new { metadata = "read" } }, ct);
        var token = tokenDocument.RootElement.GetProperty("token").GetString() ?? throw Unavailable();
        try
        {
            var repositories = new List<GitHubRepository>();
            for (var page = 1; page <= 100; page++)
            {
                using var repos = await SendAsync(HttpMethod.Get, $"installation/repositories?per_page=100&page={page}", token, null, ct);
                var batch = repos.RootElement.GetProperty("repositories");
                foreach (var repo in batch.EnumerateArray())
                {
                    if (repo.GetProperty("owner").GetProperty("id").GetInt64() != app.OwnerId)
                        throw new GitHubAppException("github_installation_mismatch", "Repository ownership differs from the intended owner.", 403);
                    repositories.Add(new(repo.GetProperty("id").GetInt64(), repo.GetProperty("full_name").GetString()!,
                        repo.GetProperty("private").GetBoolean()));
                }
                if (batch.GetArrayLength() < 100) return new(installationId, false, repositories);
            }
            throw new GitHubAppException("github_repository_limit", "This installation exceeds the supported repository-listing limit.");
        }
        finally
        {
            try { using var revoked = await SendAsync(HttpMethod.Delete, "installation/token", token, null, ct); }
            catch (Exception ex) when (ex is GitHubAppException or OperationCanceledException) { }
        }
    }

    private string Jwt(GitHubAppCredentials app)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var header = Encode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iat = now - 60, exp = now + 480, iss = app.AppId.ToString(CultureInfo.InvariantCulture) }));
        var message = header + "." + payload;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(app.Pem);
        return message + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct,
        bool tokenRevocation = false)
    {
        using var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
        request.Headers.UserAgent.ParseAdd("Minicloud-GitHub-App/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (tokenRevocation)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) return JsonDocument.Parse("{}");
                if (response.StatusCode != HttpStatusCode.NoContent) throw Unavailable();
            }
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => new GitHubAppException("github_access_revoked",
                        "GitHub denied access. Check App credentials, installation ownership and any organization approval requirements.", 409),
                    HttpStatusCode.Forbidden => new GitHubAppException("github_approval_required",
                        "GitHub denied permission. An organization owner may need to approve this operation.", 403),
                    _ => Unavailable()
                };
            if (response.StatusCode == HttpStatusCode.NoContent) return JsonDocument.Parse("{}");
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw Unavailable();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > 4 * 1024 * 1024) throw Unavailable();
                buffer.Write(chunk, 0, count);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            throw Unavailable();
        }
    }

    public static GitHubAppException Unavailable() =>
        new("github_unavailable", "GitHub is temporarily unavailable. Inspect status and reconcile; do not create another App automatically.", 503);
}
