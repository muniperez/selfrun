using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Ovhcloud;

/// <summary>
/// SDK-independent OVHcloud adapter supporting both Public Cloud virtual servers (project-scoped)
/// and Dedicated/Bare-Metal server discovery/inventory with explicit commercial ordering/cancellation gating.
/// </summary>
public sealed class OvhcloudClient :
    IVpsProviderClient,
    IVpsProviderSshKeyClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient,
    IBareMetalProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly OvhcloudOptions _options;
    private readonly ILogger<OvhcloudClient>? _logger;
    private readonly TimeProvider _timeProvider;

    public CloudProvider Provider => CloudProvider.Ovhcloud;
    public bool IsCommercialOrderingEnabled => _options.AllowBareMetalCommercialOrdering;

    [ActivatorUtilitiesConstructor]
    public OvhcloudClient(
        HttpClient httpClient,
        IOptions<OvhcloudOptions> options,
        ILogger<OvhcloudClient>? logger = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, options.Value, logger, timeProvider)
    {
    }

    public OvhcloudClient(
        HttpClient httpClient,
        OvhcloudOptions options,
        ILogger<OvhcloudClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}", string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);
        var project = await res.Content.ReadFromJsonAsync<OvhProjectDto>(cancellationToken: cancellationToken);
        var scopeId = string.IsNullOrWhiteSpace(project?.ProjectId) ? _options.ProjectId : project.ProjectId.Trim();

        return new ProviderScopeIdentity(
            ProviderWireType: "ovhcloud",
            ScopeKind: "project",
            ScopeId: scopeId,
            ObservedPrincipal: project?.Description);
    }

    public async Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var regReq = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/region", string.Empty);
        using var regRes = await _httpClient.SendAsync(regReq, cancellationToken);
        await EnsureSuccessAsync(regRes, cancellationToken);
        var regionNames = await regRes.Content.ReadFromJsonAsync<List<string>>(cancellationToken: cancellationToken) ?? [];

        return regionNames
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => new ProviderLocationInfo(
                r,
                r,
                ProviderLocationGranularity.Region,
                Available: true))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderComputeOfferInfo>> ListComputeOffersAsync(
        string? location = null,
        ProviderComputeFamily? computeFamily = null,
        CancellationToken cancellationToken = default)
    {
        if (computeFamily == ProviderComputeFamily.BareMetal)
        {
            var bareMetalOffers = await ListBareMetalOffersAsync(location, cancellationToken);
            return bareMetalOffers
                .Select(o => new ProviderComputeOfferInfo(
                    o.OfferCode,
                    o.Name,
                    ProviderComputeFamily.BareMetal,
                    [o.Architecture],
                    o.Cores,
                    o.MemoryGb * 1024,
                    0,
                    AvailableLocations: [o.Datacenter],
                    EstimatedProvisioningLeadTime: TimeSpan.FromMinutes(15)))
                .ToArray();
        }

        EnsureProjectConfigured();
        var targetRegion = string.IsNullOrWhiteSpace(location) ? _options.DefaultRegion : location.Trim();
        using var flavReq = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/flavor?region={Uri.EscapeDataString(targetRegion)}", string.Empty);
        using var flavRes = await _httpClient.SendAsync(flavReq, cancellationToken);
        await EnsureSuccessAsync(flavRes, cancellationToken);
        var flavors = await flavRes.Content.ReadFromJsonAsync<List<OvhFlavorDto>>(cancellationToken: cancellationToken) ?? [];

        return flavors
            .Where(f => !string.IsNullOrWhiteSpace(f.Name))
            .Select(f => new ProviderComputeOfferInfo(
                f.Id ?? f.Name!,
                f.Name!,
                ProviderComputeFamily.VirtualServer,
                f.Type?.Contains("arm", StringComparison.OrdinalIgnoreCase) == true ? ["linux/arm64"] : ["linux/amd64"],
                f.Vcpus,
                f.Ram,
                f.Disk,
                AvailableLocations: [targetRegion]))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var targetRegion = string.IsNullOrWhiteSpace(location) ? _options.DefaultRegion : location.Trim();
        using var imgReq = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/image?region={Uri.EscapeDataString(targetRegion)}", string.Empty);
        using var imgRes = await _httpClient.SendAsync(imgReq, cancellationToken);
        await EnsureSuccessAsync(imgRes, cancellationToken);
        var imageDtos = await imgRes.Content.ReadFromJsonAsync<List<OvhImageDto>>(cancellationToken: cancellationToken) ?? [];

        return imageDtos
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new ProviderImageInfo(
                i.Id!,
                i.Name ?? i.Id!,
                i.Name?.Contains("arm", StringComparison.OrdinalIgnoreCase) == true ? "linux/arm64" : "linux/amd64",
                Distribution: i.Type ?? "linux",
                SupportsCloudInit: true,
                AvailableLocations: [targetRegion]))
            .Where(i => string.IsNullOrWhiteSpace(architecture) || string.Equals(i.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var targetRegion = string.IsNullOrWhiteSpace(location) ? _options.DefaultRegion : location.Trim();
        using var quotaReq = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/quota", string.Empty);
        using var quotaRes = await _httpClient.SendAsync(quotaReq, cancellationToken);
        await EnsureSuccessAsync(quotaRes, cancellationToken);
        var quotaDtos = await quotaRes.Content.ReadFromJsonAsync<List<OvhQuotaDto>>(cancellationToken: cancellationToken) ?? [];

        return quotaDtos
            .Where(q => string.IsNullOrWhiteSpace(q.Region) || string.Equals(q.Region, targetRegion, StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => new[]
            {
                new ProviderQuotaInfo($"{q.Region ?? targetRegion}:instances", q.Instance?.MaxInstances ?? 0, q.Instance?.UsedInstances ?? 0, q.Region ?? targetRegion),
                new ProviderQuotaInfo($"{q.Region ?? targetRegion}:cores", q.Instance?.MaxCores ?? 0, q.Instance?.UsedCores ?? 0, q.Region ?? targetRegion)
            })
            .ToArray();
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        EnsureProjectConfigured();

        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/instance", string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(res, cancellationToken);

        var instances = await res.Content.ReadFromJsonAsync<List<OvhInstanceDto>>(cancellationToken: cancellationToken) ?? [];
        var matches = instances
            .Where(i => string.Equals(i.Name, serverName, StringComparison.Ordinal)
                && !string.Equals(i.Status, "DELETED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"OVHcloud returned {matches.Count} active Public Cloud instances named '{serverName}' in project '{_options.ProjectId}'.",
                res.StatusCode, Provider);
        }

        return MapInstance(matches[0]);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        EnsureProjectConfigured();

        var payload = new
        {
            name = request.Name,
            region = request.Region,
            flavorId = request.Plan,
            imageId = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image,
            sshKeyId = request.SshKeyReferences.FirstOrDefault(),
            userData = string.IsNullOrWhiteSpace(request.UserDataCloudInit) ? null : request.UserDataCloudInit,
            monthlyBilling = false
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = CreateSignedRequest(HttpMethod.Post, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/instance", json);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);

        var created = await res.Content.ReadFromJsonAsync<OvhInstanceDto>(cancellationToken: cancellationToken);
        if (created is null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw new ProviderApiException("OVHcloud Public Cloud returned an empty instance response.", res.StatusCode, Provider);
        }

        _logger?.LogInformation("Created OVHcloud Public Cloud instance {Name} ({Id}) in project {ProjectId}", request.Name, created.Id, _options.ProjectId);
        return MapInstance(created);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/instance/{Uri.EscapeDataString(serverId)}", string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(res, cancellationToken);
        var instance = await res.Content.ReadFromJsonAsync<OvhInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        var status = instance.Status ?? "UNKNOWN";
        var isRunning = string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
        var ipv4 = instance.IpAddresses?.FirstOrDefault(ip => ip.Version == 4 && string.Equals(ip.Type, "public", StringComparison.OrdinalIgnoreCase))?.Ip;
        var ipv6 = instance.IpAddresses?.FirstOrDefault(ip => ip.Version == 6 && string.Equals(ip.Type, "public", StringComparison.OrdinalIgnoreCase))?.Ip;
        return new ProviderServerStatus(serverId, status.ToLowerInvariant(), ipv4, ipv6, isRunning, RawStatus: status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        using var req = CreateSignedRequest(HttpMethod.Delete, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/instance/{Uri.EscapeDataString(serverId)}", string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(res, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/sshkey", string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);
        var keys = await res.Content.ReadFromJsonAsync<List<OvhSshKeyDto>>(cancellationToken: cancellationToken) ?? [];
        return keys
            .Where(k => !string.IsNullOrWhiteSpace(k.Id))
            .Select(k => new ProviderSshKeyInfo(k.Id!, k.Name ?? string.Empty, k.PublicKey))
            .ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        var json = JsonSerializer.Serialize(new { name, publicKey });
        using var req = CreateSignedRequest(HttpMethod.Post, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/sshkey", json);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);
        var key = await res.Content.ReadFromJsonAsync<OvhSshKeyDto>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("OVHcloud returned an empty SSH key response.", res.StatusCode, Provider);
        return new ProviderSshKeyInfo(key.Id ?? name, key.Name ?? name, key.PublicKey ?? publicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        EnsureProjectConfigured();
        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/volume", string.Empty);
        using var res = await _httpClient.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(res, ct);
        var volumes = await res.Content.ReadFromJsonAsync<List<OvhVolumeDto>>(cancellationToken: ct) ?? [];
        var vol = volumes.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
        return vol is null ? null : MapVolume(vol);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        EnsureProjectConfigured();
        using var req = CreateSignedRequest(HttpMethod.Get, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/volume/{Uri.EscapeDataString(id)}", string.Empty);
        using var res = await _httpClient.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(res, ct);
        var vol = await res.Content.ReadFromJsonAsync<OvhVolumeDto>(cancellationToken: ct);
        return vol is null ? null : MapVolume(vol);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var json = JsonSerializer.Serialize(new { name, size = sizeGb, region, type = "high-speed" });
        using var req = CreateSignedRequest(HttpMethod.Post, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/volume", json);
        using var res = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(res, ct);
        var vol = await res.Content.ReadFromJsonAsync<OvhVolumeDto>(cancellationToken: ct)
            ?? throw new ProviderApiException("OVHcloud returned an empty volume response.", res.StatusCode, Provider);
        return MapVolume(vol, labels);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var json = JsonSerializer.Serialize(new { instanceId = serverId });
        using var req = CreateSignedRequest(HttpMethod.Post, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/volume/{Uri.EscapeDataString(id)}/attach", json);
        using var res = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var json = JsonSerializer.Serialize(new { size = sizeGb });
        using var req = CreateSignedRequest(HttpMethod.Post, $"cloud/project/{Uri.EscapeDataString(_options.ProjectId)}/volume/{Uri.EscapeDataString(id)}/upsize", json);
        using var res = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(res, ct);
    }

    // --- Dedicated / Bare Metal path (IBareMetalProviderClient) ---

    public async Task<IReadOnlyList<BareMetalOfferInfo>> ListBareMetalOffersAsync(string? datacenter = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(datacenter)
            ? "order/catalog/public/baremetalServers?ovhSubsidiary=IE"
            : $"order/catalog/public/baremetalServers?ovhSubsidiary=IE&datacenter={Uri.EscapeDataString(datacenter)}";
        using var req = CreateSignedRequest(HttpMethod.Get, path, string.Empty);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);

        var catalog = await res.Content.ReadFromJsonAsync<OvhBareMetalCatalogResponse>(cancellationToken: cancellationToken);
        return (catalog?.Plans ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.PlanCode))
            .Where(p => string.IsNullOrWhiteSpace(datacenter) || string.Equals(p.Datacenter, datacenter, StringComparison.OrdinalIgnoreCase))
            .Select(p => new BareMetalOfferInfo(
                p.PlanCode!,
                p.InvoiceName ?? p.PlanCode!,
                p.Datacenter ?? "gra",
                p.Architecture ?? "linux/amd64",
                p.Cores,
                p.MemoryGb,
                p.ProvisioningLeadTime ?? "120s-24h",
                _options.AllowBareMetalCommercialOrdering))
            .ToArray();
    }

    public async Task<IReadOnlyList<BareMetalServerInfo>> ListBareMetalServersAsync(CancellationToken cancellationToken = default)
    {
        using var listReq = CreateSignedRequest(HttpMethod.Get, "dedicated/server", string.Empty);
        using var listRes = await _httpClient.SendAsync(listReq, cancellationToken);
        if (listRes.StatusCode == HttpStatusCode.NotFound) return [];
        await EnsureSuccessAsync(listRes, cancellationToken);

        var serviceNames = await listRes.Content.ReadFromJsonAsync<List<string>>(cancellationToken: cancellationToken) ?? [];
        var servers = new List<BareMetalServerInfo>();
        foreach (var serviceName in serviceNames.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var detail = await GetBareMetalServerDetailAsync(serviceName, cancellationToken);
            if (detail is not null)
            {
                servers.Add(detail);
            }
        }

        return servers;
    }

    public async Task<BareMetalServerInfo?> FindBareMetalServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var servers = await ListBareMetalServersAsync(cancellationToken);
        var matches = servers
            .Where(s => string.Equals(s.DisplayName, serverName, StringComparison.Ordinal)
                || string.Equals(s.ServiceName, serverName, StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"OVHcloud returned {matches.Count} dedicated bare-metal servers matching '{serverName}'.",
                HttpStatusCode.Conflict, Provider);
        }

        return matches[0];
    }

    public async Task<BareMetalServerInfo> ProvisionBareMetalServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_options.AllowBareMetalCommercialOrdering)
        {
            throw new ProviderApiException(
                "OVHcloud bare-metal commercial ordering is gated (Ovhcloud:AllowBareMetalCommercialOrdering=false). Enable the gate only after commercial approval and qualification.",
                HttpStatusCode.Forbidden, Provider);
        }

        var json = JsonSerializer.Serialize(new
        {
            displayName = request.Name,
            planCode = request.Plan,
            datacenter = request.Region,
            os = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image,
            sshKey = request.SshKeyReferences.FirstOrDefault(),
            userData = request.UserDataCloudInit
        });
        using var req = CreateSignedRequest(HttpMethod.Post, "dedicated/server/order", json);
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);
        var created = await res.Content.ReadFromJsonAsync<OvhDedicatedServerDto>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("OVHcloud bare-metal order returned an empty server record.", res.StatusCode, Provider);
        return MapDedicated(created, !_options.AllowBareMetalCommercialOrdering);
    }

    public async Task<ProviderServerStatus> GetBareMetalServerStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        var detail = await GetBareMetalServerDetailAsync(serviceName, cancellationToken);
        if (detail is null)
        {
            return new ProviderServerStatus(serviceName, "not_found", null, null, false, RawStatus: "404");
        }

        var isRunning = string.Equals(detail.State, "ok", StringComparison.OrdinalIgnoreCase);
        return new ProviderServerStatus(serviceName, isRunning ? "running" : detail.State.ToLowerInvariant(), detail.Ipv4Address, detail.Ipv6Address, isRunning, RawStatus: detail.State);
    }

    public async Task DeleteBareMetalServerAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        if (!_options.AllowBareMetalCommercialOrdering)
        {
            throw new ProviderApiException(
                "OVHcloud bare-metal commercial cancellation is gated (Ovhcloud:AllowBareMetalCommercialOrdering=false). Refusing automated dedicated server termination.",
                HttpStatusCode.Forbidden, Provider);
        }

        using var req = CreateSignedRequest(HttpMethod.Post, $"dedicated/server/{Uri.EscapeDataString(serviceName)}/terminate", "{}");
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        if (res.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(res, cancellationToken);
    }

    private async Task<BareMetalServerInfo?> GetBareMetalServerDetailAsync(string serviceName, CancellationToken ct)
    {
        using var req = CreateSignedRequest(HttpMethod.Get, $"dedicated/server/{Uri.EscapeDataString(serviceName)}", string.Empty);
        using var res = await _httpClient.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(res, ct);
        var dto = await res.Content.ReadFromJsonAsync<OvhDedicatedServerDto>(cancellationToken: ct);
        return dto is null ? null : MapDedicated(dto, !_options.AllowBareMetalCommercialOrdering);
    }

    private void EnsureProjectConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new ProviderApiException("OVHcloud Public Cloud projectId must be configured.", HttpStatusCode.BadRequest, Provider);
        }
    }

    private HttpRequestMessage CreateSignedRequest(HttpMethod method, string relativePath, string body)
    {
        var baseUri = new Uri((string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://eu.api.ovh.com/1.0/" : _options.BaseUrl).TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(_options.ApplicationKey)
            && !string.IsNullOrWhiteSpace(_options.ApplicationSecret)
            && !string.IsNullOrWhiteSpace(_options.ConsumerKey))
        {
            OvhcloudRequestSigner.SignRequest(
                request,
                body,
                _options.ApplicationKey,
                _options.ApplicationSecret,
                _options.ConsumerKey,
                _timeProvider.GetUtcNow());
        }

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var redacted = TokenRedactor.Redact(body, _options.ApplicationSecret, _options.ConsumerKey);
        throw new ProviderApiException($"OVHcloud API error ({(int)response.StatusCode}): {redacted}", response.StatusCode, Provider, redacted);
    }

    private static ProviderServerInfo MapInstance(OvhInstanceDto dto)
    {
        var ipv4 = dto.IpAddresses?.FirstOrDefault(ip => ip.Version == 4 && string.Equals(ip.Type, "public", StringComparison.OrdinalIgnoreCase))?.Ip ?? string.Empty;
        var ipv6 = dto.IpAddresses?.FirstOrDefault(ip => ip.Version == 6 && string.Equals(ip.Type, "public", StringComparison.OrdinalIgnoreCase))?.Ip;
        return new ProviderServerInfo(dto.Id ?? string.Empty, dto.Name ?? string.Empty, ipv4, (dto.Status ?? "unknown").ToLowerInvariant(), ipv6);
    }

    private static ProviderStorageVolume MapVolume(OvhVolumeDto dto, IReadOnlyDictionary<string, string>? labels = null) => new(
        dto.Id ?? string.Empty,
        dto.Name ?? string.Empty,
        dto.Size,
        dto.Region ?? string.Empty,
        dto.AttachedTo?.FirstOrDefault(),
        labels ?? new Dictionary<string, string>());

    private static BareMetalServerInfo MapDedicated(OvhDedicatedServerDto dto, bool gated) => new(
        dto.Name ?? string.Empty,
        dto.Iam?.DisplayName ?? dto.Reverse ?? dto.Name ?? string.Empty,
        dto.Datacenter ?? string.Empty,
        dto.State ?? "unknown",
        dto.Ip,
        dto.Ipv6,
        dto.Os,
        gated);

    private sealed class OvhProjectDto
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class OvhFlavorDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("vcpus")] public int Vcpus { get; set; }
        [JsonPropertyName("ram")] public int Ram { get; set; }
        [JsonPropertyName("disk")] public int Disk { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("available")] public bool? Available { get; set; }
    }

    private sealed class OvhImageDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class OvhQuotaDto
    {
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("instance")] public OvhInstanceQuotaDto? Instance { get; set; }
    }

    private sealed class OvhInstanceQuotaDto
    {
        [JsonPropertyName("usedInstances")] public int UsedInstances { get; set; }
        [JsonPropertyName("maxInstances")] public int MaxInstances { get; set; }
        [JsonPropertyName("usedCores")] public int UsedCores { get; set; }
        [JsonPropertyName("maxCores")] public int MaxCores { get; set; }
    }

    private sealed class OvhInstanceDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("ipAddresses")] public List<OvhIpAddressDto>? IpAddresses { get; set; }
    }

    private sealed class OvhIpAddressDto
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
    }

    private sealed class OvhSshKeyDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }
    }

    private sealed class OvhVolumeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("attachedTo")] public List<string>? AttachedTo { get; set; }
    }

    private sealed class OvhBareMetalCatalogResponse
    {
        [JsonPropertyName("plans")] public List<OvhBareMetalPlanDto>? Plans { get; set; }
    }

    private sealed class OvhBareMetalPlanDto
    {
        [JsonPropertyName("planCode")] public string? PlanCode { get; set; }
        [JsonPropertyName("invoiceName")] public string? InvoiceName { get; set; }
        [JsonPropertyName("datacenter")] public string? Datacenter { get; set; }
        [JsonPropertyName("architecture")] public string? Architecture { get; set; }
        [JsonPropertyName("cores")] public int Cores { get; set; }
        [JsonPropertyName("memoryGb")] public int MemoryGb { get; set; }
        [JsonPropertyName("provisioningLeadTime")] public string? ProvisioningLeadTime { get; set; }
    }

    private sealed class OvhDedicatedServerDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("reverse")] public string? Reverse { get; set; }
        [JsonPropertyName("datacenter")] public string? Datacenter { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("ip")] public string? Ip { get; set; }
        [JsonPropertyName("ipv6")] public string? Ipv6 { get; set; }
        [JsonPropertyName("os")] public string? Os { get; set; }
        [JsonPropertyName("iam")] public OvhIamDto? Iam { get; set; }
    }

    private sealed class OvhIamDto
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }
}
