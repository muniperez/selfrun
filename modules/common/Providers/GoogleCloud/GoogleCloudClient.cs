using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.GoogleCloud;

public sealed class GoogleCloudClient : IVpsProviderClient
{
    private const string ComputeScope = "https://www.googleapis.com/auth/compute";
    private readonly HttpClient _httpClient;
    private readonly GoogleCloudOptions _options;
    private readonly ILogger<GoogleCloudClient>? _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private GoogleCredential? _credential;

    public CloudProvider Provider => CloudProvider.GoogleCloud;

    [ActivatorUtilitiesConstructor]
    public GoogleCloudClient(HttpClient httpClient, IOptions<GoogleCloudOptions> options,
        ILogger<GoogleCloudClient>? logger = null)
        : this(httpClient, options.Value, logger)
    {
    }

    public GoogleCloudClient(HttpClient httpClient, GoogleCloudOptions options,
        ILogger<GoogleCloudClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        if (_httpClient.BaseAddress == null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var project = Project();
        string? pageToken = null;
        ProviderServerInfo? match = null;
        do
        {
            var path = $"projects/{Uri.EscapeDataString(project)}/aggregated/instances?filter=name%20%3D%20%22{Uri.EscapeDataString(serverName)}%22";
            if (!string.IsNullOrEmpty(pageToken)) path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<AggregatedInstancesResponse>(cancellationToken: cancellationToken)
                ?? throw new ProviderApiException("Google Cloud returned an empty instance list.", provider: Provider);
            foreach (var scoped in result.Items?.Values ?? Enumerable.Empty<InstanceScope>())
            {
                foreach (var instance in scoped.Instances ?? [])
                {
                    if (!string.Equals(instance.Name, serverName, StringComparison.Ordinal)) continue;
                    if (match is not null)
                        throw new ProviderApiException("Multiple Google Cloud instances match the managed server name.", provider: Provider);
                    match = MapInstance(instance, project);
                }
            }
            pageToken = result.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));
        return match;
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var project = Project();
        var zone = request.Region.Trim();
        var image = NormalizeImage(request.Image);
        var payload = new InsertInstanceRequest
        {
            Name = request.Name,
            MachineType = $"zones/{zone}/machineTypes/{request.Plan.Trim()}",
            Disks =
            [
                new InsertDisk
                {
                    Boot = true,
                    AutoDelete = true,
                    Type = "PERSISTENT",
                    InitializeParams = new InitializeParams { SourceImage = image }
                }
            ],
            NetworkInterfaces =
            [
                new NetworkInterface
                {
                    Network = "global/networks/default",
                    AccessConfigs = [new AccessConfig { Name = "External NAT", Type = "ONE_TO_ONE_NAT" }]
                }
            ],
            Metadata = new Metadata
            {
                Items =
                [
                    new MetadataItem { Key = "user-data", Value = request.UserDataCloudInit ?? string.Empty }
                ]
            },
            Labels = request.Labels?.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value) ?? []
        };
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post,
            $"projects/{Uri.EscapeDataString(project)}/zones/{Uri.EscapeDataString(zone)}/instances", cancellationToken);
        httpRequest.Content = JsonContent.Create(payload);
        using var response = await SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var operation = await response.Content.ReadFromJsonAsync<Operation>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("Google Cloud returned an empty insert operation.", response.StatusCode, Provider);
        await WaitForOperationAsync(project, zone, operation, cancellationToken);
        var created = await GetInstanceAsync(project, zone, request.Name, cancellationToken)
            ?? throw new ProviderApiException("Google Cloud insert completed but the instance was not discoverable.", provider: Provider);
        _logger?.LogInformation("Created Google Cloud instance {InstanceName} in {Zone}", request.Name, zone);
        return MapInstance(created, project);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId,
        CancellationToken cancellationToken = default)
    {
        var (project, zone, name) = ParseServerId(serverId);
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"projects/{Uri.EscapeDataString(project)}/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(name)}",
            cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "404");
        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<Instance>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("Google Cloud returned an empty instance.", response.StatusCode, Provider);
        var mapped = MapInstance(instance, project);
        return new ProviderServerStatus(serverId, mapped.Status, mapped.Ipv4Address, mapped.Ipv6Address,
            string.Equals(mapped.Status, "RUNNING", StringComparison.OrdinalIgnoreCase), mapped.Status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var (project, zone, name) = ParseServerId(serverId);
        using var request = await CreateRequestAsync(HttpMethod.Delete,
            $"projects/{Uri.EscapeDataString(project)}/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(name)}",
            cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
        var operation = await response.Content.ReadFromJsonAsync<Operation>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("Google Cloud returned an empty delete operation.", provider: Provider);
        await WaitForOperationAsync(project, zone, operation, cancellationToken);
        _logger?.LogInformation("Deleted Google Cloud instance {InstanceId}", serverId);
    }

    private async Task<Instance?> GetInstanceAsync(string project, string zone, string name, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get,
            $"projects/{Uri.EscapeDataString(project)}/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(name)}", ct);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<Instance>(cancellationToken: ct);
    }

    private async Task WaitForOperationAsync(string project, string zone, Operation operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.Name)) throw new ProviderApiException("Google Cloud returned an unnamed operation.", provider: Provider);
        for (var attempt = 0; ; attempt++)
        {
            using var request = await CreateRequestAsync(HttpMethod.Get,
                $"projects/{Uri.EscapeDataString(project)}/zones/{Uri.EscapeDataString(zone)}/operations/{Uri.EscapeDataString(operation.Name)}", ct);
            using var response = await SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
            var current = await response.Content.ReadFromJsonAsync<Operation>(cancellationToken: ct)
                ?? throw new ProviderApiException("Google Cloud returned an empty operation.", response.StatusCode, Provider);
            if (string.Equals(current.Status, "DONE", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Error?.Errors?.Count > 0)
                    throw new ProviderApiException($"Google Cloud operation failed: {current.Error.Errors[0].Message}",
                        provider: Provider);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, 100 * (attempt + 1))), ct);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        var token = await GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        _httpClient.SendAsync(request, ct);

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(_options.CredentialJson))
                throw new ProviderApiException("Google Cloud credentials are empty.", provider: Provider);
            if (!_options.CredentialJson.TrimStart().StartsWith('{'))
                return _options.CredentialJson.Trim();
            _credential ??= GoogleCredential.FromJson(_options.CredentialJson).CreateScoped(ComputeScope);
            return await _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        finally { _tokenLock.Release(); }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new ProviderApiException($"Google Cloud API request failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode, Provider, TokenRedactor.Redact(body, _options.CredentialJson));
    }

    private string Project() => !string.IsNullOrWhiteSpace(_options.ProjectId)
        ? _options.ProjectId.Trim()
        : throw new ProviderApiException("Google Cloud provider accounts require an explicit project ID.", provider: Provider);

    private static string NormalizeImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return "projects/debian-cloud/global/images/family/debian-12";
        var value = image.Trim();
        return value.StartsWith("projects/", StringComparison.Ordinal) ? value : $"projects/{value}";
    }

    private (string Project, string Zone, string Name) ParseServerId(string id)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts[0] != "projects" || parts[2] != "zones" || parts[4] != "instances")
            throw new ArgumentException("Google Cloud server IDs must be projects/{project}/zones/{zone}/instances/{name}.", nameof(id));
        if (!string.Equals(parts[1], Project(), StringComparison.Ordinal))
            throw new ProviderApiException("Google Cloud server ID belongs to a different project.", provider: Provider);
        return (parts[1], parts[3], parts[5]);
    }

    private static ProviderServerInfo MapInstance(Instance instance, string project)
    {
        var ipv4 = instance.NetworkInterfaces?.SelectMany(x => x.AccessConfigs ?? [])
            .Select(x => x.NatIp).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        var zone = instance.Zone?.Split('/').LastOrDefault() ?? "unknown";
        return new ProviderServerInfo(
            $"projects/{project}/zones/{zone}/instances/{instance.Name}",
            instance.Name ?? string.Empty, ipv4, instance.Status ?? "UNKNOWN", Labels: instance.Labels);
    }

    private sealed class AggregatedInstancesResponse
    {
        [JsonPropertyName("items")] public Dictionary<string, InstanceScope>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }
    private sealed class InstanceScope
    {
        [JsonPropertyName("instances")] public List<Instance>? Instances { get; set; }
    }
    private sealed class Instance
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("zone")] public string? Zone { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
        [JsonPropertyName("networkInterfaces")] public List<NetworkInterface>? NetworkInterfaces { get; set; }
    }
    private sealed class InsertInstanceRequest
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("machineType")] public string MachineType { get; set; } = string.Empty;
        [JsonPropertyName("disks")] public List<InsertDisk> Disks { get; set; } = [];
        [JsonPropertyName("networkInterfaces")] public List<NetworkInterface> NetworkInterfaces { get; set; } = [];
        [JsonPropertyName("metadata")] public Metadata Metadata { get; set; } = new();
        [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = [];
    }
    private sealed class InsertDisk
    {
        [JsonPropertyName("boot")] public bool Boot { get; set; }
        [JsonPropertyName("autoDelete")] public bool AutoDelete { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("initializeParams")] public InitializeParams InitializeParams { get; set; } = new();
    }
    private sealed class InitializeParams
    {
        [JsonPropertyName("sourceImage")] public string SourceImage { get; set; } = string.Empty;
    }
    private sealed class NetworkInterface
    {
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("accessConfigs")] public List<AccessConfig>? AccessConfigs { get; set; }
        [JsonPropertyName("networkIP")] public string? NetworkIp { get; set; }
    }
    private sealed class AccessConfig
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("natIP")] public string? NatIp { get; set; }
    }
    private sealed class Metadata
    {
        [JsonPropertyName("items")] public List<MetadataItem> Items { get; set; } = [];
    }
    private sealed class MetadataItem
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("value")] public string Value { get; set; } = string.Empty;
    }
    private sealed class Operation
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("error")] public OperationError? Error { get; set; }
    }
    private sealed class OperationError
    {
        [JsonPropertyName("errors")] public List<OperationErrorItem>? Errors { get; set; }
    }
    private sealed class OperationErrorItem
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
