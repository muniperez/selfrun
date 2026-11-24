using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicloud.Common.Providers.Scaleway;

public sealed class ScalewayOptions
{
    public const string SectionName = "Scaleway";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.scaleway.com/";
    public string SecretKey { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string DefaultZone { get; set; } = "fr-par-1";
    public string DefaultImage { get; set; } = "ubuntu_noble";
}

public sealed record ScalewayParsedCredentials(
    string SecretKey,
    string ProjectId,
    string? DefaultZone)
{
    private sealed class CredentialPayload
    {
        [JsonPropertyName("secretKey")] public string? SecretKey { get; set; }
        [JsonPropertyName("projectId")] public string? ProjectId { get; set; }
        [JsonPropertyName("defaultZone")] public string? DefaultZone { get; set; }
    }

    public static ScalewayParsedCredentials Parse(string rawCredential, string? expectedProjectId = null)
    {
        if (string.IsNullOrWhiteSpace(rawCredential))
        {
            throw new ArgumentException("Scaleway credential payload is required.", nameof(rawCredential));
        }

        var trimmed = rawCredential.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (string.IsNullOrWhiteSpace(expectedProjectId))
            {
                throw new FormatException("Scaleway secret key requires an explicit Scaleway projectId.");
            }

            return new ScalewayParsedCredentials(trimmed, expectedProjectId.Trim(), null);
        }

        var payload = JsonSerializer.Deserialize<CredentialPayload>(trimmed)
            ?? throw new FormatException("Scaleway credential JSON could not be parsed.");

        var projectId = string.IsNullOrWhiteSpace(payload.ProjectId) ? expectedProjectId?.Trim() : payload.ProjectId.Trim();
        if (string.IsNullOrWhiteSpace(payload.SecretKey) || string.IsNullOrWhiteSpace(projectId))
        {
            throw new FormatException("Scaleway credential JSON requires secretKey and projectId.");
        }

        if (!string.IsNullOrWhiteSpace(expectedProjectId)
            && !string.IsNullOrWhiteSpace(payload.ProjectId)
            && !string.Equals(expectedProjectId.Trim(), payload.ProjectId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scaleway credential projectId does not match the configured provider account project boundary.");
        }

        return new ScalewayParsedCredentials(payload.SecretKey.Trim(), projectId, payload.DefaultZone?.Trim());
    }
}
