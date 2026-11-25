using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Aws;

/// <summary>
/// SDK-independent AWS EC2 & EBS provider adapter implementing lifecycle, SSH key management,
/// durable block storage, and read-only scope/catalog/quota discovery with SigV4 signing and strict
/// account/region boundary checks.
/// </summary>
public sealed class AwsClient :
    IVpsProviderClient,
    IVpsProviderSshKeyClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient
{
    private const string Ec2ApiVersion = "2016-11-15";

    private readonly HttpClient _httpClient;
    private readonly AwsOptions _options;
    private readonly ILogger<AwsClient>? _logger;
    private readonly TimeProvider _timeProvider;

    public CloudProvider Provider => CloudProvider.Aws;

    [ActivatorUtilitiesConstructor]
    public AwsClient(
        HttpClient httpClient,
        IOptions<AwsOptions> options,
        ILogger<AwsClient>? logger = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, options.Value, logger, timeProvider)
    {
    }

    public AwsClient(
        HttpClient httpClient,
        AwsOptions options,
        ILogger<AwsClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        var region = ResolveRegion(null);
        using var request = CreateSignedGet($"?Action=DescribeRegions&Version={Ec2ApiVersion}", region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AwsRegionListResponse>(cancellationToken: cancellationToken);
        var accountId = !string.IsNullOrWhiteSpace(body?.AccountId)
            ? body.AccountId.Trim()
            : !string.IsNullOrWhiteSpace(_options.AccountId)
                ? _options.AccountId.Trim()
                : region;

        if (!string.IsNullOrWhiteSpace(_options.AccountId)
            && !string.IsNullOrWhiteSpace(body?.AccountId)
            && !string.Equals(_options.AccountId.Trim(), body.AccountId.Trim(), StringComparison.Ordinal))
        {
            throw new ProviderApiException(
                "AWS scope check returned a different account ID than configured.",
                response.StatusCode,
                Provider);
        }

        return new ProviderScopeIdentity(
            ProviderWireType: "aws",
            ScopeKind: "account",
            ScopeId: accountId,
            SecondaryScopeId: region,
            ObservedPrincipal: _options.RoleArn);
    }

    public async Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        var targetRegion = ResolveRegion(null);
        using var regionsReq = CreateSignedGet($"?Action=DescribeRegions&Version={Ec2ApiVersion}", targetRegion);
        using var regionsRes = await _httpClient.SendAsync(regionsReq, cancellationToken);
        await EnsureSuccessAsync(regionsRes, cancellationToken);
        var regionsBody = await regionsRes.Content.ReadFromJsonAsync<AwsRegionListResponse>(cancellationToken: cancellationToken);

        return (regionsBody?.Regions ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.RegionName))
            .Select(r => new ProviderLocationInfo(
                r.RegionName!,
                r.RegionName!,
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

        var targetRegion = ResolveRegion(location);
        using var typesReq = CreateSignedGet($"?Action=DescribeInstanceTypes&Version={Ec2ApiVersion}&Region={Uri.EscapeDataString(targetRegion)}", targetRegion);
        using var typesRes = await _httpClient.SendAsync(typesReq, cancellationToken);
        await EnsureSuccessAsync(typesRes, cancellationToken);
        var typesBody = await typesRes.Content.ReadFromJsonAsync<AwsInstanceTypesResponse>(cancellationToken: cancellationToken);

        return (typesBody?.InstanceTypes ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t.InstanceType))
            .Select(t => new ProviderComputeOfferInfo(
                t.InstanceType!,
                t.InstanceType!,
                ProviderComputeFamily.VirtualServer,
                t.Architectures is { Count: > 0 } ? t.Architectures : ["linux/amd64"],
                t.Vcpus,
                t.MemoryMb,
                t.DiskGb,
                AvailableLocations: [targetRegion]))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        var targetRegion = ResolveRegion(location);
        using var imagesReq = CreateSignedGet($"?Action=DescribeImages&Version={Ec2ApiVersion}&Region={Uri.EscapeDataString(targetRegion)}", targetRegion);
        using var imagesRes = await _httpClient.SendAsync(imagesReq, cancellationToken);
        await EnsureSuccessAsync(imagesRes, cancellationToken);
        var imagesBody = await imagesRes.Content.ReadFromJsonAsync<AwsImagesResponse>(cancellationToken: cancellationToken);

        return (imagesBody?.Images ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.ImageId))
            .Select(i => new ProviderImageInfo(
                i.ImageId!,
                i.Name ?? i.ImageId!,
                NormalizeArchitecture(i.Architecture),
                Distribution: i.OsFamily ?? "ubuntu",
                SupportsCloudInit: true,
                AvailableLocations: [targetRegion]))
            .Where(i => string.IsNullOrWhiteSpace(architecture) || string.Equals(i.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        var targetRegion = ResolveRegion(location);
        using var regionsReq = CreateSignedGet($"?Action=DescribeRegions&Version={Ec2ApiVersion}", targetRegion);
        using var regionsRes = await _httpClient.SendAsync(regionsReq, cancellationToken);
        await EnsureSuccessAsync(regionsRes, cancellationToken);
        var regionsBody = await regionsRes.Content.ReadFromJsonAsync<AwsRegionListResponse>(cancellationToken: cancellationToken);

        return (regionsBody?.Quotas ?? [])
            .Select(q => new ProviderQuotaInfo(q.Resource ?? "vcpu", q.Limit, q.Used, targetRegion))
            .ToArray();
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);

        var region = ResolveRegion(null);
        var query = $"?Action=DescribeInstances&Version={Ec2ApiVersion}&Filter.1.Name=tag%3AName&Filter.1.Value.1={Uri.EscapeDataString(serverName)}";
        using var request = CreateSignedGet(query, region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AwsDescribeInstancesResponse>(cancellationToken: cancellationToken);
        var matches = (body?.Instances ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.InstanceId)
                && string.Equals(i.Name ?? i.Tags?.GetValueOrDefault("Name"), serverName, StringComparison.Ordinal)
                && !IsTerminated(i.State))
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"AWS returned {matches.Count} active EC2 instances named '{serverName}' in region '{region}'. Reconcile duplicates before retrying.",
                response.StatusCode, Provider);
        }

        var instance = matches[0];
        EnsureAccountBoundary(instance.AccountId, response.StatusCode);
        return MapInstance(instance);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);

        var region = ResolveRegion(request.Region);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Name"] = request.Name,
            ["selfrun-managed"] = "true"
        };
        if (request.Labels is not null)
        {
            foreach (var label in request.Labels)
            {
                tags[label.Key] = label.Value;
            }
        }

        var payload = new AwsRunInstancesPayload
        {
            Action = "RunInstances",
            Version = Ec2ApiVersion,
            Region = region,
            InstanceType = request.Plan,
            ImageId = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image,
            KeyName = request.SshKeyReferences.FirstOrDefault(),
            UserData = string.IsNullOrWhiteSpace(request.UserDataCloudInit)
                ? null
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit)),
            ClientToken = BuildClientToken(request.Name, region),
            SubnetId = _options.SubnetId,
            SecurityGroupId = _options.SecurityGroupId,
            AccountId = _options.AccountId,
            Tags = tags
        };

        using var httpRequest = CreateSignedPost($"?Action=RunInstances&Version={Ec2ApiVersion}", payload, region);
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<AwsInstanceResponse>(cancellationToken: cancellationToken);
        var instance = body?.Instance ?? body?.Instances?.FirstOrDefault();
        if (instance is null || string.IsNullOrWhiteSpace(instance.InstanceId))
        {
            throw new ProviderApiException("AWS EC2 RunInstances returned no instance record.", response.StatusCode, Provider);
        }

        EnsureAccountBoundary(instance.AccountId, response.StatusCode);
        _logger?.LogInformation("Created AWS EC2 instance {Name} with ID {InstanceId} in {Region}", request.Name, instance.InstanceId, region);
        return MapInstance(instance);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var (region, instanceId) = ParseScopedId(serverId);

        using var request = CreateSignedGet($"?Action=DescribeInstances&Version={Ec2ApiVersion}&InstanceId.1={Uri.EscapeDataString(instanceId)}", region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AwsDescribeInstancesResponse>(cancellationToken: cancellationToken);
        var instance = body?.Instances?.FirstOrDefault(i => string.Equals(i.InstanceId, instanceId, StringComparison.Ordinal))
            ?? body?.Instances?.FirstOrDefault();
        if (instance is null || IsTerminated(instance.State))
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: instance?.State ?? "empty");
        }

        EnsureAccountBoundary(instance.AccountId, response.StatusCode);
        var state = instance.State ?? "unknown";
        var isRunning = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
        return new ProviderServerStatus(serverId, state.ToLowerInvariant(), instance.PublicIpAddress, instance.Ipv6Address, isRunning, RawStatus: state);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var (region, instanceId) = ParseScopedId(serverId);

        var payload = new AwsTerminateInstancesPayload
        {
            Action = "TerminateInstances",
            Version = Ec2ApiVersion,
            InstanceId = instanceId
        };

        using var request = CreateSignedPost($"?Action=TerminateInstances&Version={Ec2ApiVersion}&InstanceId.1={Uri.EscapeDataString(instanceId)}", payload, region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("AWS EC2 instance {ServerId} already absent during delete.", serverId);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("InvalidInstanceID.NotFound", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw BuildException(response.StatusCode, body);
        }
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        var region = ResolveRegion(null);
        using var request = CreateSignedGet($"?Action=DescribeKeyPairs&Version={Ec2ApiVersion}", region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AwsKeyPairsResponse>(cancellationToken: cancellationToken);
        return (body?.KeyPairs ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.KeyName))
            .Select(k => new ProviderSshKeyInfo(k.KeyPairId ?? k.KeyName!, k.KeyName!, k.PublicKey))
            .ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);

        var region = ResolveRegion(null);
        var payload = new AwsImportKeyPairPayload
        {
            Action = "ImportKeyPair",
            Version = Ec2ApiVersion,
            KeyName = name,
            PublicKeyMaterial = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))
        };

        using var request = CreateSignedPost($"?Action=ImportKeyPair&Version={Ec2ApiVersion}", payload, region);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<AwsKeyPairDto>(cancellationToken: cancellationToken);
        if (body is null || string.IsNullOrWhiteSpace(body.KeyName))
        {
            throw new ProviderApiException("AWS ImportKeyPair returned an empty key pair.", response.StatusCode, Provider);
        }

        return new ProviderSshKeyInfo(body.KeyPairId ?? body.KeyName, body.KeyName, body.PublicKey ?? publicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var region = ResolveRegion(null);
        using var request = CreateSignedGet($"?Action=DescribeVolumes&Version={Ec2ApiVersion}&Filter.1.Name=tag%3AName&Filter.1.Value.1={Uri.EscapeDataString(name)}", region);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync<AwsVolumesResponse>(cancellationToken: ct);
        var vol = body?.Volumes?.FirstOrDefault(v => string.Equals(v.Name ?? v.Tags?.GetValueOrDefault("Name"), name, StringComparison.Ordinal));
        return vol is null ? null : MapVolume(vol, region);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (region, volumeId) = ParseScopedId(id);
        using var request = CreateSignedGet($"?Action=DescribeVolumes&Version={Ec2ApiVersion}&VolumeId.1={Uri.EscapeDataString(volumeId)}", region);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadFromJsonAsync<AwsVolumesResponse>(cancellationToken: ct);
        var vol = body?.Volumes?.FirstOrDefault(v => string.Equals(v.VolumeId, volumeId, StringComparison.Ordinal));
        return vol is null ? null : MapVolume(vol, region);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var targetRegion = ResolveRegion(region);
        var tags = new Dictionary<string, string>(labels, StringComparer.Ordinal)
        {
            ["Name"] = name,
            ["selfrun-managed"] = "true"
        };
        var payload = new AwsCreateVolumePayload
        {
            Action = "CreateVolume",
            Version = Ec2ApiVersion,
            AvailabilityZone = targetRegion,
            Size = sizeGb,
            VolumeType = "gp3",
            Tags = tags
        };
        using var request = CreateSignedPost($"?Action=CreateVolume&Version={Ec2ApiVersion}", payload, targetRegion);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var vol = await response.Content.ReadFromJsonAsync<AwsVolumeDto>(cancellationToken: ct)
            ?? throw new ProviderApiException("AWS CreateVolume returned an empty volume.", response.StatusCode, Provider);
        return MapVolume(vol, targetRegion);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        var (region, volumeId) = ParseScopedId(id);
        var (_, instanceId) = ParseScopedId(serverId);
        var payload = new AwsAttachVolumePayload
        {
            Action = "AttachVolume",
            Version = Ec2ApiVersion,
            VolumeId = volumeId,
            InstanceId = instanceId,
            Device = "/dev/sdf"
        };
        using var request = CreateSignedPost($"?Action=AttachVolume&Version={Ec2ApiVersion}", payload, region);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        var (region, volumeId) = ParseScopedId(id);
        var payload = new AwsModifyVolumePayload
        {
            Action = "ModifyVolume",
            Version = Ec2ApiVersion,
            VolumeId = volumeId,
            Size = sizeGb
        };
        using var request = CreateSignedPost($"?Action=ModifyVolume&Version={Ec2ApiVersion}", payload, region);
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private HttpRequestMessage CreateSignedGet(string relativeQuery, string region)
    {
        var uri = BuildEndpointUri(region, relativeQuery);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        SignIfConfigured(request, string.Empty, region);
        return request;
    }

    private HttpRequestMessage CreateSignedPost<T>(string relativeQuery, T payload, string region)
    {
        var uri = BuildEndpointUri(region, relativeQuery);
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        SignIfConfigured(request, json, region);
        return request;
    }

    private void SignIfConfigured(HttpRequestMessage request, string payload, string region)
    {
        if (!string.IsNullOrWhiteSpace(_options.AccessKeyId) && !string.IsNullOrWhiteSpace(_options.SecretAccessKey))
        {
            AwsSigV4Signer.SignRequest(
                request,
                payload,
                _options.AccessKeyId,
                _options.SecretAccessKey,
                region,
                "ec2",
                _timeProvider.GetUtcNow(),
                _options.SessionToken);
        }
    }

    private Uri BuildEndpointUri(string region, string relativeQuery)
    {
        AwsOptions.ValidateRegion(region);
        var rawBase = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "https://ec2.{region}.amazonaws.com/"
            : _options.BaseUrl;
        var resolvedBase = rawBase.Replace("{region}", region, StringComparison.OrdinalIgnoreCase).TrimEnd('/') + "/";
        return new Uri(new Uri(resolvedBase), relativeQuery);
    }

    private string ResolveRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) ? (string.IsNullOrWhiteSpace(_options.DefaultRegion) ? "us-east-1" : _options.DefaultRegion) : region.Trim();

    private (string Region, string ResourceId) ParseScopedId(string compositeId)
    {
        var slash = compositeId.IndexOf('/');
        if (slash > 0 && slash < compositeId.Length - 1)
        {
            return (compositeId[..slash], compositeId[(slash + 1)..]);
        }

        return (ResolveRegion(null), compositeId);
    }

    private void EnsureAccountBoundary(string? responseAccountId, HttpStatusCode statusCode)
    {
        if (!string.IsNullOrWhiteSpace(_options.AccountId)
            && !string.IsNullOrWhiteSpace(responseAccountId)
            && !string.Equals(_options.AccountId, responseAccountId, StringComparison.Ordinal))
        {
            throw new ProviderApiException(
                "AWS response belonged to a different AWS account than the configured provider account boundary.",
                statusCode, Provider);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw BuildException(response.StatusCode, body);
    }

    private ProviderApiException BuildException(HttpStatusCode statusCode, string body)
    {
        var redactedBody = TokenRedactor.Redact(body, _options.AccessKeyId, _options.SecretAccessKey, _options.SessionToken);
        var message = $"AWS EC2 API error ({(int)statusCode}): {redactedBody}";
        return new ProviderApiException(message, statusCode, Provider, redactedBody);
    }

    private static bool IsTerminated(string? state) =>
        string.Equals(state, "terminated", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "shutting-down", StringComparison.OrdinalIgnoreCase);

    private static string BuildClientToken(string name, string region)
    {
        var raw = $"selfrun-{region}-{name}";
        return raw.Length <= 64 ? raw : raw[..64];
    }

    private static string NormalizeArchitecture(string? arch) =>
        arch?.Contains("arm", StringComparison.OrdinalIgnoreCase) == true ? "linux/arm64" : "linux/amd64";

    private static ProviderServerInfo MapInstance(AwsInstanceDto dto)
    {
        var name = dto.Name ?? dto.Tags?.GetValueOrDefault("Name") ?? dto.InstanceId ?? string.Empty;
        var id = string.IsNullOrWhiteSpace(dto.Region) ? dto.InstanceId! : $"{dto.Region}/{dto.InstanceId}";
        return new ProviderServerInfo(
            id,
            name,
            dto.PublicIpAddress ?? string.Empty,
            (dto.State ?? "unknown").ToLowerInvariant(),
            dto.Ipv6Address,
            dto.Tags);
    }

    private static ProviderStorageVolume MapVolume(AwsVolumeDto dto, string fallbackRegion)
    {
        var region = string.IsNullOrWhiteSpace(dto.Region) ? fallbackRegion : dto.Region!;
        var name = dto.Name ?? dto.Tags?.GetValueOrDefault("Name") ?? dto.VolumeId ?? string.Empty;
        return new ProviderStorageVolume(
            dto.VolumeId ?? string.Empty,
            name,
            dto.Size,
            region,
            dto.AttachedInstanceId,
            dto.Tags ?? new Dictionary<string, string>());
    }

    private sealed class AwsRegionListResponse
    {
        [JsonPropertyName("accountId")] public string? AccountId { get; set; }
        [JsonPropertyName("regions")] public List<AwsRegionDto>? Regions { get; set; }
        [JsonPropertyName("quotas")] public List<AwsQuotaDto>? Quotas { get; set; }
    }

    private sealed class AwsRegionDto
    {
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("zones")] public List<string>? Zones { get; set; }
    }

    private sealed class AwsQuotaDto
    {
        [JsonPropertyName("resource")] public string? Resource { get; set; }
        [JsonPropertyName("used")] public int Used { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
    }

    private sealed class AwsInstanceTypesResponse
    {
        [JsonPropertyName("instanceTypes")] public List<AwsInstanceTypeDto>? InstanceTypes { get; set; }
    }

    private sealed class AwsInstanceTypeDto
    {
        [JsonPropertyName("instanceType")] public string? InstanceType { get; set; }
        [JsonPropertyName("vcpus")] public int Vcpus { get; set; }
        [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; }
        [JsonPropertyName("diskGb")] public int DiskGb { get; set; }
        [JsonPropertyName("architectures")] public List<string>? Architectures { get; set; }
        [JsonPropertyName("available")] public bool? Available { get; set; }
    }

    private sealed class AwsImagesResponse
    {
        [JsonPropertyName("images")] public List<AwsImageDto>? Images { get; set; }
    }

    private sealed class AwsImageDto
    {
        [JsonPropertyName("imageId")] public string? ImageId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("architecture")] public string? Architecture { get; set; }
        [JsonPropertyName("osFamily")] public string? OsFamily { get; set; }
    }

    private sealed class AwsDescribeInstancesResponse
    {
        [JsonPropertyName("instances")] public List<AwsInstanceDto>? Instances { get; set; }
    }

    private sealed class AwsInstanceResponse
    {
        [JsonPropertyName("instance")] public AwsInstanceDto? Instance { get; set; }
        [JsonPropertyName("instances")] public List<AwsInstanceDto>? Instances { get; set; }
    }

    private sealed class AwsInstanceDto
    {
        [JsonPropertyName("instanceId")] public string? InstanceId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("accountId")] public string? AccountId { get; set; }
        [JsonPropertyName("publicIpAddress")] public string? PublicIpAddress { get; set; }
        [JsonPropertyName("ipv6Address")] public string? Ipv6Address { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
    }

    private sealed class AwsRunInstancesPayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("instanceType")] public string InstanceType { get; set; } = string.Empty;
        [JsonPropertyName("imageId")] public string ImageId { get; set; } = string.Empty;
        [JsonPropertyName("keyName")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? KeyName { get; set; }
        [JsonPropertyName("userData")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UserData { get; set; }
        [JsonPropertyName("clientToken")] public string ClientToken { get; set; } = string.Empty;
        [JsonPropertyName("subnetId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SubnetId { get; set; }
        [JsonPropertyName("securityGroupId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SecurityGroupId { get; set; }
        [JsonPropertyName("accountId")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AccountId { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string> Tags { get; set; } = [];
    }

    private sealed class AwsTerminateInstancesPayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = string.Empty;
    }

    private sealed class AwsKeyPairsResponse
    {
        [JsonPropertyName("keyPairs")] public List<AwsKeyPairDto>? KeyPairs { get; set; }
    }

    private sealed class AwsKeyPairDto
    {
        [JsonPropertyName("keyPairId")] public string? KeyPairId { get; set; }
        [JsonPropertyName("keyName")] public string? KeyName { get; set; }
        [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }
    }

    private sealed class AwsImportKeyPairPayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("keyName")] public string KeyName { get; set; } = string.Empty;
        [JsonPropertyName("publicKeyMaterial")] public string PublicKeyMaterial { get; set; } = string.Empty;
    }

    private sealed class AwsVolumesResponse
    {
        [JsonPropertyName("volumes")] public List<AwsVolumeDto>? Volumes { get; set; }
    }

    private sealed class AwsVolumeDto
    {
        [JsonPropertyName("volumeId")] public string? VolumeId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("attachedInstanceId")] public string? AttachedInstanceId { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
    }

    private sealed class AwsCreateVolumePayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("availabilityZone")] public string AvailabilityZone { get; set; } = string.Empty;
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("volumeType")] public string VolumeType { get; set; } = "gp3";
        [JsonPropertyName("tags")] public Dictionary<string, string> Tags { get; set; } = [];
    }

    private sealed class AwsAttachVolumePayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("volumeId")] public string VolumeId { get; set; } = string.Empty;
        [JsonPropertyName("instanceId")] public string InstanceId { get; set; } = string.Empty;
        [JsonPropertyName("device")] public string Device { get; set; } = string.Empty;
    }

    private sealed class AwsModifyVolumePayload
    {
        [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
        [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
        [JsonPropertyName("volumeId")] public string VolumeId { get; set; } = string.Empty;
        [JsonPropertyName("size")] public int Size { get; set; }
    }
}
