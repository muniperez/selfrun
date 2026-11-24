using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicloud.Common.Providers.Ovhcloud;

public sealed class OvhcloudOptions
{
    public const string SectionName = "Ovhcloud";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://eu.api.ovh.com/1.0/";
    public string DefaultRegion { get; set; } = "GRA11";
    public string DefaultImage { get; set; } = "Ubuntu 24.04";
    public string ProjectId { get; set; } = string.Empty;
    public string ApplicationKey { get; set; } = string.Empty;
    public string ApplicationSecret { get; set; } = string.Empty;
    public string ConsumerKey { get; set; } = string.Empty;

    /// <summary>
    /// Explicit commercial safety gate for dedicated/bare-metal server ordering and cancellation.
    /// Defaults to false so read-only bare-metal offer/inventory discovery works without risking
    /// unapproved commercial bare-metal commitments or terminations.
    /// </summary>
    public bool AllowBareMetalCommercialOrdering { get; set; }
}

public sealed record OvhcloudParsedCredentials(
    string ApplicationKey,
    string ApplicationSecret,
    string ConsumerKey,
    string ProjectId,
    string? Endpoint)
{
    private sealed class CredentialPayload
    {
        [JsonPropertyName("applicationKey")] public string? ApplicationKey { get; set; }
        [JsonPropertyName("applicationSecret")] public string? ApplicationSecret { get; set; }
        [JsonPropertyName("consumerKey")] public string? ConsumerKey { get; set; }
        [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
    }

    public static OvhcloudParsedCredentials Parse(string rawCredential, string? expectedProjectId = null)
    {
        if (string.IsNullOrWhiteSpace(rawCredential))
        {
            throw new ArgumentException("OVHcloud credential payload is required.", nameof(rawCredential));
        }

        var trimmed = rawCredential.Trim();
        if (!trimmed.StartsWith('{'))
        {
            throw new FormatException("OVHcloud credentials must be compact JSON with applicationKey, applicationSecret, consumerKey, and projectId.");
        }

        var payload = JsonSerializer.Deserialize<CredentialPayload>(trimmed)
            ?? throw new FormatException("OVHcloud credential JSON could not be parsed.");

        var projectId = string.IsNullOrWhiteSpace(payload.ProjectId) ? expectedProjectId?.Trim() : payload.ProjectId.Trim();
        if (string.IsNullOrWhiteSpace(payload.ApplicationKey)
            || string.IsNullOrWhiteSpace(payload.ApplicationSecret)
            || string.IsNullOrWhiteSpace(payload.ConsumerKey)
            || string.IsNullOrWhiteSpace(projectId))
        {
            throw new FormatException("OVHcloud credentials require applicationKey, applicationSecret, consumerKey, and projectId.");
        }

        if (!string.IsNullOrWhiteSpace(expectedProjectId)
            && !string.IsNullOrWhiteSpace(payload.ProjectId)
            && !string.Equals(expectedProjectId.Trim(), payload.ProjectId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OVHcloud credential projectId does not match the configured provider account project boundary.");
        }

        return new OvhcloudParsedCredentials(
            payload.ApplicationKey.Trim(),
            payload.ApplicationSecret.Trim(),
            payload.ConsumerKey.Trim(),
            projectId,
            payload.Endpoint?.Trim());
    }
}
