using System.Text.Json;
using System.Text.Json.Serialization;

namespace Selfrun.Common.Providers.GoogleCloud;

public sealed class GoogleCloudOptions
{
    public const string SectionName = "GoogleCloud";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://compute.googleapis.com/compute/v1/";
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
    public string DefaultZone { get; set; } = "us-central1-a";
    public string DefaultImage { get; set; } = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-amd64";
    public string ProjectId { get; set; } = string.Empty;
    public string? ClientEmail { get; set; }
    public string? PrivateKeyPem { get; set; }
    public string? AccessToken { get; set; }
    public string? CredentialsJson { get; set; }
}

public sealed record GoogleCloudParsedCredentials(
    string ProjectId,
    string? ClientEmail,
    string? PrivateKeyPem,
    string? TokenUri,
    string? AccessToken,
    string CredentialType)
{
    private sealed class CredentialPayload
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("projectId")] public string? ProjectIdCamel { get; set; }
        [JsonPropertyName("client_email")] public string? ClientEmail { get; set; }
        [JsonPropertyName("private_key")] public string? PrivateKey { get; set; }
        [JsonPropertyName("token_uri")] public string? TokenUri { get; set; }
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("accessToken")] public string? AccessTokenCamel { get; set; }
    }

    public static GoogleCloudParsedCredentials Parse(string rawCredential, string? expectedProjectId = null)
    {
        if (string.IsNullOrWhiteSpace(rawCredential))
        {
            throw new ArgumentException("Google Cloud credential payload is required.", nameof(rawCredential));
        }

        var trimmed = rawCredential.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (string.IsNullOrWhiteSpace(expectedProjectId))
            {
                throw new FormatException("GCP bearer token credentials require an explicit project ID.");
            }

            return new GoogleCloudParsedCredentials(expectedProjectId.Trim(), null, null, null, trimmed, "bearer_token");
        }

        var payload = JsonSerializer.Deserialize<CredentialPayload>(trimmed)
            ?? throw new FormatException("Google Cloud credential JSON could not be parsed.");
        if (payload.TokenUri is not null && payload.TokenUri != "https://oauth2.googleapis.com/token")
            throw new FormatException("Google Cloud account credentials must use the canonical token URI.");

        var payloadProjectId = !string.IsNullOrWhiteSpace(payload.ProjectId)
            ? payload.ProjectId.Trim()
            : payload.ProjectIdCamel?.Trim();
        var accessToken = !string.IsNullOrWhiteSpace(payload.AccessToken)
            ? payload.AccessToken.Trim()
            : payload.AccessTokenCamel?.Trim();

        var projectId = string.IsNullOrWhiteSpace(payloadProjectId) ? expectedProjectId?.Trim() : payloadProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new FormatException("Google Cloud credentials require a project_id or explicit account projectId.");
        }

        if (!string.IsNullOrWhiteSpace(expectedProjectId)
            && !string.IsNullOrWhiteSpace(payloadProjectId)
            && !string.Equals(expectedProjectId.Trim(), payloadProjectId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GCP credential project_id does not match the configured provider account project boundary.");
        }

        var credType = string.IsNullOrWhiteSpace(payload.Type) ? "service_account" : payload.Type.Trim();
        var hasServiceAccount = !string.IsNullOrWhiteSpace(payload.ClientEmail) && !string.IsNullOrWhiteSpace(payload.PrivateKey);
        var hasAccessToken = !string.IsNullOrWhiteSpace(accessToken);

        if (!hasServiceAccount && !hasAccessToken && !string.Equals(credType, "external_account", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("GCP credentials require service_account (client_email + private_key), external_account, or access_token.");
        }

        return new GoogleCloudParsedCredentials(
            projectId,
            payload.ClientEmail?.Trim(),
            payload.PrivateKey,
            payload.TokenUri?.Trim(),
            accessToken,
            credType);
    }
}
