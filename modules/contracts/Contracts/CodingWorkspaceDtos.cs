namespace Minicloud.Common.Contracts;

public sealed record CreateCodingWorkspaceRequest(
    string OrganizationId,
    string Name,
    string Plan,
    string Agent,
    string Repository,
    string SshKeyId);

public sealed record CodingWorkspaceResponse(
    string Id,
    string OrganizationId,
    string Name,
    string Plan,
    string Provider,
    string ProviderAccountId,
    string ProviderSshKeyId,
    string SshFingerprint,
    string Region,
    string Sku,
    string? ProviderServerId,
    string? IpAddress,
    bool AllocationReconciliationRequired,
    string Agent,
    string AgentVersion,
    string RequestedAgentVersion,
    string T3Version,
    string RequestedT3Version,
    string Repository,
    string Stage,
    string ProviderStatus,
    string BootstrapStatus,
    string T3Health,
    string CodexAuthentication,
    string GitHubAuthentication,
    string RepositoryStatus,
    string Connectivity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastObservedAt,
    DateTimeOffset? ProviderObservedAt,
    DateTimeOffset? BootstrapObservedAt,
    DateTimeOffset? T3ObservedAt,
    DateTimeOffset? CodexAuthenticationObservedAt,
    DateTimeOffset? GitHubAuthenticationObservedAt,
    DateTimeOffset? RepositoryObservedAt,
    string? LastErrorCode,
    string? LastError);

public sealed record CodingWorkspaceOperationResponse(
    string Id,
    string WorkspaceId,
    string Kind,
    string Status,
    string Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record CreateCodingWorkspaceResponse(
    CodingWorkspaceResponse Workspace,
    CodingWorkspaceOperationResponse Operation,
    bool Resumed);

public sealed record CodingWorkspaceAuthRequest(string Provider);

public sealed record CompleteCodingWorkspaceAuthRequest(
    string OperationId,
    bool Authenticated,
    bool RepositoryReady = false,
    string? AgentVersion = null,
    string? T3Version = null);

public sealed record StartCodingWorkspaceAuthRequest(string Provider);

public sealed record CodingWorkspaceAuthOperationResponse(
    CodingWorkspaceOperationResponse Operation,
    string Provider);
