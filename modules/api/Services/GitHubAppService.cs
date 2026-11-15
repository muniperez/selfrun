using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class GitHubAppService(MinicloudDbContext db, ISecretStore secrets, GitHubAppClient github,
    TimeProvider clock, IConfiguration configuration, IOptions<OpenBaoSecretStoreOptions> secretOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string NewState() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public async Task<GitHubAppStatus> StatusAsync(string organizationId, CancellationToken ct)
    {
        var connection = await db.GitHubAppConnections.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        return connection is null ? new(organizationId, "not_configured") : Status(connection);
    }

    public static GitHubAppStatus Status(GitHubAppConnection value) =>
        new(value.OrganizationId, value.Status, value.Id, value.OwnerLogin, value.OwnerType, value.AppId, value.AppSlug,
            value.InstallationId, value.FailureCode, value.LastVerifiedAt);

    public string SetupUrl(string organizationId) => Origin("Console:BaseUrl") + "/settings/github?organizationId=" + Uri.EscapeDataString(organizationId);

    public async Task<GitHubAppManifestResponse> BeginAsync(GitHubAppSetupRequest request, string userId, CancellationToken ct)
    {
        if (!Regex.IsMatch(request.OwnerLogin ?? "", "^[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$") ||
            request.OwnerType is not ("User" or "Organization"))
            throw new GitHubAppException("github_owner_invalid", "Choose a personal account or organization and its GitHub login.");
        var console = Origin("Console:BaseUrl");
        var api = Origin("GitHub:ApiUrl");
        if (await db.GitHubAppConnections.AnyAsync(x => x.OrganizationId == request.OrganizationId, ct))
            throw new GitHubAppException("github_setup_exists", "A registration already exists. Continue it or explicitly disconnect/replace it; never create a duplicate automatically.", 409);
        var state = NewState();
        var expires = clock.GetUtcNow().AddMinutes(30);
        var connection = new GitHubAppConnection
        {
            Id = Guid.NewGuid().ToString("N"), OrganizationId = request.OrganizationId,
            OwnerLogin = request.OwnerLogin!, OwnerType = request.OwnerType,
            SetupActorId = userId, SetupStateHash = Hash(state), SetupExpiresAtTicks = expires.UtcTicks,
            CredentialGeneration = Guid.NewGuid().ToString("N")
        };
        connection.SecretProvider = OpenBaoSecretStore.ProviderName;
        connection.SecretPath = OpenBaoSecretStore.IntegrationPathFor(secretOptions.Value.EnvironmentPrefix,
            connection.OrganizationId, connection.Id, connection.CredentialGeneration);
        db.GitHubAppConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        var post = request.OwnerType == "Organization"
            ? "https://github.com/organizations/" + Uri.EscapeDataString(request.OwnerLogin!) + "/settings/apps/new"
            : "https://github.com/settings/apps/new";
        var hostLabel = new Uri(console).Host.Replace('.', '-');
        return new(connection.Id, post + "?state=" + state, new
        {
            name = "Minicloud-" + hostLabel[..Math.Min(16, hostLabel.Length)] + "-" + connection.Id[..6],
            url = console,
            description = "Private GitHub connection for this Minicloud organization. The registering administrator owns this App.",
            @public = false,
            redirect_url = console + "/settings/github/callback?connectionId=" + connection.Id,
            setup_url = console + "/settings/github/installed?connectionId=" + connection.Id,
            setup_on_update = true,
            hook_attributes = new { url = api + "/api/github-apps/" + connection.Id + "/webhook", active = true },
            default_permissions = new { contents = "write", pull_requests = "write", metadata = "read" },
            // GitHub delivers installation lifecycle events automatically, not through manual subscriptions.
            default_events = Array.Empty<string>()
        }, expires);
    }

    public async Task<GitHubAppStatus> CompleteAsync(GitHubAppCompleteRequest request, string userId, CancellationToken ct)
    {
        var connection = await GetAsync(request.ConnectionId, ct);
        ValidateState(connection.SetupStateHash, connection.SetupActorId, connection.SetupExpiresAtTicks, request.State, userId);
        if (!Regex.IsMatch(request.Code ?? "", "^[a-zA-Z0-9]{1,512}$"))
            throw new GitHubAppException("github_code_invalid", "The manifest callback code is invalid.");
        var codeHash = Hash(request.Code!);
        if (connection.ConversionCodeHash is not null && connection.ConversionCodeHash != codeHash)
            throw new GitHubAppException("github_callback_replayed", "This registration already consumed a different callback.", 409);
        if (connection.Status is "app_created" or "connected" && connection.ConversionCodeHash == codeHash)
            return Status(connection);
        if (connection.Status == "converting")
        {
            try
            {
                var saved = JsonSerializer.Deserialize<GitHubAppCredentials>(await secrets.GetAsync(Reference(connection), ct), JsonOptions)
                    ?? throw GitHubAppClient.Unavailable();
                return await StoreMetadataAsync(connection, saved, ct);
            }
            catch (SecretStoreUnavailableException)
            {
                throw new GitHubAppException("github_conversion_uncertain",
                    "App creation may have completed but credentials could not be recovered. Check GitHub and restore OpenBao before explicitly replacing; do not retry App creation automatically.", 409);
            }
        }
        if (connection.Status != "setup_pending")
            throw new GitHubAppException("github_setup_not_pending", "This registration cannot consume a manifest callback.", 409);
        var claimed = await db.GitHubAppConnections.Where(x => x.Id == connection.Id && x.Status == "setup_pending"
            && x.SetupStateHash == connection.SetupStateHash).ExecuteUpdateAsync(
                setters => setters.SetProperty(x => x.Status, "converting").SetProperty(x => x.ConversionCodeHash, codeHash)
                    .SetProperty(x => x.Revision, x => x.Revision + 1), ct);
        if (claimed != 1) throw new GitHubAppException("github_conversion_pending", "Another callback is being processed. Inspect status; do not create another App.", 409);
        await db.Entry(connection).ReloadAsync(ct);
        if (connection.Status != "converting")
            throw new GitHubAppException("github_connection_changed", "Connection changed while processing; inspect its status.", 409);
        try
        {
            var credentials = await github.ConvertAsync(request.Code!, ct);
            ValidateOwner(connection, credentials);
            var reference = await secrets.PutAsync(new SecretStoreWriteRequest(connection.OrganizationId, "", "", "github-app",
                JsonSerializer.Serialize(credentials, JsonOptions))
                { IntegrationId = connection.Id, CredentialGeneration = connection.CredentialGeneration }, ct);
            connection.SecretProvider = reference.Provider; connection.SecretPath = reference.Path; connection.SecretVersion = reference.Version;
            return await StoreMetadataAsync(connection, credentials, ct);
        }
        catch (Exception ex) when (ex is GitHubAppException or SecretStoreUnavailableException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            // The one-use conversion may already have succeeded; never issue it a second time.
            connection.FailureCode = "github_conversion_uncertain";
            await db.SaveChangesAsync(ct);
            throw new GitHubAppException("github_conversion_uncertain",
                "Registration conversion could not be completed safely. Inspect GitHub, OpenBao and connection status before retrying or explicitly replacing.", 409);
        }
    }

    private async Task<GitHubAppStatus> StoreMetadataAsync(GitHubAppConnection connection, GitHubAppCredentials credentials, CancellationToken ct)
    {
        ValidateOwner(connection, credentials);
        if (connection.Status != "converting") throw new GitHubAppException("github_setup_not_pending", "Registration is not pending.", 409);
        connection.AppId = credentials.AppId; connection.AppSlug = credentials.Slug; connection.OwnerId = credentials.OwnerId;
        connection.Status = "app_created"; connection.FailureCode = null;
        await db.SaveChangesAsync(ct);
        return Status(connection);
    }

    public async Task<GitHubAppInstallationUrl> InstallationUrlAsync(string id, string userId, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct);
        if (connection.Status is not ("app_created" or "revoked" or "suspended" or "repositories_unverified"))
            throw new GitHubAppException("github_installation_not_pending", "Create or reconnect the App before installing.", 409);
        var state = NewState();
        var expires = clock.GetUtcNow().AddMinutes(30);
        connection.InstallationStateHash = Hash(state); connection.InstallationActorId = userId;
        connection.InstallationExpiresAtTicks = expires.UtcTicks;
        connection.InstallationStateUsed = false;
        await db.SaveChangesAsync(ct);
        return new("https://github.com/apps/" + connection.AppSlug + "/installations/new?state=" + state, expires);
    }

    public async Task<GitHubAppStatus> AssociateAsync(GitHubAppInstallRequest request, string userId, CancellationToken ct)
    {
        var connection = await GetAsync(request.ConnectionId, ct);
        ValidateState(connection.InstallationStateHash, connection.InstallationActorId, connection.InstallationExpiresAtTicks, request.State, userId);
        if (connection.InstallationStateUsed)
        {
            if (connection.InstallationId == request.InstallationId) return Status(connection);
            throw new GitHubAppException("github_callback_replayed", "This installation callback was already consumed.", 409);
        }
        if (connection.Status == "disconnected" || request.InstallationId <= 0)
            throw new GitHubAppException("github_installation_invalid", "Reconnect before associating an installation.", 409);
        if (connection.InstallationId is not null && connection.InstallationId != request.InstallationId && connection.Status == "connected")
            throw new GitHubAppException("github_installation_mismatch", "The callback cannot replace an already connected installation.", 403);
        var result = await VerifyAsync(connection, request.InstallationId, ct);
        connection.InstallationStateUsed = true;
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<GitHubAppStatus> ReconcileAsync(string id, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct);
        if (connection.Status is "disconnected" or "removing") throw new GitHubAppException("github_disconnected", "Explicitly reconnect before using GitHub.", 409);
        if (connection.InstallationId is null) return Status(connection);
        return await VerifyAsync(connection, connection.InstallationId.Value, ct);
    }

    private async Task<GitHubAppStatus> VerifyAsync(GitHubAppConnection connection, long installationId, CancellationToken ct)
    {
        try
        {
            var credentials = await CredentialsAsync(connection, ct);
            var installation = await github.VerifyInstallationAsync(credentials, installationId, ct);
            connection.InstallationId = installation.Id;
            connection.Status = installation.Suspended ? "suspended" : "connected";
            connection.RepositoriesJson = JsonSerializer.Serialize(installation.Repositories, JsonOptions);
            connection.LastVerifiedAt = clock.GetUtcNow(); connection.FailureCode = null;
            await db.SaveChangesAsync(ct);
            return Status(connection);
        }
        catch (GitHubAppException ex)
        {
            connection.Status = ex.Code == "github_access_revoked" ? "revoked" : "failed";
            connection.RepositoriesJson = "[]"; connection.FailureCode = ex.Code;
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<GitHubRepository>> RepositoriesAsync(string id, CancellationToken ct)
    {
        var status = await ReconcileAsync(id, ct);
        if (status.Status != "connected") return [];
        return JsonSerializer.Deserialize<List<GitHubRepository>>((await GetAsync(id, ct)).RepositoriesJson, JsonOptions) ?? [];
    }

    public async Task<GitHubAppStatus> DisconnectAsync(string id, bool reconnect, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct);
        if (reconnect)
        {
            if (connection.Status != "disconnected" || connection.AppId is null)
                throw new GitHubAppException("github_reconnect_invalid", "Only an existing disconnected App can reconnect.", 409);
            connection.Status = "app_created";
        }
        else
        {
            if (connection.Status == "removing") throw new GitHubAppException("github_removal_pending", "Credential removal is already pending.", 409);
            connection.Status = "disconnected"; connection.SetupStateHash = null; connection.InstallationStateHash = null;
            connection.RepositoriesJson = "[]";
        }
        await db.SaveChangesAsync(ct);
        if (reconnect && connection.InstallationId is not null) return await ReconcileAsync(id, ct);
        return Status(connection);
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct);
        if (connection.Status is not ("disconnected" or "removing"))
            throw new GitHubAppException("github_disconnect_required", "Disconnect before removing stored credentials. This does not delete the user-owned App on GitHub.", 409);
        connection.Status = "removing";
        await db.SaveChangesAsync(ct);
        await secrets.DeleteAsync(Reference(connection), ct);
        db.GitHubAppConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
    }

    public async Task WebhookAsync(string id, string signature, string delivery, string eventName, byte[] body, CancellationToken ct)
    {
        var connection = await GetAsync(id, ct);
        if (!Regex.IsMatch(delivery, "^[a-zA-Z0-9-]{1,100}$") || !Regex.IsMatch(signature, "^sha256=[a-fA-F0-9]{64}$"))
            throw new GitHubAppException("github_signature_invalid", "Webhook signature or delivery is invalid.", 401);
        var credentials = await CredentialsAsync(connection, ct, allowDisconnected: true);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(credentials.WebhookSecret), body);
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature[7..])))
            throw new GitHubAppException("github_signature_invalid", "Webhook signature is invalid.", 401);
        using var document = JsonDocument.Parse(body);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.GitHubWebhookReceipts.AnyAsync(x => x.ConnectionId == id && x.DeliveryId == delivery, ct)) return;
        if (connection.Status is not ("disconnected" or "removing") && eventName is "installation" or "installation_repositories")
        {
            var installation = document.RootElement.GetProperty("installation");
            if (connection.InstallationId == installation.GetProperty("id").GetInt64())
            {
                var action = document.RootElement.GetProperty("action").GetString();
                connection.Status = action switch
                {
                    "deleted" => "revoked",
                    "suspend" => "suspended",
                    _ => "repositories_unverified"
                };
                connection.RepositoriesJson = "[]";
            }
        }
        db.GitHubWebhookReceipts.Add(new() { ConnectionId = id, DeliveryId = delivery, ReceivedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<GitHubAppConnection> GetAsync(string id, CancellationToken ct) =>
        await db.GitHubAppConnections.SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new GitHubAppException("github_connection_not_found", "Connection was not found.", 404);

    private async Task<GitHubAppCredentials> CredentialsAsync(GitHubAppConnection connection, CancellationToken ct, bool allowDisconnected = false)
    {
        if ((!allowDisconnected && connection.Status is "disconnected" or "removing") || connection.Status == "setup_pending" || connection.AppId is null)
            throw new GitHubAppException("github_disconnected", "This connection is not enabled.", 409);
        var credentials = JsonSerializer.Deserialize<GitHubAppCredentials>(await secrets.GetAsync(Reference(connection), ct), JsonOptions)
            ?? throw GitHubAppClient.Unavailable();
        ValidateOwner(connection, credentials);
        if (credentials.AppId != connection.AppId) throw GitHubAppClient.Unavailable();
        return credentials;
    }

    private void ValidateState(string? hash, string? actor, long expires, string state, string userId)
    {
        if (!Regex.IsMatch(state ?? "", "^[a-f0-9]{64}$") || actor != userId || expires <= clock.GetUtcNow().UtcTicks ||
            hash is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Hash(state!))))
            throw new GitHubAppException("github_state_invalid", "Setup state is expired, already used, or belongs to another identity.", 403);
    }

    private static void ValidateOwner(GitHubAppConnection connection, GitHubAppCredentials credentials)
    {
        if (!string.Equals(connection.OwnerLogin, credentials.OwnerLogin, StringComparison.OrdinalIgnoreCase) || connection.OwnerType != credentials.OwnerType ||
            (connection.OwnerId is not null && connection.OwnerId != credentials.OwnerId))
            throw new GitHubAppException("github_owner_mismatch", "The created App does not belong to the intended GitHub owner.", 403);
    }

    private static StoredSecretReference Reference(GitHubAppConnection value) =>
        new(value.SecretProvider!, value.SecretPath!, value.SecretVersion);

    private string Origin(string key)
    {
        var value = configuration[key];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new GitHubAppException("github_canonical_url_required", "Configure canonical public HTTPS Console:BaseUrl and GitHub:ApiUrl before setup.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
