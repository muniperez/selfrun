using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;

namespace Minicloud.Common.Providers.Scaleway;

public sealed class ScalewayClient : IVpsProviderClient, IVpsProviderSshKeyClient, IStorageVolumeProvider, IScalewayBootstrapDataClient
{
    private static readonly string[] Zones =
    [
        "fr-par-1", "fr-par-2", "fr-par-3",
        "nl-ams-1", "nl-ams-2", "nl-ams-3",
        "pl-waw-1", "pl-waw-2", "pl-waw-3",
        "it-mil-1", "it-mil-2", "it-mil-3",
        "es-mad-1", "es-mad-2", "es-mad-3"
    ];

    private readonly HttpClient _http;
    private readonly ScalewayOptions _options;
    private readonly ILogger<ScalewayClient>? _logger;

    public CloudProvider Provider => CloudProvider.Scaleway;

    [ActivatorUtilitiesConstructor]
    public ScalewayClient(HttpClient httpClient, IOptions<ScalewayOptions> options, ILogger<ScalewayClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public ScalewayClient(HttpClient httpClient, ScalewayOptions options, ILogger<ScalewayClient>? logger = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var matches = new List<ProviderServerInfo>();
        foreach (var zone in Zones)
        {
            string? pageToken = null;
            do
            {
                var path = $"instance/v2alpha1/zones/{zone}/servers?project_id={Escape(_options.ProjectId!)}&name={Escape(serverName)}&page_size=100";
                if (pageToken is not null) path += $"&page_token={Escape(pageToken)}";
                using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound) break;
                await EnsureSuccessAsync(response, cancellationToken);
                var result = await response.Content.ReadFromJsonAsync<ServerListResponse>(cancellationToken: cancellationToken);
                if (result?.Servers is not { } servers)
                    throw new ProviderApiException("Scaleway returned an invalid instance list.", response.StatusCode, Provider);
                foreach (var server in servers)
                {
                    if (string.Equals(server.Name, serverName, StringComparison.Ordinal) &&
                        server.Tags?.Contains("minicloud", StringComparer.Ordinal) == true &&
                        server.ProjectId == _options.ProjectId)
                        matches.Add(MapServer(server, zone));
                }
                pageToken = result?.NextPageToken;
            } while (!string.IsNullOrEmpty(pageToken));
        }
        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple Scaleway instances match the Minicloud server identity.");
        return matches.SingleOrDefault();
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ValidateZone(request.Region);
        if (!_options.Enabled)
            throw new ProviderCatalogException("provider_not_qualified", "Scaleway provisioning is disabled until an operator enables it after live qualification.");

        var tags = new HashSet<string>(request.Tags ?? [], StringComparer.Ordinal) { "minicloud" };
        foreach (var (key, value) in request.Labels ?? new Dictionary<string, string>())
            tags.Add(LabelTag(key, value));

        using var response = await SendAsync(HttpMethod.Post,
            $"instance/v2alpha1/zones/{request.Region}/servers",
            new
            {
                name = request.Name,
                project_id = _options.ProjectId,
                server_type = request.Plan,
                tags = tags.ToArray(),
                public_network_interface = new
                {
                    ips = new[]
                    {
                        new { new_ip = new { type = "zonal_ipv4" } },
                        new { new_ip = new { type = "zonal_ipv6" } }
                    }
                },
                volumes = new[]
                {
                    new
                    {
                        volume_type = "sbs",
                        new_volume = new { name = request.Name + "-root", image_label = request.Image ?? "ubuntu_noble" }
                    }
                }
            }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var created = await response.Content.ReadFromJsonAsync<ServerEnvelope>(cancellationToken: cancellationToken);
        if (created?.Server is not { Id.Length: > 0 } server)
            throw new ProviderApiException("Scaleway API returned an empty instance after creation.", response.StatusCode, Provider);
        if (server.ProjectId != _options.ProjectId)
            throw new ProviderApiException("Scaleway created an instance outside the configured project.", HttpStatusCode.Forbidden, Provider);

        var serverInfo = MapServer(server, request.Region);
        try
        {
            await SetCloudInitDataAsync(serverInfo.Id, request.UserDataCloudInit, cancellationToken);
        }
        catch (Exception ex) when (ex is ProviderApiException or HttpRequestException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new ProviderApiException(
                $"Scaleway instance {serverInfo.Id} was created, but its cloud-init data could not be installed. Reconcile this instance before another allocation.",
                HttpStatusCode.InternalServerError, Provider, innerException: ex);
        }

        _logger?.LogInformation("Created Scaleway instance {InstanceName} with ID {InstanceId} in {Zone}.",
            request.Name, serverInfo.Id, request.Region);
        return serverInfo;
    }

    public async Task SetCloudInitDataAsync(string serverId, string cloudInit, CancellationToken ct)
    {
        EnsureProvisioningEnabled();
        var (zone, id) = SplitServerId(serverId);
        await GetServerInProjectAsync(zone, id, ct);
        using var response = await SendAsync(HttpMethod.Put,
            $"instance/v2alpha1/zones/{zone}/servers/{Escape(id)}/user-data/cloud-init",
            new { content = cloudInit }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var (zone, id) = SplitServerId(serverId);
        using var response = await SendAsync(HttpMethod.Get,
            $"instance/v2alpha1/zones/{zone}/servers/{Escape(id)}", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ServerEnvelope>(cancellationToken: cancellationToken);
        if (result?.Server is not { } server)
            throw new ProviderApiException("Scaleway returned an invalid instance response.", response.StatusCode, Provider);
        EnsureProject(server.ProjectId);
        var state = server.Status ?? server.State ?? "unknown";
        var (ipv4, ipv6) = ReadAddresses(server);
        return new ProviderServerStatus(serverId, state, ipv4, ipv6,
            string.Equals(state, "running", StringComparison.OrdinalIgnoreCase), RawStatus: state);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var (zone, id) = SplitServerId(serverId);
        using (var get = await SendAsync(HttpMethod.Get, $"instance/v2alpha1/zones/{zone}/servers/{Escape(id)}", null, cancellationToken))
        {
            if (get.StatusCode == HttpStatusCode.NotFound) return;
            await EnsureSuccessAsync(get, cancellationToken);
            var result = await get.Content.ReadFromJsonAsync<ServerEnvelope>(cancellationToken: cancellationToken);
            if (result?.Server is not { } server)
                throw new ProviderApiException("Scaleway returned an invalid instance response.", get.StatusCode, Provider);
            EnsureProject(server.ProjectId);
        }

        using var response = await SendAsync(HttpMethod.Delete,
            $"instance/v2alpha1/zones/{zone}/servers/{Escape(id)}",
            new { delete_all_ips = true, delete_all_volumes = false }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
        _logger?.LogInformation("Deleted Scaleway instance {InstanceId} in {Zone}.", id, zone);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"iam/v1alpha1/ssh-keys?project_id={Escape(_options.ProjectId!)}&page_size=100", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<SshKeyListResponse>(cancellationToken: cancellationToken);
        if (result?.SshKeys is not { } keys)
            throw new ProviderApiException("Scaleway returned an invalid SSH key list.", response.StatusCode, Provider);
        return keys.Where(x => !string.IsNullOrWhiteSpace(x.Id) && x.ProjectId == _options.ProjectId)
            .Select(x => new ProviderSshKeyInfo(x.Id!, x.Name ?? string.Empty, x.PublicKey)).ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        EnsureProvisioningEnabled();
        using var response = await SendAsync(HttpMethod.Post, "iam/v1alpha1/ssh-keys",
            new { name, public_key = publicKey, project_id = _options.ProjectId }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var key = await response.Content.ReadFromJsonAsync<SshKeyDto>(cancellationToken: cancellationToken);
        if (key is not { Id.Length: > 0 } || key.ProjectId != _options.ProjectId)
            throw new ProviderApiException("Scaleway API returned an invalid project SSH key.", response.StatusCode, Provider);
        return new ProviderSshKeyInfo(key.Id!, key.Name ?? name, key.PublicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        var matches = new List<ProviderStorageVolume>();
        foreach (var zone in Zones)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"block/v1/zones/{zone}/volumes?project_id={Escape(_options.ProjectId!)}&name={Escape(name)}&page_size=100",
                null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            await EnsureSuccessAsync(response, ct);
            var result = await response.Content.ReadFromJsonAsync<VolumeListResponse>(cancellationToken: ct);
            if (result?.Volumes is not { } volumes)
                throw new ProviderApiException("Scaleway returned an invalid block volume list.", response.StatusCode, Provider);
            matches.AddRange(volumes.Where(x =>
                x.Name == name && x.ProjectId == _options.ProjectId).Select(MapVolume));
        }
        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple Scaleway block volumes match the Minicloud storage identity.");
        return matches.SingleOrDefault();
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        var (zone, volumeId) = SplitResourceId(id);
        using var response = await SendAsync(HttpMethod.Get,
            $"block/v1/zones/{zone}/volumes/{Escape(volumeId)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<VolumeEnvelope>(cancellationToken: ct);
        if (result?.Volume is not { } volume)
            throw new ProviderApiException("Scaleway returned an invalid block volume response.", response.StatusCode, Provider);
        EnsureProject(volume.ProjectId);
        return MapVolume(volume);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(
        string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EnsureProvisioningEnabled();
        ValidateZone(region);
        if (sizeGb < 1 || sizeGb > 10_000) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        using var response = await SendAsync(HttpMethod.Post, $"block/v1/zones/{region}/volumes", new
        {
            name,
            project_id = _options.ProjectId,
            type = "sbs_5k",
            from_empty = new { size = checked((long)sizeGb * 1_000_000_000L) },
            tags = ToTags(labels)
        }, ct);
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<VolumeEnvelope>(cancellationToken: ct);
        if (result?.Volume is not { Id.Length: > 0 } volume)
            throw new ProviderApiException("Scaleway API returned an empty block volume after creation.", response.StatusCode, Provider);
        EnsureProject(volume.ProjectId);
        await WaitForVolumeReadyAsync(region, volume.Id, ct);
        return MapVolume(volume);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        EnsureProvisioningEnabled();
        var (zone, volumeId) = SplitResourceId(id);
        var (serverZone, serverProviderId) = SplitServerId(serverId);
        if (zone != serverZone)
            throw new InvalidOperationException("Scaleway block volumes can only attach to an instance in the same availability zone.");
        var current = await GetVolumeAsync(id, ct) ?? throw new InvalidOperationException("Scaleway block volume was not found.");
        if (current.ServerId is not null)
        {
            if (current.ServerId != serverId) throw new InvalidOperationException("Scaleway block volume is attached to another instance.");
            return;
        }
        await GetServerInProjectAsync(zone, serverProviderId, ct);
        using var response = await SendAsync(HttpMethod.Post,
            $"instance/v2alpha1/zones/{zone}/servers/{Escape(serverProviderId)}/attach-volume",
            new { volume_id = volumeId, boot_volume = false }, ct);
        await EnsureSuccessAsync(response, ct);
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var attached = await GetVolumeAsync(id, ct);
            if (attached?.ServerId == serverId) return;
            if (attached?.ServerId is not null) throw new InvalidOperationException("Scaleway attached the block volume to a different instance.");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Scaleway block volume was not attached within five minutes.");
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        EnsureProvisioningEnabled();
        if (sizeGb is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        var (zone, volumeId) = SplitResourceId(id);
        var current = await GetVolumeAsync(id, ct) ?? throw new InvalidOperationException("Scaleway block volume was not found.");
        if (current.SizeGb >= sizeGb) return;
        using var response = await SendAsync(HttpMethod.Patch, $"block/v1/zones/{zone}/volumes/{Escape(volumeId)}",
            new { size = checked((long)sizeGb * 1_000_000_000L) }, ct);
        await EnsureSuccessAsync(response, ct);
        await WaitForVolumeReadyAsync(zone, volumeId, ct);
    }

    private async Task WaitForVolumeReadyAsync(string zone, string id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            using var response = await SendAsync(HttpMethod.Get,
                $"block/v1/zones/{zone}/volumes/{Escape(id)}", null, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Scaleway block volume disappeared during provisioning.");
            await EnsureSuccessAsync(response, ct);
            var envelope = await response.Content.ReadFromJsonAsync<VolumeEnvelope>(cancellationToken: ct);
            if (envelope?.Volume is not { } volume)
                throw new ProviderApiException("Scaleway returned an empty block volume response.", response.StatusCode, Provider);
            EnsureProject(volume.ProjectId);
            if (volume.Status is "available" or "in_use") return;
            if (volume.Status is "error" or "deleted")
                throw new InvalidOperationException($"Scaleway block volume entered terminal state '{volume.Status}'.");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("Scaleway block volume did not become ready within five minutes.");
    }

    private async Task<ServerDto> GetServerInProjectAsync(string zone, string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"instance/v2alpha1/zones/{zone}/servers/{Escape(id)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Scaleway instance was not found in the configured project.");
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<ServerEnvelope>(cancellationToken: ct);
        if (result?.Server is not { } server)
            throw new ProviderApiException("Scaleway returned an invalid instance response.", response.StatusCode, Provider);
        EnsureProject(server.ProjectId);
        return server;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Auth-Token", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = JsonContent.Create(payload);
        if (method != HttpMethod.Get) request.Options.Set(Hetzner.HetznerClient.NoRetry, true);
        return await _http.SendAsync(request, ct);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var safeBody = TokenRedactor.Redact(body, _options.ApiKey);
        var message = $"Scaleway API request failed with HTTP {(int)response.StatusCode}.";
        _logger?.LogError("Scaleway API call failed: {Message}", message);
        throw new ProviderApiException(message, response.StatusCode, Provider, safeBody);
    }

    private void EnsureProject(string? projectId)
    {
        if (!string.Equals(projectId, _options.ProjectId, StringComparison.Ordinal))
            throw new ProviderApiException("Scaleway resource is not in the configured project.", HttpStatusCode.Forbidden, Provider);
    }

    private void EnsureProvisioningEnabled()
    {
        if (!_options.Enabled)
            throw new ProviderCatalogException("provider_not_qualified", "Scaleway provisioning is disabled until an operator enables it after live qualification.");
    }

    private ProviderServerInfo MapServer(ServerDto server, string zone)
    {
        if (string.IsNullOrWhiteSpace(server.Id))
            throw new ProviderApiException("Scaleway returned an instance without an ID.", provider: Provider);
        var (ipv4, ipv6) = ReadAddresses(server);
        return new ProviderServerInfo($"{zone}:{server.Id}", server.Name ?? string.Empty, ipv4 ?? string.Empty,
            server.Status ?? server.State ?? "unknown", ipv6, ReadLabels(server.Tags));
    }

    private static (string? Ipv4, string? Ipv6) ReadAddresses(ServerDto server)
    {
        string? ipv4 = null;
        string? ipv6 = null;
        foreach (var address in server.PublicIps ?? [])
        {
            var value = address.Address;
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (address.Type?.Contains("ipv6", StringComparison.OrdinalIgnoreCase) == true || value.Contains(':'))
                ipv6 ??= value;
            else
                ipv4 ??= value;
        }
        ipv4 ??= ReadAddress(server.PublicIp);
        ipv6 ??= ReadAddress(server.Ipv6);
        return (ipv4, ipv6);
    }

    private static string? ReadAddress(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("address", out var address)
            ? address.GetString() : null;
    }

    private static ProviderStorageVolume MapVolume(VolumeDto volume)
    {
        if (string.IsNullOrWhiteSpace(volume.Id) || string.IsNullOrWhiteSpace(volume.Zone))
            throw new ProviderApiException("Scaleway returned a block volume without its ID or zone.", provider: CloudProvider.Scaleway);
        var references = volume.References ?? [];
        if (references.Count > 1)
            throw new InvalidOperationException("Scaleway block volume has multiple resource references.");
        var serverId = references.SingleOrDefault() is { ProductResourceType: "instance" or "instance_server", ProductResourceId.Length: > 0 } reference
            ? $"{volume.Zone}:{reference.ProductResourceId}" : null;
        var sizeGb = checked((int)Math.Ceiling(volume.Size / 1_000_000_000d));
        return new ProviderStorageVolume($"{volume.Zone}:{volume.Id}", volume.Name ?? string.Empty,
            sizeGb, volume.Zone, serverId, ReadLabels(volume.Tags));
    }

    private static IReadOnlyDictionary<string, string> ReadLabels(IEnumerable<string>? tags)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags ?? [])
        {
            if (!tag.StartsWith("mc-label-", StringComparison.Ordinal)) continue;
            var label = new[] { "managed-by", "app-id", "storage-name", "purpose" }
                .FirstOrDefault(key => tag.StartsWith($"mc-label-{key}-", StringComparison.Ordinal));
            if (label is not null) labels[label] = tag[($"mc-label-{label}-").Length..];
        }
        return labels;
    }

    private static string[] ToTags(IReadOnlyDictionary<string, string> labels) =>
        labels.Select(pair => LabelTag(pair.Key, pair.Value)).Distinct(StringComparer.Ordinal).ToArray();

    private static string LabelTag(string key, string value) => "mc-label-" + key + "-" + value;

    private static (string Zone, string Id) SplitServerId(string value) => SplitResourceId(value);

    private static (string Zone, string Id) SplitResourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException("Scaleway resource IDs must include their availability zone.", nameof(value));
        var zone = value[..separator];
        ValidateZone(zone);
        return (zone, value[(separator + 1)..]);
    }

    private static void ValidateZone(string zone)
    {
        if (!Zones.Contains(zone, StringComparer.Ordinal))
            throw new ArgumentException($"Scaleway availability zone '{zone}' is not currently supported.", nameof(zone));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class ServerEnvelope
    {
        [JsonPropertyName("server")] public ServerDto? Server { get; set; }
    }

    private sealed class ServerListResponse
    {
        [JsonPropertyName("servers")] public List<ServerDto>? Servers { get; set; }
        [JsonPropertyName("next_page_token")] public string? NextPageToken { get; set; }
    }

    private sealed class ServerDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("project")] public string? Project { get => ProjectId; set => ProjectId ??= value; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("public_ips")] public List<PublicIpDto>? PublicIps { get; set; }
        [JsonPropertyName("public_ip")] public JsonElement PublicIp { get; set; }
        [JsonPropertyName("ipv6")] public JsonElement Ipv6 { get; set; }
    }

    private sealed class PublicIpDto
    {
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class SshKeyListResponse
    {
        [JsonPropertyName("ssh_keys")] public List<SshKeyDto>? SshKeys { get; set; }
    }

    private sealed class SshKeyDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    }

    private sealed class VolumeEnvelope
    {
        [JsonPropertyName("volume")] public VolumeDto? Volume { get; set; }
    }

    private sealed class VolumeListResponse
    {
        [JsonPropertyName("volumes")] public List<VolumeDto>? Volumes { get; set; }
    }

    private sealed class VolumeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("zone")] public string? Zone { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("references")] public List<VolumeReferenceDto>? References { get; set; }
    }

    private sealed class VolumeReferenceDto
    {
        [JsonPropertyName("product_resource_type")] public string? ProductResourceType { get; set; }
        [JsonPropertyName("product_resource_id")] public string? ProductResourceId { get; set; }
    }
}
