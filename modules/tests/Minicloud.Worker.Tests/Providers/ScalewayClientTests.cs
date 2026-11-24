using System.Net;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Scaleway;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ScalewayClientTests
{
    private const string ProjectId = "11111111-2222-3333-4444-555555555555";

    private static (ScalewayClient Client, TestHttpMessageHandler Handler) CreateClient()
    {
        var handler = new TestHttpMessageHandler();
        var options = new ScalewayOptions
        {
            Enabled = true,
            BaseUrl = "https://api.scaleway.test/",
            SecretKey = "synthetic-scaleway-secret-key-value",
            ProjectId = ProjectId,
            DefaultZone = "fr-par-1"
        };
        return (new ScalewayClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task ConnectionCheckAndCatalogDiscoveryRespectProjectScope()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"servers":[]}""");
        handler.QueueJsonResponse("""{"servers":{"DEV1-S":{"ncpus":2,"ram":2147483648,"arch":"x86_64"},"COPARM1-2C-8G":{"ncpus":2,"ram":8589934592,"arch":"arm64"}}}""");
        handler.QueueJsonResponse("""{"images":[{"id":"ubuntu_noble","name":"Ubuntu 24.04 Noble","arch":"x86_64"}]}""");
        handler.QueueJsonResponse("""{"quotas":[{"name":"servers","used":1,"limit":10}]}""");

        var scope = await client.VerifyScopeAsync();
        Assert.Equal(ProjectId, scope.ScopeId);

        var locations = await client.ListLocationsAsync();
        var offers = await client.ListComputeOffersAsync("fr-par-1");
        var images = await client.ListImagesAsync("fr-par-1");
        var quotas = await client.GetQuotasAsync("fr-par-1");
        Assert.NotEmpty(locations);
        Assert.Equal(2, offers.Count);
        Assert.Contains(offers, o => o.Architectures.Contains("linux/arm64"));
        Assert.Single(images);
        Assert.Single(quotas);
        Assert.Contains($"project={ProjectId}", handler.RecordedRequests[0].Uri);
    }

    [Fact]
    public async Task CreateStagesCloudInitPowersOnAndReportsPartialFailureForReconciliation()
    {
        var (client, handler) = CreateClient();
        // Create server -> PATCH user-data -> POST poweron -> GET status -> POST terminate
        handler.QueueJsonResponse("""{"server":{"id":"srv-1","name":"mc-scw-1","state":"stopped","project":"__PROJ__","public_ip":{"address":"198.51.100.41"}}}""".Replace("__PROJ__", ProjectId));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueJsonResponse("""{"task":{"id":"task-1"}}""");
        handler.QueueJsonResponse("""{"server":{"id":"srv-1","name":"mc-scw-1","state":"running","project":"__PROJ__","public_ip":{"address":"198.51.100.41"}}}""".Replace("__PROJ__", ProjectId));
        handler.QueueJsonResponse("""{"task":{"id":"task-2"}}""");

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-scw-1", "DEV1-S", "fr-par-1", [], "#cloud-config\n"));
        Assert.Equal("fr-par-1/srv-1", created.Id);
        Assert.Equal(HttpMethod.Patch, handler.RecordedRequests[1].Method);
        Assert.Contains("/user_data/cloud-init", handler.RecordedRequests[1].Uri);
        Assert.Contains("/action", handler.RecordedRequests[2].Uri);

        var status = await client.GetServerStatusAsync(created.Id);
        Assert.True(status.IsRunning);
        await client.DeleteServerAsync(created.Id);

        // Partial failure: server created, but poweron fails with quota error -> surfaces created ID for reconciliation
        handler.QueueJsonResponse("""{"server":{"id":"srv-2","name":"mc-scw-2","state":"stopped","project":"__PROJ__"}}""".Replace("__PROJ__", ProjectId));
        handler.QueueJsonResponse("""{"message":"Quota exceeded synthetic-scaleway-secret-key-value"}""", HttpStatusCode.TooManyRequests);

        var partialEx = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-scw-2", "DEV1-S", "fr-par-1", [], "")));
        Assert.Contains("fr-par-1/srv-2", partialEx.Message);
        Assert.DoesNotContain("synthetic-scaleway-secret-key-value", partialEx.Message);
    }

    [Fact]
    public async Task RejectsCrossProjectResourcesDuplicatesAndCrossZoneVolumeAttach()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"servers":[{"id":"srv-alien","name":"alien","project":"other-project"}]}""");
        var boundaryEx = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("alien"));
        Assert.Contains("different project", boundaryEx.Message, StringComparison.OrdinalIgnoreCase);

        handler.QueueJsonResponse("""{"servers":[{"id":"s1","name":"dup","project":"__PROJ__"},{"id":"s2","name":"dup","project":"__PROJ__"}]}""".Replace("__PROJ__", ProjectId));
        await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("dup"));

        await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.AttachVolumeAsync("fr-par-1/vol-1", "fr-par-2/srv-1", default));
    }

    [Fact]
    public void CredentialParserEnforcesProjectBoundary()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScalewayParsedCredentials.Parse("""{"secretKey":"sec","projectId":"other-proj"}""", ProjectId));
    }
}
