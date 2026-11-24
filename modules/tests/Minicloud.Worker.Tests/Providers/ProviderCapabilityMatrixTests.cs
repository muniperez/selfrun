using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Aws;
using Minicloud.Common.Providers.Azure;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.GoogleCloud;
using Minicloud.Common.Providers.Linode;
using Minicloud.Common.Providers.Ovhcloud;
using Minicloud.Common.Providers.Scaleway;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ProviderCapabilityMatrixTests
{
    [Fact]
    public void DefaultRegistryExposesOnlyQualifiedProviders()
    {
        var registry = VpsProviderRegistry.CreateDefault();
        var enabled = registry.GetSupported().Select(d => d.WireType).OrderBy(x => x).ToArray();
        Assert.Equal(["digital-ocean", "hetzner", "vultr"], enabled);

        Assert.False(registry.IsSupported(CloudProvider.Aws));
        Assert.False(registry.IsSupported(CloudProvider.Azure));
        Assert.False(registry.IsSupported(CloudProvider.GoogleCloud));
        Assert.False(registry.IsSupported(CloudProvider.Ovhcloud));
        Assert.False(registry.IsSupported(CloudProvider.Linode));
        Assert.False(registry.IsSupported(CloudProvider.Scaleway));
    }

    [Theory]
    [InlineData("aws", CloudProvider.Aws)]
    [InlineData("ec2", CloudProvider.Aws)]
    [InlineData("azure", CloudProvider.Azure)]
    [InlineData("microsoft-azure", CloudProvider.Azure)]
    [InlineData("gcp", CloudProvider.GoogleCloud)]
    [InlineData("google-cloud", CloudProvider.GoogleCloud)]
    [InlineData("ovhcloud", CloudProvider.Ovhcloud)]
    [InlineData("ovh", CloudProvider.Ovhcloud)]
    [InlineData("linode", CloudProvider.Linode)]
    [InlineData("akamai", CloudProvider.Linode)]
    [InlineData("scaleway", CloudProvider.Scaleway)]
    [InlineData("scw", CloudProvider.Scaleway)]
    public void WireTypeAndAliasesResolveCleanlyAndGateOptInWorks(string alias, CloudProvider expected)
    {
        var defaultRegistry = VpsProviderRegistry.CreateDefault();
        Assert.True(defaultRegistry.TryGetByWireType(alias, out var registration));
        Assert.Equal(expected, registration.Descriptor.BuiltInProvider);
        Assert.Equal(ProviderQualificationState.Experimental, registration.Descriptor.QualificationState);
        Assert.False(defaultRegistry.TryParseBuiltIn(alias, out _));

        var gated = VpsProviderRegistry.CreateDefault(
            options: Options.Create(new ProviderRegistryOptions
            {
                EnabledGatedProviders = new HashSet<string>([alias], StringComparer.OrdinalIgnoreCase)
            }));

        Assert.True(gated.IsSupported(expected));
        Assert.True(gated.TryParseBuiltIn(alias, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Contains(gated.GetSupported(), d => d.BuiltInProvider == expected);
    }

    [Fact]
    public void PerProviderEnabledOptionUnlocksSingleExperimentalProvider()
    {
        var registry = VpsProviderRegistry.CreateDefault(
            aws: Options.Create(new AwsOptions { Enabled = true }),
            ovhcloud: Options.Create(new OvhcloudOptions { Enabled = true }));

        Assert.True(registry.IsSupported(CloudProvider.Aws));
        Assert.True(registry.IsSupported(CloudProvider.Ovhcloud));
        Assert.False(registry.IsSupported(CloudProvider.Azure));
        Assert.False(registry.IsSupported(CloudProvider.GoogleCloud));
        Assert.False(registry.IsSupported(CloudProvider.Linode));
        Assert.False(registry.IsSupported(CloudProvider.Scaleway));

        var ovh = BuiltInProviderDescriptors.ForBuiltIn(CloudProvider.Ovhcloud);
        Assert.True(ovh.Capabilities.ComputeFamilies.HasFlag(ProviderComputeFamily.VirtualServer));
        Assert.True(ovh.Capabilities.ComputeFamilies.HasFlag(ProviderComputeFamily.BareMetal));
    }

    [Fact]
    public void ProviderRegistrationsResolveAllNineProvidersViaDi()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMinicloudProviders(new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var registrations = sp.GetServices<IVpsProviderRegistration>()
            .Where(r => r.Descriptor.BuiltInProvider is not null)
            .ToDictionary(r => r.Descriptor.BuiltInProvider!.Value);
        Assert.Equal(9, registrations.Count);

        using var http = new HttpClient(new TestHttpMessageHandler());
        Assert.IsType<AwsClient>(registrations[CloudProvider.Aws].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.Aws, ProjectId = "123456789012" },
                """{"accessKeyId":"AKIAEXAMPLE","secretAccessKey":"secret-key-example","accountId":"123456789012"}""",
                http,
                NullLoggerFactory.Instance)));

        Assert.IsType<AzureClient>(registrations[CloudProvider.Azure].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.Azure, ProjectId = "sub-1/rg-1" },
                """{"accessToken":"synthetic-token","subscriptionId":"sub-1","resourceGroup":"rg-1"}""",
                http,
                NullLoggerFactory.Instance)));

        Assert.IsType<GoogleCloudClient>(registrations[CloudProvider.GoogleCloud].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.GoogleCloud, ProjectId = "proj-1" },
                """{"access_token":"synthetic-token","project_id":"proj-1"}""",
                http,
                NullLoggerFactory.Instance)));

        Assert.IsType<OvhcloudClient>(registrations[CloudProvider.Ovhcloud].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.Ovhcloud, ProjectId = "ovh-proj-1" },
                """{"applicationKey":"app-key","applicationSecret":"app-sec-12345678","consumerKey":"cons-key-12345678","projectId":"ovh-proj-1"}""",
                http,
                NullLoggerFactory.Instance)));

        Assert.IsType<LinodeClient>(registrations[CloudProvider.Linode].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.Linode },
                "synthetic-linode-token",
                http,
                NullLoggerFactory.Instance)));

        Assert.IsType<ScalewayClient>(registrations[CloudProvider.Scaleway].CreateClient(
            new ProviderClientCreationContext(
                new ProviderAccount { ProviderType = CloudProvider.Scaleway, ProjectId = "scw-proj-1" },
                "synthetic-scw-secret",
                http,
                NullLoggerFactory.Instance)));
    }

    [Fact]
    public void TokenRedactorMasksNewProviderCredentialPatterns()
    {
        var input = "Authorization: AWS4-HMAC-SHA256 Credential=AKIA12345678/20261124/us-east-1/ec2/aws4_request, SignedHeaders=host;x-amz-date, Signature=abcdef0123456789 "
            + "X-Ovh-Signature=$1$0123456789abcdef0123456789abcdef01234567 "
            + "x-auth-token=scw-secret-value-12345 "
            + "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC\n-----END PRIVATE KEY-----";

        var redacted = TokenRedactor.Redact(input);
        Assert.DoesNotContain("AKIA12345678", redacted);
        Assert.DoesNotContain("abcdef0123456789", redacted);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef01234567", redacted);
        Assert.DoesNotContain("scw-secret-value-12345", redacted);
        Assert.DoesNotContain("MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC", redacted);
    }
}
