using System.Net;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Ovhcloud;

namespace Minicloud.Worker.Tests.Providers;

public sealed class OvhcloudClientTests
{
    private readonly TestHttpMessageHandler _handler = new();

    private OvhcloudClient CreateClient(bool bareMetalOrders = false) =>
        new(new HttpClient(_handler) { BaseAddress = new Uri("https://api.example.test/1.0/") },
            new OvhcloudOptions { BaseUrl = "https://api.example.test/1.0/", EnableBareMetalOrders = bareMetalOrders },
            """{"projectId":"project-fixture","bearerToken":"token-fixture"}""");

    [Fact]
    public async Task PublicCloudLifecycleUsesProjectScopedOVHEndpoints()
    {
        _handler.QueueJsonResponse("""{"id":"instance-1","name":"minicloud-web","status":"ACTIVE","ipAddresses":[{"version":4,"ip":"192.0.2.10"}]}""", HttpStatusCode.Accepted);
        _handler.QueueJsonResponse("""{"id":"instance-1","name":"minicloud-web","status":"ACTIVE","ipAddresses":[{"version":4,"ip":"192.0.2.10"}]}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient();

        var created = await client.CreateServerAsync(new CreateServerRequest("minicloud-web", "b2-7", "GRA9", [], "#cloud-config"));
        var status = await client.GetServerStatusAsync(created.Id);
        await client.DeleteServerAsync(created.Id);

        Assert.Equal(CloudProvider.Ovhcloud, client.Provider);
        Assert.Equal("instance-1", created.Id);
        Assert.True(status.IsRunning);
        Assert.Contains("/cloud/project/project-fixture/instance", _handler.RecordedRequests[0].Uri);
        Assert.Contains("\"flavorId\":\"b2-7\"", _handler.RecordedRequests[0].Body);
    }

    [Fact]
    public async Task DiscoveryChecksPublicCloudThenDedicatedInventoryWithoutAdoptingUnrelatedServers()
    {
        _handler.QueueJsonResponse("[]");
        _handler.QueueJsonResponse("""["unrelated-server"]""");
        _handler.QueueJsonResponse("""{"name":"unrelated-server","state":"ok","ip":"192.0.2.20"}""");

        var result = await CreateClient().FindServerAsync("minicloud-metal");

        Assert.Null(result);
        Assert.Equal(3, _handler.RecordedRequests.Count);
        Assert.Contains("/dedicated/server", _handler.RecordedRequests[1].Uri);
    }

    [Fact]
    public async Task BareMetalIntentNeverReinstallsOrAllocatesAnUnownedServer()
    {
        var client = CreateClient();
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("minicloud-metal", "baremetal:rise-1", "GRA", [], "")));

        Assert.Equal(HttpStatusCode.NotImplemented, ex.StatusCode);
        Assert.Contains("will not reinstall", ex.Message);
        Assert.Empty(_handler.RecordedRequests);
    }

    [Fact]
    public async Task DedicatedDeletionIsExplicitlyBlockedAsCommercialCancellation()
    {
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().DeleteServerAsync("dedicated:server-1"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, ex.StatusCode);
        Assert.Contains("commercial service termination", ex.Message);
        Assert.Empty(_handler.RecordedRequests);
    }

    [Fact]
    public async Task ProviderErrorsRemainExplicitAndRedacted()
    {
        _handler.QueueJsonResponse("""{"message":"token-fixture is invalid"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().FindServerAsync("probe"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("token-fixture", ex.ResponseBody);
        Assert.Contains("[REDACTED]", ex.ResponseBody);
    }
}
