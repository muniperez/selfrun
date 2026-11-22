using System.Net;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.GoogleCloud;
using Minicloud.Common.Providers.Models;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public sealed class GoogleCloudClientTests
{
    [Fact]
    public async Task CreateServerAsync_WaitsForOperationAndReturnsExactProjectScopedInstance()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"name":"insert-1","status":"PENDING"}""", HttpStatusCode.OK);
        handler.QueueJsonResponse("""{"name":"insert-1","status":"DONE"}""", HttpStatusCode.OK);
        handler.QueueJsonResponse("""
        {
          "name":"mc-app",
          "zone":"https://www.googleapis.com/compute/v1/projects/project-1/zones/us-central1-a",
          "status":"RUNNING",
          "networkInterfaces":[{"accessConfigs":[{"natIP":"203.0.113.10"}]}],
          "labels":{"managed-by":"minicloud"}
        }
        """, HttpStatusCode.OK);
        var client = Create(handler);

        var result = await client.CreateServerAsync(new CreateServerRequest(
            "mc-app", "e2-micro", "us-central1-a", [], "#cloud-config",
            new Dictionary<string, string> { ["managed-by"] = "minicloud" }));

        Assert.Equal(CloudProvider.GoogleCloud, client.Provider);
        Assert.Equal("projects/project-1/zones/us-central1-a/instances/mc-app", result.Id);
        Assert.Equal("203.0.113.10", result.Ipv4Address);
        Assert.Equal(3, handler.RecordedRequests.Count);
        Assert.Contains("/projects/project-1/zones/us-central1-a/instances", handler.RecordedRequests[0].Uri);
        Assert.Contains("\"machineType\":\"zones/us-central1-a/machineTypes/e2-micro\"", handler.RecordedRequests[0].Body);
        Assert.Contains("\"autoDelete\":true", handler.RecordedRequests[0].Body);
        Assert.Equal("Bearer synthetic-token", handler.RecordedRequests[0].AuthorizationHeader);
    }

    [Fact]
    public async Task FindServerAsync_UsesAggregatedListAndDoesNotAcceptLookalikeNames()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"items":{"zones/us-central1-a":{"instances":[{"name":"mc-other","zone":"zone","status":"RUNNING"}]}}}""");
        var client = Create(handler);

        var result = await client.FindServerAsync("mc-app");

        Assert.Null(result);
        Assert.Contains("aggregated/instances", Assert.Single(handler.RecordedRequests).Uri);
    }

    [Fact]
    public async Task DeleteServerAsync_WhenOperationFailsSurfacesProviderError()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"name":"delete-1","status":"PENDING"}""", HttpStatusCode.OK);
        handler.QueueJsonResponse("""{"name":"delete-1","status":"DONE","error":{"errors":[{"message":"quota lock"}]}}""", HttpStatusCode.OK);
        var client = Create(handler);

        var error = await Assert.ThrowsAsync<Minicloud.Common.Providers.Common.ProviderApiException>(() =>
            client.DeleteServerAsync("projects/project-1/zones/us-central1-a/instances/mc-app"));

        Assert.Contains("quota lock", error.Message);
        Assert.Equal(CloudProvider.GoogleCloud, error.Provider);
    }

    private static GoogleCloudClient Create(TestHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://compute.googleapis.com/compute/v1/") },
            new GoogleCloudOptions { ProjectId = "project-1", CredentialJson = "synthetic-token" });
}
