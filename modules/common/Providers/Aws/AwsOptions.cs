using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicloud.Common.Providers.Aws;

public sealed class AwsOptions
{
    public const string SectionName = "Aws";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://ec2.{region}.amazonaws.com/";
    public string DefaultRegion { get; set; } = "us-east-1";
    public string DefaultImage { get; set; } = "ami-ubuntu-24.04-lts";
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string? SessionToken { get; set; }
    public string? AccountId { get; set; }
    public string? RoleArn { get; set; }
    public string? SubnetId { get; set; }
    public string? SecurityGroupId { get; set; }
}

public sealed record AwsParsedCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string? SessionToken,
    string? AccountId,
    string? RoleArn,
    string? Region,
    string? SubnetId,
    string? SecurityGroupId)
{
    private sealed class CredentialPayload
    {
        [JsonPropertyName("accessKeyId")] public string? AccessKeyId { get; set; }
        [JsonPropertyName("secretAccessKey")] public string? SecretAccessKey { get; set; }
        [JsonPropertyName("sessionToken")] public string? SessionToken { get; set; }
        [JsonPropertyName("accountId")] public string? AccountId { get; set; }
        [JsonPropertyName("roleArn")] public string? RoleArn { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("subnetId")] public string? SubnetId { get; set; }
        [JsonPropertyName("securityGroupId")] public string? SecurityGroupId { get; set; }
    }

    public static AwsParsedCredentials Parse(string rawCredential, string? expectedAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(rawCredential))
        {
            throw new ArgumentException("AWS credential payload is required.", nameof(rawCredential));
        }

        var trimmed = rawCredential.Trim();
        AwsParsedCredentials parsed;
        if (trimmed.StartsWith('{'))
        {
            var payload = JsonSerializer.Deserialize<CredentialPayload>(trimmed)
                ?? throw new FormatException("AWS credential JSON could not be parsed.");
            if (string.IsNullOrWhiteSpace(payload.AccessKeyId) || string.IsNullOrWhiteSpace(payload.SecretAccessKey))
            {
                throw new FormatException("AWS credential JSON requires non-empty accessKeyId and secretAccessKey.");
            }

            parsed = new AwsParsedCredentials(
                payload.AccessKeyId.Trim(),
                payload.SecretAccessKey.Trim(),
                string.IsNullOrWhiteSpace(payload.SessionToken) ? null : payload.SessionToken.Trim(),
                string.IsNullOrWhiteSpace(payload.AccountId) ? null : payload.AccountId.Trim(),
                string.IsNullOrWhiteSpace(payload.RoleArn) ? null : payload.RoleArn.Trim(),
                string.IsNullOrWhiteSpace(payload.Region) ? null : payload.Region.Trim(),
                string.IsNullOrWhiteSpace(payload.SubnetId) ? null : payload.SubnetId.Trim(),
                string.IsNullOrWhiteSpace(payload.SecurityGroupId) ? null : payload.SecurityGroupId.Trim());
        }
        else
        {
            var sep = trimmed.IndexOf(':');
            if (sep <= 0 || sep == trimmed.Length - 1)
            {
                throw new FormatException("AWS credentials must be compact JSON or 'accessKeyId:secretAccessKey'.");
            }

            parsed = new AwsParsedCredentials(
                trimmed[..sep].Trim(),
                trimmed[(sep + 1)..].Trim(),
                null,
                expectedAccountId,
                null,
                null,
                null,
                null);
        }

        if (!string.IsNullOrWhiteSpace(expectedAccountId)
            && !string.IsNullOrWhiteSpace(parsed.AccountId)
            && !string.Equals(expectedAccountId.Trim(), parsed.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AWS credential accountId does not match the configured provider account scope.");
        }

        return parsed;
    }
}
