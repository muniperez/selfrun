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
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.GoogleCloud;

/// <summary>
/// SDK-independent Google Cloud Compute Engine adapter supporting service-account RS256 JWT assertion
/// exchange and pre-issued WIF/bearer tokens with strict project boundaries, paginated aggregated
/// discovery, async zone operation validation, and persistent disk management.
/// </summary>
public sealed class GoogleCloudClient :
    IVpsProviderClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient
{
    private const string ComputeScope = "https://www.googleapis.com/auth/compute";

    private readonly HttpClient _httpClient;
    private readonly GoogleCloudOptions _options;
    private readonly ILogger<GoogleCloudClient>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry;

    public CloudProvider Provider => CloudProvider.GoogleCloud;

    [ActivatorUtilitiesConstructor]
    public GoogleCloudClient(
        HttpClient httpClient,
        IOptions<GoogleCloudOptions> options,
        ILogger<GoogleCloudClient>? logger = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, options.Value, logger, timeProvider)
    {
    }

    public GoogleCloudClient(
        HttpClient httpClient,
        GoogleCloudOptions options,
        ILogger<GoogleCloudClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!string.IsNullOrWhiteSpace(_options.CredentialsJson)
            && string.IsNullOrWhiteSpace(_options.PrivateKeyPem)
            && string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            var parsed = GoogleCloudParsedCredentials.Parse(_options.CredentialsJson, string.IsNullOrWhiteSpace(_options.ProjectId) ? null : _options.ProjectId);
            _options.ProjectId = parsed.ProjectId;
            _options.ClientEmail = parsed.ClientEmail;
            _options.PrivateKeyPem = parsed.PrivateKeyPem;
            _options.AccessToken = parsed.AccessToken;
            if (!string.IsNullOrWhiteSpace(parsed.TokenUri))
            {
                _options.TokenEndpoint = parsed.TokenUri;
            }
        }
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var request = await CreateRequestAsync(HttpMethod.Get, $"projects/{Uri.EscapeDataString(_options.ProjectId)}", cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var project = await response.Content.ReadFromJsonAsync<GcpProjectDto>(cancellationToken: cancellationToken);
        var scopeId = string.IsNullOrWhiteSpace(project?.Name) ? _options.ProjectId : project.Name.Trim();

        return new ProviderScopeIdentity(
            ProviderWireType: "gcp",
            ScopeKind: "project",
            ScopeId: scopeId,
            ObservedPrincipal: _options.ClientEmail);
    }

    public async Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var zonesReq = await CreateRequestAsync(HttpMethod.Get, $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones", cancellationToken);
        using var zonesRes = await _httpClient.SendAsync(zonesReq, cancellationToken);
        await EnsureSuccessAsync(zonesRes, cancellationToken);
        var zonesBody = await zonesRes.Content.ReadFromJsonAsync<GcpItemsResponse<GcpZoneDto>>(cancellationToken: cancellationToken);

        return (zonesBody?.Items ?? [])
            .Where(z => !string.IsNullOrWhiteSpace(z.Name))
            .Select(z => new ProviderLocationInfo(
                z.Name!,
                z.Name!,
                ProviderLocationGranularity.Zone,
                ParentRegion: ExtractRegionFromZone(z.Name!),
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

        EnsureProjectConfigured();
        var zone = ResolveZone(location);
        using var mtReq = await CreateRequestAsync(HttpMethod.Get, $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/machineTypes", cancellationToken);
        using var mtRes = await _httpClient.SendAsync(mtReq, cancellationToken);
        await EnsureSuccessAsync(mtRes, cancellationToken);
        var mtBody = await mtRes.Content.ReadFromJsonAsync<GcpItemsResponse<GcpMachineTypeDto>>(cancellationToken: cancellationToken);

        return (mtBody?.Items ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new ProviderComputeOfferInfo(
                m.Name!,
                m.Name!,
                ProviderComputeFamily.VirtualServer,
                InferArchitectures(m.Name!),
                m.GuestCpus,
                m.MemoryMb,
                0,
                AvailableLocations: [zone]))
            .ToArray();
    }

    public Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        var images = new ProviderImageInfo[]
        {
            new("projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-amd64", "Ubuntu 24.04 LTS (amd64)", "linux/amd64", Distribution: "ubuntu", SupportsCloudInit: true),
            new("projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-arm64", "Ubuntu 24.04 LTS (arm64)", "linux/arm64", Distribution: "ubuntu", SupportsCloudInit: true)
        };

        IReadOnlyList<ProviderImageInfo> filtered = string.IsNullOrWhiteSpace(architecture)
            ? images
            : images.Where(i => string.Equals(i.Architecture, architecture, StringComparison.OrdinalIgnoreCase)).ToArray();

        return Task.FromResult(filtered);
    }

    public async Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default)
    {
        EnsureProjectConfigured();
        using var projectReq = await CreateRequestAsync(HttpMethod.Get, $"projects/{Uri.EscapeDataString(_options.ProjectId)}", cancellationToken);
        using var projectRes = await _httpClient.SendAsync(projectReq, cancellationToken);
        await EnsureSuccessAsync(projectRes, cancellationToken);
        var project = await projectRes.Content.ReadFromJsonAsync<GcpProjectDto>(cancellationToken: cancellationToken);

        return (project?.Quotas ?? [])
            .Select(q => new ProviderQuotaInfo(q.Metric ?? "CPUS", (long)q.Limit, (long)q.Usage, location))
            .ToArray();
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        EnsureProjectConfigured();

        var matches = new List<(string Zone, GcpInstanceDto Instance)>();
        string? pageToken = null;
        var pages = 0;

        do
        {
            pages++;
            if (pages > 100)
            {
                throw new ProviderApiException("GCP aggregated instance discovery exceeded maximum pagination depth.", HttpStatusCode.InternalServerError, Provider);
            }

            var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/aggregated/instances?filter={Uri.EscapeDataString($"name = \"{serverName}\"")}";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            }

            using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<GcpAggregatedInstancesResponse>(cancellationToken: cancellationToken);
            if (body?.Items is not null)
            {
                foreach (var entry in body.Items)
                {
                    var zone = entry.Key.StartsWith("zones/", StringComparison.OrdinalIgnoreCase)
                        ? entry.Key["zones/".Length..]
                        : entry.Key;
                    foreach (var instance in entry.Value.Instances ?? [])
                    {
                        if (string.Equals(instance.Name, serverName, StringComparison.Ordinal))
                        {
                            EnsureProjectBoundary(instance.SelfLink, response.StatusCode);
                            matches.Add((ExtractZone(instance.Zone) ?? zone, instance));
                        }
                    }
                }
            }

            pageToken = body?.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"GCP returned {matches.Count} instances named '{serverName}' across zones in project '{_options.ProjectId}'. Reconcile duplicates before retrying.",
                HttpStatusCode.Conflict, Provider);
        }

        var (matchedZone, matched) = matches[0];
        return MapInstance(matchedZone, matched);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        EnsureProjectConfigured();

        var zone = ResolveZone(request.Region);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minicloud-managed"] = "true"
        };
        if (request.Labels is not null)
        {
            foreach (var pair in request.Labels)
            {
                labels[SanitizeLabelKey(pair.Key)] = SanitizeLabelValue(pair.Value);
            }
        }

        var metadataItems = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.UserDataCloudInit))
        {
            metadataItems.Add(new { key = "user-data", value = request.UserDataCloudInit });
        }

        if (request.SshKeyReferences.Count > 0)
        {
            var sshLines = string.Join('\n', request.SshKeyReferences.Select(k => k.Contains(':') ? k : $"minicloud:{k}"));
            metadataItems.Add(new { key = "ssh-keys", value = sshLines });
        }

        var sourceImage = string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image;
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/instances";
        using var httpRequest = await CreateRequestAsync(HttpMethod.Post, path, cancellationToken);
        httpRequest.Content = JsonContent.Create(new
        {
            name = request.Name,
            machineType = $"zones/{zone}/machineTypes/{request.Plan}",
            labels,
            disks = new[]
            {
                new
                {
                    boot = true,
                    autoDelete = true,
                    initializeParams = new { sourceImage }
                }
            },
            networkInterfaces = new[]
            {
                new
                {
                    network = "global/networks/default",
                    accessConfigs = new[] { new { type = "ONE_TO_ONE_NAT", name = "External NAT" } }
                }
            },
            metadata = new { items = metadataItems }
        });

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var op = await response.Content.ReadFromJsonAsync<GcpOperationDto>(cancellationToken: cancellationToken);
        if (op is null || string.IsNullOrWhiteSpace(op.Name))
        {
            throw new ProviderApiException("GCP Compute insert did not return a zone operation.", response.StatusCode, Provider);
        }

        EnsureProjectBoundary(op.SelfLink ?? op.TargetLink, response.StatusCode);
        if (op.Error?.Errors is { Count: > 0 } errors)
        {
            var details = string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"));
            throw new ProviderApiException($"GCP instance creation operation failed: {details}", response.StatusCode, Provider);
        }

        _logger?.LogInformation("Submitted GCP instance creation {Name} in {Project}/{Zone} (op {OpName})", request.Name, _options.ProjectId, zone, op.Name);
        return new ProviderServerInfo(
            $"{zone}/{request.Name}",
            request.Name,
            string.Empty,
            (op.Status ?? "provisioning").ToLowerInvariant(),
            null,
            labels);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        var (zone, name) = ParseZoneAndName(serverId);
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(name)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var instance = await response.Content.ReadFromJsonAsync<GcpInstanceDto>(cancellationToken: cancellationToken);
        if (instance is null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        EnsureProjectBoundary(instance.SelfLink, response.StatusCode);
        var status = instance.Status ?? "UNKNOWN";
        var isRunning = string.Equals(status, "RUNNING", StringComparison.OrdinalIgnoreCase);
        var ipv4 = ExtractNatIp(instance);
        var ipv6 = ExtractIpv6(instance);
        return new ProviderServerStatus(serverId, status.ToLowerInvariant(), ipv4, ipv6, isRunning, RawStatus: status);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureProjectConfigured();

        var (zone, name) = ParseZoneAndName(serverId);
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/instances/{Uri.EscapeDataString(name)}";
        using var request = await CreateRequestAsync(HttpMethod.Delete, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("GCP instance {ServerId} already absent during delete.", serverId);
            return;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var op = await response.Content.ReadFromJsonAsync<GcpOperationDto>(cancellationToken: cancellationToken);
        if (op is null || string.IsNullOrWhiteSpace(op.Name))
        {
            throw new ProviderApiException("GCP instance delete response did not include an operation.", response.StatusCode, Provider);
        }

        EnsureProjectBoundary(op.SelfLink ?? op.TargetLink, response.StatusCode);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(null);
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/disks?filter={Uri.EscapeDataString($"name = \"{name}\"")}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var list = await response.Content.ReadFromJsonAsync<GcpItemsResponse<GcpDiskDto>>(cancellationToken: ct);
        var disk = list?.Items?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
        return disk is null ? null : MapDisk(zone, disk);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (zone, name) = ParseZoneAndName(id);
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/disks/{Uri.EscapeDataString(name)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var disk = await response.Content.ReadFromJsonAsync<GcpDiskDto>(cancellationToken: ct);
        return disk is null ? null : MapDisk(zone, disk);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var zone = ResolveZone(region);
        var safeLabels = new Dictionary<string, string>(StringComparer.Ordinal) { ["minicloud-managed"] = "true" };
        foreach (var pair in labels) safeLabels[SanitizeLabelKey(pair.Key)] = SanitizeLabelValue(pair.Value);

        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/disks";
        using var request = await CreateRequestAsync(HttpMethod.Post, path, ct);
        request.Content = JsonContent.Create(new
        {
            name,
            sizeGb = sizeGb.ToString(),
            type = $"zones/{zone}/diskTypes/pd-balanced",
            labels = safeLabels
        });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return new ProviderStorageVolume($"{zone}/{name}", name, sizeGb, zone, null, safeLabels);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (diskZone, diskName) = ParseZoneAndName(id);
        var (vmZone, vmName) = ParseZoneAndName(serverId);
        if (!string.Equals(diskZone, vmZone, StringComparison.Ordinal))
        {
            throw new ProviderApiException("GCP zonal persistent disks can only attach to instances in the same zone.", HttpStatusCode.BadRequest, Provider);
        }

        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(vmZone)}/instances/{Uri.EscapeDataString(vmName)}/attachDisk";
        using var request = await CreateRequestAsync(HttpMethod.Post, path, ct);
        request.Content = JsonContent.Create(new
        {
            source = $"projects/{_options.ProjectId}/zones/{diskZone}/disks/{diskName}",
            deviceName = diskName
        });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        EnsureProjectConfigured();
        var (zone, name) = ParseZoneAndName(id);
        var path = $"projects/{Uri.EscapeDataString(_options.ProjectId)}/zones/{Uri.EscapeDataString(zone)}/disks/{Uri.EscapeDataString(name)}/resize";
        using var request = await CreateRequestAsync(HttpMethod.Post, path, ct);
        request.Content = JsonContent.Create(new { sizeGb = sizeGb.ToString() });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private void EnsureProjectConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new ProviderApiException("Google Cloud projectId must be configured.", HttpStatusCode.BadRequest, Provider);
        }
    }

    private void EnsureProjectBoundary(string? selfLink, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(selfLink)) return;
        var expected = $"/projects/{_options.ProjectId}/";
        if (!selfLink.Contains(expected, StringComparison.Ordinal))
        {
            throw new ProviderApiException(
                "GCP resource belongs to a different project than the configured provider account boundary.",
                statusCode, Provider);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        var token = await ResolveAccessTokenAsync(ct);
        var baseUri = new Uri((string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://compute.googleapis.com/compute/v1/" : _options.BaseUrl).TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<string> ResolveAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return _options.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(_options.ClientEmail) || string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
        {
            return string.Empty;
        }

        var now = _timeProvider.GetUtcNow();
        if (_cachedToken is not null && now < _cachedTokenExpiry)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cachedToken is not null && now < _cachedTokenExpiry)
            {
                return _cachedToken;
            }

            var assertion = CreateSignedJwtAssertion(_options.ClientEmail, _options.PrivateKeyPem, _options.TokenEndpoint, now);
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };

            using var tokenRes = await _httpClient.SendAsync(tokenReq, ct);
            if (!tokenRes.IsSuccessStatusCode)
            {
                var body = await tokenRes.Content.ReadAsStringAsync(ct);
                var redacted = TokenRedactor.Redact(body, _options.PrivateKeyPem, _options.AccessToken);
                throw new ProviderApiException($"GCP OAuth token exchange failed ({(int)tokenRes.StatusCode}): {redacted}", tokenRes.StatusCode, Provider, redacted);
            }

            var tokenDto = await tokenRes.Content.ReadFromJsonAsync<GcpTokenResponse>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(tokenDto?.AccessToken))
            {
                throw new ProviderApiException("GCP OAuth token exchange did not return an access_token.", tokenRes.StatusCode, Provider);
            }

            var expiresIn = tokenDto.ExpiresIn > 120 ? tokenDto.ExpiresIn - 60 : Math.Max(10, tokenDto.ExpiresIn);
            _cachedToken = tokenDto.AccessToken;
            _cachedTokenExpiry = now.AddSeconds(expiresIn);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    internal static string CreateSignedJwtAssertion(string clientEmail, string privateKeyPem, string tokenUri, DateTimeOffset now)
    {
        var headerJson = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var iat = now.ToUnixTimeSeconds();
        var exp = now.AddHours(1).ToUnixTimeSeconds();
        var payloadJson = JsonSerializer.Serialize(new
        {
            iss = clientEmail,
            scope = ComputeScope,
            aud = tokenUri,
            iat,
            exp
        });

        var unsignedToken = $"{Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson))}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson))}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.Replace("\\n", "\n", StringComparison.Ordinal));
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(unsignedToken), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var redacted = TokenRedactor.Redact(body, _options.PrivateKeyPem, _options.AccessToken, _cachedToken);
        throw new ProviderApiException($"GCP Compute API error ({(int)response.StatusCode}): {redacted}", response.StatusCode, Provider, redacted);
    }

    private string ResolveZone(string? zoneOrRegion)
    {
        var raw = string.IsNullOrWhiteSpace(zoneOrRegion) ? _options.DefaultZone : zoneOrRegion.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "us-central1-a";
        // If caller passes a region (two hyphen segments like "us-central1"), append "-a".
        return raw.Count(c => c == '-') == 1 ? $"{raw}-a" : raw;
    }

    private (string Zone, string Name) ParseZoneAndName(string serverId)
    {
        var slash = serverId.IndexOf('/');
        if (slash > 0 && slash < serverId.Length - 1)
        {
            return (serverId[..slash], serverId[(slash + 1)..]);
        }

        return (ResolveZone(null), serverId);
    }

    private static string? ExtractZone(string? zoneUrl)
    {
        if (string.IsNullOrWhiteSpace(zoneUrl)) return null;
        var lastSlash = zoneUrl.LastIndexOf('/');
        return lastSlash >= 0 ? zoneUrl[(lastSlash + 1)..] : zoneUrl;
    }

    private static string ExtractRegionFromZone(string zone)
    {
        var lastDash = zone.LastIndexOf('-');
        return lastDash > 0 ? zone[..lastDash] : zone;
    }

    private static IReadOnlyList<string> InferArchitectures(string machineType) =>
        machineType.StartsWith("t2a-", StringComparison.OrdinalIgnoreCase)
        || machineType.StartsWith("c4a-", StringComparison.OrdinalIgnoreCase)
            ? ["linux/arm64"]
            : ["linux/amd64"];

    private static string SanitizeLabelKey(string key) =>
        new(key.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());

    private static string SanitizeLabelValue(string value) =>
        new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray());

    private static string? ExtractNatIp(GcpInstanceDto dto) =>
        dto.NetworkInterfaces?
            .SelectMany(n => n.AccessConfigs ?? [])
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.NatIP))?.NatIP;

    private static string? ExtractIpv6(GcpInstanceDto dto) =>
        dto.NetworkInterfaces?
            .SelectMany(n => n.Ipv6AccessConfigs ?? [])
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.ExternalIpv6))?.ExternalIpv6;

    private static ProviderServerInfo MapInstance(string zone, GcpInstanceDto dto) => new(
        $"{zone}/{dto.Name}",
        dto.Name ?? string.Empty,
        ExtractNatIp(dto) ?? string.Empty,
        (dto.Status ?? "unknown").ToLowerInvariant(),
        ExtractIpv6(dto),
        dto.Labels);

    private static ProviderStorageVolume MapDisk(string zone, GcpDiskDto dto)
    {
        int.TryParse(dto.SizeGb, out var sizeGb);
        var attached = dto.Users?.FirstOrDefault() is { } userUrl
            ? $"{zone}/{ExtractZone(userUrl)}"
            : null;
        return new ProviderStorageVolume(
            $"{zone}/{dto.Name}",
            dto.Name ?? string.Empty,
            sizeGb,
            zone,
            attached,
            dto.Labels ?? new Dictionary<string, string>());
    }

    private sealed class GcpTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class GcpProjectDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("quotas")] public List<GcpQuotaDto>? Quotas { get; set; }
    }

    private sealed class GcpQuotaDto
    {
        [JsonPropertyName("metric")] public string? Metric { get; set; }
        [JsonPropertyName("usage")] public double Usage { get; set; }
        [JsonPropertyName("limit")] public double Limit { get; set; }
    }

    private sealed class GcpItemsResponse<T>
    {
        [JsonPropertyName("items")] public List<T>? Items { get; set; }
    }

    private sealed class GcpZoneDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class GcpMachineTypeDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("guestCpus")] public int GuestCpus { get; set; }
        [JsonPropertyName("memoryMb")] public int MemoryMb { get; set; }
    }

    private sealed class GcpAggregatedInstancesResponse
    {
        [JsonPropertyName("items")] public Dictionary<string, GcpZoneInstancesBucket>? Items { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    }

    private sealed class GcpZoneInstancesBucket
    {
        [JsonPropertyName("instances")] public List<GcpInstanceDto>? Instances { get; set; }
    }

    private sealed class GcpInstanceDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("zone")] public string? Zone { get; set; }
        [JsonPropertyName("selfLink")] public string? SelfLink { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
        [JsonPropertyName("networkInterfaces")] public List<GcpNetworkInterfaceDto>? NetworkInterfaces { get; set; }
    }

    private sealed class GcpNetworkInterfaceDto
    {
        [JsonPropertyName("accessConfigs")] public List<GcpAccessConfigDto>? AccessConfigs { get; set; }
        [JsonPropertyName("ipv6AccessConfigs")] public List<GcpIpv6AccessConfigDto>? Ipv6AccessConfigs { get; set; }
    }

    private sealed class GcpAccessConfigDto
    {
        [JsonPropertyName("natIP")] public string? NatIP { get; set; }
    }

    private sealed class GcpIpv6AccessConfigDto
    {
        [JsonPropertyName("externalIpv6")] public string? ExternalIpv6 { get; set; }
    }

    private sealed class GcpOperationDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("selfLink")] public string? SelfLink { get; set; }
        [JsonPropertyName("targetLink")] public string? TargetLink { get; set; }
        [JsonPropertyName("error")] public GcpOperationErrorDto? Error { get; set; }
    }

    private sealed class GcpOperationErrorDto
    {
        [JsonPropertyName("errors")] public List<GcpErrorItemDto>? Errors { get; set; }
    }

    private sealed class GcpErrorItemDto
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class GcpDiskDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("sizeGb")] public string? SizeGb { get; set; }
        [JsonPropertyName("users")] public List<string>? Users { get; set; }
        [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    }
}
