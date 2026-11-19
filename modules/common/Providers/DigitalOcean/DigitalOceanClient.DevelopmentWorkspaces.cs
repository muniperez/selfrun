using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;

namespace Minicloud.Common.Providers.DigitalOcean;

/// <summary>
/// Dev environment resources on DigitalOcean. Ownership labels become <c>mcw:key:value</c> tags on droplets
/// and volumes. Cloud Firewalls carry no tags of their own, so each one is named after the label digest and
/// applies through a dedicated tag that the droplet receives at creation, so it is protected from first boot.
/// </summary>
public sealed partial class DigitalOceanClient : IDevelopmentWorkspaceProvider
{
    private const string TagPrefix = "mcw:";
    private const string FirewallTagPrefix = "mcw-fw-";
    private const string DeleteResource = "droplet-delete";
    private static readonly Regex WorkspaceHostname = new("^[a-zA-Z0-9][a-zA-Z0-9.-]{0,61}[a-zA-Z0-9]$", RegexOptions.CultureInvariant);
    private static readonly Regex VolumeName = new("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex TagPart = new("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly string[] AnyAddress = ["0.0.0.0/0", "::/0"];

    public string GetWorkspaceVolumeDeviceId(WorkspaceCloudVolume volume) => "scsi-0DO_Volume_" + volume.Name;

    public async Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var tag = Uri.EscapeDataString(WorkspaceTags(labels).Single(t => t.StartsWith(TagPrefix + "workspace:", StringComparison.Ordinal)));
        return (await DoListAsync("droplets", $"droplets?tag_name={tag}", ct))
            .Select(droplet => ReadDroplet(droplet, null))
            .Where(server => WorkspaceOwnership.Matches(server.Labels, labels)).ToArray();
    }

    public async Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Get, "droplets/" + DoNumericId(id), null, ct);
        if (doc is null) return null;
        var droplet = doc.RootElement.GetProperty("droplet");
        var firewallTags = Tags(droplet).Where(tag => tag.StartsWith(FirewallTagPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var firewalls = firewallTags.Count == 0 ? [] : (await DoListAsync("firewalls", "firewalls", ct))
            .Where(firewall => Tags(firewall).Any(firewallTags.Contains) ||
                               firewall.TryGetProperty("droplet_ids", out var ids) && ids.EnumerateArray().Any(x => DoJsonId(x) == DoJsonId(droplet.GetProperty("id"))))
            .ToArray();
        return ReadDroplet(droplet, firewalls);
    }

    public async Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct)
    {
        var tags = WorkspaceTags(request.Labels);
        if (!WorkspaceHostname.IsMatch(request.Name) || Encoding.UTF8.GetByteCount(request.UserData) > 65536)
            throw new ArgumentException("Workspace server name or bootstrap size is invalid.");
        var firewall = await GetFirewallAsync(request.FirewallId, ct) ?? throw new InvalidOperationException("Workspace firewall is missing.");
        if (firewall.Name != FirewallName(request.Labels)) throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        var firewallTag = FirewallTag(request.Labels);
        if (!firewall.Tags.Contains(firewallTag)) throw new InvalidOperationException("Workspace firewall does not protect its dedicated tag.");
        using var doc = await DoRequestAsync(HttpMethod.Post, "droplets", new
        {
            name = request.Name, region = request.Region, size = request.Sku,
            image = long.Parse(DoNumericId(request.ImageId), CultureInfo.InvariantCulture),
            user_data = request.UserData, ipv6 = false, monitoring = false, backups = false, with_droplet_agent = false,
            tags = tags.Append(firewallTag).ToArray()
        }, ct);
        var server = ReadDroplet(doc!.RootElement.GetProperty("droplet"), null);
        WorkspaceOwnership.Require(server.Labels, request.Labels);
        var action = doc.RootElement.TryGetProperty("links", out var links) && links.TryGetProperty("actions", out var actions)
            ? actions.EnumerateArray().Select(a => new WorkspaceCloudAction("actions", DoJsonId(a.GetProperty("id")), "running")).FirstOrDefault()
            : null;
        return new(server, action ?? new WorkspaceCloudAction("actions-none", server.Id, "success"));
    }

    public async Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var server = await GetWorkspaceServerAsync(id, ct);
        if (server is null) return null;
        WorkspaceOwnership.Require(server.Labels, labels);
        using var deleted = await DoRequestAsync(HttpMethod.Delete, "droplets/" + DoNumericId(id), null, ct);
        return new(DeleteResource, server.Id, "running");
    }

    // A droplet's public IPv4 is released with the droplet; there is no separately billed address to protect.
    public Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.Require(server.Labels, labels);
        if (string.IsNullOrEmpty(server.Ipv4)) throw new InvalidOperationException("Workspace IPv4 identity is unavailable.");
        return Task.CompletedTask;
    }

    public Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct) => Task.CompletedTask;

    public async Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Get, "images/" + DoNumericId(id), null, ct);
        if (doc is null) return null;
        var image = doc.RootElement.GetProperty("image");
        var type = image.TryGetProperty("type", out var kind) ? kind.GetString() : null;
        var distribution = image.TryGetProperty("distribution", out var os) ? os.GetString() : null;
        return new(DoJsonId(image.GetProperty("id")),
            image.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            type is "snapshot" or "custom" ? "snapshot" : type ?? "",
            // DigitalOcean images are x86_64 only.
            "x86",
            string.Equals(distribution, "Ubuntu", StringComparison.OrdinalIgnoreCase) ? "ubuntu" : distribution?.ToLowerInvariant(),
            TagLabels(image));
    }

    public async Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct)
    {
        switch (resource)
        {
            case "actions":
                using (var doc = await DoRequestAsync(HttpMethod.Get, "actions/" + DoNumericId(id), null, ct)
                    ?? throw new InvalidOperationException("Workspace provider action cannot be verified."))
                {
                    var status = doc.RootElement.GetProperty("action").GetProperty("status").GetString();
                    return new(resource, id, status switch { "completed" => "success", "errored" => "error", _ => "running" });
                }
            case "actions-none":
                return new(resource, id, "success");
            case DeleteResource:
                using (var doc = await DoRequestAsync(HttpMethod.Get, "droplets/" + DoNumericId(id), null, ct))
                    return new(resource, id, doc is null ? "success" : "running");
            default:
                throw new ArgumentException("Unsupported workspace action resource.");
        }
    }

    public async Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceTags(labels);
        return (await DoListAsync("volumes", "volumes", ct)).Select(ReadVolume)
            .Where(volume => WorkspaceOwnership.Matches(volume.Labels, labels)).ToArray();
    }

    public async Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Get, "volumes/" + DoUuid(id), null, ct);
        return doc is null ? null : ReadVolume(doc.RootElement.GetProperty("volume"));
    }

    public async Task DeleteWorkspaceVolumeAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.RequireRetainedVolumeLabels(labels);
        var volume = await GetWorkspaceVolumeAsync(id, ct);
        if (volume is null) return;
        WorkspaceOwnership.Require(volume.Labels, labels);
        if (volume.Id != id || volume.ServerId is not null)
            throw new InvalidOperationException("Refusing to delete an unexpected or attached workspace volume.");
        using var deleted = await DoRequestAsync(HttpMethod.Delete, "volumes/" + DoUuid(id), null, ct);
    }

    public async Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(
        string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var tags = WorkspaceTags(labels);
        if (sizeGb is < 10 or > 16384) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        if (!VolumeName.IsMatch(name)) throw new ArgumentException("Workspace volume name is invalid.");
        using var doc = await DoRequestAsync(HttpMethod.Post, "volumes", new
        {
            name, size_gigabytes = sizeGb, region, filesystem_type = "ext4", tags,
            description = "Minicloud dev environment storage"
        }, ct);
        var volume = ReadVolume(doc!.RootElement.GetProperty("volume"));
        WorkspaceOwnership.Require(volume.Labels, labels);
        // Volume creation is synchronous; there is no action to wait for.
        return new(volume, null, []);
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
        return await VolumeActionAsync(id, "attach", serverId, volume.Region, ct);
    }

    public async Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(
        string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var volume = await GetWorkspaceVolumeAsync(id, ct) ?? throw new InvalidOperationException("Retained volume is missing.");
        WorkspaceOwnership.Require(volume.Labels, labels);
        if (volume.ServerId is null) return null;
        if (volume.ServerId != serverId) throw new InvalidOperationException("Refusing to detach a replacement writer.");
        return await VolumeActionAsync(id, "detach", serverId, volume.Region, ct);
    }

    public async Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var name = FirewallName(labels);
        return (await DoListAsync("firewalls", "firewalls", ct)).Select(ReadFirewall)
            .Where(firewall => firewall.Name == name)
            .Select(firewall => new WorkspaceCloudFirewall(firewall.Id, firewall.Name, firewall.Attached, labels)).ToArray();
    }

    public async Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        WorkspaceOwnership.RequireManagementAddresses(managementAddresses);
        var tag = FirewallTag(labels);
        using (var created = await DoRequestAsync(HttpMethod.Post, "tags", new { name = tag }, ct, tolerateUnprocessable: true)) { }
        using var doc = await DoRequestAsync(HttpMethod.Post, "firewalls", new
        {
            name = FirewallName(labels),
            inbound_rules = new[] { new { protocol = "tcp", ports = "22", sources = new { addresses = managementAddresses } } },
            outbound_rules = new object[]
            {
                new { protocol = "tcp", ports = "80", destinations = new { addresses = AnyAddress } },
                new { protocol = "tcp", ports = "443", destinations = new { addresses = AnyAddress } },
                new { protocol = "tcp", ports = "53", destinations = new { addresses = AnyAddress } },
                new { protocol = "udp", ports = "53", destinations = new { addresses = AnyAddress } },
                new { protocol = "udp", ports = "123", destinations = new { addresses = AnyAddress } }
            },
            tags = new[] { tag }
        }, ct);
        var firewall = ReadFirewall(doc!.RootElement.GetProperty("firewall"));
        if (firewall.Name != FirewallName(labels) || !firewall.Tags.Contains(tag))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        return new(firewall.Id, firewall.Name, firewall.Attached, labels);
    }

    public async Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var firewall = await GetFirewallAsync(id, ct);
        if (firewall is null) return;
        if (firewall.Name != FirewallName(labels))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
        var tag = FirewallTag(labels);
        if (firewall.Attached != 0 || (await DoListAsync("droplets", "droplets?tag_name=" + Uri.EscapeDataString(tag), ct)).Count != 0)
            throw new InvalidOperationException("Workspace firewall still protects a server.");
        using (var deleted = await DoRequestAsync(HttpMethod.Delete, "firewalls/" + DoUuid(id), null, ct)) { }
        using (var tagDeleted = await DoRequestAsync(HttpMethod.Delete, "tags/" + Uri.EscapeDataString(tag), null, ct)) { }
    }

    private async Task<WorkspaceCloudAction> VolumeActionAsync(string id, string type, string serverId, string region, CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Post, "volumes/" + DoUuid(id) + "/actions", new
        {
            type, droplet_id = long.Parse(DoNumericId(serverId), CultureInfo.InvariantCulture), region
        }, ct);
        return new("actions", DoJsonId(doc!.RootElement.GetProperty("action").GetProperty("id")), "running");
    }

    private sealed record DoFirewall(string Id, string Name, int Attached, IReadOnlyList<string> Tags);

    private async Task<DoFirewall?> GetFirewallAsync(string id, CancellationToken ct)
    {
        using var doc = await DoRequestAsync(HttpMethod.Get, "firewalls/" + DoUuid(id), null, ct);
        return doc is null ? null : ReadFirewall(doc.RootElement.GetProperty("firewall"));
    }

    private static DoFirewall ReadFirewall(JsonElement value) =>
        new(value.GetProperty("id").GetString()!, value.GetProperty("name").GetString()!,
            value.TryGetProperty("droplet_ids", out var ids) && ids.ValueKind == JsonValueKind.Array ? ids.GetArrayLength() : 0,
            Tags(value));

    private static string FirewallName(IReadOnlyDictionary<string, string> labels) => "mcw-" + WorkspaceOwnership.Digest(labels);
    private static string FirewallTag(IReadOnlyDictionary<string, string> labels) => FirewallTagPrefix + WorkspaceOwnership.Digest(labels)[..32];

    private static string[] WorkspaceTags(IReadOnlyDictionary<string, string> labels)
    {
        WorkspaceOwnership.Validate(labels);
        if (labels.Any(pair => !TagPart.IsMatch(pair.Key) || !TagPart.IsMatch(pair.Value)))
            throw new ArgumentException("Workspace ownership labels cannot be represented as DigitalOcean tags.");
        return labels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => TagPrefix + p.Key + ":" + p.Value).ToArray();
    }

    private static IReadOnlyList<string> Tags(JsonElement value) =>
        value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray().Select(t => t.GetString() ?? "").ToArray() : [];

    private static Dictionary<string, string> TagLabels(JsonElement value)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in Tags(value))
        {
            if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            var parts = tag[TagPrefix.Length..].Split(':', 2);
            if (parts.Length == 2 && !labels.TryAdd(parts[0], parts[1])) labels[parts[0]] = "";
        }
        return labels;
    }

    private static WorkspaceCloudServer ReadDroplet(JsonElement value, IReadOnlyList<JsonElement>? firewalls)
    {
        var ipv4 = value.TryGetProperty("networks", out var networks) && networks.TryGetProperty("v4", out var v4)
            ? v4.EnumerateArray().Where(n => n.GetProperty("type").GetString() == "public")
                .Select(n => n.GetProperty("ip_address").GetString()).FirstOrDefault()
            : null;
        var status = value.GetProperty("status").GetString() switch
        {
            "active" => "running", "new" => "initializing", "archive" => "deleting", var other => other ?? "unknown"
        };
        var image = value.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object && img.TryGetProperty("id", out var imageId)
            ? DoJsonId(imageId) : "";
        var region = value.GetProperty("region").GetProperty("slug").GetString()!;
        var applied = firewalls?.Select(ReadFirewallState).ToArray() ?? [];
        return new(DoJsonId(value.GetProperty("id")), value.GetProperty("name").GetString()!, status, region,
            value.GetProperty("size_slug").GetString()!, image, ipv4, null, TagLabels(value),
            applied.Select(f => f.Id).ToArray(), applied.Length > 0 && applied.All(f => f.Succeeded));
    }

    private static (string Id, bool Succeeded) ReadFirewallState(JsonElement value) =>
        (value.GetProperty("id").GetString()!, value.TryGetProperty("status", out var status) && status.GetString() == "succeeded");

    private static WorkspaceCloudVolume ReadVolume(JsonElement value)
    {
        var droplets = value.TryGetProperty("droplet_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(DoJsonId).ToArray() : [];
        if (droplets.Length > 1) throw new InvalidOperationException("Workspace volume has multiple writers.");
        var name = value.GetProperty("name").GetString()!;
        var filesystem = value.TryGetProperty("filesystem_type", out var fs) ? fs.GetString() : null;
        return new(value.GetProperty("id").GetString()!, name, value.GetProperty("size_gigabytes").GetInt32(),
            value.GetProperty("region").GetProperty("slug").GetString()!, droplets.SingleOrDefault(),
            string.IsNullOrEmpty(filesystem) ? null : filesystem, "/dev/disk/by-id/scsi-0DO_Volume_" + name, TagLabels(value));
    }

    private async Task<IReadOnlyList<JsonElement>> DoListAsync(string property, string path, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        for (var page = 1; page <= 100; page++)
        {
            using var doc = await DoRequestAsync(HttpMethod.Get, $"{path}{separator}per_page=200&page={page}", null, ct)
                ?? throw new InvalidOperationException("Workspace provider inventory is unavailable.");
            foreach (var item in doc.RootElement.GetProperty(property).EnumerateArray()) items.Add(item.Clone());
            if (!doc.RootElement.TryGetProperty("links", out var links) || !links.TryGetProperty("pages", out var pages) ||
                !pages.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                return items;
        }
        throw new InvalidOperationException("Workspace provider inventory exceeded its bounded page limit.");
    }

    private async Task<JsonDocument?> DoRequestAsync(HttpMethod method, string path, object? payload, CancellationToken ct,
        bool tolerateUnprocessable = false)
    {
        using var request = CreateRequest(method, path);
        request.Options.Set(HetznerClient.NoRetry, method != HttpMethod.Get);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && (method == HttpMethod.Get || method == HttpMethod.Delete)) return null;
        if (tolerateUnprocessable && response.StatusCode == HttpStatusCode.UnprocessableEntity) return null;
        if (!response.IsSuccessStatusCode)
            throw new ProviderApiException($"DigitalOcean workspace request failed with HTTP {(int)response.StatusCode}.", response.StatusCode, Provider);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return null;
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string DoNumericId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Invalid provider resource identity.");

    private static string DoUuid(string id) =>
        Guid.TryParseExact(id, "D", out var value) ? value.ToString("D") : throw new ArgumentException("Invalid provider resource identity.");

    private static string DoJsonId(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetInt64().ToString(CultureInfo.InvariantCulture) : value.GetString()!;
}
