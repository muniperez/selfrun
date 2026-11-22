using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Linode;
using Minicloud.Common.Providers.Models;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public sealed class LinodeClientTests
{
    private const string Token = "linode-fixture-token-123456";
    private const string OwnerTag = "mc-owner-0123456789abcdef0123456789abcdef";
    private static readonly string ServerOwnerTag = ProviderStorageVolume.OwnershipTag(new Dictionary<string, string>
    {
        ["managed-by"] = "minicloud",
        ["server-name"] = "minicloud-org-app"
    });
    private readonly TestHttpMessageHandler _handler = new();

    private LinodeClient CreateClient() => new(new HttpClient(_handler)
    {
        BaseAddress = new Uri("https://api.linode.com/v4/")
    }, new LinodeOptions { ApiToken = Token });

    [Fact]
    public async Task DiscoveryUsesExactLabelFilterAndDoesNotAdoptUnrelatedInstances()
    {
        _handler.QueueResponse(request =>
        {
            Assert.Equal("Bearer " + Token, request.Headers.Authorization?.ToString());
            Assert.Equal("{\"label\":\"minicloud-org-app\"}", Assert.Single(request.Headers.GetValues("X-Filter")));
            return Json($$"""{"data":[{"id":7,"label":"another-app","status":"running"},{"id":8,"label":"minicloud-org-app","status":"running","ipv4":["192.168.1.4","203.0.113.8"],"ipv6":"2600:3c03::1/64","tags":["minicloud","{{ServerOwnerTag}}"]}],"page":1,"pages":1,"results":2}""");
        });

        var server = await CreateClient().FindServerAsync("minicloud-org-app");

        Assert.NotNull(server);
        Assert.Equal("8", server.Id);
        Assert.Equal("203.0.113.8", server.Ipv4Address);
        Assert.Equal("2600:3c03::1", server.Ipv6Address);
        Assert.Empty(server.Labels!);
    }

    [Fact]
    public async Task DiscoveryRejectsDuplicateLabelsAcrossPages()
    {
        _handler.QueueJsonResponse($$"""{"data":[{"id":7,"label":"same","tags":["minicloud","{{OwnerTag}}"]}],"page":1,"pages":2,"results":2}""");
        _handler.QueueJsonResponse($$"""{"data":[{"id":8,"label":"same","tags":["minicloud","{{OwnerTag}}"]}],"page":2,"pages":2,"results":2}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().FindServerAsync("same"));
        Assert.Equal(2, _handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task CreateServerResolvesProviderKeyAndEncodesCloudInitMetadata()
    {
        const string publicKey = "ssh-ed25519 AAAAC3Nza fixture@host";
        const string userData = "#cloud-config\npackages: [docker.io]";
        var labels = new Dictionary<string, string> { ["managed-by"] = "minicloud", ["app"] = "org-app" };
        _handler.QueueJsonResponse("""{"data":[{"id":33,"label":"minicloud","ssh_key":"ssh-ed25519 AAAAC3Nza fixture@host"}],"page":1,"pages":1,"results":1}""");
        _handler.QueueJsonResponse($$"""{"id":99,"label":"minicloud-org-app","status":"provisioning","ipv4":[],"tags":["minicloud","{{ServerOwnerTag}}"]}""", HttpStatusCode.OK);

        var server = await CreateClient().CreateServerAsync(new CreateServerRequest(
            "minicloud-org-app", "g6-standard-1", "us-east", ["33"], userData,
            Labels: labels, Image: "linode/ubuntu24.04", SshPublicKey: publicKey));

        Assert.Equal("99", server.Id);
        Assert.Equal("minicloud-org-app", server.Name);
        Assert.Equal(2, _handler.RecordedRequests.Count);
        Assert.Contains("/profile/sshkeys", _handler.RecordedRequests[0].Uri);
        using var payload = JsonDocument.Parse(_handler.RecordedRequests[1].Body!);
        var root = payload.RootElement;
        Assert.Equal("g6-standard-1", root.GetProperty("type").GetString());
        Assert.Equal("us-east", root.GetProperty("region").GetString());
        Assert.Equal("linode/ubuntu24.04", root.GetProperty("image").GetString());
        Assert.True(root.GetProperty("booted").GetBoolean());
        Assert.Contains(ServerOwnerTag, root.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.False(root.TryGetProperty("private_ip", out _));
        Assert.Equal(new[] { publicKey }, root.GetProperty("authorized_keys").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(userData)), root.GetProperty("metadata").GetProperty("user_data").GetString());
    }

    [Fact]
    public async Task CreateWithoutAnySshKeyFailsBeforeProviderAllocation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().CreateServerAsync(
            new CreateServerRequest("minicloud-org-app", "g6-standard-1", "us-east", [], "#cloud-config")));
        Assert.Empty(_handler.RecordedRequests);
    }

    [Fact]
    public async Task StatusSelectsPublicAddressesAndNotPrivateAddress()
    {
        _handler.QueueJsonResponse($$"""{"id":99,"label":"minicloud-org-app","status":"running","ipv4":["10.0.0.4","203.0.113.9"],"ipv6":"2600:3c03::9/64","tags":["minicloud","{{ServerOwnerTag}}"]}""");

        var status = await CreateClient().GetServerStatusAsync("99");

        Assert.True(status.IsRunning);
        Assert.Equal("203.0.113.9", status.Ipv4Address);
        Assert.Equal("2600:3c03::9", status.Ipv6Address);
    }

    [Fact]
    public async Task StatusDoesNotExposeUniqueLocalIpv6AsPublic()
    {
        _handler.QueueJsonResponse($$"""{"id":99,"label":"minicloud-org-app","status":"running","ipv4":[],"ipv6":"fd12:3456::9/64","tags":["minicloud","{{ServerOwnerTag}}"]}""");

        var status = await CreateClient().GetServerStatusAsync("99");

        Assert.Null(status.Ipv6Address);
    }

    [Fact]
    public async Task CreateQuotaFailureIsNotRetriedAndRedactsCredential()
    {
        _handler.QueueJsonResponse("""{"errors":[{"reason":"linode-fixture-token-123456 quota exceeded"}]}""", HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().CreateServerAsync(
            new CreateServerRequest("minicloud-org-app", "g6-standard-1", "us-east", [], "#cloud-config", SshPublicKey: "ssh-ed25519 AAAA fixture")));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(CloudProvider.Linode, error.Provider);
        Assert.DoesNotContain(Token, error.ResponseBody);
        Assert.Single(_handler.RecordedRequests);
    }

    [Fact]
    public async Task DiscoveryFailsClosedForAnUnownedMatchingLabel()
    {
        _handler.QueueJsonResponse("""{"data":[{"id":7,"label":"minicloud-org-app"}],"page":1,"pages":1,"results":1}""");

        await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().FindServerAsync("minicloud-org-app"));
        Assert.Single(_handler.RecordedRequests);
    }

    [Fact]
    public async Task DeleteRefusesAnUnownedLinodeBeforeIssuingDelete()
    {
        _handler.QueueJsonResponse("""{"id":99,"label":"unrelated-server","tags":[]}""");

        await Assert.ThrowsAsync<ProviderApiException>(() => CreateClient().DeleteServerAsync("99"));
        Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, _handler.RecordedRequests[0].Method);
    }

    [Fact]
    public async Task CreateVolumeUsesDeterministicShortLabelAndOwnerTagThenWaitsUntilActive()
    {
        var storageName = new string('a', 35);
        var expectedLabel = "mc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storageName))).ToLowerInvariant()[..29];
        var volumeJson = $$"""{"id":201,"label":"{{expectedLabel}}","size":20,"region":"us-east","status":"creating","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_{{expectedLabel}}","io_ready":false,"tags":["minicloud"]}""";
        var readyJson = $$"""{"id":201,"label":"{{expectedLabel}}","size":20,"region":"us-east","status":"active","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_{{expectedLabel}}","io_ready":false,"tags":["minicloud"]}""";
        var labels = new Dictionary<string, string>
        {
            ["managed-by"] = "minicloud", ["app-id"] = "app_fixture", ["storage-name"] = "uploads", ["purpose"] = "customer-storage"
        };
        _handler.QueueJsonResponse(volumeJson, HttpStatusCode.OK);
        _handler.QueueJsonResponse(readyJson);

        var volume = await CreateClient().CreateVolumeAsync(storageName, 20, "us-east", labels, default);

        Assert.Equal("201", volume.Id);
        Assert.Equal(expectedLabel, volume.Name);
        Assert.Equal($"/dev/disk/by-id/scsi-0Linode_Volume_{expectedLabel}", volume.FilesystemPath);
        using var body = JsonDocument.Parse(_handler.RecordedRequests[0].Body!);
        Assert.Equal(32, body.RootElement.GetProperty("label").GetString()!.Length);
        Assert.Contains(ProviderStorageVolume.OwnershipTag(labels), body.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("mc-format-pending", body.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("/volumes/201", _handler.RecordedRequests[1].Uri);
    }

    [Fact]
    public async Task FilesystemInitializationRemovesOnlyPendingTagForOwnedVolume()
    {
        var ownerTag = ProviderStorageVolume.OwnershipTag(new Dictionary<string, string>
        {
            ["managed-by"] = "minicloud", ["app-id"] = "app_fixture", ["storage-name"] = "uploads", ["purpose"] = "customer-storage"
        });
        _handler.QueueJsonResponse($$"""{"id":201,"label":"mc-volume","tags":["minicloud","{{ownerTag}}","mc-format-pending","operator-tag"]}""");
        _handler.QueueJsonResponse("""{"id":201,"label":"mc-volume","tags":["minicloud","operator-tag"]}""");

        await CreateClient().MarkFilesystemInitializedAsync("201", ownerTag, default);

        using var body = JsonDocument.Parse(_handler.RecordedRequests[1].Body!);
        var tags = body.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "minicloud", ownerTag, "operator-tag" }, tags);
    }

    [Fact]
    public async Task AttachAndResizeUseNonRetriedMutationRequestsAndVerifyReadyState()
    {
        var detached = $$"""{"id":201,"label":"mc-volume","size":20,"region":"us-east","status":"active","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_mc-volume","io_ready":false,"tags":["minicloud","{{OwnerTag}}"]}""";
        var attached = $$"""{"id":201,"label":"mc-volume","size":20,"region":"us-east","linode_id":99,"status":"active","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_mc-volume","io_ready":true,"tags":["minicloud","{{OwnerTag}}"]}""";
        var resized = $$"""{"id":201,"label":"mc-volume","size":30,"region":"us-east","linode_id":99,"status":"active","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_mc-volume","io_ready":true,"tags":["minicloud","{{OwnerTag}}"]}""";
        _handler.QueueJsonResponse(detached);
        _handler.QueueJsonResponse("""{"id":201,"label":"mc-volume"}""");
        _handler.QueueJsonResponse(attached);
        _handler.QueueJsonResponse(detached);
        _handler.QueueJsonResponse("""{"id":201,"label":"mc-volume","size":30}""");
        _handler.QueueJsonResponse(resized);

        var client = CreateClient();
        await client.AttachVolumeAsync("201", "99", default);
        await client.ResizeVolumeAsync("201", 30, default);

        Assert.EndsWith("/volumes/201/attach", _handler.RecordedRequests[1].Uri);
        Assert.Contains("\"linode_id\":99", _handler.RecordedRequests[1].Body);
        Assert.EndsWith("/volumes/201/resize", _handler.RecordedRequests[4].Uri);
        Assert.Contains("\"size\":30", _handler.RecordedRequests[4].Body);
        Assert.Equal(6, _handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task ResizeRefusesUnownedVolumeAndCannotShrink()
    {
        _handler.QueueJsonResponse("""{"id":201,"label":"unowned","size":30,"region":"us-east","status":"active","tags":[]}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().ResizeVolumeAsync("201", 40, default));
        Assert.Single(_handler.RecordedRequests);

        _handler.QueueJsonResponse($$"""{"id":201,"label":"owned","size":30,"region":"us-east","status":"active","tags":["minicloud","{{OwnerTag}}"]}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().ResizeVolumeAsync("201", 20, default));
        Assert.Equal(2, _handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task CanceledVolumeCreationDoesNotContinuePollingOrRetry()
    {
        using var cts = new CancellationTokenSource();
        _handler.QueueResponse(_ =>
        {
            cts.Cancel();
            return Json("""{"id":201,"label":"mc-volume","size":20,"region":"us-east","status":"creating","filesystem_path":"/dev/disk/by-id/scsi-0Linode_Volume_mc-volume","io_ready":false}""");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateClient().CreateVolumeAsync(
            "mc-volume", 20, "us-east", new Dictionary<string, string>(), cts.Token));
        Assert.Single(_handler.RecordedRequests);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
