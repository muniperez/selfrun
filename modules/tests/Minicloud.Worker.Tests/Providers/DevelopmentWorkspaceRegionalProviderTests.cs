using System.Net;
using System.Text;
using System.Text.Json;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Vultr;

namespace Minicloud.Worker.Tests.Providers;

public sealed class DevelopmentWorkspaceRegionalProviderTests
{
    private static Dictionary<string, string> Labels(string kind = "primary") => new()
    {
        ["managed-by"] = "minicloud-workspaces", ["organization"] = "org_test",
        ["workspace"] = "workspace_test", ["kind"] = kind
    };

    private static Dictionary<string, string> ComputeLabels() => new(Labels())
    {
        ["vm"] = "devvm_test", ["generation"] = "g1", ["operation"] = "op_test"
    };

    private static DigitalOceanClient Do(TestHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.digitalocean.com/v2/") },
            new DigitalOceanOptions { ApiToken = "provider-secret" });

    private static VultrClient Vultr(TestHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.vultr.com/v2/") },
            new VultrOptions { ApiKey = "provider-secret" });

    private static string Tags(IReadOnlyDictionary<string, string> labels, params string[] extra) =>
        JsonSerializer.Serialize(labels.Select(p => $"mcw:{p.Key}:{p.Value}").Concat(extra));

    private static string Droplet(IReadOnlyDictionary<string, string> labels, string status = "active", params string[] extraTags) => $$$"""
        {"id":301,"name":"mc-devvm-test-g1","status":"{{{status}}}","size_slug":"s-4vcpu-8gb","region":{"slug":"fra1"},
         "image":{"id":501},"networks":{"v4":[{"type":"private","ip_address":"10.0.0.2"},{"type":"public","ip_address":"198.51.100.10"}]},
         "tags":{{{Tags(labels, extraTags)}}} }
        """;

    private static string DoFirewallTag => "mcw-fw-" + WorkspaceOwnership.Digest(ComputeLabels())[..32];
    private static string DoFirewallName => "mcw-" + WorkspaceOwnership.Digest(ComputeLabels());
    private const string FirewallId = "0d1f1c86-3e1b-4b66-9a33-7e1f6b2c0a11";
    private const string VolumeId = "506f78a4-e098-11e5-ad9f-000f53306ae1";

    [Fact]
    public async Task DigitalOceanDropletIsCreatedWithOwnershipTagsAndItsFirewallTag()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse($$$"""{"firewall":{"id":"{{{FirewallId}}}","name":"{{{DoFirewallName}}}","tags":["{{{DoFirewallTag}}}"],"droplet_ids":[]}}""");
        handler.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels(), "new", DoFirewallTag)}}},"links":{"actions":[{"id":701,"rel":"create"}]}}""",
            HttpStatusCode.Accepted);

        var allocation = await Do(handler).CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "s-4vcpu-8gb", "fra1", "501", "#cloud-config\n", FirewallId, ComputeLabels()), default);

        Assert.Equal(new WorkspaceCloudAction("actions", "701", "running"), allocation.Action);
        Assert.Equal("initializing", allocation.Server.Status);
        Assert.Equal("198.51.100.10", allocation.Server.Ipv4);
        Assert.Null(allocation.Server.PrimaryIpId);
        Assert.True(WorkspaceOwnership.Matches(allocation.Server.Labels, ComputeLabels()));
        var create = handler.RecordedRequests[1];
        Assert.Equal("Bearer provider-secret", create.AuthorizationHeader);
        using var body = JsonDocument.Parse(create.Body!);
        Assert.Equal(501, body.RootElement.GetProperty("image").GetInt64());
        Assert.False(body.RootElement.GetProperty("ipv6").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("ssh_keys", out _));
        Assert.False(body.RootElement.TryGetProperty("volumes", out _));
        var tags = body.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("mcw:workspace:workspace_test", tags);
        Assert.Contains(DoFirewallTag, tags);
    }

    [Fact]
    public async Task DigitalOceanRefusesAFirewallThatDoesNotMatchTheOwnershipDigest()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse($$$"""{"firewall":{"id":"{{{FirewallId}}}","name":"mcw-foreign","tags":["{{{DoFirewallTag}}}"],"droplet_ids":[]}}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(handler).CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "s-4vcpu-8gb", "fra1", "501", "#cloud-config\n", FirewallId, ComputeLabels()), default));
        Assert.Single(handler.RecordedRequests);
    }

    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("waiting", false)]
    public async Task DigitalOceanReportsFirewallsAppliedThroughTheDedicatedTag(string firewallStatus, bool applied)
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels(), "active", DoFirewallTag)}}} }""");
        handler.QueueJsonResponse($$$"""
            {"firewalls":[{"id":"{{{FirewallId}}}","name":"{{{DoFirewallName}}}","status":"{{{firewallStatus}}}","tags":["{{{DoFirewallTag}}}"],"droplet_ids":[]},
                          {"id":"0d1f1c86-3e1b-4b66-9a33-7e1f6b2c0a12","name":"other","status":"succeeded","tags":["other"],"droplet_ids":[]}],
             "links":{}}
            """);

        var server = await Do(handler).GetWorkspaceServerAsync("301", default);

        Assert.Equal("running", server!.Status);
        Assert.Equal([FirewallId], server.FirewallIds);
        Assert.Equal(applied, server.FirewallsApplied);
    }

    [Fact]
    public async Task DigitalOceanFirewallRestrictsSshAndEgressAndIsRecognizedByDigest()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"tag\":{\"name\":\"x\"}}", HttpStatusCode.Created);
        handler.QueueJsonResponse($$$"""{"firewall":{"id":"{{{FirewallId}}}","name":"{{{DoFirewallName}}}","tags":["{{{DoFirewallTag}}}"],"droplet_ids":[]}}""",
            HttpStatusCode.Accepted);

        var firewall = await Do(handler).CreateWorkspaceFirewallAsync("ignored", ["198.51.100.1/32"], ComputeLabels(), default);

        Assert.Equal(FirewallId, firewall.Id);
        Assert.Equal(DoFirewallTag, JsonDocument.Parse(handler.RecordedRequests[0].Body!).RootElement.GetProperty("name").GetString());
        using var body = JsonDocument.Parse(handler.RecordedRequests[1].Body!);
        var inbound = Assert.Single(body.RootElement.GetProperty("inbound_rules").EnumerateArray());
        Assert.Equal("22", inbound.GetProperty("ports").GetString());
        Assert.Equal("198.51.100.1/32", inbound.GetProperty("sources").GetProperty("addresses")[0].GetString());
        Assert.Equal(["80", "443", "53", "53", "123"],
            body.RootElement.GetProperty("outbound_rules").EnumerateArray().Select(x => x.GetProperty("ports").GetString()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Do(new TestHttpMessageHandler()).CreateWorkspaceFirewallAsync("ignored", ["0.0.0.0/0"], ComputeLabels(), default));
    }

    [Fact]
    public async Task DigitalOceanVolumeUsesProviderExt4AndItsByIdDevice()
    {
        var created = new Dictionary<string, string>(Labels()) { ["created-for"] = "op_test" };
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse($$$"""
            {"volume":{"id":"{{{VolumeId}}}","name":"mc-workspace-test","size_gigabytes":20,"region":{"slug":"fra1"},
             "droplet_ids":[],"filesystem_type":"ext4","tags":{{{Tags(created)}}} }}
            """, HttpStatusCode.Created);

        var client = Do(handler);
        var allocation = await client.CreateWorkspaceVolumeAsync("mc-workspace-test", 20, "fra1", created, default);

        Assert.Null(allocation.Action);
        Assert.Equal("ext4", allocation.Volume.Filesystem);
        Assert.Equal("scsi-0DO_Volume_mc-workspace-test", ((IDevelopmentWorkspaceProvider)client).GetWorkspaceVolumeDeviceId(allocation.Volume));
        Assert.False(((IDevelopmentWorkspaceProvider)client).WorkspaceVolumesFormatInGuest);
        Assert.Equal("ext4", JsonDocument.Parse(handler.RecordedRequests[0].Body!).RootElement.GetProperty("filesystem_type").GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateWorkspaceVolumeAsync("Bad_Name", 20, "fra1", created, default));
    }

    [Fact]
    public async Task DigitalOceanAttachUsesAVolumeActionAndRefusesAnotherWriter()
    {
        var handler = new TestHttpMessageHandler();
        var volume = $$$"""{"volume":{"id":"{{{VolumeId}}}","name":"mc-workspace-test","size_gigabytes":20,"region":{"slug":"fra1"},"droplet_ids":[],"filesystem_type":"ext4","tags":{{{Tags(Labels())}}} }}""";
        handler.QueueJsonResponse(volume);
        handler.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels())}}} }""");
        handler.QueueJsonResponse("""{"action":{"id":702,"status":"in-progress"}}""", HttpStatusCode.Accepted);
        handler.QueueJsonResponse("""{"action":{"id":702,"status":"completed"}}""");

        var client = Do(handler);
        var action = await client.AttachWorkspaceVolumeAsync(VolumeId, "301", Labels(), default);

        Assert.Equal(new WorkspaceCloudAction("actions", "702", "running"), action);
        using var body = JsonDocument.Parse(handler.RecordedRequests[2].Body!);
        Assert.Equal("attach", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(301, body.RootElement.GetProperty("droplet_id").GetInt64());
        Assert.Equal("success", (await client.GetWorkspaceActionAsync("actions", "702", default)).Status);

        var busy = new TestHttpMessageHandler();
        busy.QueueJsonResponse(volume.Replace("\"droplet_ids\":[]", "\"droplet_ids\":[999]"));
        busy.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels())}}} }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Do(busy).AttachWorkspaceVolumeAsync(VolumeId, "301", Labels(), default));
    }

    [Fact]
    public async Task DigitalOceanDeleteCompletesOnlyWhenTheDropletIsGone()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels())}}} }""");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueJsonResponse($$$"""{"droplet":{{{Droplet(ComputeLabels(), "archive")}}} }""");

        var client = Do(handler);
        var action = await client.DeleteWorkspaceServerAsync("301", ComputeLabels(), default);
        Assert.Equal("running", (await client.GetWorkspaceActionAsync(action!.Resource, action.Id, default)).Status);
        // The handler returns 404 once its queue is empty.
        Assert.Equal("success", (await client.GetWorkspaceActionAsync(action.Resource, action.Id, default)).Status);
        Assert.Equal(HttpMethod.Delete, handler.RecordedRequests[1].Method);
    }

    [Fact]
    public async Task DigitalOceanImageNormalizesCustomUbuntuImages()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"image":{"id":501,"type":"custom","distribution":"Ubuntu","status":"available","tags":[]}}""");
        var image = await Do(handler).GetWorkspaceImageAsync("501", default);
        Assert.Equal(new[] { "available", "snapshot", "x86", "ubuntu" }, new[] { image!.Status, image.Type, image.Architecture, image.OsFlavor });
    }

    [Fact]
    public async Task DigitalOceanRejectsLabelsThatCannotBeTags()
    {
        var labels = new Dictionary<string, string>(Labels()) { ["workspace"] = "workspace.test" };
        await Assert.ThrowsAsync<ArgumentException>(() => Do(new TestHttpMessageHandler()).FindWorkspaceServersAsync(labels, default));
    }

    private static string Instance(IReadOnlyDictionary<string, string> labels, string status = "active", string power = "running",
        string firewall = "fw-owned") => $$$"""
        {"id":"inst-owned","label":"mc-devvm-test-g1","status":"{{{status}}}","power_status":"{{{power}}}","region":"fra","plan":"vc2-4c-8gb",
         "main_ip":"198.51.100.20","firewall_group_id":"{{{firewall}}}",
         "tags":{{{InstanceTags(labels)}}} }
        """;

    private static string InstanceTags(IReadOnlyDictionary<string, string> labels) =>
        JsonSerializer.Serialize(labels.Select(p => $"mcw:{p.Key}={p.Value}").Append("mcw:image=snap-owned"));

    private static string FirewallGroup(int instances = 0, int rules = 1, string? description = null) => $$$"""
        {"firewall_group":{"id":"fw-owned","description":"{{{description ?? "mcw-" + WorkspaceOwnership.Digest(ComputeLabels())}}}","instance_count":{{{instances}}},"rule_count":{{{rules}}} }}
        """;

    [Fact]
    public async Task VultrInstanceIsCreatedFromTheSnapshotWithItsFirewallGroupAndImageTag()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(FirewallGroup());
        handler.QueueJsonResponse($$$"""{"instance":{{{Instance(ComputeLabels(), "pending", "stopped")}}} }""", HttpStatusCode.Accepted);
        handler.QueueJsonResponse($$$"""{"instance":{{{Instance(ComputeLabels())}}} }""");

        var client = Vultr(handler);
        var allocation = await client.CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "vc2-4c-8gb", "fra", "snap-owned", "#cloud-config\n", "fw-owned", ComputeLabels()), default);

        Assert.Equal("snap-owned", allocation.Server.ImageId);
        Assert.Equal(["fw-owned"], allocation.Server.FirewallIds);
        Assert.True(WorkspaceOwnership.Matches(allocation.Server.Labels, ComputeLabels()));
        Assert.False(allocation.Server.Labels.ContainsKey("image"));
        Assert.Equal("success", (await client.GetWorkspaceActionAsync(allocation.Action.Resource, allocation.Action.Id, default)).Status);
        using var body = JsonDocument.Parse(handler.RecordedRequests[1].Body!);
        Assert.Equal("snap-owned", body.RootElement.GetProperty("snapshot_id").GetString());
        Assert.Equal("fw-owned", body.RootElement.GetProperty("firewall_group_id").GetString());
        Assert.Equal("#cloud-config\n", Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("user_data").GetString()!)));
        Assert.False(body.RootElement.TryGetProperty("sshkey_id", out _));
        Assert.Contains("mcw:image=snap-owned", body.RootElement.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
    }

    [Theory]
    [InlineData(1, "mcw-foreign")]
    [InlineData(0, null)]
    public async Task VultrRefusesAForeignOrEmptyFirewallGroup(int rules, string? description)
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(FirewallGroup(rules: rules, description: description));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Vultr(handler).CreateWorkspaceServerAsync(
            new("mc-devvm-test-g1", "vc2-4c-8gb", "fra", "snap-owned", "#cloud-config\n", "fw-owned", ComputeLabels()), default));
        Assert.Single(handler.RecordedRequests);
    }

    [Fact]
    public void VultrBlockLabelsRoundTripOwnershipAndRejectUnknownKeys()
    {
        var labels = new Dictionary<string, string>(Labels()) { ["created-for"] = "op_test" };
        var encoded = VultrClient.EncodeBlockLabel(labels);
        Assert.Equal("mcw;c=op_test;k=primary;o=org_test;w=workspace_test", encoded);
        Assert.Equal(labels.OrderBy(x => x.Key), VultrClient.DecodeBlockLabel(encoded).OrderBy(x => x.Key));
        Assert.Empty(VultrClient.DecodeBlockLabel("mcw;w=a;w=b"));
        Assert.Empty(VultrClient.DecodeBlockLabel("unmanaged"));
        Assert.Throws<ArgumentException>(() => VultrClient.EncodeBlockLabel(new Dictionary<string, string>(labels) { ["team"] = "x" }));
    }

    [Fact]
    public async Task VultrRawBlockIsFormattedInGuestAndAttachCompletesWhenAttached()
    {
        var labels = new Dictionary<string, string>(Labels()) { ["created-for"] = "op_test" };
        string Block(string attached = "", string status = "active") => $$$"""
            {"block":{"id":"blk-owned","label":"{{{VultrClient.EncodeBlockLabel(labels)}}}","size_gb":20,"region":"fra","status":"{{{status}}}",
             "attached_to_instance":"{{{attached}}}","mount_id":"ewr-2f5d7a314fe44f0a8b"}}
            """;
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(Block(status: "pending"), HttpStatusCode.Accepted);
        handler.QueueJsonResponse(Block());
        handler.QueueJsonResponse($$$"""{"instance":{{{Instance(ComputeLabels())}}} }""");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueJsonResponse(Block("inst-owned"));

        IDevelopmentWorkspaceProvider client = Vultr(handler);
        var allocation = await client.CreateWorkspaceVolumeAsync("mc-workspace-test", 20, "fra", labels, default);
        Assert.Equal("block-create", allocation.Action!.Resource);
        Assert.Null(allocation.Volume.Filesystem);
        Assert.True(client.WorkspaceVolumesFormatInGuest);
        Assert.Equal("virtio-ewr-2f5d7a314fe44f0a", client.GetWorkspaceVolumeDeviceId(allocation.Volume));
        Assert.Equal("high_perf", JsonDocument.Parse(handler.RecordedRequests[0].Body!).RootElement.GetProperty("block_type").GetString());

        var attach = await client.AttachWorkspaceVolumeAsync("blk-owned", "inst-owned", Labels(), default);
        Assert.Equal(new WorkspaceCloudAction("block-attach", "blk-owned/inst-owned", "running"), attach);
        Assert.EndsWith("blocks/blk-owned/attach", handler.RecordedRequests[3].Uri);
        Assert.Equal("success", (await client.GetWorkspaceActionAsync(attach!.Resource, attach.Id, default)).Status);
    }

    [Fact]
    public async Task VultrSnapshotNormalizesUbuntuX64()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"snapshot":{"id":"snap-owned","status":"complete","os_id":2284}}""");
        handler.QueueJsonResponse("""{"os":[{"id":1743,"family":"ubuntu","arch":"x64"},{"id":2284,"family":"ubuntu","arch":"x64"}],"meta":{"links":{"next":""}}}""");
        var image = await Vultr(handler).GetWorkspaceImageAsync("snap-owned", default);
        Assert.Equal(new[] { "available", "snapshot", "x86", "ubuntu" }, new[] { image!.Status, image.Type, image.Architecture, image.OsFlavor });
    }

    [Fact]
    public async Task VultrFirewallGroupAllowsOnlyManagementSshAndCannotBeDeletedWhileInUse()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse(FirewallGroup(rules: 0), HttpStatusCode.Created);
        handler.QueueJsonResponse("""{"firewall_rule":{"id":1}}""", HttpStatusCode.Created);
        handler.QueueJsonResponse("""{"firewall_rule":{"id":2}}""", HttpStatusCode.Created);
        var client = Vultr(handler);

        await client.CreateWorkspaceFirewallAsync("ignored", ["198.51.100.1/32", "2001:db8::1/128"], ComputeLabels(), default);

        using var v4 = JsonDocument.Parse(handler.RecordedRequests[1].Body!);
        Assert.Equal(("v4", "198.51.100.1", 32, "22"), (v4.RootElement.GetProperty("ip_type").GetString(), v4.RootElement.GetProperty("subnet").GetString(),
            v4.RootElement.GetProperty("subnet_size").GetInt32(), v4.RootElement.GetProperty("port").GetString()));
        Assert.Equal("v6", JsonDocument.Parse(handler.RecordedRequests[2].Body!).RootElement.GetProperty("ip_type").GetString());

        var inUse = new TestHttpMessageHandler();
        inUse.QueueJsonResponse(FirewallGroup(instances: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Vultr(inUse).DeleteWorkspaceFirewallAsync("fw-owned", ComputeLabels(), default));
        Assert.Single(inUse.RecordedRequests);
    }
}
