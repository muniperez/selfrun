using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Scaleway;

/// <summary>
/// SDK-independent Scaleway Instance, IAM SSH Key, and Block Storage adapter with strict
/// project-scoped queries, X-Auth-Token authentication, cloud-init user-data staging,
/// explicit poweron reconciliation, and quota/catalog discovery.
/// </summary>
public sealed class ScalewayClient :
    IVpsProviderClient,
    IVpsProviderSshKeyClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly ScalewayOptions _options;
    private readonly ILogger<ScalewayClient>? _logger;

    public CloudProvider Provider => CloudProvider.Scaleway;

    [ActivatorUtilitiesConstructor]
    public ScalewayClient(
        HttpClient httpClient,
        IOptions<ScalewayOptions> options,
        ILogger<ScalewayClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public ScalewayClient(
        HttpClient httpClient,
        ScalewayOptions options,
        ILogger<ScalewayClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(null);
        using var req = CreateRequest(HttpMethod.Get, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers?project={Uri.EscapeDataString(_options.ProjectId)}&per_page=1");
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);

        return new ProviderScopeIdentity(
            ProviderWireType: "scaleway",
            ScopeKind: "project",
            ScopeId: _options.ProjectId,
            SecondaryScopeId: zone);
    }

    public Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(null);
        var regionPrefix = zone.LastIndexOf('-') > 0 ? zone[..zone.LastIndexOf('-')] : zone;
        IReadOnlyList<ProviderLocationInfo> locations =
        [
            new ProviderLocationInfo(
                zone,
                zone,
                ProviderLocationGranularity.Zone,
                ParentRegion: regionPrefix,
                Available: true)
        ];
        return Task.FromResult(locations);
    }

    public async Task<IReadOnlyList<ProviderComputeOfferInfo>> ListComputeOffersAsync(
        string? location = null,
        ProviderComputeFamily? computeFamily = null,
        CancellationToken cancellationToken = default)
    {
        if (computeFamily is { } family && (family & ProviderComputeFamily.VirtualServer) == 0)
        {
            return [];
        }

        EnsureProjectConfigured();
        var zone = ResolveZone(location);
        using var prodReq = CreateRequest(HttpMethod.Get, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/products/servers");
        using var prodRes = await _httpClient.SendAsync(prodReq, cancellationToken);
        await EnsureSuccessAsync(prodRes, cancellationToken);
        var prodBody = await prodRes.Content.ReadFromJsonAsync<ScalewayServerProductsResponse>(cancellationToken: cancellationToken);

        return (prodBody?.Servers ?? new Dictionary<string, ScalewayServerProductDto>())
            .Select(pair => new ProviderComputeOfferInfo(
                pair.Key,
                pair.Key,
                ProviderComputeFamily.VirtualServer,
                pair.Value.Arch?.Contains("arm", StringComparison.OrdinalIgnoreCase) == true ? ["linux/arm64"] : ["linux/amd64"],
                pair.Value.Ncpus,
                (int)(pair.Value.Ram / (1024 * 1024)),
                0,
                AvailableLocations: [zone]))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(location);
        using var imgReq = CreateRequest(HttpMethod.Get, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/images?public=true&per_page=50");
        using var imgRes = await _httpClient.SendAsync(imgReq, cancellationToken);
        await EnsureSuccessAsync(imgRes, cancellationToken);
        var imgBody = await imgRes.Content.ReadFromJsonAsync<ScalewayImagesResponse>(cancellationToken: cancellationToken);

        return (imgBody?.Images ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new ProviderImageInfo(
                i.Id!,
                i.Name ?? i.Id!,
                i.Arch?.Contains("arm", StringComparison.OrdinalIgnoreCase) == true ? "linux/arm64" : "linux/amd64",
                Distribution: "linux",
                SupportsCloudInit: true,
                AvailableLocations: [zone]))
            .Where(i => string.IsNullOrWhiteSpace(architecture) || string.Equals(i.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(location);
        using var quotaReq = CreateRequest(HttpMethod.Get, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/quotas?project={Uri.EscapeDataString(_options.ProjectId)}");
        using var quotaRes = await _httpClient.SendAsync(quotaReq, cancellationToken);
        await EnsureSuccessAsync(quotaRes, cancellationToken);
        var quotaBody = await quotaRes.Content.ReadFromJsonAsync<ScalewayQuotasResponse>(cancellationToken: cancellationToken);

        return (quotaBody?.Quotas ?? [])
            .Select(q => new ProviderQuotaInfo(q.Name ?? "servers", q.Limit, q.Used, zone))
            .ToArray();
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        EnsureProjectConfigured();

        var zone = ResolveZone(null);
        var path = $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers?project={Uri.EscapeDataString(_options.ProjectId)}&name={Uri.EscapeDataString(serverName)}";
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<ScalewayServersResponse>(cancellationToken: cancellationToken);
        var matches = (body?.Servers ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Id)
                && string.Equals(s.Name, serverName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"Scaleway returned {matches.Count} servers named '{serverName}' in zone '{zone}' (project '{_options.ProjectId}').",
                response.StatusCode, Provider);
        }

        var server = matches[0];
        EnsureProjectBoundary(server.Project, response.StatusCode);
        return MapServer(zone, server);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        EnsureProjectConfigured();

        var zone = ResolveZone(request.Region);
        var tags = new List<string> { "minicloud" };
        if (request.Tags is not null) tags.AddRange(request.Tags);
        if (request.Labels is not null)
        {
            foreach (var pair in request.Labels) tags.Add($"{pair.Key}={pair.Value}");
        }

        using var createReq = CreateRequest(HttpMethod.Post, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers");
        createReq.Content = JsonContent.Create(new
        {
            name = request.Name,
            commercial_type = request.Plan,
            image = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image,
            project = _options.ProjectId,
            dynamic_ip_required = true,
            tags = tags.Distinct(StringComparer.Ordinal).ToArray()
        });

        using var createRes = await _httpClient.SendAsync(createReq, cancellationToken);
        await EnsureSuccessAsync(createRes, cancellationToken);

        var created = await createRes.Content.ReadFromJsonAsync<ScalewayServerResponse>(cancellationToken: cancellationToken);
        var server = created?.Server;
        if (server is null || string.IsNullOrWhiteSpace(server.Id))
        {
            throw new ProviderApiException("Scaleway Instance create returned an empty server.", createRes.StatusCode, Provider);
        }

        EnsureProjectBoundary(server.Project, createRes.StatusCode);

        try
        {
            if (!string.IsNullOrWhiteSpace(request.UserDataCloudInit))
            {
                using var udReq = CreateRequest(HttpMethod.Patch, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers/{Uri.EscapeDataString(server.Id)}/user_data/cloud-init");
                udReq.Content = new StringContent(request.UserDataCloudInit, Encoding.UTF8, "text/plain");
                using var udRes = await _httpClient.SendAsync(udReq, cancellationToken);
                await EnsureSuccessAsync(udRes, cancellationToken);
            }

            using var powerReq = CreateRequest(HttpMethod.Post, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers/{Uri.EscapeDataString(server.Id)}/action");
            powerReq.Content = JsonContent.Create(new { action = "poweron" });
            using var powerRes = await _httpClient.SendAsync(powerReq, cancellationToken);
            await EnsureSuccessAsync(powerRes, cancellationToken);
        }
        catch (ProviderApiException ex)
        {
            throw new ProviderApiException(
                $"Scaleway server {zone}/{server.Id} was created, but cloud-init or poweron failed. Reconcile the existing server before retrying.",
                ex.StatusCode, Provider, ex.ResponseBody, ex);
        }

        _logger?.LogInformation("Created and powered on Scaleway server {Name} ({Zone}/{Id})", request.Name, zone, server.Id);
        return MapServer(zone, server);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        var (zone, id) = ParseZoneAndId(serverId);
        using var request = CreateRequest(HttpMethod.Get, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ScalewayServerResponse>(cancellationToken: cancellationToken);
        var server = body?.Server;
        if (server is null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        EnsureProjectBoundary(server.Project, response.StatusCode);
        var state = server.State ?? "unknown";
        var isRunning = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
        var ipv4 = server.PublicIp?.Address ?? server.PublicIps?.FirstOrDefault(p => string.Equals(p.Family, "inet", StringComparison.OrdinalIgnoreCase))?.Address;
        var ipv6 = server.Ipv6?.Address ?? server.PublicIps?.FirstOrDefault(p => string.Equals(p.Family, "inet6", StringComparison.OrdinalIgnoreCase))?.Address;
        return new ProviderServerStatus(serverId, state.ToLowerInvariant(), ipv4, ipv6, isRunning, RawStatus: state);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        var (zone, id) = ParseZoneAndId(serverId);
        using var request = CreateRequest(HttpMethod.Post, $"instance/v1/zones/{Uri.EscapeDataString(zone)}/servers/{Uri.EscapeDataString(id)}/action");
        request.Content = JsonContent.Create(new { action = "terminate" });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var request = CreateRequest(HttpMethod.Get, $"iam/v1alpha1/ssh-keys?project_id={Uri.EscapeDataString(_options.ProjectId)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ScalewaySshKeysResponse>(cancellationToken: cancellationToken);
        return (body?.SshKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.Id))
            .Select(k => new ProviderSshKeyInfo(k.Id!, k.Name ?? string.Empty, k.PublicKey))
            .ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var request = CreateRequest(HttpMethod.Post, "iam/v1alpha1/ssh-keys");
        request.Content = JsonContent.Create(new { name, public_key = publicKey, project_id = _options.ProjectId });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var key = await response.Content.ReadFromJsonAsync<ScalewaySshKeyDto>(cancellationToken: cancellationToken);
        if (key is null || string.IsNullOrWhiteSpace(key.Id))
        {
            throw new ProviderApiException("Scaleway IAM returned an empty SSH key response.", response.StatusCode, Provider);
        }

        return new ProviderSshKeyInfo(key.Id, key.Name ?? name, key.PublicKey ?? publicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(null);
        using var request = CreateRequest(HttpMethod.Get, $"block/v1alpha1/zones/{Uri.EscapeDataString(zone)}/volumes?project_id={Uri.EscapeDataString(_options.ProjectId)}&name={Uri.EscapeDataString(name)}");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync<ScalewayVolumesResponse>(cancellationToken: ct);
        var vol = body?.Volumes?.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
        return vol is null ? null : MapVolume(zone, vol);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (zone, volumeId) = ParseZoneAndId(id);
        using var request = CreateRequest(HttpMethod.Get, $"block/v1alpha1/zones/{Uri.EscapeDataString(zone)}/volumes/{Uri.EscapeDataString(volumeId)}");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var vol = await response.Content.ReadFromJsonAsync<ScalewayVolumeDto>(cancellationToken: ct);
        return vol is null ? null : MapVolume(zone, vol);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(region);
        var tags = labels.Select(pair => $"{pair.Key}={pair.Value}").Prepend("minicloud").Distinct(StringComparer.Ordinal).ToArray();
        using var request = CreateRequest(HttpMethod.Post, $"block/v1alpha1/zones/{Uri.EscapeDataString(zone)}/volumes");
        request.Content = JsonContent.Create(new
        {
            name,
            project_id = _options.ProjectId,
            from_empty = new { size = (long)sizeGb * 1_000_000_000L },
            perf_iops = 5000,
            tags
        });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var vol = await response.Content.ReadFromJsonAsync<ScalewayVolumeDto>(cancellationToken: ct)
            ?? throw new ProviderApiException("Scaleway Block Storage returned an empty volume response.", response.StatusCode, Provider);
        return MapVolume(zone, vol, labels);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (volZone, volumeId) = ParseZoneAndId(id);
        var (srvZone, srvId) = ParseZoneAndId(serverId);
        if (!string.Equals(volZone, srvZone, StringComparison.Ordinal))
        {
            throw new ProviderApiException("Scaleway block volumes must be in the same zone as the target server.", HttpStatusCode.BadRequest, Provider);
        }

        using var request = CreateRequest(HttpMethod.Post, $"instance/v1/zones/{Uri.EscapeDataString(srvZone)}/servers/{Uri.EscapeDataString(srvId)}/attach_volume");
        request.Content = JsonContent.Create(new { volume_id = volumeId, volume_type = "sbs_volume" });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (zone, volumeId) = ParseZoneAndId(id);
        using var request = CreateRequest(HttpMethod.Patch, $"block/v1alpha1/zones/{Uri.EscapeDataString(zone)}/volumes/{Uri.EscapeDataString(volumeId)}");
        request.Content = JsonContent.Create(new { size = (long)sizeGb * 1_000_000_000L });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private void EnsureProjectConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new ProviderApiException("Scaleway projectId must be configured.", HttpStatusCode.BadRequest, Provider);
        }
    }

    private void EnsureProjectBoundary(string? responseProjectId, HttpStatusCode statusCode)
    {
        if (!string.IsNullOrWhiteSpace(responseProjectId)
            && !string.Equals(responseProjectId, _options.ProjectId, StringComparison.Ordinal))
        {
            throw new ProviderApiException(
                "Scaleway resource belongs to a different project than the configured provider account boundary.",
                statusCode, Provider);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var baseUri = new Uri((string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.scaleway.com/" : _options.BaseUrl).TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            request.Headers.TryAddWithoutValidation("X-Auth-Token", _options.SecretKey);
        }

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var redacted = TokenRedactor.Redact(body, _options.SecretKey);
        throw new ProviderApiException($"Scaleway API error ({(int)response.StatusCode}): {redacted}", response.StatusCode, Provider, redacted);
    }

    private string ResolveZone(string? zone) =>
        string.IsNullOrWhiteSpace(zone) ? (string.IsNullOrWhiteSpace(_options.DefaultZone) ? "fr-par-1" : _options.DefaultZone) : zone.Trim();

    private (string Zone, string Id) ParseZoneAndId(string compositeId)
    {
        var slash = compositeId.IndexOf('/');
        if (slash > 0 && slash < compositeId.Length - 1)
        {
            return (compositeId[..slash], compositeId[(slash + 1)..]);
        }

        return (ResolveZone(null), compositeId);
    }

    private static ProviderServerInfo MapServer(string zone, ScalewayServerDto server)
    {
        var ipv4 = server.PublicIp?.Address ?? server.PublicIps?.FirstOrDefault(p => string.Equals(p.Family, "inet", StringComparison.OrdinalIgnoreCase))?.Address ?? string.Empty;
        var ipv6 = server.Ipv6?.Address ?? server.PublicIps?.FirstOrDefault(p => string.Equals(p.Family, "inet6", StringComparison.OrdinalIgnoreCase))?.Address;
        return new ProviderServerInfo(
            $"{zone}/{server.Id}",
            server.Name ?? string.Empty,
            ipv4,
            (server.State ?? "unknown").ToLowerInvariant(),
            ipv6,
            ParseTags(server.Tags));
    }

    private static ProviderStorageVolume MapVolume(string zone, ScalewayVolumeDto vol, IReadOnlyDictionary<string, string>? fallbackLabels = null)
    {
        var labels = ParseTags(vol.Tags);
        if (labels.Count == 0 && fallbackLabels is not null)
        {
            foreach (var pair in fallbackLabels) labels[pair.Key] = pair.Value;
        }

        var sizeGb = vol.Size >= 1_000_000_000L ? (int)(vol.Size / 1_000_000_000L) : (int)vol.Size;
        var serverId = vol.References?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ProductResourceId)) is { } r
            ? $"{zone}/{r.ProductResourceId}"
            : null;
        return new ProviderStorageVolume($"{zone}/{vol.Id}", vol.Name ?? string.Empty, sizeGb, zone, serverId, labels);
    }

    private static Dictionary<string, string> ParseTags(List<string>? tags)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tags is null) return labels;
        foreach (var tag in tags)
        {
            var eq = tag.IndexOf('=');
            if (eq > 0 && eq < tag.Length - 1)
            {
                labels[tag[..eq]] = tag[(eq + 1)..];
            }
        }

        return labels;
    }

    private sealed class ScalewayServerProductsResponse
    {
        [JsonPropertyName("servers")] public Dictionary<string, ScalewayServerProductDto>? Servers { get; set; }
    }

    private sealed class ScalewayServerProductDto
    {
        [JsonPropertyName("ncpus")] public int Ncpus { get; set; }
        [JsonPropertyName("ram")] public long Ram { get; set; }
        [JsonPropertyName("arch")] public string? Arch { get; set; }
    }

    private sealed class ScalewayImagesResponse
    {
        [JsonPropertyName("images")] public List<ScalewayImageDto>? Images { get; set; }
    }

    private sealed class ScalewayImageDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arch")] public string? Arch { get; set; }
    }

    private sealed class ScalewayQuotasResponse
    {
        [JsonPropertyName("quotas")] public List<ScalewayQuotaDto>? Quotas { get; set; }
    }

    private sealed class ScalewayQuotaDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("used")] public int Used { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
    }

    private sealed class ScalewayServersResponse
    {
        [JsonPropertyName("servers")] public List<ScalewayServerDto>? Servers { get; set; }
    }

    private sealed class ScalewayServerResponse
    {
        [JsonPropertyName("server")] public ScalewayServerDto? Server { get; set; }
    }

    private sealed class ScalewayServerDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("project")] public string? Project { get; set; }
        [JsonPropertyName("public_ip")] public ScalewayIpDto? PublicIp { get; set; }
        [JsonPropertyName("public_ips")] public List<ScalewayIpDto>? PublicIps { get; set; }
        [JsonPropertyName("ipv6")] public ScalewayIpDto? Ipv6 { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    }

    private sealed class ScalewayIpDto
    {
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("family")] public string? Family { get; set; }
    }

    private sealed class ScalewaySshKeysResponse
    {
        [JsonPropertyName("ssh_keys")] public List<ScalewaySshKeyDto>? SshKeys { get; set; }
    }

    private sealed class ScalewaySshKeyDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
    }

    private sealed class ScalewayVolumesResponse
    {
        [JsonPropertyName("volumes")] public List<ScalewayVolumeDto>? Volumes { get; set; }
    }

    private sealed class ScalewayVolumeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("references")] public List<ScalewayVolumeRefDto>? References { get; set; }
    }

    private sealed class ScalewayVolumeRefDto
    {
        [JsonPropertyName("product_resource_id")] public string? ProductResourceId { get; set; }
    }
}
