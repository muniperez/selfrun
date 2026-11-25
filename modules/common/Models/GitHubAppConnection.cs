namespace Selfrun.Common.Models;

public sealed class GitHubAppConnection
{
    public string Id { get; set; } = "";
    public string OrganizationId { get; set; } = "";
    public string OwnerLogin { get; set; } = "";
    public string OwnerType { get; set; } = "";
    public string Status { get; set; } = "setup_pending";
    public string? SetupActorId { get; set; }
    public string? SetupStateHash { get; set; }
    public string? ConversionCodeHash { get; set; }
    public long SetupExpiresAtTicks { get; set; }
    public string? InstallationStateHash { get; set; }
    public string? InstallationActorId { get; set; }
    public long InstallationExpiresAtTicks { get; set; }
    public bool InstallationStateUsed { get; set; }
    public long Revision { get; set; }
    public long? AppId { get; set; }
    public long? OwnerId { get; set; }
    public string? AppSlug { get; set; }
    public long? InstallationId { get; set; }
    public string? SecretProvider { get; set; }
    public string? SecretPath { get; set; }
    public string? SecretVersion { get; set; }
    public string CredentialGeneration { get; set; } = "";
    public string RepositoriesJson { get; set; } = "[]";
    public string? FailureCode { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
}

public sealed class GitHubWebhookReceipt
{
    public string ConnectionId { get; set; } = "";
    public string DeliveryId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
}
