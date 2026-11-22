using System.Net;
using System.Text;
using System.Text.Json;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Scaleway;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ScalewayClientTests
{
    private const string ProjectId = "project-fixture";
    private const string SecretKey = "synthetic-scaleway-secret";
    private readonly CaptureHandler _handler = new();

    private ScalewayClient CreateClient(bool enabled = true) => new(
        new HttpClient(_handler) { BaseAddress = new Uri("https://api.scaleway.com/") },
        new ScalewayOptions { ApiKey = SecretKey, ProjectId = ProjectId, Enabled = enabled });

    [Fact]
    public async Task CreateServerScopesProjectZonePublicIpsRootVolumeAndCloudInit()
    {
        _handler.Queue(HttpStatusCode.Created, """
            {"server":{"id":"instance-1","name":"mc-app","project_id":"project-fixture","status":"starting","public_ips":[],"tags":["minicloud"]}}
            """);
        _handler.Queue(HttpStatusCode.OK, """{"server":{"id":"instance-1","project_id":"project-fixture"}}""");
        _handler.Queue(HttpStatusCode.NoContent, "");

        var result = await CreateClient().CreateServerAsync(new CreateServerRequest(
            "mc-app", "DEV1-S", "fr-par-1", [], "#cloud-config\npackages: []",
            new Dictionary<string, string> { ["app-id"] = "app-fixture" }, ["minicloud-app-app-fixture"], Image: "ubuntu_noble"));

        Assert.Equal("fr-par-1:instance-1", result.Id);
        Assert.Equal("mc-app", result.Name);
        Assert.Equal(3, _handler.Requests.Count);
        Assert.Equal("synthetic-scaleway-secret", _handler.Requests[0].Token);
        Assert.Contains("/instance/v2alpha1/zones/fr-par-1/servers", _handler.Requests[0].Uri);
        using (var body = JsonDocument.Parse(_handler.Requests[0].Body!))
        {
            var root = body.RootElement;
            Assert.Equal(ProjectId, root.GetProperty("project_id").GetString());
            Assert.Equal("DEV1-S", root.GetProperty("server_type").GetString());
            Assert.Equal("zonal_ipv4", root.GetProperty("public_network_interface").GetProperty("ips")[0].GetProperty("new_ip").GetProperty("type").GetString());
            Assert.Equal("ubuntu_noble", root.GetProperty("volumes")[0].GetProperty("new_volume").GetProperty("image_label").GetString());
            Assert.Contains("mc-label-app-id-app-fixture", root.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        }
        Assert.Equal(HttpMethod.Get, _handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Put, _handler.Requests[2].Method);
        Assert.EndsWith("/servers/instance-1/user-data/cloud-init", _handler.Requests[2].Uri);
        Assert.Contains("#cloud-config", _handler.Requests[2].Body);
    }

    [Fact]
    public async Task UnqualifiedMutationPathsDoNotDispatchWhenGateIsOff()
    {
        var client = CreateClient(enabled: false);
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-app", "DEV1-S", "fr-par-1", [], "")));
        var cloudInitError = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            client.SetCloudInitDataAsync("fr-par-1:instance-1", "#cloud-config", default));
        var attachError = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            client.AttachVolumeAsync("fr-par-1:volume-1", "fr-par-1:instance-1", default));
        var resizeError = await Assert.ThrowsAsync<ProviderCatalogException>(() =>
            client.ResizeVolumeAsync("fr-par-1:volume-1", 20, default));

        Assert.Equal("provider_not_qualified", error.Code);
        Assert.Equal("provider_not_qualified", cloudInitError.Code);
        Assert.Equal("provider_not_qualified", attachError.Code);
        Assert.Equal("provider_not_qualified", resizeError.Code);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task FindServerSearchesZonesAndRequiresExactManagedProjectIdentity()
    {
        _handler.Queue(HttpStatusCode.OK, """{"servers":[]}""");
        _handler.Queue(HttpStatusCode.OK, """
            {"servers":[
              {"id":"wrong-project","name":"mc-app","project_id":"other-project","tags":["minicloud"]},
              {"id":"wrong-name","name":"mc-app-copy","project_id":"project-fixture","tags":["minicloud"]},
              {"id":"found","name":"mc-app","project_id":"project-fixture","status":"running","tags":["minicloud","mc-label-app-id-app-fixture"]}
            ]}
            """);

        var result = await CreateClient().FindServerAsync("mc-app");

        Assert.NotNull(result);
        Assert.Equal("fr-par-2:found", result.Id);
        Assert.Equal("app-fixture", result.Labels!["app-id"]);
        Assert.True(_handler.Requests.Count > 2);
        Assert.Contains("project_id=project-fixture", _handler.Requests[0].Uri);
        Assert.Contains("/zones/fr-par-2/servers", _handler.Requests[1].Uri);
    }

    [Fact]
    public async Task GetServerStatusVerifiesProjectAndReturnsPublicAddresses()
    {
        _handler.Queue(HttpStatusCode.OK, """
            {"server":{"id":"instance-3","project_id":"project-fixture","status":"running",
              "public_ips":[{"address":"203.0.113.12","type":"routed_ipv4"},{"address":"2001:db8::12","type":"routed_ipv6"}]}}
            """);

        var status = await CreateClient().GetServerStatusAsync("fr-par-2:instance-3");

        Assert.True(status.IsRunning);
        Assert.Equal("203.0.113.12", status.Ipv4Address);
        Assert.Equal("2001:db8::12", status.Ipv6Address);
        Assert.Contains("/zones/fr-par-2/servers/instance-3", Assert.Single(_handler.Requests).Uri);
    }

    [Fact]
    public async Task DeleteServerIsIdempotentAndPreservesAttachedBlockVolumes()
    {
        _handler.Queue(HttpStatusCode.OK, """
            {"server":{"id":"instance-4","project_id":"project-fixture","status":"stopped"}}
            """);
        _handler.Queue(HttpStatusCode.NoContent, "");

        await CreateClient().DeleteServerAsync("pl-waw-3:instance-4");

        var delete = _handler.Requests[1];
        Assert.Equal(HttpMethod.Delete, delete.Method);
        using var body = JsonDocument.Parse(delete.Body!);
        Assert.True(body.RootElement.GetProperty("delete_all_ips").GetBoolean());
        Assert.False(body.RootElement.GetProperty("delete_all_volumes").GetBoolean());
    }

    [Fact]
    public async Task CreateAndAttachBlockVolumeKeepsExactLabelsAndVerifiesAttachment()
    {
        _handler.Queue(HttpStatusCode.Created, """
            {"volume":{"id":"volume-5","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":20000000000,"status":"creating","tags":["mc-label-managed-by-minicloud","mc-label-app-id-app-fixture","mc-label-storage-name-uploads","mc-label-purpose-customer-storage"],"references":[]}}
            """);
        _handler.Queue(HttpStatusCode.OK, """
            {"volume":{"id":"volume-5","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":20000000000,"status":"available","tags":["mc-label-managed-by-minicloud","mc-label-app-id-app-fixture","mc-label-storage-name-uploads","mc-label-purpose-customer-storage"],"references":[]}}
            """);

        var client = CreateClient();
        var volume = await client.CreateVolumeAsync("mc-volume", 20, "fr-par-1", new Dictionary<string, string>
        {
            ["managed-by"] = "minicloud",
            ["app-id"] = "app-fixture",
            ["storage-name"] = "uploads",
            ["purpose"] = "customer-storage"
        }, default);

        Assert.Equal("fr-par-1:volume-5", volume.Id);
        Assert.Equal("customer-storage", volume.Labels["purpose"]);
        Assert.Equal("uploads", volume.Labels["storage-name"]);
        using (var request = JsonDocument.Parse(_handler.Requests[0].Body!))
        {
            Assert.Equal(ProjectId, request.RootElement.GetProperty("project_id").GetString());
            Assert.Equal(20_000_000_000L, request.RootElement.GetProperty("from_empty").GetProperty("size").GetInt64());
        }

        _handler.Queue(HttpStatusCode.OK, """
            {"volume":{"id":"volume-5","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":20000000000,"status":"available","tags":["mc-label-managed-by-minicloud","mc-label-app-id-app-fixture","mc-label-storage-name-uploads","mc-label-purpose-customer-storage"],"references":[]}}
            """);
        _handler.Queue(HttpStatusCode.OK, """{"server":{"id":"instance-6","project_id":"project-fixture"}}""");
        _handler.Queue(HttpStatusCode.OK, "");
        _handler.Queue(HttpStatusCode.OK, """
            {"volume":{"id":"volume-5","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":20000000000,"status":"in_use","tags":["mc-label-managed-by-minicloud","mc-label-app-id-app-fixture","mc-label-storage-name-uploads","mc-label-purpose-customer-storage"],"references":[{"product_resource_type":"instance","product_resource_id":"instance-6"}]}}
            """);

        await client.AttachVolumeAsync(volume.Id, "fr-par-1:instance-6", default);

        Assert.Contains("/servers/instance-6/attach-volume", _handler.Requests[4].Uri);
        Assert.Contains("\"boot_volume\":false", _handler.Requests[4].Body);
    }

    [Fact]
    public async Task ResizeBlockVolumeOnlyGrowsAndUsesByteSizedPatch()
    {
        _handler.Queue(HttpStatusCode.OK, """
            {"volume":{"id":"volume-8","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":10000000000,"status":"available","tags":[],"references":[]}}
            """);
        _handler.Queue(HttpStatusCode.NoContent, "");
        _handler.Queue(HttpStatusCode.OK, """
            {"volume":{"id":"volume-8","name":"mc-volume","project_id":"project-fixture","zone":"fr-par-1","size":15000000000,"status":"available","tags":[],"references":[]}}
            """);

        await CreateClient().ResizeVolumeAsync("fr-par-1:volume-8", 15, default);

        var patch = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Patch);
        using var body = JsonDocument.Parse(patch.Body!);
        Assert.Equal(15_000_000_000L, body.RootElement.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task CloudInitRefusesToMutateAnInstanceOutsideTheConfiguredProject()
    {
        _handler.Queue(HttpStatusCode.OK, """{"server":{"id":"instance-9","project_id":"other-project"}}""");

        await Assert.ThrowsAsync<ProviderApiException>(() =>
            CreateClient().SetCloudInitDataAsync("fr-par-1:instance-9", "#cloud-config", default));

        Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Method);
    }

    [Fact]
    public async Task ProviderErrorsAreTypedAndNeverExposeTheApiKey()
    {
        _handler.Queue(HttpStatusCode.Forbidden, """{"message":"synthetic-scaleway-secret is forbidden"}""");

        var error = await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().FindServerAsync("missing"));

        Assert.Equal(CloudProvider.Scaleway, error.Provider);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain(SecretKey, error.ToString());
        Assert.DoesNotContain(SecretKey, error.ResponseBody);
    }

    [Fact]
    public async Task CallerCancellationIsPassedToProviderTransport()
    {
        using var cancellation = new CancellationTokenSource();
        _handler.CancelOnSend = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient().GetServerStatusAsync("fr-par-1:instance-7", cancellation.Token));
        Assert.Single(_handler.Requests);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<CapturedRequest> Requests { get; } = [];
        public CancellationTokenSource? CancelOnSend { get; set; }

        public void Queue(HttpStatusCode status, string body) =>
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(),
                request.Headers.GetValues("X-Auth-Token").Single(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (CancelOnSend is not null)
            {
                CancelOnSend.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Token, string? Body);
}
