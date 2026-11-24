using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicloud.Common.Providers.Azure;

public sealed class AzureOptions
{
    public const string SectionName = "Azure";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://management.azure.com/";
    public string TokenEndpoint { get; set; } = "https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
    public string ApiVersion { get; set; } = "2024-07-01";
    public string DefaultLocation { get; set; } = "eastus";
    public string DefaultImage { get; set; } = "Canonical:ubuntu-24_04-lts:server:latest";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
}

public sealed record AzureParsedCredentials(
    string SubscriptionId,
    string ResourceGroup,
    string? TenantId,
    string? ClientId,
    string? ClientSecret,
    string? AccessToken,
    string? TokenEndpoint)
{
    private sealed class CredentialPayload
    {
        [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
        [JsonPropertyName("clientId")] public string? ClientId { get; set; }
        [JsonPropertyName("clientSecret")] public string? ClientSecret { get; set; }
        [JsonPropertyName("subscriptionId")] public string? SubscriptionId { get; set; }
        [JsonPropertyName("resourceGroup")] public string? ResourceGroup { get; set; }
        [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("tokenEndpoint")] public string? TokenEndpoint { get; set; }
    }

    public static AzureParsedCredentials Parse(string rawCredential, string? expectedScope = null)
    {
        if (string.IsNullOrWhiteSpace(rawCredential))
        {
            throw new ArgumentException("Azure credential payload is required.", nameof(rawCredential));
        }

        var (expectedSub, expectedRg) = SplitScope(expectedScope);
        var trimmed = rawCredential.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (string.IsNullOrWhiteSpace(expectedSub) || string.IsNullOrWhiteSpace(expectedRg))
            {
                throw new FormatException("Azure bearer token credentials require account scope 'subscriptionId/resourceGroup' or compact JSON.");
            }

            return new AzureParsedCredentials(expectedSub, expectedRg, null, null, null, trimmed, null);
        }

        var payload = JsonSerializer.Deserialize<CredentialPayload>(trimmed)
            ?? throw new FormatException("Azure credential JSON could not be parsed.");
        if (payload.TokenEndpoint is not null)
            throw new FormatException("Configure Azure token endpoints through trusted operator options, not account credentials.");

        var subscriptionId = string.IsNullOrWhiteSpace(payload.SubscriptionId) ? expectedSub : payload.SubscriptionId.Trim();
        var resourceGroup = string.IsNullOrWhiteSpace(payload.ResourceGroup) ? expectedRg : payload.ResourceGroup.Trim();

        if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(resourceGroup))
        {
            throw new FormatException("Azure credentials require subscriptionId and resourceGroup.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSub) && !string.Equals(expectedSub, subscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Azure credential subscriptionId does not match the configured account scope.");
        }

        if (!string.IsNullOrWhiteSpace(expectedRg) && !string.Equals(expectedRg, resourceGroup, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Azure credential resourceGroup does not match the configured account scope.");
        }

        var hasServicePrincipal = !string.IsNullOrWhiteSpace(payload.TenantId)
            && !string.IsNullOrWhiteSpace(payload.ClientId)
            && !string.IsNullOrWhiteSpace(payload.ClientSecret);
        var hasAccessToken = !string.IsNullOrWhiteSpace(payload.AccessToken);
        if (!hasServicePrincipal && !hasAccessToken)
        {
            throw new FormatException("Azure credentials require either service-principal (tenantId, clientId, clientSecret) or accessToken.");
        }

        return new AzureParsedCredentials(
            subscriptionId,
            resourceGroup,
            payload.TenantId?.Trim(),
            payload.ClientId?.Trim(),
            payload.ClientSecret?.Trim(),
            payload.AccessToken?.Trim(),
            payload.TokenEndpoint?.Trim());
    }

    public static (string? SubscriptionId, string? ResourceGroup) SplitScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return (null, null);
        }

        var trimmed = scope.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash > 0 && slash < trimmed.Length - 1)
        {
            return (trimmed[..slash].Trim(), trimmed[(slash + 1)..].Trim());
        }

        return (trimmed, null);
    }
}
