using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;

namespace Minicloud.Common.Providers.Vultr;

/// <summary>
/// Dev environment resources on Vultr. Instances carry <c>mcw:key=value</c> tags. Block storage has only a
/// label, so ownership is encoded compactly in it. Firewall groups are matched by a label digest in their
/// description. Vultr has no action API, so actions are synthetic and resolve by polling the resource.
/// Vultr block storage is raw: the guest creates ext4 once on a blank volume this operation created.
/// </summary>
public sealed partial class VultrClient : IDevelopmentWorkspaceProvider
{
    private const string TagPrefix = "mcw:";
    private const string LabelPrefix = "mcw;";
    private const int MaxBlockLabelLength = 200;
    private static readonly Regex WorkspaceHostname = new("^[a-zA-Z0-9][a-zA-Z0-9.-]{0,61}[a-zA-Z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex VultrId = new("^[a-zA-Z0-9-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> ShortKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["organization"] = "o", ["workspace"] = "w", ["kind"] = "k", ["created-for"] = "c",
        ["vm"] = "v", ["generation"] = "g", ["operation"] = "p"
    };

    public string GetWorkspaceVolumeDeviceId(WorkspaceCloudVolume volume) => volume.Device["/dev/disk/by-id/".Length..];

    public bool WorkspaceVolumesFormatInGuest => true;

    public async Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var tag = Uri.EscapeDataString(InstanceTags(labels).Single(t => t.StartsWith(TagPrefix + "workspace=", StringComparison.Ordinal)));
        return (await VultrListAsync("instances", $"instances?tag={tag}", ct)).Select(ReadInstance)
            .Where(server => WorkspaceOwnership.Matches(server.Labels, labels)).ToArray();
    }

    public async Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct)
    {
        using var doc = await VultrRequestAsync(HttpMethod.Get, "instances/" + VultrResourceId(id), null, ct);
        return doc is null ? null : ReadInstance(doc.RootElement.GetProperty("instance"));
    }

    public async Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct)
    {
        var tags = InstanceTags(request.Labels).Append(TagPrefix + "image=" + VultrResourceId(request.ImageId)).ToArray();
        if (!WorkspaceHostname.IsMatch(request.Name) || Encoding.UTF8.GetByteCount(request.UserData) > 65536)
            throw new ArgumentException("Workspace server name or bootstrap size is invalid.");
        var firewall = await GetFirewallGroupAsync(request.FirewallId, ct) ?? throw new InvalidOperationException("Workspace firewall is missing.");
        if (firewall.Description != FirewallDescription(request.Labels))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        if (firewall.Rules < 1) throw new InvalidOperationException("Workspace firewall has no management rule.");
        using var doc = await VultrRequestAsync(HttpMethod.Post, "instances", new
        {
            region = request.Region, plan = request.Sku, snapshot_id = VultrResourceId(request.ImageId),
            label = request.Name, hostname = request.Name,
            user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserData)),
            firewall_group_id = VultrResourceId(request.FirewallId), enable_ipv6 = false, backups = "disabled",
            activation_email = false, tags
        }, ct);
        var server = ReadInstance(doc!.RootElement.GetProperty("instance"));
        WorkspaceOwnership.Require(server.Labels, request.Labels);
        return new(server, new("instance-create", server.Id, "running"));
    }

    public async Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var server = await GetWorkspaceServerAsync(id, ct);
        if (server is null) return null;
        WorkspaceOwnership.Require(server.Labels, labels);
        using var deleted = await VultrRequestAsync(HttpMethod.Delete, "instances/" + VultrResourceId(id), null, ct);
        return new("instance-delete", server.Id, "running");
    }

    // An instance's main IPv4 is released with the instance; there is no separately billed address to protect.
    public Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.Require(server.Labels, labels);
        if (string.IsNullOrEmpty(server.Ipv4)) throw new InvalidOperationException("Workspace IPv4 identity is unavailable.");
        return Task.CompletedTask;
    }

    public Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct) => Task.CompletedTask;

    public async Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct)
    {
        using var doc = await VultrRequestAsync(HttpMethod.Get, "snapshots/" + VultrResourceId(id), null, ct);
        if (doc is null) return null;
        var snapshot = doc.RootElement.GetProperty("snapshot");
        string? family = null, architecture = null;
        if (snapshot.TryGetProperty("os_id", out var osId) && osId.ValueKind == JsonValueKind.Number)
        {
            foreach (var os in await VultrListAsync("os", "os", ct))
            {
                if (os.GetProperty("id").GetInt32() != osId.GetInt32()) continue;
                family = os.TryGetProperty("family", out var f) ? f.GetString() : null;
                architecture = os.TryGetProperty("arch", out var a) ? a.GetString() : null;
            }
        }
        return new(snapshot.GetProperty("id").GetString()!,
            snapshot.GetProperty("status").GetString() == "complete" ? "available" : snapshot.GetProperty("status").GetString()!,
            "snapshot", architecture == "x64" ? "x86" : architecture ?? "", family, new Dictionary<string, string>());
    }

    public async Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct)
    {
        switch (resource)
        {
            case "instance-create":
                var created = await GetWorkspaceServerAsync(id, ct) ?? throw new InvalidOperationException("Allocated workspace VM disappeared.");
                return new(resource, id, created.Status == "running" ? "success" : "running");
            case "instance-delete":
                return new(resource, id, await GetWorkspaceServerAsync(id, ct) is null ? "success" : "running");
            case "block-create":
                using (var doc = await VultrRequestAsync(HttpMethod.Get, "blocks/" + VultrResourceId(id), null, ct)
                    ?? throw new InvalidOperationException("Retained volume disappeared."))
                    return new(resource, id, doc.RootElement.GetProperty("block").GetProperty("status").GetString() == "active" ? "success" : "running");
            case "block-attach":
            case "block-detach":
                var parts = id.Split('/', 2);
                var volume = await GetWorkspaceVolumeAsync(parts[0], ct) ?? throw new InvalidOperationException("Retained volume disappeared.");
                var done = resource == "block-attach" ? parts.Length == 2 && volume.ServerId == parts[1] : volume.ServerId is null;
                return new(resource, id, done ? "success" : "running");
            default:
                throw new ArgumentException("Unsupported workspace action resource.");
        }
    }

    public async Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EncodeBlockLabel(labels);
        return (await VultrListAsync("blocks", "blocks", ct)).Select(ReadBlock)
            .Where(volume => WorkspaceOwnership.Matches(volume.Labels, labels)).ToArray();
    }

    public async Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct)
    {
        using var doc = await VultrRequestAsync(HttpMethod.Get, "blocks/" + VultrResourceId(id), null, ct);
        return doc is null ? null : ReadBlock(doc.RootElement.GetProperty("block"));
    }

    public async Task DeleteWorkspaceVolumeAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.RequireRetainedVolumeLabels(labels);
        var volume = await GetWorkspaceVolumeAsync(id, ct);
        if (volume is null) return;
        WorkspaceOwnership.Require(volume.Labels, labels);
        if (volume.Id != id || volume.ServerId is not null)
            throw new InvalidOperationException("Refusing to delete an unexpected or attached workspace volume.");
        using var deleted = await VultrRequestAsync(HttpMethod.Delete, "blocks/" + VultrResourceId(id), null, ct);
    }

    public async Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(
        string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var label = EncodeBlockLabel(labels);
        if (sizeGb is < 10 or > 10000) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        using var doc = await VultrRequestAsync(HttpMethod.Post, "blocks",
            new { region, size_gb = sizeGb, label, block_type = "high_perf" }, ct);
        var volume = ReadBlock(doc!.RootElement.GetProperty("block"));
        WorkspaceOwnership.Require(volume.Labels, labels);
        return new(volume, new("block-create", volume.Id, "running"), []);
    }

    public async Task<WorkspaceCloudAction?> AttachWorkspaceVolumeAsync(
        string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var volume = await GetWorkspaceVolumeAsync(id, ct) ?? throw new InvalidOperationException("Retained volume is missing.");
        WorkspaceOwnership.Require(volume.Labels, labels);
        if (!labels.TryGetValue("kind", out var kind) || kind != "primary")
            throw new InvalidOperationException("Only a primary VM may attach retained workspace storage.");
        var server = await GetWorkspaceServerAsync(serverId, ct) ?? throw new InvalidOperationException("Primary VM is missing.");
        WorkspaceOwnership.Require(server.Labels, labels);
        if (volume.ServerId == serverId) return null;
        if (volume.ServerId is not null) throw new InvalidOperationException("Retained volume still has a different writer.");
        using var attached = await VultrRequestAsync(HttpMethod.Post, "blocks/" + VultrResourceId(id) + "/attach",
            new { instance_id = VultrResourceId(serverId), live = true }, ct);
        return new("block-attach", volume.Id + "/" + server.Id, "running");
    }

    public async Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(
        string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var volume = await GetWorkspaceVolumeAsync(id, ct) ?? throw new InvalidOperationException("Retained volume is missing.");
        WorkspaceOwnership.Require(volume.Labels, labels);
        if (volume.ServerId is null) return null;
        if (volume.ServerId != serverId) throw new InvalidOperationException("Refusing to detach a replacement writer.");
        using var detached = await VultrRequestAsync(HttpMethod.Post, "blocks/" + VultrResourceId(id) + "/detach", new { live = true }, ct);
        return new("block-detach", volume.Id, "running");
    }

    public async Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var description = FirewallDescription(labels);
        return (await VultrListAsync("firewall_groups", "firewalls", ct)).Select(ReadFirewallGroup)
            .Where(group => group.Description == description)
            .Select(group => new WorkspaceCloudFirewall(group.Id, group.Description, group.Instances, labels)).ToArray();
    }

    // Vultr firewall groups filter inbound traffic only; outbound traffic is not restricted.
    public async Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.RequireManagementAddresses(managementAddresses);
        var description = FirewallDescription(labels);
        using var doc = await VultrRequestAsync(HttpMethod.Post, "firewalls", new { description }, ct);
        var group = ReadFirewallGroup(doc!.RootElement.GetProperty("firewall_group"));
        if (group.Description != description)
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        await AddFirewallRulesAsync(group.Id, managementAddresses, ct);
        return new(group.Id, group.Description, group.Instances, labels);
    }

    // Rules are added one request at a time, so a retry must finish what an interrupted create started.
    public async Task EnsureWorkspaceFirewallRulesAsync(string id, IReadOnlyList<string> managementAddresses,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.RequireManagementAddresses(managementAddresses);
        var group = await GetFirewallGroupAsync(id, ct) ?? throw new InvalidOperationException("Workspace firewall is missing.");
        if (group.Description != FirewallDescription(labels))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        var expected = managementAddresses.Select(ManagementRule).ToHashSet();
        var existing = (await VultrListAsync("firewall_rules", "firewalls/" + VultrResourceId(id) + "/rules", ct))
            .Select(rule => (
                IpType: rule.TryGetProperty("ip_type", out var t) ? t.GetString() : rule.TryGetProperty("type", out var u) ? u.GetString() : null,
                Protocol: rule.TryGetProperty("protocol", out var p) ? p.GetString()?.ToLowerInvariant() : null,
                Subnet: rule.TryGetProperty("subnet", out var s) && System.Net.IPAddress.TryParse(s.GetString(), out var ip) ? ip.ToString() : null,
                Size: rule.TryGetProperty("subnet_size", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt32() : -1,
                Port: rule.TryGetProperty("port", out var o) ? o.GetString() : null)).ToArray();
        if (existing.Any(rule => !expected.Contains(rule)))
            throw new InvalidOperationException("Workspace firewall has rules Minicloud did not create; review it before retrying.");
        await AddFirewallRulesAsync(id, managementAddresses.Where(address => !existing.Contains(ManagementRule(address))).ToArray(), ct);
    }

    private static (string? IpType, string? Protocol, string? Subnet, int Size, string? Port) ManagementRule(string address)
    {
        var parts = address.Split('/');
        var ip = System.Net.IPAddress.Parse(parts[0]);
        return (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "v6" : "v4", "tcp", ip.ToString(),
            int.Parse(parts[1], CultureInfo.InvariantCulture), "22");
    }

    private async Task AddFirewallRulesAsync(string id, IReadOnlyList<string> managementAddresses, CancellationToken ct)
    {
        foreach (var address in managementAddresses)
        {
            var rule = ManagementRule(address);
            using var created = await VultrRequestAsync(HttpMethod.Post, "firewalls/" + VultrResourceId(id) + "/rules", new
            {
                ip_type = rule.IpType, protocol = rule.Protocol, subnet = rule.Subnet,
                subnet_size = rule.Size, port = rule.Port, notes = "minicloud management"
            }, ct);
        }
    }

    public async Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var group = await GetFirewallGroupAsync(id, ct);
        if (group is null) return;
        if (group.Description != FirewallDescription(labels))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        if (group.Instances != 0) throw new InvalidOperationException("Workspace firewall still protects a server.");
        using var deleted = await VultrRequestAsync(HttpMethod.Delete, "firewalls/" + VultrResourceId(id), null, ct);
    }

    private sealed record VultrFirewallGroup(string Id, string Description, int Instances, int Rules);

    private async Task<VultrFirewallGroup?> GetFirewallGroupAsync(string id, CancellationToken ct)
    {
        using var doc = await VultrRequestAsync(HttpMethod.Get, "firewalls/" + VultrResourceId(id), null, ct);
        return doc is null ? null : ReadFirewallGroup(doc.RootElement.GetProperty("firewall_group"));
    }

    private static VultrFirewallGroup ReadFirewallGroup(JsonElement value) =>
        new(value.GetProperty("id").GetString()!, value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            value.TryGetProperty("instance_count", out var i) ? i.GetInt32() : 0,
            value.TryGetProperty("rule_count", out var r) ? r.GetInt32() : 0);

    private static string FirewallDescription(IReadOnlyDictionary<string, string> labels) => "mcw-" + WorkspaceOwnership.Digest(labels);

    private static string[] InstanceTags(IReadOnlyDictionary<string, string> labels)
    {
        WorkspaceOwnership.Validate(labels);
        return labels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => TagPrefix + p.Key + "=" + p.Value).ToArray();
    }

    private static Dictionary<string, string> InstanceLabels(JsonElement value)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!value.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return labels;
        foreach (var tag in tags.EnumerateArray().Select(t => t.GetString() ?? ""))
        {
            if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            var parts = tag[TagPrefix.Length..].Split('=', 2);
            if (parts.Length == 2 && !labels.TryAdd(parts[0], parts[1])) labels[parts[0]] = "";
        }
        return labels;
    }

    public static string EncodeBlockLabel(IReadOnlyDictionary<string, string> labels)
    {
        WorkspaceOwnership.Validate(labels);
        var parts = new List<string>();
        foreach (var (key, value) in labels.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (key == "managed-by") continue;
            if (!ShortKeys.TryGetValue(key, out var shortKey))
                throw new ArgumentException("Workspace ownership labels cannot be represented as a Vultr block label.");
            parts.Add(shortKey + "=" + value);
        }
        var label = LabelPrefix + string.Join(';', parts);
        return label.Length <= MaxBlockLabelLength ? label
            : throw new ArgumentException("Workspace ownership labels exceed the Vultr block label length.");
    }

    public static Dictionary<string, string> DecodeBlockLabel(string? label)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (label is null || !label.StartsWith(LabelPrefix, StringComparison.Ordinal)) return labels;
        labels["managed-by"] = "minicloud-workspaces";
        foreach (var part in label[LabelPrefix.Length..].Split(';'))
        {
            var pair = part.Split('=', 2);
            var key = pair.Length == 2 ? ShortKeys.FirstOrDefault(k => k.Value == pair[0]).Key : null;
            if (key is null || !labels.TryAdd(key, pair[1])) return [];
        }
        return labels;
    }

    private static WorkspaceCloudServer ReadInstance(JsonElement value)
    {
        var labels = InstanceLabels(value);
        labels.Remove("image", out var image);
        var mainIp = value.TryGetProperty("main_ip", out var ip) ? ip.GetString() : null;
        var instanceStatus = value.GetProperty("status").GetString();
        var power = value.TryGetProperty("power_status", out var p) ? p.GetString() : null;
        var status = instanceStatus switch
        {
            "active" when power == "running" => "running",
            "active" => power ?? "off",
            "pending" => "initializing",
            var other => other ?? "unknown"
        };
        var firewall = value.TryGetProperty("firewall_group_id", out var fw) ? fw.GetString() : null;
        IReadOnlyList<string> firewalls = string.IsNullOrEmpty(firewall) ? [] : [firewall];
        return new(value.GetProperty("id").GetString()!, value.GetProperty("label").GetString()!, status,
            value.GetProperty("region").GetString()!, value.GetProperty("plan").GetString()!, image ?? "",
            string.IsNullOrEmpty(mainIp) || mainIp == "0.0.0.0" ? null : mainIp, null, labels,
            firewalls, firewalls.Count > 0);
    }

    private static WorkspaceCloudVolume ReadBlock(JsonElement value)
    {
        var attached = value.TryGetProperty("attached_to_instance", out var a) ? a.GetString() : null;
        var mountId = value.TryGetProperty("mount_id", out var m) ? m.GetString() ?? "" : "";
        // virtio-blk exposes at most 20 serial characters under /dev/disk/by-id.
        var serial = mountId.Length > 20 ? mountId[..20] : mountId;
        return new(value.GetProperty("id").GetString()!, value.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            value.GetProperty("size_gb").GetInt32(), value.GetProperty("region").GetString()!,
            string.IsNullOrEmpty(attached) ? null : attached, null, "/dev/disk/by-id/virtio-" + serial,
            DecodeBlockLabel(value.TryGetProperty("label", out var label) ? label.GetString() : null));
    }

    private async Task<IReadOnlyList<JsonElement>> VultrListAsync(string property, string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        var cursor = "";
        for (var page = 1; page <= 100; page++)
        {
            var url = $"{path}{separator}per_page=100" + (cursor.Length > 0 ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            using var doc = await VultrRequestAsync(HttpMethod.Get, url, null, ct)
                ?? throw new InvalidOperationException("Workspace provider inventory is unavailable.");
            foreach (var item in doc.RootElement.GetProperty(property).EnumerateArray()) items.Add(item.Clone());
            cursor = doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("links", out var links) &&
                     links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString()! : "";
            if (cursor.Length == 0) return items;
        }
        throw new InvalidOperationException("Workspace provider inventory exceeded its bounded page limit.");
    }

    private async Task<JsonDocument?> VultrRequestAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        request.Options.Set(HetznerClient.NoRetry, method != HttpMethod.Get);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && (method == HttpMethod.Get || method == HttpMethod.Delete)) return null;
        if (!response.IsSuccessStatusCode)
            throw new ProviderApiException($"Vultr workspace request failed with HTTP {(int)response.StatusCode}.", response.StatusCode, Provider);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return null;
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string VultrResourceId(string id) =>
        VultrId.IsMatch(id) ? id : throw new ArgumentException("Invalid provider resource identity.");
}
