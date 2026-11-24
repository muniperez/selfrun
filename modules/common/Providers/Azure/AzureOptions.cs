namespace Minicloud.Common.Providers.Azure;

public sealed record AzureOptions
{
    public const string SectionName = "Azure";

    public string TenantId { get; init; } = string.Empty;
    public string SubscriptionId { get; init; } = string.Empty;
    public string ResourceGroupName { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ManagementBaseUrl { get; init; } = "https://management.azure.com/";
    public string AuthorityBaseUrl { get; init; } = "https://login.microsoftonline.com/";
}
