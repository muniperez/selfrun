using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Linode;

/// <summary>
/// SDK-independent Akamai Connected Cloud (Linode API v4) provider adapter supporting
/// scoped API token auth, instance lifecycle, SSH keys, Block Storage volumes, and catalog discovery.
/// </summary>
public sealed class LinodeClient :
    IVpsProviderClient,
    IVpsProviderSshKeyClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly LinodeOptions _options;
    private readonly ILogger<LinodeClient>? _logger;

    public CloudProvider Provider => CloudProvider.Linode;

    [ActivatorUtilitiesConstructor]
    public LinodeClient(
        HttpClient httpClient,
        IOptions<LinodeOptions> options,
        ILogger<LinodeClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public LinodeClient(
        HttpClient httpClient,
        LinodeOptions options,
        ILogger<LinodeClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "profile");
        using var res = await _httpClient.SendAsync(req, cancellationToken);
        await EnsureSuccessAsync(res, cancellationToken);
        var profile = await res.Content.ReadFromJsonAsync<LinodeProfileDto>(cancellationToken: cancellationToken);
        var scope = profile?.Uid?.ToString() ?? profile?.Username ?? "linode-account";

        return new ProviderScopeIdentity(
            ProviderWireType: "linode",
            ScopeKind: "account",
            ScopeId: scope,
            ObservedPrincipal: profile?.Username ?? scope);
    }

    public async Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        using var regReq = CreateRequest(HttpMethod.Get, "regions");
        using var regRes = await _httpClient.SendAsync(regReq, cancellationToken);
        await EnsureSuccessAsync(regRes, cancellationToken);
        var regBody = await regRes.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeRegionDto>>(cancellationToken: cancellationToken);

        return (regBody?.Data ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new ProviderLocationInfo(
                r.Id!,
                r.Label ?? r.Id!,
                ProviderLocationGranularity.Region,
                Available: true))
            .ToArray();
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

        using var typeReq = CreateRequest(HttpMethod.Get, "linode/types");
        using var typeRes = await _httpClient.SendAsync(typeReq, cancellationToken);
        await EnsureSuccessAsync(typeRes, cancellationToken);
        var typeBody = await typeRes.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeTypeDto>>(cancellationToken: cancellationToken);

        return (typeBody?.Data ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => new ProviderComputeOfferInfo(
                t.Id!,
                t.Label ?? t.Id!,
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64"],
                t.Vcpus,
                t.Memory,
                t.Disk / 1024,
                AvailableLocations: string.IsNullOrWhiteSpace(location) ? null : [location.Trim()]))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        using var imgReq = CreateRequest(HttpMethod.Get, "images");
        using var imgRes = await _httpClient.SendAsync(imgReq, cancellationToken);
        await EnsureSuccessAsync(imgRes, cancellationToken);
        var imgBody = await imgRes.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeImageDto>>(cancellationToken: cancellationToken);

        return (imgBody?.Data ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Id))
            .Select(i => new ProviderImageInfo(
                i.Id!,
                i.Label ?? i.Id!,
                "linux/amd64",
                Distribution: i.Vendor ?? "linux",
                SupportsCloudInit: true))
            .Where(i => string.IsNullOrWhiteSpace(architecture) || string.Equals(i.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProviderQuotaInfo>>([]);

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        using var request = CreateRequest(HttpMethod.Get, "linode/instances");
        request.Headers.TryAddWithoutValidation("X-Filter", JsonSerializer.Serialize(new { label = serverName }));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);

        var list = await response.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeInstanceDto>>(cancellationToken: cancellationToken);
        var matches = (list?.Data ?? [])
            .Where(i => i.Id > 0 && string.Equals(i.Label, serverName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"Linode returned {matches.Count} instances labeled '{serverName}'. Reconcile duplicates before retrying.",
                response.StatusCode, Provider);
        }

        return MapInstance(matches[0]);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var tags = new List<string> { "selfrun" };
        if (request.Tags is not null) tags.AddRange(request.Tags);
        if (request.Labels is not null)
        {
            foreach (var pair in request.Labels) tags.Add($"{pair.Key}:{pair.Value}");
        }

        var ephemeralRootPass = GenerateEphemeralRootPassword();
        var payload = new Dictionary<string, object?>
        {
            ["label"] = request.Name,
            ["region"] = request.Region,
            ["type"] = request.Plan,
            ["image"] = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image,
            ["root_pass"] = ephemeralRootPass,
            ["booted"] = true,
            ["tags"] = tags.Distinct(StringComparer.Ordinal).ToArray()
        };

        if (request.SshKeyReferences.Count > 0)
        {
            payload["authorized_keys"] = request.SshKeyReferences.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(request.UserDataCloudInit))
        {
            payload["metadata"] = new
            {
                user_data = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit))
            };
        }

        using var httpRequest = CreateRequest(HttpMethod.Post, "linode/instances");
        httpRequest.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken, ephemeralRootPass);

        var instance = await response.Content.ReadFromJsonAsync<LinodeInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null || instance.Id <= 0)
        {
            throw new ProviderApiException("Linode API returned an empty instance in create response.", response.StatusCode, Provider);
        }

        _logger?.LogInformation("Created Linode instance {Name} ({Id}) in {Region}", request.Name, instance.Id, request.Region);
        return MapInstance(instance);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Get, $"linode/instances/{Uri.EscapeDataString(serverId)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<LinodeInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        var status = instance.Status ?? "unknown";
        var isRunning = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
        return new ProviderServerStatus(serverId, status.ToLowerInvariant(), SelectPublicIpv4(instance.Ipv4), StripCidr(instance.Ipv6), isRunning, RawStatus: status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        using var request = CreateRequest(HttpMethod.Delete, $"linode/instances/{Uri.EscapeDataString(serverId)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "profile/sshkeys");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeSshKeyDto>>(cancellationToken: cancellationToken);
        return (body?.Data ?? [])
            .Where(k => k.Id > 0)
            .Select(k => new ProviderSshKeyInfo(k.Id.ToString(), k.Label ?? string.Empty, k.SshKey))
            .ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "profile/sshkeys");
        request.Content = JsonContent.Create(new { label = name, ssh_key = publicKey });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var key = await response.Content.ReadFromJsonAsync<LinodeSshKeyDto>(cancellationToken: cancellationToken);
        if (key is null || key.Id <= 0)
        {
            throw new ProviderApiException("Linode API returned an empty SSH key response.", response.StatusCode, Provider);
        }

        return new ProviderSshKeyInfo(key.Id.ToString(), key.Label ?? name, key.SshKey ?? publicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "volumes");
        request.Headers.TryAddWithoutValidation("X-Filter", JsonSerializer.Serialize(new { label = name }));
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var list = await response.Content.ReadFromJsonAsync<LinodePagedResponse<LinodeVolumeDto>>(cancellationToken: ct);
        var vol = list?.Data?.FirstOrDefault(v => string.Equals(v.Label, name, StringComparison.Ordinal));
        return vol is null ? null : MapVolume(vol);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"volumes/{Uri.EscapeDataString(id)}");
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var vol = await response.Content.ReadFromJsonAsync<LinodeVolumeDto>(cancellationToken: ct);
        return vol is null ? null : MapVolume(vol);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var tags = labels.Select(pair => $"{pair.Key}:{pair.Value}").Prepend("selfrun").Distinct(StringComparer.Ordinal).ToArray();
        using var request = CreateRequest(HttpMethod.Post, "volumes");
        request.Content = JsonContent.Create(new { label = name, size = sizeGb, region, tags });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var vol = await response.Content.ReadFromJsonAsync<LinodeVolumeDto>(cancellationToken: ct)
            ?? throw new ProviderApiException("Linode CreateVolume returned an empty response.", response.StatusCode, Provider);
        return MapVolume(vol, labels);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        if (!long.TryParse(serverId, out var linodeId) || linodeId <= 0)
        {
            throw new ProviderApiException($"Invalid Linode instance ID '{serverId}' for volume attach.", HttpStatusCode.BadRequest, Provider);
        }

        using var request = CreateRequest(HttpMethod.Post, $"volumes/{Uri.EscapeDataString(id)}/attach");
        request.Content = JsonContent.Create(new { linode_id = linodeId, persist_across_boots = true });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, $"volumes/{Uri.EscapeDataString(id)}/resize");
        request.Content = JsonContent.Create(new { size = sizeGb });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var baseUri = new Uri((string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://api.linode.com/v4/" : _options.BaseUrl).TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        }

        return request;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct, string? extraSecret = null)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var redacted = TokenRedactor.Redact(body, _options.ApiToken, extraSecret);
        throw new ProviderApiException($"Linode API error ({(int)response.StatusCode}): {redacted}", response.StatusCode, Provider, redacted);
    }

    private static string GenerateEphemeralRootPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return "Mc!" + Convert.ToBase64String(bytes).Replace('+', 'A').Replace('/', 'B').Replace('=', '9');
    }

    private static string? SelectPublicIpv4(List<string>? addresses) =>
        addresses?.FirstOrDefault(ip => !string.IsNullOrWhiteSpace(ip) && !ip.StartsWith("192.168.", StringComparison.Ordinal) && !ip.StartsWith("10.", StringComparison.Ordinal))
        ?? addresses?.FirstOrDefault();

    private static string? StripCidr(string? ipv6)
    {
        if (string.IsNullOrWhiteSpace(ipv6)) return null;
        var slash = ipv6.IndexOf('/');
        return slash > 0 ? ipv6[..slash] : ipv6;
    }

    private static ProviderServerInfo MapInstance(LinodeInstanceDto dto) => new(
        dto.Id.ToString(),
        dto.Label ?? string.Empty,
        SelectPublicIpv4(dto.Ipv4) ?? string.Empty,
        (dto.Status ?? "unknown").ToLowerInvariant(),
        StripCidr(dto.Ipv6),
        ParseTagLabels(dto.Tags));

    private static ProviderStorageVolume MapVolume(LinodeVolumeDto dto, IReadOnlyDictionary<string, string>? fallbackLabels = null)
    {
        var parsed = ParseTagLabels(dto.Tags);
        if (parsed.Count == 0 && fallbackLabels is not null)
        {
            foreach (var pair in fallbackLabels) parsed[pair.Key] = pair.Value;
        }

        return new ProviderStorageVolume(
            dto.Id.ToString(),
            dto.Label ?? string.Empty,
            dto.Size,
            dto.Region ?? string.Empty,
            dto.LinodeId?.ToString(),
            parsed);
    }

    private static Dictionary<string, string> ParseTagLabels(List<string>? tags)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tags is null) return labels;
        foreach (var tag in tags)
        {
            var colon = tag.IndexOf(':');
            if (colon > 0 && colon < tag.Length - 1)
            {
                labels[tag[..colon]] = tag[(colon + 1)..];
            }
        }

        return labels;
    }

    private sealed class LinodePagedResponse<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
    }

    private sealed class LinodeProfileDto
    {
        [JsonPropertyName("uid")] public long? Uid { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
    }

    private sealed class LinodeRegionDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    private sealed class LinodeTypeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("vcpus")] public int Vcpus { get; set; }
        [JsonPropertyName("memory")] public int Memory { get; set; }
        [JsonPropertyName("disk")] public int Disk { get; set; }
    }

    private sealed class LinodeImageDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    }

    private sealed class LinodeInstanceDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("ipv4")] public List<string>? Ipv4 { get; set; }
        [JsonPropertyName("ipv6")] public string? Ipv6 { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    }

    private sealed class LinodeSshKeyDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("ssh_key")] public string? SshKey { get; set; }
    }

    private sealed class LinodeVolumeDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("linode_id")] public long? LinodeId { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    }
}
