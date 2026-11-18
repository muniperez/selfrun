namespace Minicloud.Common.Models;

public sealed class DevelopmentWorkspaceGatewaySession
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public long Generation { get; set; }
    public string Service { get; set; } = "t3";
    public string? UserId { get; set; }
    public string? ApiKeyId { get; set; }
    public string LaunchDigest { get; set; } = default!;
    public string? SessionDigest { get; set; }
    public DateTimeOffset LaunchExpiresAt { get; set; }
    public DateTimeOffset SessionExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long Revision { get; set; }
}

public sealed class DevelopmentWorkspaceGatewayRoute
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public long Generation { get; set; }
    public string Service { get; set; } = "t3";
    public int LoopbackPort { get; set; }
    // Serialized StoredSecretReference, never the T3 bearer itself.
    public string? T3CredentialReference { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
