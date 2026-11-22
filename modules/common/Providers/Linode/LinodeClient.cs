using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Linode;

public sealed partial class LinodeClient : IVpsProviderClient, IVpsProviderSshKeyClient,
    IStorageVolumeProvider, IStorageVolumeInitializationProvider
{
    private const int PageSize = 500;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly LinodeOptions _options;
    private readonly ILogger<LinodeClient>? _logger;

    public CloudProvider Provider => CloudProvider.Linode;

    [ActivatorUtilitiesConstructor]
    public LinodeClient(HttpClient httpClient, IOptions<LinodeOptions> options, ILogger<LinodeClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public LinodeClient(HttpClient httpClient, LinodeOptions options, ILogger<LinodeClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        if (_httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var matches = new List<LinodeInstanceDto>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"linode/instances?page={page}&page_size={PageSize}");
            request.Headers.TryAddWithoutValidation("X-Filter", JsonSerializer.Serialize(new { label = serverName }));
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<LinodePage<LinodeInstanceDto>>(cancellationToken: cancellationToken);
            var items = result?.Data ?? [];
            matches.AddRange(items.Where(x => string.Equals(x.Label, serverName, StringComparison.Ordinal)));
            if (result is null || page >= result.Pages || items.Count == 0) break;
        }

        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple Linodes match the reserved Minicloud server label.");
        if (matches.Count == 1 && !IsManagedInstance(matches[0]))
            throw new ProviderApiException("A Linode with this managed label is not marked as a Minicloud resource.", provider: Provider);
        return matches.Count == 0 ? null : MapServer(matches[0]);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        var sshKeys = request.SshKeyReferences.Count == 0
            ? []
            : await ResolveSshKeysAsync(request.SshKeyReferences, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SshPublicKey))
            sshKeys = sshKeys.Append(request.SshPublicKey).Distinct(StringComparer.Ordinal).ToArray();
        if (sshKeys.Length == 0)
            throw new InvalidOperationException("Linode requires an SSH key to create a bootable instance.");
        var payload = new LinodeCreateInstancePayload
        {
            Label = request.Name,
            Region = request.Region,
            Type = request.Plan,
            Image = string.IsNullOrWhiteSpace(request.Image) ? "linode/ubuntu24.04" : request.Image,
            AuthorizedKeys = sshKeys,
            Booted = true,
            Tags = ["minicloud", InstanceOwnershipTag(request.Name)],
            Metadata = new LinodeMetadata { UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit ?? string.Empty)) }
        };
        using var httpRequest = CreateRequest(HttpMethod.Post, "linode/instances");
        httpRequest.Options.Set(HetznerClient.NoRetry, true);
        httpRequest.Content = JsonContent.Create(payload);
        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<LinodeInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null || instance.Id <= 0 || !IsManagedInstance(instance))
            throw new ProviderApiException("Linode API returned an empty instance in the create response.", response.StatusCode, Provider);
        _logger?.LogInformation("Created Linode instance {LinodeId} with label {Label}", instance.Id, instance.Label);
        return MapServer(instance);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var id = ParseResourceId(serverId, "Linode instance");
        using var request = CreateRequest(HttpMethod.Get, $"linode/instances/{id}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "404");
        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<LinodeInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "empty");
        if (!IsManagedInstance(instance))
            throw new ProviderApiException("Refusing to manage a Linode not marked as a Minicloud resource.", provider: Provider);
        var status = instance.Status ?? "unknown";
        return new ProviderServerStatus(serverId, status, PublicIpv4(instance.Ipv4), PublicIpv6(instance.Ipv6),
            string.Equals(status, "running", StringComparison.OrdinalIgnoreCase), status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var id = ParseResourceId(serverId, "Linode instance");
        using (var inspection = CreateRequest(HttpMethod.Get, $"linode/instances/{id}"))
        using (var inspected = await SendAsync(inspection, cancellationToken))
        {
            if (inspected.StatusCode == HttpStatusCode.NotFound) return;
            await EnsureSuccessAsync(inspected, cancellationToken);
            var instance = await inspected.Content.ReadFromJsonAsync<LinodeInstanceDto>(cancellationToken: cancellationToken);
            if (instance is null || !IsManagedInstance(instance))
                throw new ProviderApiException("Refusing to delete a Linode not marked as a Minicloud resource.", provider: Provider);
        }
        using var request = CreateRequest(HttpMethod.Delete, $"linode/instances/{id}");
        request.Options.Set(HetznerClient.NoRetry, true);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken);
        _logger?.LogInformation("Deleted Linode instance {LinodeId} (or it was already absent).", id);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        var keys = new List<ProviderSshKeyInfo>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"profile/sshkeys?page={page}&page_size={PageSize}");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<LinodePage<LinodeSshKeyDto>>(cancellationToken: cancellationToken);
            var items = result?.Data ?? [];
            keys.AddRange(items.Where(x => x.Id > 0)
                .Select(x => new ProviderSshKeyInfo(x.Id.ToString(CultureInfo.InvariantCulture), x.Label ?? string.Empty, x.SshKey)));
            if (result is null || page >= result.Pages || items.Count == 0) return keys;
        }
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        using var request = CreateRequest(HttpMethod.Post, "profile/sshkeys");
        request.Options.Set(HetznerClient.NoRetry, true);
        request.Content = JsonContent.Create(new LinodeCreateSshKeyPayload { Label = name, SshKey = publicKey });
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var key = await response.Content.ReadFromJsonAsync<LinodeSshKeyDto>(cancellationToken: cancellationToken);
        if (key is null || key.Id <= 0 || string.IsNullOrWhiteSpace(key.SshKey))
            throw new ProviderApiException("Linode API returned an empty SSH key in the registration response.", response.StatusCode, Provider);
        return new ProviderSshKeyInfo(key.Id.ToString(CultureInfo.InvariantCulture), key.Label ?? name, key.SshKey);
    }

    public async Task<IReadOnlyList<LinodeRegion>> ListRegionsAsync(CancellationToken cancellationToken = default) =>
        await ReadPublicCatalogAsync<LinodeRegion>("regions", cancellationToken);

    public async Task<IReadOnlyList<LinodeType>> ListTypesAsync(CancellationToken cancellationToken = default) =>
        await ReadPublicCatalogAsync<LinodeType>("linode/types", cancellationToken);

    public async Task<IReadOnlyList<LinodeImage>> ListPublicImagesAsync(CancellationToken cancellationToken = default) =>
        await ReadPublicCatalogAsync<LinodeImage>("images", cancellationToken);

    private async Task<IReadOnlyList<T>> ReadPublicCatalogAsync<T>(string path, CancellationToken ct)
    {
        var results = new List<T>();
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?page={page}&page_size={PageSize}");
            using var response = await _httpClient.SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
            var result = await response.Content.ReadFromJsonAsync<LinodePage<T>>(cancellationToken: ct);
            var items = result?.Data ?? [];
            results.AddRange(items);
            if (result is null || page >= result.Pages || items.Count == 0) return results;
        }
    }

    private async Task<string[]> ResolveSshKeysAsync(IReadOnlyList<string> references, CancellationToken ct)
    {
        var keys = await ListSshKeysAsync(ct);
        var result = new List<string>(references.Count);
        foreach (var reference in references)
        {
            if (!long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new InvalidOperationException("Linode SSH key references must be numeric profile key IDs.");
            var key = keys.SingleOrDefault(x => x.Id == id.ToString(CultureInfo.InvariantCulture));
            if (key is null || string.IsNullOrWhiteSpace(key.PublicKey))
                throw new InvalidOperationException("A selected Linode SSH key is no longer available.");
            result.Add(key.PublicKey);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.ApiToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
        if (method != HttpMethod.Get) request.Options.Set(HetznerClient.NoRetry, true);
        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        _httpClient.SendAsync(request, ct);

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var safeBody = TokenRedactor.Redact(body, _options.ApiToken);
        var message = $"Linode API request failed with HTTP {(int)response.StatusCode}.";
        _logger?.LogError("Linode API call failed: {Message}", message);
        throw new ProviderApiException(message, response.StatusCode, Provider, safeBody);
    }

    private static ProviderServerInfo MapServer(LinodeInstanceDto instance)
    {
        var name = instance.Label ?? string.Empty;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        return new ProviderServerInfo(instance.Id.ToString(CultureInfo.InvariantCulture), name,
            PublicIpv4(instance.Ipv4) ?? string.Empty, instance.Status ?? "unknown", PublicIpv6(instance.Ipv6), labels);
    }

    private static bool IsManagedInstance(LinodeInstanceDto instance) =>
        !string.IsNullOrWhiteSpace(instance.Label)
        && instance.Tags?.Contains("minicloud", StringComparer.Ordinal) == true
        && instance.Tags.Contains(InstanceOwnershipTag(instance.Label), StringComparer.Ordinal);

    private static string InstanceOwnershipTag(string name) =>
        ProviderStorageVolume.OwnershipTag(new Dictionary<string, string>
        {
            ["managed-by"] = "minicloud",
            ["server-name"] = name
        });

    private static string? PublicIpv4(IEnumerable<string>? addresses) =>
        addresses?.FirstOrDefault(IsPublicIpv4);

    private static bool IsPublicIpv4(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = parsed.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }

    private static string? PublicIpv6(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var literal = address.Split('/', 2)[0];
        if (!IPAddress.TryParse(literal, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(parsed) || parsed.IsIPv4MappedToIPv6
            || parsed.IsIPv6LinkLocal || parsed.IsIPv6SiteLocal || parsed.IsIPv6Multicast
            || (parsed.GetAddressBytes()[0] & 0xfe) == 0xfc)
            return null;
        return literal;
    }

    private static long ParseResourceId(string id, string resource)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"{resource} IDs must be positive integers.", nameof(id));
        return value;
    }

    private sealed class LinodePage<T>
    {
        [JsonPropertyName("data")] public List<T>? Data { get; set; }
        [JsonPropertyName("pages")] public int Pages { get; set; }
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

    private sealed class LinodeCreateInstancePayload
    {
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("image")] public string Image { get; set; } = string.Empty;
        [JsonPropertyName("authorized_keys")] public string[] AuthorizedKeys { get; set; } = [];
        [JsonPropertyName("booted")] public bool Booted { get; set; }
        [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
        [JsonPropertyName("metadata")] public LinodeMetadata Metadata { get; set; } = new();
    }

    private sealed class LinodeMetadata
    {
        [JsonPropertyName("user_data")] public string UserData { get; set; } = string.Empty;
    }

    private sealed class LinodeSshKeyDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("ssh_key")] public string? SshKey { get; set; }
    }

    private sealed class LinodeCreateSshKeyPayload
    {
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("ssh_key")] public string SshKey { get; set; } = string.Empty;
    }

    public sealed record LinodeRegion(
        string Id, string Label,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);
    public sealed record LinodeType(
        string Id, string Label, string Class,
        [property: JsonPropertyName("memory")] int MemoryMb,
        [property: JsonPropertyName("vcpus")] int Vcpus,
        [property: JsonPropertyName("disk")] int DiskGb,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);
    public sealed record LinodeImage(
        string Id, string Label,
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);
}
