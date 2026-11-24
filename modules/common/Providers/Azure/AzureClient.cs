using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Azure;

/// <summary>
/// SDK-independent Microsoft Azure Compute/Network/Storage adapter supporting service-principal OAuth2
/// and managed-identity bearer credentials with explicit subscription and resource-group boundaries.
/// </summary>
public sealed class AzureClient :
    IVpsProviderClient,
    IVpsProviderSshKeyClient,
    IStorageVolumeProvider,
    IVpsProviderScopeClient,
    IVpsProviderCatalogDiscoveryClient
{
    private readonly HttpClient _httpClient;
    private readonly AzureOptions _options;
    private readonly ILogger<AzureClient>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiry;

    public CloudProvider Provider => CloudProvider.Azure;

    [ActivatorUtilitiesConstructor]
    public AzureClient(
        HttpClient httpClient,
        IOptions<AzureOptions> options,
        ILogger<AzureClient>? logger = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, options.Value, logger, timeProvider)
    {
    }

    public AzureClient(
        HttpClient httpClient,
        AzureOptions options,
        ILogger<AzureClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default)
    {
        EnsureScopeConfigured();
        var path = $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(_options.ResourceGroup)}?api-version=2022-09-01";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        _ = await response.Content.ReadFromJsonAsync<AzureResourceGroupDto>(cancellationToken: cancellationToken);

        return new ProviderScopeIdentity(
            ProviderWireType: "azure",
            ScopeKind: "subscription-resource-group",
            ScopeId: $"{_options.SubscriptionId}/{_options.ResourceGroup}",
            SecondaryScopeId: string.IsNullOrWhiteSpace(_options.TenantId) ? null : _options.TenantId,
            ObservedPrincipal: string.IsNullOrWhiteSpace(_options.ClientId) ? null : _options.ClientId);
    }

    public async Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default)
    {
        EnsureScopeConfigured();
        using var locReq = await CreateRequestAsync(HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/locations?api-version=2022-12-01", cancellationToken);
        using var locRes = await _httpClient.SendAsync(locReq, cancellationToken);
        await EnsureSuccessAsync(locRes, cancellationToken);
        var locBody = await locRes.Content.ReadFromJsonAsync<AzureListResponse<AzureLocationDto>>(cancellationToken: cancellationToken);

        return (locBody?.Value ?? [])
            .Where(l => !string.IsNullOrWhiteSpace(l.Name))
            .Select(l => new ProviderLocationInfo(
                l.Name!,
                l.DisplayName ?? l.Name!,
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

        EnsureScopeConfigured();
        var targetLocation = string.IsNullOrWhiteSpace(location) ? _options.DefaultLocation : location.Trim();
        using var skuReq = await CreateRequestAsync(HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/providers/Microsoft.Compute/locations/{Uri.EscapeDataString(targetLocation)}/vmSizes?api-version={_options.ApiVersion}", cancellationToken);
        using var skuRes = await _httpClient.SendAsync(skuReq, cancellationToken);
        await EnsureSuccessAsync(skuRes, cancellationToken);
        var skuBody = await skuRes.Content.ReadFromJsonAsync<AzureListResponse<AzureVmSizeDto>>(cancellationToken: cancellationToken);

        return (skuBody?.Value ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new ProviderComputeOfferInfo(
                s.Name!,
                s.Name!,
                ProviderComputeFamily.VirtualServer,
                s.Architectures is { Count: > 0 } ? s.Architectures : ["linux/amd64"],
                s.NumberOfCores,
                s.MemoryInMb,
                s.OsDiskSizeInMb / 1024,
                AvailableLocations: [targetLocation]))
            .ToArray();
    }

    public Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default)
    {
        var images = new ProviderImageInfo[]
        {
            new(_options.DefaultImage, "Ubuntu 24.04 LTS (amd64)", "linux/amd64", Distribution: "ubuntu", SupportsCloudInit: true),
            new("Canonical:ubuntu-24_04-lts:server-arm64:latest", "Ubuntu 24.04 LTS (arm64)", "linux/arm64", Distribution: "ubuntu", SupportsCloudInit: true)
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
        EnsureScopeConfigured();
        var targetLocation = string.IsNullOrWhiteSpace(location) ? _options.DefaultLocation : location.Trim();
        using var usageReq = await CreateRequestAsync(HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/providers/Microsoft.Compute/locations/{Uri.EscapeDataString(targetLocation)}/usages?api-version={_options.ApiVersion}", cancellationToken);
        using var usageRes = await _httpClient.SendAsync(usageReq, cancellationToken);
        await EnsureSuccessAsync(usageRes, cancellationToken);
        var usageBody = await usageRes.Content.ReadFromJsonAsync<AzureListResponse<AzureUsageDto>>(cancellationToken: cancellationToken);

        return (usageBody?.Value ?? [])
            .Select(u => new ProviderQuotaInfo(u.Name?.Value ?? "cores", u.Limit, u.CurrentValue, targetLocation))
            .ToArray();
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        EnsureScopeConfigured();

        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/virtualMachines?api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);

        var list = await response.Content.ReadFromJsonAsync<AzureListResponse<AzureVmDto>>(cancellationToken: cancellationToken);
        var matches = (list?.Value ?? [])
            .Where(vm => string.Equals(vm.Name, serverName, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            throw new ProviderApiException(
                $"Azure returned {matches.Count} virtual machines named '{serverName}' in resource group '{_options.ResourceGroup}'.",
                response.StatusCode, Provider);
        }

        var vm = matches[0];
        EnsureResourceOwnership(vm.Id, response.StatusCode);
        return MapVm(vm);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        EnsureScopeConfigured();

        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minicloud-managed"] = "true"
        };
        if (request.Labels is not null)
        {
            foreach (var label in request.Labels) tags[label.Key] = label.Value;
        }

        var pipName = $"{request.Name}-pip";
        var nicName = $"{request.Name}-nic";
        var pipCreated = false;
        var nicCreated = false;

        try
        {
            // 1. Allocate public IP in scoped resource group
            using var pipReq = await CreateRequestAsync(HttpMethod.Put,
                $"{ResourceGroupPrefix}/providers/Microsoft.Network/publicIPAddresses/{Uri.EscapeDataString(pipName)}?api-version=2024-01-01",
                cancellationToken);
            pipReq.Content = JsonContent.Create(new
            {
                location = request.Region,
                sku = new { name = "Standard" },
                properties = new { publicIPAllocationMethod = "Static" },
                tags
            });
            using var pipRes = await _httpClient.SendAsync(pipReq, cancellationToken);
            await EnsureSuccessAsync(pipRes, cancellationToken);
            var pip = await pipRes.Content.ReadFromJsonAsync<AzurePublicIpDto>(cancellationToken: cancellationToken);
            pipCreated = true;

            // 2. Allocate NIC referencing public IP
            using var nicReq = await CreateRequestAsync(HttpMethod.Put,
                $"{ResourceGroupPrefix}/providers/Microsoft.Network/networkInterfaces/{Uri.EscapeDataString(nicName)}?api-version=2024-01-01",
                cancellationToken);
            nicReq.Content = JsonContent.Create(new
            {
                location = request.Region,
                properties = new
                {
                    ipConfigurations = new[]
                    {
                        new
                        {
                            name = "ipconfig1",
                            properties = new
                            {
                                publicIPAddress = new { id = pip?.Id ?? $"/{ResourceGroupPrefix}/providers/Microsoft.Network/publicIPAddresses/{pipName}" }
                            }
                        }
                    }
                },
                tags
            });
            using var nicRes = await _httpClient.SendAsync(nicReq, cancellationToken);
            await EnsureSuccessAsync(nicRes, cancellationToken);
            var nic = await nicRes.Content.ReadFromJsonAsync<AzureNicDto>(cancellationToken: cancellationToken);
            nicCreated = true;

            // 3. Create VM with deleteOption=Delete on OS disk & NIC and base64 cloud-init customData
            var (publisher, offer, sku, version) = ParseImageReference(string.IsNullOrWhiteSpace(request.Image) ? _options.DefaultImage : request.Image);
            var sshKeys = request.SshKeyReferences
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => new { path = "/home/minicloud/.ssh/authorized_keys", keyData = k })
                .ToArray();

            using var vmReq = await CreateRequestAsync(HttpMethod.Put,
                $"{ResourceGroupPrefix}/providers/Microsoft.Compute/virtualMachines/{Uri.EscapeDataString(request.Name)}?api-version={_options.ApiVersion}",
                cancellationToken);
            vmReq.Content = JsonContent.Create(new
            {
                location = request.Region,
                tags,
                properties = new
                {
                    hardwareProfile = new { vmSize = request.Plan },
                    storageProfile = new
                    {
                        imageReference = new { publisher, offer, sku, version },
                        osDisk = new { createOption = "FromImage", deleteOption = "Delete" }
                    },
                    osProfile = new
                    {
                        computerName = request.Hostname ?? request.Name,
                        adminUsername = "minicloud",
                        customData = string.IsNullOrWhiteSpace(request.UserDataCloudInit)
                            ? null
                            : Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit)),
                        linuxConfiguration = new
                        {
                            disablePasswordAuthentication = true,
                            ssh = new { publicKeys = sshKeys }
                        }
                    },
                    networkProfile = new
                    {
                        networkInterfaces = new[]
                        {
                            new { id = nic?.Id ?? $"/{ResourceGroupPrefix}/providers/Microsoft.Network/networkInterfaces/{nicName}", properties = new { deleteOption = "Delete" } }
                        }
                    }
                }
            });

            using var vmRes = await _httpClient.SendAsync(vmReq, cancellationToken);
            await EnsureSuccessAsync(vmRes, cancellationToken);
            var vm = await vmRes.Content.ReadFromJsonAsync<AzureVmDto>(cancellationToken: cancellationToken)
                ?? throw new ProviderApiException("Azure Compute returned an empty VM response.", vmRes.StatusCode, Provider);

            EnsureResourceOwnership(vm.Id, vmRes.StatusCode);
            var info = MapVm(vm);
            if (string.IsNullOrWhiteSpace(info.Ipv4Address) && !string.IsNullOrWhiteSpace(pip?.Properties?.IpAddress))
            {
                info = info with { Ipv4Address = pip.Properties.IpAddress };
            }

            _logger?.LogInformation("Created Azure VM {VmName} in {Subscription}/{ResourceGroup}", request.Name, _options.SubscriptionId, _options.ResourceGroup);
            return info;
        }
        catch (ProviderApiException)
        {
            // Best-effort cleanup of partially allocated network resources so failed allocations don't leak PIPs/NICs.
            if (nicCreated)
            {
                await TryDeleteResourceAsync($"{ResourceGroupPrefix}/providers/Microsoft.Network/networkInterfaces/{Uri.EscapeDataString(nicName)}?api-version=2024-01-01", cancellationToken);
            }

            if (pipCreated)
            {
                await TryDeleteResourceAsync($"{ResourceGroupPrefix}/providers/Microsoft.Network/publicIPAddresses/{Uri.EscapeDataString(pipName)}?api-version=2024-01-01", cancellationToken);
            }

            throw;
        }
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureScopeConfigured();

        var vmName = ExtractVmName(serverId);
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/virtualMachines/{Uri.EscapeDataString(vmName)}?$expand=instanceView&api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "404");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var vm = await response.Content.ReadFromJsonAsync<AzureVmDto>(cancellationToken: cancellationToken);
        if (vm is null)
        {
            return new ProviderServerStatus(serverId, "not_found", null, null, false, RawStatus: "empty");
        }

        EnsureResourceOwnership(vm.Id, response.StatusCode);
        var powerCode = vm.Properties?.InstanceView?.Statuses?
            .FirstOrDefault(s => s.Code?.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase) == true)?.Code;
        var provisioningState = vm.Properties?.ProvisioningState ?? "unknown";
        var isRunning = string.Equals(powerCode, "PowerState/running", StringComparison.OrdinalIgnoreCase)
            || (powerCode is null && string.Equals(provisioningState, "Succeeded", StringComparison.OrdinalIgnoreCase));
        var normalized = isRunning ? "running" : (powerCode ?? provisioningState).ToLowerInvariant();
        return new ProviderServerStatus(serverId, normalized, vm.PublicIpv4 ?? vm.Properties?.PublicIpAddress, vm.PublicIpv6, isRunning, RawStatus: powerCode ?? provisioningState);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        EnsureScopeConfigured();

        var vmName = ExtractVmName(serverId);
        var vmPath = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/virtualMachines/{Uri.EscapeDataString(vmName)}?api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Delete, vmPath, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        // Clean up companion NIC and Public IP idempotently (404 ignored).
        await TryDeleteResourceAsync($"{ResourceGroupPrefix}/providers/Microsoft.Network/networkInterfaces/{Uri.EscapeDataString(vmName + "-nic")}?api-version=2024-01-01", cancellationToken);
        await TryDeleteResourceAsync($"{ResourceGroupPrefix}/providers/Microsoft.Network/publicIPAddresses/{Uri.EscapeDataString(vmName + "-pip")}?api-version=2024-01-01", cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default)
    {
        EnsureScopeConfigured();
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/sshPublicKeys?api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<AzureListResponse<AzureSshKeyDto>>(cancellationToken: cancellationToken);
        return (list?.Value ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => new ProviderSshKeyInfo(k.Id ?? k.Name!, k.Name!, k.Properties?.PublicKey))
            .ToArray();
    }

    public async Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        EnsureScopeConfigured();

        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/sshPublicKeys/{Uri.EscapeDataString(name)}?api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Put, path, cancellationToken);
        request.Content = JsonContent.Create(new
        {
            location = _options.DefaultLocation,
            properties = new { publicKey }
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var dto = await response.Content.ReadFromJsonAsync<AzureSshKeyDto>(cancellationToken: cancellationToken)
            ?? throw new ProviderApiException("Azure returned an empty SSH public key response.", response.StatusCode, Provider);
        return new ProviderSshKeyInfo(dto.Id ?? dto.Name ?? name, dto.Name ?? name, dto.Properties?.PublicKey ?? publicKey);
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        EnsureScopeConfigured();
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/disks?api-version=2024-03-02";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var list = await response.Content.ReadFromJsonAsync<AzureListResponse<AzureDiskDto>>(cancellationToken: ct);
        var disk = list?.Value?.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
        return disk is null ? null : MapDisk(disk);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        EnsureScopeConfigured();
        var diskName = ExtractVmName(id);
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/disks/{Uri.EscapeDataString(diskName)}?api-version=2024-03-02";
        using var request = await CreateRequestAsync(HttpMethod.Get, path, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var disk = await response.Content.ReadFromJsonAsync<AzureDiskDto>(cancellationToken: ct);
        return disk is null ? null : MapDisk(disk);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        EnsureScopeConfigured();
        var tags = new Dictionary<string, string>(labels, StringComparer.Ordinal) { ["minicloud-managed"] = "true" };
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/disks/{Uri.EscapeDataString(name)}?api-version=2024-03-02";
        using var request = await CreateRequestAsync(HttpMethod.Put, path, ct);
        request.Content = JsonContent.Create(new
        {
            location = region,
            sku = new { name = "StandardSSD_LRS" },
            tags,
            properties = new { diskSizeGB = sizeGb, creationData = new { createOption = "Empty" } }
        });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var disk = await response.Content.ReadFromJsonAsync<AzureDiskDto>(cancellationToken: ct)
            ?? throw new ProviderApiException("Azure CreateDisk returned an empty response.", response.StatusCode, Provider);
        return MapDisk(disk);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        EnsureScopeConfigured();
        var vmName = ExtractVmName(serverId);
        var diskName = ExtractVmName(id);
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/virtualMachines/{Uri.EscapeDataString(vmName)}?api-version={_options.ApiVersion}";
        using var request = await CreateRequestAsync(HttpMethod.Patch, path, ct);
        request.Content = JsonContent.Create(new
        {
            properties = new
            {
                storageProfile = new
                {
                    dataDisks = new[]
                    {
                        new
                        {
                            lun = 0,
                            name = diskName,
                            createOption = "Attach",
                            managedDisk = new { id = $"/{ResourceGroupPrefix}/providers/Microsoft.Compute/disks/{diskName}" }
                        }
                    }
                }
            }
        });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        EnsureScopeConfigured();
        var diskName = ExtractVmName(id);
        var path = $"{ResourceGroupPrefix}/providers/Microsoft.Compute/disks/{Uri.EscapeDataString(diskName)}?api-version=2024-03-02";
        using var request = await CreateRequestAsync(HttpMethod.Patch, path, ct);
        request.Content = JsonContent.Create(new { properties = new { diskSizeGB = sizeGb } });
        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private string ResourceGroupPrefix =>
        $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(_options.ResourceGroup)}";

    private void EnsureScopeConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.SubscriptionId) || string.IsNullOrWhiteSpace(_options.ResourceGroup))
        {
            throw new ProviderApiException("Azure subscriptionId and resourceGroup scope must be configured.", HttpStatusCode.BadRequest, Provider);
        }
    }

    private void EnsureResourceOwnership(string? resourceId, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return;
        var expectedFragment = $"/subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroup}/";
        if (!resourceId.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderApiException(
                "Azure resource belongs to a different subscription or resource group than the configured account boundary.",
                statusCode, Provider);
        }
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relativePath, CancellationToken ct)
    {
        var token = await ResolveAccessTokenAsync(ct);
        var baseUri = new Uri((string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://management.azure.com/" : _options.BaseUrl).TrimEnd('/') + "/");
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

        if (string.IsNullOrWhiteSpace(_options.TenantId)
            || string.IsNullOrWhiteSpace(_options.ClientId)
            || string.IsNullOrWhiteSpace(_options.ClientSecret))
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

            var endpoint = _options.TokenEndpoint.Replace("{tenantId}", Uri.EscapeDataString(_options.TenantId), StringComparison.OrdinalIgnoreCase);
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                    ["scope"] = "https://management.azure.com/.default"
                })
            };

            using var tokenResponse = await _httpClient.SendAsync(tokenRequest, ct);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                var body = await tokenResponse.Content.ReadAsStringAsync(ct);
                var redacted = TokenRedactor.Redact(body, _options.ClientSecret, _options.AccessToken);
                throw new ProviderApiException($"Azure OAuth token acquisition failed ({(int)tokenResponse.StatusCode}): {redacted}", tokenResponse.StatusCode, Provider, redacted);
            }

            var tokenDto = await tokenResponse.Content.ReadFromJsonAsync<AzureTokenResponse>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(tokenDto?.AccessToken))
            {
                throw new ProviderApiException("Azure OAuth token response did not include an access_token.", tokenResponse.StatusCode, Provider);
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

    private async Task TryDeleteResourceAsync(string path, CancellationToken ct)
    {
        try
        {
            using var req = await CreateRequestAsync(HttpMethod.Delete, path, ct);
            using var res = await _httpClient.SendAsync(req, ct);
            _ = res.StatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Best-effort Azure companion resource cleanup failed for {Path}", path);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var redacted = TokenRedactor.Redact(body, _options.ClientSecret, _options.AccessToken, _cachedToken);
        throw new ProviderApiException($"Azure Management API error ({(int)response.StatusCode}): {redacted}", response.StatusCode, Provider, redacted);
    }

    private static string ExtractVmName(string idOrName)
    {
        var trimmed = idOrName.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
    }

    private static (string Publisher, string Offer, string Sku, string Version) ParseImageReference(string image)
    {
        var parts = image.Split(':');
        return parts.Length == 4
            ? (parts[0], parts[1], parts[2], parts[3])
            : ("Canonical", "ubuntu-24_04-lts", "server", "latest");
    }

    private static ProviderServerInfo MapVm(AzureVmDto vm) => new(
        vm.Id ?? vm.Name ?? string.Empty,
        vm.Name ?? string.Empty,
        vm.PublicIpv4 ?? vm.Properties?.PublicIpAddress ?? string.Empty,
        (vm.Properties?.ProvisioningState ?? "unknown").ToLowerInvariant(),
        vm.PublicIpv6,
        vm.Tags);

    private static ProviderStorageVolume MapDisk(AzureDiskDto disk) => new(
        disk.Id ?? disk.Name ?? string.Empty,
        disk.Name ?? string.Empty,
        disk.Properties?.DiskSizeGb ?? 0,
        disk.Location ?? string.Empty,
        disk.ManagedBy,
        disk.Tags ?? new Dictionary<string, string>());

    private sealed class AzureTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class AzureListResponse<T>
    {
        [JsonPropertyName("value")] public List<T>? Value { get; set; }
    }

    private sealed class AzureResourceGroupDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
    }

    private sealed class AzureLocationDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("zones")] public List<string>? Zones { get; set; }
    }

    private sealed class AzureVmSizeDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("numberOfCores")] public int NumberOfCores { get; set; }
        [JsonPropertyName("memoryInMB")] public int MemoryInMb { get; set; }
        [JsonPropertyName("osDiskSizeInMB")] public int OsDiskSizeInMb { get; set; }
        [JsonPropertyName("architectures")] public List<string>? Architectures { get; set; }
    }

    private sealed class AzureUsageDto
    {
        [JsonPropertyName("currentValue")] public int CurrentValue { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("name")] public AzureUsageNameDto? Name { get; set; }
    }

    private sealed class AzureUsageNameDto
    {
        [JsonPropertyName("value")] public string? Value { get; set; }
    }

    private sealed class AzurePublicIpDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("properties")] public AzurePublicIpProperties? Properties { get; set; }
    }

    private sealed class AzurePublicIpProperties
    {
        [JsonPropertyName("ipAddress")] public string? IpAddress { get; set; }
    }

    private sealed class AzureNicDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class AzureVmDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("publicIpv4")] public string? PublicIpv4 { get; set; }
        [JsonPropertyName("publicIpv6")] public string? PublicIpv6 { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
        [JsonPropertyName("properties")] public AzureVmProperties? Properties { get; set; }
    }

    private sealed class AzureVmProperties
    {
        [JsonPropertyName("provisioningState")] public string? ProvisioningState { get; set; }
        [JsonPropertyName("publicIpAddress")] public string? PublicIpAddress { get; set; }
        [JsonPropertyName("instanceView")] public AzureInstanceView? InstanceView { get; set; }
    }

    private sealed class AzureInstanceView
    {
        [JsonPropertyName("statuses")] public List<AzureStatusEntry>? Statuses { get; set; }
    }

    private sealed class AzureStatusEntry
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
    }

    private sealed class AzureSshKeyDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("properties")] public AzureSshKeyProperties? Properties { get; set; }
    }

    private sealed class AzureSshKeyProperties
    {
        [JsonPropertyName("publicKey")] public string? PublicKey { get; set; }
    }

    private sealed class AzureDiskDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("managedBy")] public string? ManagedBy { get; set; }
        [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
        [JsonPropertyName("properties")] public AzureDiskProperties? Properties { get; set; }
    }

    private sealed class AzureDiskProperties
    {
        [JsonPropertyName("diskSizeGB")] public int DiskSizeGb { get; set; }
    }
}
