using System.Net;
using System.Text.Json;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;

namespace Minicloud.Worker.Tests.Providers;

public sealed class DevelopmentWorkspaceProviderTests
{
    private static Dictionary<string, string> Labels(string kind = "primary") => new()
    {
        ["managed-by"] = "minicloud-workspaces", ["organization"] = "org_test",
        ["workspace"] = "workspace_test", ["kind"] = kind
    };

    private static HetznerClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new HetznerOptions { ApiToken = "provider-secret", BaseUrl = "https://api.hetzner.cloud/v1/" });

    private static string Server(string kind = "primary", string workspace = "workspace_test") =>
        JsonSerializer.Serialize(new
        {
            id = 301, name = "mc-devvm-test-g1", status = "running",
            datacenter = new { location = new { name = "ash" } }, server_type = new { name = "standard-test" },
            image = new { id = 501 },
            public_net = new { ipv4 = new { ip = "198.51.100.10", id = 401 }, firewalls = new[] { new { id = 601, status = "applied" } } },
            labels = new Dictionary<string, string>(Labels(kind)) { ["workspace"] = workspace }
        });

    private static string Volume(string? server = null, IReadOnlyDictionary<string, string>? labels = null) =>
        JsonSerializer.Serialize(new
        {
            id = 201, name = "mc-workspace-test", size = 20, format = "ext4",
            location = new { name = "ash" }, server = server is null ? (long?)null : long.Parse(server),
            linux_device = "/dev/disk/by-id/scsi-0HC_Volume_201", labels = labels ?? Labels()
        });

    [Fact]
    public async Task WorkspaceCreateIsNotRetriedAfterAmbiguousProviderFailure()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"error":{"message":"user_data could contain enrollment-secret"}}""", HttpStatusCode.ServiceUnavailable);
        using var retry = new ResilientHttpRetryHandler(handler, baseDelay: TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<ProviderApiException>(() => Client(retry).CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "standard-test", "ash", "501", "enrollment-secret", "601", Labels("ephemeral")), default));
        Assert.Single(handler.RecordedRequests);
        Assert.DoesNotContain("enrollment-secret", error.ToString());
        Assert.DoesNotContain("provider-secret", error.ToString());
    }

    [Fact]
    public async Task AllocationCarriesFirewallAndNoStorageOrPrivateNetwork()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"server\":" + Server("ephemeral") + ",\"action\":{\"id\":701,\"status\":\"running\"}}");
        var allocation = await Client(handler).CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "standard-test", "ash", "501", "#cloud-config\n", "601", Labels("ephemeral")), default);
        Assert.Equal("701", allocation.Action.Id);
        Assert.True(allocation.Server.FirewallsApplied);
        Assert.Contains("601", allocation.Server.FirewallIds!);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RecordedRequests).Body!);
        Assert.Equal(601, payload.RootElement.GetProperty("firewalls")[0].GetProperty("firewall").GetInt64());
        Assert.False(payload.RootElement.TryGetProperty("volumes", out _));
        Assert.False(payload.RootElement.TryGetProperty("networks", out _));
        Assert.False(payload.RootElement.GetProperty("public_net").GetProperty("enable_ipv6").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentServerLocationSupportsAllocationAndRecoveryDiscovery(bool ipv4Pending)
    {
        var server = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Server())!;
        server.Remove("datacenter");
        server["location"] = JsonSerializer.SerializeToElement(new { id = 4, name = "ash" });
        if (ipv4Pending)
        {
            server["status"] = JsonSerializer.SerializeToElement("initializing");
            server["public_net"] = JsonSerializer.SerializeToElement(new
            {
                ipv4 = (object?)null,
                firewalls = new[] { new { id = 601, status = "pending" } }
            });
        }
        var body = JsonSerializer.Serialize(server);
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"server\":" + body + ",\"action\":{\"id\":701,\"status\":\"running\"}}");
        handler.QueueJsonResponse("{\"servers\":[" + body + "]}");
        handler.QueueJsonResponse("{\"server\":" + body + "}");
        var client = Client(handler);
        var allocated = await client.CreateWorkspaceServerAsync(
            new("mc-workspacevm-synthetic-g1", "standard-test", "ash", "501", "#cloud-config\n", "601", Labels()), default);
        var discovered = Assert.Single(await client.FindWorkspaceServersAsync(Labels(), default));
        var fetched = await client.GetWorkspaceServerAsync("301", default);
        foreach (var result in new[] { allocated.Server, discovered, fetched! })
        {
            Assert.Equal("ash", result.Region);
            Assert.Equal("301", result.Id);
            Assert.Equal(ipv4Pending ? null : "401", result.PrimaryIpId);
            Assert.Equal(!ipv4Pending, result.FirewallsApplied);
        }
        Assert.Equal("701", allocated.Action.Id);
        Assert.Single(handler.RecordedRequests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task VolumeCreationExposesFormattingActionsAndNeverAutomounts()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volume\":" + Volume() + ",\"action\":null,\"next_actions\":[{\"id\":702,\"status\":\"running\"}]}");
        var result = await Client(handler).CreateWorkspaceVolumeAsync("mc-workspace-test", 20, "ash", Labels(), default);
        Assert.Null(result.Action);
        Assert.Equal("702", Assert.Single(result.NextActions).Id);
        Assert.Equal("ext4", result.Volume.Filesystem);
        using var payload = JsonDocument.Parse(Assert.Single(handler.RecordedRequests).Body!);
        Assert.False(payload.RootElement.GetProperty("automount").GetBoolean());
        Assert.Equal("ext4", payload.RootElement.GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("ephemeral", "workspace_test")]
    [InlineData("primary", "workspace_other")]
    public async Task VolumeCannotAttachToEphemeralOrDifferentWorkspace(string kind, string workspace)
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volume\":" + Volume() + "}");
        handler.QueueJsonResponse("{\"server\":" + Server(kind, workspace) + "}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).AttachWorkspaceVolumeAsync("201", "301", Labels(), default));
        Assert.All(handler.RecordedRequests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task RefusesDetachingAReplacementWriter()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volume\":" + Volume("999") + "}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).DetachWorkspaceVolumeAsync("201", "301", Labels(), default));
        Assert.Single(handler.RecordedRequests);
    }

    [Theory]
    [InlineData("managed-by", "ordinary-app")]
    [InlineData("organization", "org_other")]
    [InlineData("workspace", "workspace_other")]
    [InlineData("kind", "ephemeral")]
    [InlineData(null, null)]
    public async Task PermanentVolumeDeletionRefusesMismatchedOwnershipOrAnAttachedWriter(string? key, string? value)
    {
        var handler = new TestHttpMessageHandler();
        var labels = Labels();
        if (key is not null) labels[key] = value!;
        handler.QueueJsonResponse("{\"volume\":" + Volume(server: key is null ? "301" : null, labels: labels) + "}");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client(handler).DeleteWorkspaceVolumeAsync("201", Labels(), default));

        var request = Assert.Single(handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/volumes/201", new Uri(request.Uri).AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PermanentVolumeDeletionAcceptsSuccessOrDisappearanceAfterTheOwnershipRead(HttpStatusCode status)
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volume\":" + Volume() + "}");
        handler.QueueResponse(new HttpResponseMessage(status));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);

        await client.DeleteWorkspaceVolumeAsync("201", Labels(), default);
        await client.DeleteWorkspaceVolumeAsync("201", Labels(), default);

        Assert.Equal(new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Get },
            handler.RecordedRequests.Select(x => x.Method));
        Assert.All(handler.RecordedRequests, request => Assert.Equal("/v1/volumes/201", new Uri(request.Uri).AbsolutePath));
    }

    [Fact]
    public async Task ExactOwnershipIsRequiredBeforeAnyServerDeletion()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"server\":" + Server(workspace: "workspace_other") + "}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).DeleteWorkspaceServerAsync("301", Labels(), default));
        Assert.Single(handler.RecordedRequests);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await Client(handler).DeleteWorkspaceServerAsync("301", Labels(), default));
        Assert.DoesNotContain(handler.RecordedRequests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task DiscoveryFiltersUnrelatedInventoryAndPreservesAmbiguity()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"servers\":[" + Server() + "," + Server(workspace: "workspace_other") + "," + Server() + "]}");
        var matches = await Client(handler).FindWorkspaceServersAsync(Labels(), default);
        Assert.Equal(2, matches.Count);
        Assert.Contains("label_selector=", Assert.Single(handler.RecordedRequests).Uri);
    }

    [Fact]
    public async Task FirewallAllowsOnlyManagementSshAndBoundedEgressProtocols()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(JsonSerializer.Serialize(new { firewall = new { id = 601, name = "mc-devvm-test-g1", labels = Labels(), applied_to = Array.Empty<object>() } }));
        await Client(handler).CreateWorkspaceFirewallAsync("mc-devvm-test-g1", ["198.51.100.20/32"], Labels(), default);
        using var body = JsonDocument.Parse(Assert.Single(handler.RecordedRequests).Body!);
        var rules = body.RootElement.GetProperty("rules").EnumerateArray().ToArray();
        var inbound = Assert.Single(rules, rule => rule.GetProperty("direction").GetString() == "in");
        Assert.Equal("22", inbound.GetProperty("port").GetString());
        Assert.Equal("198.51.100.20/32", inbound.GetProperty("source_ips")[0].GetString());
        Assert.DoesNotContain(rules, rule => rule.GetProperty("direction").GetString() == "out" && rule.GetProperty("port").GetString() == "22");
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("198.51.100.0/24")]
    [InlineData("127.0.0.1/32")]
    public async Task BroadOrLoopbackManagementFirewallRuleIsRejected(string cidr)
    {
        var handler = new TestHttpMessageHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateWorkspaceFirewallAsync("mc-devvm-test-g1", [cidr], Labels(), default));
        Assert.Empty(handler.RecordedRequests);
    }

    [Fact]
    public async Task AssignedOrUnownedPrimaryIpIsNeverDeleted()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(JsonSerializer.Serialize(new { primary_ip = new { id = 401, assignee_id = 301, labels = Labels() } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).DeleteWorkspacePrimaryIpAsync("401", Labels(), default));
        Assert.Single(handler.RecordedRequests);
    }
}
