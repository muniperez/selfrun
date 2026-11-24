using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Ovhcloud;

/// <summary>
/// OVHcloud adapter for Public Cloud instances and safe dedicated-server inventory.
/// Bare-metal ordering is intentionally gated because it is a commercial order,
/// not an idempotent VM create, and cancellation can require provider support.
/// </summary>
public sealed class OvhcloudClient : IVpsProviderClient
{
    private readonly HttpClient _httpClient;
    private readonly OvhcloudOptions _options;
    private readonly OvhcloudCredential _credential;
    private readonly ILogger<OvhcloudClient>? _logger;

    public CloudProvider Provider => CloudProvider.Ovhcloud;

    [ActivatorUtilitiesConstructor]
    public OvhcloudClient(HttpClient httpClient, IOptions<OvhcloudOptions> options,
        ILogger<OvhcloudClient>? logger = null)
        : this(httpClient, options.Value, options.Value.Credential, logger) { }

    public OvhcloudClient(HttpClient httpClient, OvhcloudOptions options, string credential,
        ILogger<OvhcloudClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credential = OvhcloudCredential.Parse(credential);
        _logger = logger;
        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var cloud = await FindPublicCloudInstanceAsync(serverName, cancellationToken);
        var dedicated = await FindDedicatedServerAsync(serverName, cancellationToken);
        if (cloud is not null && dedicated is not null)
            throw new ProviderApiException($"OVHcloud returned both a public-cloud instance and dedicated server named '{serverName}'. Refusing ambiguous ownership.",
                HttpStatusCode.Conflict, Provider);
        return cloud ?? dedicated;
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        if (IsBareMetal(request.Plan))
        {
            if (!_options.EnableBareMetalOrders)
                throw BareMetalOrderBlocked();
            throw new ProviderApiException(
                "OVHcloud bare-metal orders are not enabled: ordering requires an explicit cart, stock, price and contract review and is not a safe retryable server create.",
                HttpStatusCode.NotImplemented, Provider);
        }

        var project = RequireProject();
        var payload = new PublicCloudCreatePayload
        {
            Name = request.Name,
            FlavorId = request.Plan,
            Region = request.Region,
            ImageId = request.Image,
            UserData = request.UserDataCloudInit,
            SshKeyId = request.SshKeyReferences.FirstOrDefault(),
            MonthlyBilling = false
        };
        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = CreateRequest(HttpMethod.Post,
            $"cloud/project/{Uri.EscapeDataString(project)}/instance", json);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<PublicCloudInstance>(cancellationToken: cancellationToken)
            ?? throw EmptyResponse("create instance");
        if (string.IsNullOrWhiteSpace(result.Id))
            throw EmptyResponse("create instance");
        _logger?.LogInformation("Created OVHcloud public-cloud instance {InstanceId} for {InstanceName}", result.Id, request.Name);
        return MapPublicCloud(result, request.Name);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (serverId.StartsWith("dedicated:", StringComparison.Ordinal))
            return await GetDedicatedStatusAsync(serverId["dedicated:".Length..], cancellationToken);

        var project = RequireProject();
        using var request = CreateRequest(HttpMethod.Get,
            $"cloud/project/{Uri.EscapeDataString(project)}/instance/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "404");
        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<PublicCloudInstance>(cancellationToken: cancellationToken)
            ?? throw EmptyResponse("get instance");
        return MapStatus(serverId, instance.Status, instance.Ipv4, instance.Ipv6);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (serverId.StartsWith("dedicated:", StringComparison.Ordinal))
            throw new ProviderApiException(
                "OVHcloud dedicated-server cancellation is a commercial service termination and is not performed by Minicloud.",
                HttpStatusCode.MethodNotAllowed, Provider);

        var project = RequireProject();
        using var request = CreateRequest(HttpMethod.Delete,
            $"cloud/project/{Uri.EscapeDataString(project)}/instance/{Uri.EscapeDataString(serverId)}");
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<ProviderServerInfo?> FindPublicCloudInstanceAsync(string name, CancellationToken ct)
    {
        var project = RequireProject();
        using var request = CreateRequest(HttpMethod.Get,
            $"cloud/project/{Uri.EscapeDataString(project)}/instance?name={Uri.EscapeDataString(name)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var instances = await response.Content.ReadFromJsonAsync<List<PublicCloudInstance>>(cancellationToken: ct) ?? [];
        var instance = instances.SingleOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        return instance is null ? null : MapPublicCloud(instance, name);
    }

    private async Task<ProviderServerInfo?> FindDedicatedServerAsync(string name, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "dedicated/server");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var ids = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: ct) ?? [];
        foreach (var id in ids)
        {
            using var detailRequest = CreateRequest(HttpMethod.Get, $"dedicated/server/{Uri.EscapeDataString(id)}");
            using var detailResponse = await SendAsync(detailRequest, ct);
            if (detailResponse.StatusCode == HttpStatusCode.NotFound) continue;
            await EnsureSuccessAsync(detailResponse, ct);
            var detail = await detailResponse.Content.ReadFromJsonAsync<DedicatedServer>(cancellationToken: ct);
            if (detail is not null && string.Equals(detail.Name ?? id, name, StringComparison.Ordinal))
                return new ProviderServerInfo("dedicated:" + id, detail.Name ?? name, detail.Ipv4 ?? string.Empty,
                    detail.State ?? "unknown", Labels: new Dictionary<string, string> { ["provider-family"] = "bare-metal" });
        }
        return null;
    }

    private async Task<ProviderServerStatus> GetDedicatedStatusAsync(string id, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"dedicated/server/{Uri.EscapeDataString(id)}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderServerStatus("dedicated:" + id, "not_found", null, null, false, "404");
        await EnsureSuccessAsync(response, ct);
        var detail = await response.Content.ReadFromJsonAsync<DedicatedServer>(cancellationToken: ct)
            ?? throw EmptyResponse("get dedicated server");
        var status = detail.State ?? "unknown";
        return new ProviderServerStatus("dedicated:" + id, status, detail.Ipv4, null,
            string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase), status);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string body = "")
    {
        var request = new HttpRequestMessage(method, path);
        var absolute = new Uri(_httpClient.BaseAddress!, path).ToString();
        if (_credential.IsSigned)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var signatureInput = $"{_credential.ApplicationSecret}+{_credential.ConsumerKey}+{timestamp}+{method.Method}+{absolute}+{body}";
            var signature = "$1$" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signatureInput))).ToLowerInvariant();
            request.Headers.Add("X-Ovh-Application", _credential.ApplicationKey);
            request.Headers.Add("X-Ovh-Consumer", _credential.ConsumerKey);
            request.Headers.Add("X-Ovh-Timestamp", timestamp);
            request.Headers.Add("X-Ovh-Signature", signature);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(_credential.BearerToken))
                request.Headers.Add("X-Ovh-Token", _credential.BearerToken);
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        await _httpClient.SendAsync(request, ct);

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ProviderApiException($"OVHcloud API request failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode, Provider, TokenRedactor.Redact(body, _credential.SecretForRedaction));
    }

    private string RequireProject() =>
        !string.IsNullOrWhiteSpace(_credential.ProjectId) ? _credential.ProjectId :
        throw new ProviderApiException("OVHcloud provider accounts require the OVHcloud service/project identifier.", HttpStatusCode.BadRequest, Provider);

    private static bool IsBareMetal(string plan) => plan.StartsWith("baremetal:", StringComparison.OrdinalIgnoreCase)
        || plan.StartsWith("bare-metal:", StringComparison.OrdinalIgnoreCase);

    private static ProviderServerInfo MapPublicCloud(PublicCloudInstance instance, string fallbackName) =>
        new(instance.Id!, instance.Name ?? fallbackName, instance.Ipv4 ?? string.Empty, instance.Status ?? "unknown", instance.Ipv6,
            new Dictionary<string, string> { ["provider-family"] = "public-cloud" });

    private static ProviderServerStatus MapStatus(string id, string? status, string? ipv4, string? ipv6) =>
        new(id, status ?? "unknown", ipv4, ipv6, string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase), status);

    private static ProviderApiException EmptyResponse(string operation) =>
        new($"OVHcloud API returned an empty response for {operation}.", HttpStatusCode.BadGateway, CloudProvider.Ovhcloud);

    private static ProviderApiException BareMetalOrderBlocked() =>
        new("OVHcloud bare-metal provisioning is gated: use an explicitly reviewed order workflow; Minicloud will not reinstall or adopt an unowned server.", HttpStatusCode.NotImplemented, CloudProvider.Ovhcloud);

    private sealed class PublicCloudCreatePayload
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("flavorId")] public string FlavorId { get; init; } = string.Empty;
        [JsonPropertyName("region")] public string Region { get; init; } = string.Empty;
        [JsonPropertyName("imageId")] public string? ImageId { get; init; }
        [JsonPropertyName("userData")] public string UserData { get; init; } = string.Empty;
        [JsonPropertyName("sshKeyId")] public string? SshKeyId { get; init; }
        [JsonPropertyName("monthlyBilling")] public bool MonthlyBilling { get; init; }
    }

    private sealed class PublicCloudInstance
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("ipAddresses")] public List<IpAddress>? IpAddresses { get; init; }
        [JsonPropertyName("ipv4")] public string? Ipv4 => IpAddresses?.FirstOrDefault(x => x.Version == 4)?.Ip;
        [JsonPropertyName("ipv6")] public string? Ipv6 => IpAddresses?.FirstOrDefault(x => x.Version == 6)?.Ip;
    }

    private sealed class IpAddress
    {
        [JsonPropertyName("ip")] public string? Ip { get; init; }
        [JsonPropertyName("version")] public int Version { get; init; }
    }

    private sealed class DedicatedServer
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("ip")] public string? Ipv4 { get; init; }
    }
}

public sealed record OvhcloudCredential(
    string ProjectId,
    string? ApplicationKey,
    string? ApplicationSecret,
    string? ConsumerKey,
    string? BearerToken)
{
    public bool IsSigned => !string.IsNullOrWhiteSpace(ApplicationKey)
        && !string.IsNullOrWhiteSpace(ApplicationSecret)
        && !string.IsNullOrWhiteSpace(ConsumerKey);
    public string SecretForRedaction => ApplicationSecret ?? BearerToken ?? string.Empty;

    public static OvhcloudCredential Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new OvhcloudCredential("", null, null, null, null);
        try
        {
            var parsed = JsonSerializer.Deserialize<OvhcloudCredential>(value,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.ProjectId)
                && (parsed.IsSigned || !string.IsNullOrWhiteSpace(parsed.BearerToken)))
                return parsed;
        }
        catch (JsonException) { }
        throw new ArgumentException("OVHcloud credentials must be JSON containing projectId and either bearerToken or applicationKey/applicationSecret/consumerKey.", nameof(value));
    }
}
