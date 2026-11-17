namespace Minicloud.Common.Contracts;

public sealed record GitHubAppStatus(string OrganizationId, string Status, string? ConnectionId = null,
    string? OwnerLogin = null, string? OwnerType = null, long? AppId = null, string? AppSlug = null,
    long? InstallationId = null, string? FailureCode = null, DateTimeOffset? LastVerifiedAt = null);
public sealed record GitHubAppSetupRequest(string OrganizationId, string OwnerLogin, string OwnerType);
public sealed record GitHubAppManifestResponse(string ConnectionId, string PostUrl, object Manifest, DateTimeOffset ExpiresAt);
public sealed record GitHubAppCompleteRequest(string ConnectionId, string State, string Code);
public sealed record GitHubAppInstallRequest(string ConnectionId, string State, long InstallationId);
public sealed record GitHubAppInstallationUrl(string Url, DateTimeOffset ExpiresAt);
public sealed record GitHubRepository(long Id, string FullName, bool Private);
public sealed record GitHubAppSetupUrl(string Url);
