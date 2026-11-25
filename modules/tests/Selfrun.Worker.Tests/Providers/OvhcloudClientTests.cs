using System.Net;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Providers.Ovhcloud;

namespace Selfrun.Worker.Tests.Providers;

public sealed class OvhcloudClientTests
{
    private const string ProjectId = "ovh-project-123456";

    private static (OvhcloudClient Client, TestHttpMessageHandler Handler) CreateClient(bool allowBareMetalOrdering = false)
    {
        var handler = new TestHttpMessageHandler();
        var options = new OvhcloudOptions
        {
            Enabled = true,
            BaseUrl = "https://eu.api.ovh.test/1.0/",
            ProjectId = ProjectId,
            ApplicationKey = "ovh-app-key",
            ApplicationSecret = "ovh-super-secret-app-value",
            ConsumerKey = "ovh-super-secret-consumer-value",
            AllowBareMetalCommercialOrdering = allowBareMetalOrdering
        };
        return (new OvhcloudClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task PublicCloudLifecycleAndDiscoverySignEveryRequest()
    {
        var (client, handler) = CreateClient();
        // Discovery: regions, flavors, images, quota
        handler.QueueJsonResponse("""["GRA11","SBG5"]""");
        handler.QueueJsonResponse("""[{"id":"flav-1","name":"d2-4","vcpus":2,"ram":4096,"disk":50,"type":"ovh.vps-ssd","available":true}]""");
        handler.QueueJsonResponse("""[{"id":"img-1","name":"Ubuntu 24.04","type":"linux"}]""");
        handler.QueueJsonResponse("""[{"region":"GRA11","instance":{"usedInstances":1,"maxInstances":10,"usedCores":2,"maxCores":32}}]""");

        var locations = await client.ListLocationsAsync();
        var offers = await client.ListComputeOffersAsync("GRA11");
        var images = await client.ListImagesAsync("GRA11");
        var quotas = await client.GetQuotasAsync("GRA11");

        Assert.Equal(2, locations.Count);
        Assert.Equal("GRA11", locations[0].Code);
        Assert.Single(offers);
        Assert.Single(images);
        Assert.Equal(2, quotas.Count);

        // Public cloud instance create, status, delete
        handler.QueueJsonResponse("""{"id":"inst-1","name":"mc-ovh-1","status":"BUILD","ipAddresses":[{"ip":"198.51.100.21","type":"public","version":4}]}""");
        handler.QueueJsonResponse("""{"id":"inst-1","name":"mc-ovh-1","status":"ACTIVE","ipAddresses":[{"ip":"198.51.100.21","type":"public","version":4}]}""");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-ovh-1", "flav-1", "GRA11", ["key-1"], "#cloud-config\n"));
        Assert.Equal("inst-1", created.Id);
        Assert.Equal("198.51.100.21", created.Ipv4Address);

        var status = await client.GetServerStatusAsync("inst-1");
        Assert.True(status.IsRunning);

        await client.DeleteServerAsync("inst-1");
    }

    [Fact]
    public async Task BareMetalOffersAndInventoryWorkWhileCommercialOrderingAndCancellationAreGatedByDefault()
    {
        var (client, handler) = CreateClient(allowBareMetalOrdering: false);
        Assert.False(client.IsCommercialOrderingEnabled);

        handler.QueueJsonResponse("""{"plans":[{"planCode":"24sk10","invoiceName":"Rise-1","datacenter":"gra","architecture":"linux/amd64","cores":6,"memoryGb":64,"provisioningLeadTime":"120s"}]}""");
        handler.QueueJsonResponse("""["ns123456.ip-198-51-100.eu"]""");
        handler.QueueJsonResponse("""{"name":"ns123456.ip-198-51-100.eu","datacenter":"gra","state":"ok","ip":"198.51.100.90","os":"ubuntu2404_64","iam":{"displayName":"mc-bm-1"}}""");
        handler.QueueJsonResponse("""{"name":"ns123456.ip-198-51-100.eu","datacenter":"gra","state":"ok","ip":"198.51.100.90","os":"ubuntu2404_64","iam":{"displayName":"mc-bm-1"}}""");

        var offers = await client.ListBareMetalOffersAsync("gra");
        Assert.Single(offers);
        Assert.False(offers[0].OrderingEnabled);
        Assert.Equal("24sk10", offers[0].OfferCode);

        var found = await client.FindBareMetalServerAsync("mc-bm-1");
        Assert.NotNull(found);
        Assert.True(found!.CommercialOrderingGated);
        Assert.Equal("198.51.100.90", found.Ipv4Address);

        var bmStatus = await client.GetBareMetalServerStatusAsync(found.ServiceName);
        Assert.True(bmStatus.IsRunning);

        // Commercial ordering and cancellation MUST fail deterministically while gated
        var orderEx = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.ProvisionBareMetalServerAsync(new CreateServerRequest("mc-bm-new", "24sk10", "gra", [], "")));
        Assert.Equal(HttpStatusCode.Forbidden, orderEx.StatusCode);
        Assert.Contains("gated", orderEx.Message, StringComparison.OrdinalIgnoreCase);

        var cancelEx = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.DeleteBareMetalServerAsync(found.ServiceName));
        Assert.Equal(HttpStatusCode.Forbidden, cancelEx.StatusCode);
        Assert.Contains("gated", cancelEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BareMetalProvisioningAndTerminationWorkWhenGateIsExplicitlyEnabled()
    {
        var (client, handler) = CreateClient(allowBareMetalOrdering: true);
        Assert.True(client.IsCommercialOrderingEnabled);

        handler.QueueJsonResponse("""{"name":"ns999.ip-198-51-100.eu","datacenter":"gra","state":"ok","ip":"198.51.100.99","os":"ubuntu2404_64","iam":{"displayName":"mc-bm-2"}}""");
        handler.QueueJsonResponse("""{"taskId":1}""");

        var server = await client.ProvisionBareMetalServerAsync(new CreateServerRequest("mc-bm-2", "24sk10", "gra", ["key-1"], ""));
        Assert.Equal("ns999.ip-198-51-100.eu", server.ServiceName);
        Assert.False(server.CommercialOrderingGated);

        await client.DeleteBareMetalServerAsync(server.ServiceName);
        Assert.Equal(2, handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task DuplicateDiscoveryAndErrorRedactionAreEnforced()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""[{"id":"i1","name":"dup","status":"ACTIVE"},{"id":"i2","name":"dup","status":"ACTIVE"}]""");
        await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("dup"));

        handler.QueueJsonResponse("""{"message":"Quota exceeded ovh-super-secret-app-value ovh-super-secret-consumer-value"}""", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-q", "d2-4", "GRA11", [], "")));
        Assert.DoesNotContain("ovh-super-secret-app-value", ex.Message);
        Assert.DoesNotContain("ovh-super-secret-consumer-value", ex.Message);
    }

    [Fact]
    public void CredentialParserEnforcesProjectBoundary()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OvhcloudParsedCredentials.Parse("""{"applicationKey":"a","applicationSecret":"b","consumerKey":"c","projectId":"wrong-proj"}""", ProjectId));
    }
}
