using System.Net;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Linode;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Worker.Tests.Providers;

public sealed class LinodeClientTests
{
    private static (LinodeClient Client, TestHttpMessageHandler Handler) CreateClient()
    {
        var handler = new TestHttpMessageHandler();
        var options = new LinodeOptions
        {
            Enabled = true,
            BaseUrl = "https://api.linode.test/v4/",
            ApiToken = "synthetic-linode-secret-token-value"
        };
        return (new LinodeClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task ConnectionCheckAndCatalogDiscoveryUseBearerAuth()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"uid":4242,"username":"selfrun-ops"}""");
        handler.QueueJsonResponse("""{"data":[{"id":"us-east","label":"Newark, NJ"}]}""");
        handler.QueueJsonResponse("""{"data":[{"id":"g6-nanode-1","label":"Nanode 1GB","vcpus":1,"memory":1024,"disk":25600}]}""");
        handler.QueueJsonResponse("""{"data":[{"id":"linode/ubuntu24.04","label":"Ubuntu 24.04 LTS","vendor":"Ubuntu"}]}""");

        var scope = await client.VerifyScopeAsync();
        Assert.Equal("4242", scope.ScopeId);

        var locations = await client.ListLocationsAsync();
        var offers = await client.ListComputeOffersAsync();
        var images = await client.ListImagesAsync();
        Assert.Single(locations);
        Assert.Single(offers);
        Assert.Single(images);
        Assert.All(handler.RecordedRequests, r => Assert.Equal("Bearer synthetic-linode-secret-token-value", r.AuthorizationHeader));
    }

    [Fact]
    public async Task NormalLifecycleCreateFindStatusDeleteAndVolumes()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"id":9001,"label":"mc-linode-1","status":"provisioning","ipv4":["192.168.10.5","198.51.100.31"],"ipv6":"2001:db8::1/64","tags":["selfrun"]}""");
        handler.QueueJsonResponse("""{"data":[{"id":9001,"label":"mc-linode-1","status":"running","ipv4":["198.51.100.31"],"ipv6":"2001:db8::1/64","tags":["selfrun"]}]}""");
        handler.QueueJsonResponse("""{"id":9001,"label":"mc-linode-1","status":"running","ipv4":["198.51.100.31"],"ipv6":"2001:db8::1/64"}""");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-linode-1", "g6-nanode-1", "us-east", ["ssh-ed25519 AAAAC3"], "#cloud-config\n"));
        Assert.Equal("9001", created.Id);
        Assert.Equal("198.51.100.31", created.Ipv4Address);
        Assert.Equal("2001:db8::1", created.Ipv6Address);

        var found = await client.FindServerAsync("mc-linode-1");
        Assert.NotNull(found);
        Assert.Equal("9001", found!.Id);

        var status = await client.GetServerStatusAsync("9001");
        Assert.True(status.IsRunning);

        await client.DeleteServerAsync("9001");

        // Block storage lifecycle
        handler.QueueJsonResponse("""{"id":555,"label":"mc-vol","size":20,"region":"us-east","tags":["selfrun","app-id:app-1"]}""");
        handler.QueueJsonResponse("""{"id":555,"linode_id":9001}""");
        handler.QueueJsonResponse("""{}""");

        var vol = await client.CreateVolumeAsync("mc-vol", 20, "us-east", new Dictionary<string, string> { ["app-id"] = "app-1" }, default);
        Assert.Equal("555", vol.Id);
        Assert.Equal("app-1", vol.Labels["app-id"]);
        await client.AttachVolumeAsync("555", "9001", default);
        await client.ResizeVolumeAsync("555", 50, default);
    }

    [Fact]
    public async Task RejectsDuplicateInstancesAndRedactsSecretsOnErrors()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"data":[{"id":1,"label":"dup"},{"id":2,"label":"dup"}]}""");
        await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("dup"));

        handler.QueueJsonResponse("""{"errors":[{"reason":"Account quota exceeded synthetic-linode-secret-token-value"}]}""", HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-err", "g6-nanode-1", "us-east", [], "")));
        Assert.DoesNotContain("synthetic-linode-secret-token-value", ex.Message);
    }
}
