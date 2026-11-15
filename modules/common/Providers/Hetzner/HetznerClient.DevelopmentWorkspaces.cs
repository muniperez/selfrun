using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Minicloud.Common.Providers.Common;

namespace Minicloud.Common.Providers.Hetzner;

public sealed partial class HetznerClient : IDevelopmentWorkspaceProvider
{
    private static readonly Regex WorkspaceLabel = new("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$", RegexOptions.CultureInvariant);
    private static readonly Regex WorkspaceHostname = new("^[a-zA-Z0-9][a-zA-Z0-9.-]{0,61}[a-zA-Z0-9]$", RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        (await WorkspaceListAsync("servers", labels, ct)).Select(ReadWorkspaceServer).ToArray();

    public async Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct)
    {
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, "servers/" + NumericWorkspaceId(id), null, ct);
        return doc is null ? null : ReadWorkspaceServer(doc.RootElement.GetProperty("server"));
    }

    public async Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct)
    {
        ValidateWorkspaceLabels(request.Labels);
        if (!WorkspaceHostname.IsMatch(request.Name) || Encoding.UTF8.GetByteCount(request.UserData) > 32768)
            throw new ArgumentException("Workspace server name or bootstrap size is invalid.");
        using var doc = await WorkspaceRequestAsync(HttpMethod.Post, "servers", new
        {
            name = request.Name, server_type = request.Sku, location = request.Region,
            image = long.Parse(NumericWorkspaceId(request.ImageId), CultureInfo.InvariantCulture),
            user_data = request.UserData, start_after_create = true,
            public_net = new { enable_ipv4 = true, enable_ipv6 = false },
            firewalls = new[] { new { firewall = long.Parse(NumericWorkspaceId(request.FirewallId), CultureInfo.InvariantCulture) } },
            labels = request.Labels
        }, ct);
        var server = ReadWorkspaceServer(doc!.RootElement.GetProperty("server"));
        RequireWorkspaceOwnership(server.Labels, request.Labels);
        return new(server, ReadWorkspaceAction("servers", doc.RootElement.GetProperty("action")));
    }

    public async Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var server = await GetWorkspaceServerAsync(id, ct);
        if (server is null) return null;
        RequireWorkspaceOwnership(server.Labels, labels);
        using var doc = await WorkspaceRequestAsync(HttpMethod.Delete, "servers/" + NumericWorkspaceId(id), null, ct);
        return doc is null ? null : ReadWorkspaceAction("servers", doc.RootElement.GetProperty("action"));
    }

    public async Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        RequireWorkspaceOwnership(server.Labels, labels);
        if (server.PrimaryIpId is null) throw new InvalidOperationException("Workspace IPv4 identity is unavailable.");
        var path = "primary_ips/" + NumericWorkspaceId(server.PrimaryIpId);
        using var original = await WorkspaceRequestAsync(HttpMethod.Get, path, null, ct)
            ?? throw new InvalidOperationException("Workspace IPv4 disappeared.");
        var ip = original.RootElement.GetProperty("primary_ip");
        if (ip.GetProperty("assignee_type").GetString() != "server" ||
            ip.GetProperty("assignee_id").GetInt64().ToString(CultureInfo.InvariantCulture) != server.Id)
            throw new InvalidOperationException("Workspace IPv4 is not assigned to its recorded server.");
        using var updated = await WorkspaceRequestAsync(HttpMethod.Put, path, new { auto_delete = true, labels }, ct);
        using var verified = await WorkspaceRequestAsync(HttpMethod.Get, path, null, ct)
            ?? throw new InvalidOperationException("Workspace IPv4 disappeared during ownership verification.");
        var result = verified.RootElement.GetProperty("primary_ip");
        RequireWorkspaceOwnership(WorkspaceLabels(result), labels);
        if (!result.GetProperty("auto_delete").GetBoolean())
            throw new InvalidOperationException("Workspace IPv4 automatic cleanup was not confirmed.");
    }

    public async Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var path = "primary_ips/" + NumericWorkspaceId(id);
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, path, null, ct);
        if (doc is null) return;
        var ip = doc.RootElement.GetProperty("primary_ip");
        RequireWorkspaceOwnership(WorkspaceLabels(ip), labels);
        if (ip.TryGetProperty("assignee_id", out var assigned) && assigned.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Refusing to delete an assigned workspace IPv4 address.");
        using var deleted = await WorkspaceRequestAsync(HttpMethod.Delete, path, null, ct);
    }

    public async Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct)
    {
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, "images/" + NumericWorkspaceId(id), null, ct);
        if (doc is null) return null;
        var image = doc.RootElement.GetProperty("image");
        return new(NumericJsonId(image.GetProperty("id")), image.GetProperty("status").GetString()!,
            image.GetProperty("type").GetString()!, image.GetProperty("architecture").GetString()!,
            image.TryGetProperty("os_flavor", out var os) ? os.GetString() : null, WorkspaceLabels(image));
    }

    public async Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct)
    {
        if (resource is not ("servers" or "volumes" or "firewalls" or "primary_ips"))
            throw new ArgumentException("Unsupported workspace action resource.");
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, resource + "/actions/" + NumericWorkspaceId(id), null, ct)
            ?? throw new InvalidOperationException("Workspace provider action cannot be verified.");
        return ReadWorkspaceAction(resource, doc.RootElement.GetProperty("action"));
    }

    public async Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        (await WorkspaceListAsync("volumes", labels, ct)).Select(ReadWorkspaceVolume).ToArray();

    public async Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct)
    {
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, "volumes/" + NumericWorkspaceId(id), null, ct);
        return doc is null ? null : ReadWorkspaceVolume(doc.RootElement.GetProperty("volume"));
    }

    public async Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(
        string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ValidateWorkspaceLabels(labels);
        if (sizeGb is < 10 or > 10000) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        using var doc = await WorkspaceRequestAsync(HttpMethod.Post, "volumes",
            new { name, size = sizeGb, location = region, format = "ext4", automount = false, labels }, ct);
        var volume = ReadWorkspaceVolume(doc!.RootElement.GetProperty("volume"));
        RequireWorkspaceOwnership(volume.Labels, labels);
        var next = doc.RootElement.TryGetProperty("next_actions", out var actions)
            ? actions.EnumerateArray().Select(value => ReadWorkspaceAction("volumes", value)).ToArray()
            : [];
        var action = doc.RootElement.TryGetProperty("action", out var pending) && pending.ValueKind != JsonValueKind.Null
            ? ReadWorkspaceAction("volumes", pending) : null;
        return new(volume, action, next);
    }

    public async Task<WorkspaceCloudAction?> AttachWorkspaceVolumeAsync(
        string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var volume = await GetWorkspaceVolumeAsync(id, ct) ?? throw new InvalidOperationException("Retained volume is missing.");
        RequireWorkspaceOwnership(volume.Labels, labels);
        if (!labels.TryGetValue("kind", out var kind) || kind != "primary")
            throw new InvalidOperationException("Only a primary VM may attach retained workspace storage.");
        var server = await GetWorkspaceServerAsync(serverId, ct) ?? throw new InvalidOperationException("Primary VM is missing.");
        RequireWorkspaceOwnership(server.Labels, labels);
        if (volume.ServerId == serverId) return null;
        if (volume.ServerId is not null) throw new InvalidOperationException("Retained volume still has a different writer.");
        using var doc = await WorkspaceRequestAsync(HttpMethod.Post, "volumes/" + NumericWorkspaceId(id) + "/actions/attach",
            new { server = long.Parse(NumericWorkspaceId(serverId), CultureInfo.InvariantCulture), automount = false }, ct);
        return ReadWorkspaceAction("volumes", doc!.RootElement.GetProperty("action"));
    }

    public async Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(
        string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var volume = await GetWorkspaceVolumeAsync(id, ct) ?? throw new InvalidOperationException("Retained volume is missing.");
        RequireWorkspaceOwnership(volume.Labels, labels);
        if (volume.ServerId is null) return null;
        if (volume.ServerId != serverId) throw new InvalidOperationException("Refusing to detach a replacement writer.");
        using var doc = await WorkspaceRequestAsync(HttpMethod.Post, "volumes/" + NumericWorkspaceId(id) + "/actions/detach", new { }, ct);
        return ReadWorkspaceAction("volumes", doc!.RootElement.GetProperty("action"));
    }

    public async Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        (await WorkspaceListAsync("firewalls", labels, ct)).Select(ReadWorkspaceFirewall).ToArray();

    public async Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ValidateWorkspaceLabels(labels);
        if (managementAddresses.Count is < 1 or > 8 || managementAddresses.Any(address =>
            !IPAddress.TryParse(address.Split('/')[0], out var ip) ||
            address != ip + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128") ||
            IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
            throw new ArgumentException("Workspace management rules require explicit control-plane host CIDRs.");
        var rules = new List<object>
        {
            new { direction = "in", protocol = "tcp", port = "22", source_ips = managementAddresses },
            new { direction = "out", protocol = "tcp", port = "80", destination_ips = new[] { "0.0.0.0/0", "::/0" } },
            new { direction = "out", protocol = "tcp", port = "443", destination_ips = new[] { "0.0.0.0/0", "::/0" } },
            new { direction = "out", protocol = "tcp", port = "53", destination_ips = new[] { "0.0.0.0/0", "::/0" } },
            new { direction = "out", protocol = "udp", port = "53", destination_ips = new[] { "0.0.0.0/0", "::/0" } },
            new { direction = "out", protocol = "udp", port = "123", destination_ips = new[] { "0.0.0.0/0", "::/0" } }
        };
        using var doc = await WorkspaceRequestAsync(HttpMethod.Post, "firewalls", new { name, labels, rules }, ct);
        var firewall = ReadWorkspaceFirewall(doc!.RootElement.GetProperty("firewall"));
        RequireWorkspaceOwnership(firewall.Labels, labels);
        return firewall;
    }

    public async Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var path = "firewalls/" + NumericWorkspaceId(id);
        using var doc = await WorkspaceRequestAsync(HttpMethod.Get, path, null, ct);
        if (doc is null) return;
        var firewall = ReadWorkspaceFirewall(doc.RootElement.GetProperty("firewall"));
        RequireWorkspaceOwnership(firewall.Labels, labels);
        if (firewall.AttachedServers != 0) throw new InvalidOperationException("Workspace firewall still protects a server.");
        using var deleted = await WorkspaceRequestAsync(HttpMethod.Delete, path, null, ct);
    }

    private async Task<IReadOnlyList<JsonElement>> WorkspaceListAsync(string resource, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ValidateWorkspaceLabels(labels);
        var selector = Uri.EscapeDataString(string.Join(",", labels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
        var items = new List<JsonElement>();
        for (var page = 1; page <= 100; page++)
        {
            using var doc = await WorkspaceRequestAsync(HttpMethod.Get, $"{resource}?label_selector={selector}&per_page=50&page={page}", null, ct)
                ?? throw new InvalidOperationException("Workspace provider inventory is unavailable.");
            foreach (var item in doc.RootElement.GetProperty(resource).EnumerateArray())
            {
                var actual = WorkspaceLabels(item);
                if (labels.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value))
                    items.Add(item.Clone());
            }
            if (!doc.RootElement.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("pagination", out var pagination) ||
                !pagination.TryGetProperty("next_page", out var next) || next.ValueKind == JsonValueKind.Null)
                return items;
        }
        throw new InvalidOperationException("Workspace provider inventory exceeded its bounded page limit.");
    }

    private async Task<JsonDocument?> WorkspaceRequestAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        request.Options.Set(NoRetry, method != HttpMethod.Get);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound && (method == HttpMethod.Get || method == HttpMethod.Delete)) return null;
        if (!response.IsSuccessStatusCode)
            throw new ProviderApiException($"Hetzner workspace request failed with HTTP {(int)response.StatusCode}.", response.StatusCode, Provider);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return null;
        await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public static void RequireWorkspaceOwnership(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected)
    {
        ValidateWorkspaceLabels(expected);
        if (!expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
    }

    private static void ValidateWorkspaceLabels(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count < 4 || !labels.TryGetValue("managed-by", out var product) || product != "minicloud-workspaces" ||
            !labels.ContainsKey("organization") || !labels.ContainsKey("workspace") ||
            labels.Any(pair => !WorkspaceLabel.IsMatch(pair.Key) || !WorkspaceLabel.IsMatch(pair.Value)))
            throw new ArgumentException("Explicit workspace ownership labels are required.");
    }

    private static string NumericWorkspaceId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Invalid provider resource identity.");

    private static string NumericJsonId(JsonElement value) => value.GetInt64().ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> WorkspaceLabels(JsonElement value) =>
        value.TryGetProperty("labels", out var labels) ? labels.Deserialize<Dictionary<string, string>>() ?? [] : [];

    private static WorkspaceCloudAction ReadWorkspaceAction(string resource, JsonElement value) =>
        new(resource, NumericJsonId(value.GetProperty("id")), value.GetProperty("status").GetString()!);

    private static WorkspaceCloudServer ReadWorkspaceServer(JsonElement value)
    {
        var network = value.GetProperty("public_net");
        var ipv4 = network.GetProperty("ipv4");
        var firewalls = network.TryGetProperty("firewalls", out var rules) ? rules.EnumerateArray().ToArray() : [];
        return new(NumericJsonId(value.GetProperty("id")), value.GetProperty("name").GetString()!, value.GetProperty("status").GetString()!,
            value.GetProperty("datacenter").GetProperty("location").GetProperty("name").GetString()!,
            value.GetProperty("server_type").GetProperty("name").GetString()!,
            value.TryGetProperty("image", out var image) && image.ValueKind != JsonValueKind.Null
                ? NumericJsonId(image.GetProperty("id")) : "",
            ipv4.ValueKind == JsonValueKind.Null ? null : ipv4.GetProperty("ip").GetString(),
            ipv4.ValueKind == JsonValueKind.Null ? null : NumericJsonId(ipv4.GetProperty("id")), WorkspaceLabels(value),
            firewalls.Select(firewall => NumericJsonId(firewall.GetProperty("id"))).ToArray(),
            firewalls.Length > 0 && firewalls.All(firewall => firewall.GetProperty("status").GetString() == "applied"));
    }

    private static WorkspaceCloudVolume ReadWorkspaceVolume(JsonElement value) =>
        new(NumericJsonId(value.GetProperty("id")), value.GetProperty("name").GetString()!, value.GetProperty("size").GetInt32(),
            value.GetProperty("location").GetProperty("name").GetString()!,
            value.TryGetProperty("server", out var server) && server.ValueKind != JsonValueKind.Null ? NumericJsonId(server) : null,
            value.TryGetProperty("format", out var format) ? format.GetString() : null,
            value.GetProperty("linux_device").GetString()!, WorkspaceLabels(value));

    private static WorkspaceCloudFirewall ReadWorkspaceFirewall(JsonElement value) =>
        new(NumericJsonId(value.GetProperty("id")), value.GetProperty("name").GetString()!,
            value.TryGetProperty("applied_to", out var servers) ? servers.GetArrayLength() : 0, WorkspaceLabels(value));
}
