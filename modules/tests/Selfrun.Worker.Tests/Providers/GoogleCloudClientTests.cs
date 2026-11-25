using System.Net;
using System.Security.Cryptography;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.GoogleCloud;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Worker.Tests.Providers;

public sealed class GoogleCloudClientTests
{
    private const string ProjectId = "selfrun-gcp-test";

    private static (GoogleCloudClient Client, TestHttpMessageHandler Handler) CreateClient(string? pem = null)
    {
        var handler = new TestHttpMessageHandler();
        var options = new GoogleCloudOptions
        {
            Enabled = true,
            BaseUrl = "https://compute.googleapis.test/compute/v1/",
            TokenEndpoint = "https://oauth2.googleapis.test/token",
            ProjectId = ProjectId,
            DefaultZone = "us-central1-a",
            ClientEmail = pem is not null ? "selfrun@selfrun-gcp-test.iam.gserviceaccount.com" : null,
            PrivateKeyPem = pem,
            AccessToken = pem is null ? "synthetic-gcp-access-token" : null
        };
        return (new GoogleCloudClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task ServiceAccountJwtAssertionExchangesAndCachesAccessToken()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var (client, handler) = CreateClient(pem);

        handler.QueueJsonResponse("""{"access_token":"minted-gcp-token","expires_in":3600}""");
        handler.QueueJsonResponse($$"""{"name":"{{ProjectId}}","quotas":[{"metric":"CPUS","usage":2,"limit":24}]}""");

        var scope = await client.VerifyScopeAsync();
        Assert.Equal(ProjectId, scope.ScopeId);
        Assert.Equal(2, handler.RecordedRequests.Count);
        Assert.Contains("urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", handler.RecordedRequests[0].Body);
        Assert.Equal("Bearer minted-gcp-token", handler.RecordedRequests[1].AuthorizationHeader);
    }

    [Fact]
    public async Task PaginatedAggregatedDiscoveryFindsInstanceAndRejectsDuplicatesAndCrossProject()
    {
        var (client, handler) = CreateClient();
        // Page 1 has no matches, page 2 has exact match
        handler.QueueJsonResponse("""{"items":{"zones/us-central1-a":{"instances":[]}},"nextPageToken":"page-2"}""");
        handler.QueueJsonResponse("""{"items":{"zones/us-central1-b":{"instances":[{"name":"mc-gcp-1","status":"RUNNING","zone":"https://compute.googleapis.test/compute/v1/projects/__PROJ__/zones/us-central1-b","selfLink":"https://compute.googleapis.test/compute/v1/projects/__PROJ__/zones/us-central1-b/instances/mc-gcp-1","networkInterfaces":[{"accessConfigs":[{"natIP":"198.51.100.77"}]}]}]}}}""".Replace("__PROJ__", ProjectId));

        var found = await client.FindServerAsync("mc-gcp-1");
        Assert.NotNull(found);
        Assert.Equal("us-central1-b/mc-gcp-1", found!.Id);
        Assert.Equal("198.51.100.77", found.Ipv4Address);
        Assert.Contains("pageToken=page-2", handler.RecordedRequests[1].Uri);

        // Duplicate same-name instances across two zones must fail safely
        handler.QueueJsonResponse("""{"items":{"zones/us-central1-a":{"instances":[{"name":"dup","selfLink":"/projects/__PROJ__/zones/us-central1-a/instances/dup"}]},"zones/us-central1-b":{"instances":[{"name":"dup","selfLink":"/projects/__PROJ__/zones/us-central1-b/instances/dup"}]}}}""".Replace("__PROJ__", ProjectId));
        await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("dup"));

        // Cross-project selfLink must be rejected
        handler.QueueJsonResponse("""{"items":{"zones/us-central1-a":{"instances":[{"name":"alien","selfLink":"/projects/other-project/zones/us-central1-a/instances/alien"}]}}}""");
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("alien"));
        Assert.Contains("different project", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRequiresOperationDeleteRequiresOperationAndDiscoversCatalog()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"name":"op-insert-1","status":"RUNNING","selfLink":"/projects/__PROJ__/zones/us-central1-a/operations/op-insert-1"}""".Replace("__PROJ__", ProjectId));
        handler.QueueJsonResponse("""{"name":"mc-gcp-2","status":"RUNNING","selfLink":"/projects/__PROJ__/zones/us-central1-a/instances/mc-gcp-2","networkInterfaces":[{"accessConfigs":[{"natIP":"198.51.100.88"}]}]}""".Replace("__PROJ__", ProjectId));
        handler.QueueJsonResponse("""{"name":"op-delete-1","status":"RUNNING","selfLink":"/projects/__PROJ__/zones/us-central1-a/operations/op-delete-1"}""".Replace("__PROJ__", ProjectId));

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-gcp-2", "e2-small", "us-central1-a", ["ssh-ed25519 AAAAC3"], "#cloud-config\n"));
        Assert.Equal("us-central1-a/mc-gcp-2", created.Id);

        var status = await client.GetServerStatusAsync(created.Id);
        Assert.True(status.IsRunning);
        Assert.Equal("198.51.100.88", status.Ipv4Address);

        await client.DeleteServerAsync(created.Id);

        // Delete with empty operation must fail rather than silently succeed
        handler.QueueJsonResponse("""{}""");
        await Assert.ThrowsAsync<ProviderApiException>(() => client.DeleteServerAsync(created.Id));
    }

    [Fact]
    public async Task PersistentDiskLifecycleRejectsCrossZoneAttach()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"name":"op-disk-1","status":"RUNNING"}""");
        handler.QueueJsonResponse("""{"name":"op-attach-1","status":"RUNNING"}""");
        handler.QueueJsonResponse("""{"name":"op-resize-1","status":"RUNNING"}""");

        var vol = await client.CreateVolumeAsync("mc-disk", 30, "us-central1-a", new Dictionary<string, string> { ["app-id"] = "app-1" }, default);
        Assert.Equal("us-central1-a/mc-disk", vol.Id);
        await client.AttachVolumeAsync("us-central1-a/mc-disk", "us-central1-a/mc-gcp-2", default);
        await client.ResizeVolumeAsync("us-central1-a/mc-disk", 60, default);

        await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.AttachVolumeAsync("us-central1-a/mc-disk", "us-central1-b/mc-gcp-2", default));
    }

    [Fact]
    public void CredentialParserRejectsCrossProjectMismatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            GoogleCloudParsedCredentials.Parse("""{"type":"service_account","project_id":"other-proj","client_email":"a@b","private_key":"pem"}""", ProjectId));
    }
}
