using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Selfrun.Common.Models;
using Selfrun.Common.Providers.Aws;
using Selfrun.Common.Providers.Azure;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.GoogleCloud;
using Selfrun.Common.Providers.Linode;
using Selfrun.Common.Providers.Ovhcloud;
using Selfrun.Common.Providers.Scaleway;
using Selfrun.Common.Services;

namespace Selfrun.Common.Providers.Extensibility;

internal static class ExpansionProviderCredentialValidators
{
    public static ProviderCredentialValidationResult ValidateAws(ProviderAccount account, string credential) =>
        Validate(() => AwsParsedCredentials.Parse(credential, account.ProjectId));

    public static ProviderCredentialValidationResult ValidateAzure(ProviderAccount account, string credential) =>
        Validate(() => AzureParsedCredentials.Parse(credential, account.ProjectId));

    public static ProviderCredentialValidationResult ValidateGoogleCloud(ProviderAccount account, string credential) =>
        Validate(() => GoogleCloudParsedCredentials.Parse(credential, account.ProjectId));

    public static ProviderCredentialValidationResult ValidateOvhcloud(ProviderAccount account, string credential) =>
        Validate(() => OvhcloudParsedCredentials.Parse(credential, account.ProjectId));

    public static ProviderCredentialValidationResult ValidateScaleway(ProviderAccount account, string credential) =>
        Validate(() => ScalewayParsedCredentials.Parse(credential, account.ProjectId));

    private static ProviderCredentialValidationResult Validate(Action parse)
    {
        try
        {
            parse();
            return ProviderCredentialValidationResult.Valid();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return ProviderCredentialValidationResult.Invalid(
                "provider_credential_invalid",
                TokenRedactor.Redact(ex.Message));
        }
    }
}

public sealed class AwsProviderRegistration(IOptions<AwsOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Aws;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context)
    {
        AwsParsedCredentials parsed;
        try { parsed = AwsParsedCredentials.Parse(context.Credential, context.Account.ProjectId); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_credential_invalid", TokenRedactor.Redact(ex.Message));
        }

        return new AwsClient(context.HttpClient, new AwsOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = options.Value.BaseUrl,
            DefaultRegion = parsed.Region ?? options.Value.DefaultRegion,
            DefaultImage = options.Value.DefaultImage,
            AccessKeyId = parsed.AccessKeyId,
            SecretAccessKey = parsed.SecretAccessKey,
            SessionToken = parsed.SessionToken,
            AccountId = parsed.AccountId ?? context.Account.ProjectId ?? options.Value.AccountId,
            RoleArn = parsed.RoleArn ?? options.Value.RoleArn,
            SubnetId = parsed.SubnetId ?? options.Value.SubnetId,
            SecurityGroupId = parsed.SecurityGroupId ?? options.Value.SecurityGroupId
        }, context.LoggerFactory.CreateLogger<AwsClient>());
    }
}

public sealed class AzureProviderRegistration(IOptions<AzureOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Azure;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context)
    {
        AzureParsedCredentials parsed;
        try { parsed = AzureParsedCredentials.Parse(context.Credential, context.Account.ProjectId); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_credential_invalid", TokenRedactor.Redact(ex.Message));
        }

        return new AzureClient(context.HttpClient, new AzureOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = options.Value.BaseUrl,
            TokenEndpoint = options.Value.TokenEndpoint,
            ApiVersion = options.Value.ApiVersion,
            DefaultLocation = options.Value.DefaultLocation,
            DefaultImage = options.Value.DefaultImage,
            SubscriptionId = parsed.SubscriptionId,
            ResourceGroup = parsed.ResourceGroup,
            TenantId = parsed.TenantId ?? string.Empty,
            ClientId = parsed.ClientId ?? string.Empty,
            ClientSecret = parsed.ClientSecret ?? string.Empty,
            AccessToken = parsed.AccessToken
        }, context.LoggerFactory.CreateLogger<AzureClient>());
    }
}

public sealed class GoogleCloudProviderRegistration(IOptions<GoogleCloudOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.GoogleCloud;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context)
    {
        GoogleCloudParsedCredentials parsed;
        try { parsed = GoogleCloudParsedCredentials.Parse(context.Credential, context.Account.ProjectId); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_credential_invalid", TokenRedactor.Redact(ex.Message));
        }

        return new GoogleCloudClient(context.HttpClient, new GoogleCloudOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = options.Value.BaseUrl,
            TokenEndpoint = options.Value.TokenEndpoint,
            DefaultZone = options.Value.DefaultZone,
            DefaultImage = options.Value.DefaultImage,
            ProjectId = parsed.ProjectId,
            ClientEmail = parsed.ClientEmail,
            PrivateKeyPem = parsed.PrivateKeyPem,
            AccessToken = parsed.AccessToken
        }, context.LoggerFactory.CreateLogger<GoogleCloudClient>());
    }
}

public sealed class OvhcloudProviderRegistration(IOptions<OvhcloudOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Ovhcloud;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context)
    {
        OvhcloudParsedCredentials parsed;
        try { parsed = OvhcloudParsedCredentials.Parse(context.Credential, context.Account.ProjectId); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_credential_invalid", TokenRedactor.Redact(ex.Message));
        }

        return new OvhcloudClient(context.HttpClient, new OvhcloudOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = parsed.Endpoint ?? options.Value.BaseUrl,
            DefaultRegion = options.Value.DefaultRegion,
            DefaultImage = options.Value.DefaultImage,
            ProjectId = parsed.ProjectId,
            ApplicationKey = parsed.ApplicationKey,
            ApplicationSecret = parsed.ApplicationSecret,
            ConsumerKey = parsed.ConsumerKey,
            AllowBareMetalCommercialOrdering = options.Value.AllowBareMetalCommercialOrdering
        }, context.LoggerFactory.CreateLogger<OvhcloudClient>());
    }
}

public sealed class LinodeProviderRegistration(IOptions<LinodeOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Linode;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context) =>
        new LinodeClient(context.HttpClient, new LinodeOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = options.Value.BaseUrl,
            DefaultRegion = options.Value.DefaultRegion,
            DefaultImage = options.Value.DefaultImage,
            ApiToken = context.Credential
        }, context.LoggerFactory.CreateLogger<LinodeClient>());
}

public sealed class ScalewayProviderRegistration(IOptions<ScalewayOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Scaleway;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
    public bool IsEnabledByOptions => options.Value.Enabled;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context)
    {
        ScalewayParsedCredentials parsed;
        try { parsed = ScalewayParsedCredentials.Parse(context.Credential, context.Account.ProjectId); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new ProviderCatalogException("provider_credential_invalid", TokenRedactor.Redact(ex.Message));
        }

        return new ScalewayClient(context.HttpClient, new ScalewayOptions
        {
            Enabled = options.Value.Enabled,
            BaseUrl = options.Value.BaseUrl,
            DefaultZone = parsed.DefaultZone ?? options.Value.DefaultZone,
            DefaultImage = options.Value.DefaultImage,
            SecretKey = parsed.SecretKey,
            ProjectId = parsed.ProjectId
        }, context.LoggerFactory.CreateLogger<ScalewayClient>());
    }
}
