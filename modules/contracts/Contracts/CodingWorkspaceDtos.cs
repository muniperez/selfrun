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
    string? ProviderServerId,
    string? IpAddress,
    string Agent,
    string AgentVersion,
    string T3Version,
    string Repository,
    string Stage,
    string Connectivity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastObservedAt,
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

public sealed record CodingWorkspaceAuthResponse(
    string WorkspaceId,
    string Provider,
    string Status,
    string? AuthorizationUrl,
    string? UserCode,
    DateTimeOffset? ExpiresAt);
