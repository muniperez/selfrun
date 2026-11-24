namespace Minicloud.Common.Providers.Aws;

public sealed record AwsCredentialBundle(string AccessKeyId, string SecretAccessKey, string? SessionToken = null)
{
    public static AwsCredentialBundle Parse(string value)
    {
        try
        {
            var credentials = System.Text.Json.JsonSerializer.Deserialize<AwsCredentialBundle>(value,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.AccessKeyId)
                || string.IsNullOrWhiteSpace(credentials.SecretAccessKey)
                || credentials.AccessKeyId.Any(char.IsControl)
                || credentials.SecretAccessKey.Any(char.IsControl)
                || credentials.SessionToken?.Any(char.IsControl) == true)
                throw new FormatException();
            return credentials;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new Minicloud.Common.Services.ProviderCatalogException("provider_credential_invalid",
                "AWS credentials must be a JSON object with accessKeyId, secretAccessKey, and an optional sessionToken.");
        }
        catch (FormatException)
        {
            throw new Minicloud.Common.Services.ProviderCatalogException("provider_credential_invalid",
                "AWS credentials must be a JSON object with accessKeyId, secretAccessKey, and an optional sessionToken.");
        }
    }
}

public sealed record AwsProviderSettings(string Region, string SubnetId, string SecurityGroupId);

public interface IProviderScopeIdentity
{
    Task<string> GetScopeIdentityAsync(CancellationToken cancellationToken);
}

public interface IAwsAccountValidator
{
    Task ValidateAccountConfigurationAsync(CancellationToken cancellationToken);
}
