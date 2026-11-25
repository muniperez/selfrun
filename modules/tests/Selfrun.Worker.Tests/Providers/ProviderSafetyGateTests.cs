using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Aws;
using Selfrun.Common.Providers.Azure;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.GoogleCloud;
using Selfrun.Common.Providers.Ovhcloud;
using Selfrun.Common.Services;

namespace Selfrun.Worker.Tests.Providers;

public sealed class ProviderSafetyGateTests
{
    [Theory]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.GoogleCloud)]
    [InlineData(CloudProvider.Ovhcloud)]
    [InlineData(CloudProvider.Linode)]
    [InlineData(CloudProvider.Scaleway)]
    public void PersistedEnabledPlansCannotAllocateWithDeferredProviders(CloudProvider provider)
    {
        var plan = new ProviderPlan { Enabled = true, ProviderAccount = new ProviderAccount { Enabled = true, ProviderType = provider } };
        var error = Assert.Throws<ProviderCatalogException>(() => ProviderCatalogService.EnsureEnabled(plan));
        Assert.Equal("provider_not_qualified", error.Code);
        Assert.True(VpsProviderRegistry.CreateDefault().TryGet(provider, out _));
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner)]
    [InlineData(CloudProvider.DigitalOcean)]
    [InlineData(CloudProvider.Vultr)]
    public void EstablishedProviderAdmissionIsUnchanged(CloudProvider provider)
    {
        ProviderCatalogService.EnsureEnabled(new ProviderPlan
        {
            Enabled = true, ProviderAccount = new ProviderAccount { Enabled = true, ProviderType = provider }
        });
    }

    [Theory]
    [InlineData("us-east-1")]
    [InlineData("eu-central-1")]
    [InlineData("us-gov-west-1")]
    public void AwsRegionsAcceptCanonicalHostnameComponents(string region) =>
        Assert.Equal(region, AwsOptions.ValidateRegion(region));

    [Theory]
    [InlineData("")]
    [InlineData("US-EAST-1")]
    [InlineData("not a region")]
    [InlineData("region.example")]
    [InlineData("-region")]
    public void AwsRegionsRejectInvalidHostnameComponents(string region)
    {
        Assert.Throws<FormatException>(() => AwsOptions.ValidateRegion(region));
        if (region.Length > 0)
            Assert.Throws<FormatException>(() => AwsParsedCredentials.Parse(
                System.Text.Json.JsonSerializer.Serialize(new { accessKeyId = "synthetic", secretAccessKey = "synthetic", region })));
    }

    [Fact]
    public void AccountCredentialsCannotOverrideTokenDestinations()
    {
        Assert.Throws<FormatException>(() => AzureParsedCredentials.Parse(
            """{"subscriptionId":"sub-test","resourceGroup":"rg-test","accessToken":"synthetic","tokenEndpoint":"https://auth.example.test/token"}"""));
        Assert.Throws<FormatException>(() => GoogleCloudParsedCredentials.Parse(
            """{"projectId":"project-test","accessToken":"synthetic","token_uri":"https://auth.example.test/token"}"""));
        var parsed = GoogleCloudParsedCredentials.Parse(
            """{"projectId":"project-test","accessToken":"synthetic","token_uri":"https://oauth2.googleapis.com/token"}""");
        Assert.Equal("project-test", parsed.ProjectId);
    }

    [Theory]
    [InlineData("ovh-eu", "https://eu.api.ovh.com/1.0/")]
    [InlineData("ovh-ca", "https://ca.api.ovh.com/1.0/")]
    [InlineData("ovh-us", "https://api.us.ovhcloud.com/1.0/")]
    public void OvhAccountEndpointSelectionUsesCanonicalDestinations(string endpoint, string expected) =>
        Assert.Equal(expected, OvhcloudParsedCredentials.Parse(OvhCredential(endpoint)).Endpoint);

    [Fact]
    public void OvhAccountEndpointDoesNotAcceptCustomDestinations() =>
        Assert.Throws<FormatException>(() => OvhcloudParsedCredentials.Parse(OvhCredential("https://api.example.test/")));

    private static string OvhCredential(string endpoint) => System.Text.Json.JsonSerializer.Serialize(new
    {
        applicationKey = "synthetic", applicationSecret = "synthetic", consumerKey = "synthetic", projectId = "project-test", endpoint
    });
}
