using System.Globalization;
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
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Azure;

public sealed class AzureClient : IVpsProviderClient
{
    private const string ComputeApiVersion = "2024-03-01";
    private const string NetworkApiVersion = "2024-05-01";
    private const string ResourceApiVersion = "2022-12-01";
    private const string DefaultImage = "Canonical:0001-com-ubuntu-server-jammy:22_04-lts:latest";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LongOperationTimeout = TimeSpan.FromMinutes(30);
    private readonly HttpClient _http;
    private readonly AzureOptions _options;
    private readonly ServicePrincipalCredential _credential;
    private readonly ILogger<AzureClient>? _logger;
    private readonly Uri _managementBase;
    private readonly Uri _authorityBase;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _scopeLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;

    public CloudProvider Provider => CloudProvider.Azure;

    [ActivatorUtilitiesConstructor]
    public AzureClient(HttpClient http, IOptions<AzureOptions> options, ILogger<AzureClient>? logger = null)
        : this(http, options.Value,
            JsonSerializer.Serialize(new ServicePrincipalCredential
            {
                ClientId = options.Value.ClientId,
                ClientSecret = options.Value.ClientSecret
            }, JsonOptions), logger)
    {
    }

    public AzureClient(HttpClient http, AzureOptions options, string credential, ILogger<AzureClient>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _managementBase = new Uri(options.ManagementBaseUrl.TrimEnd('/') + "/");
        _authorityBase = new Uri(options.AuthorityBaseUrl.TrimEnd('/') + "/");
        if (_managementBase.Scheme != Uri.UriSchemeHttps || _authorityBase.Scheme != Uri.UriSchemeHttps)
            throw new ProviderApiException("Azure Resource Manager and Entra ID endpoints must use HTTPS.", HttpStatusCode.BadRequest, Provider);
        try
        {
            _credential = JsonSerializer.Deserialize<ServicePrincipalCredential>(credential, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new ProviderApiException("Azure credentials must be a JSON service-principal clientId/clientSecret object.", HttpStatusCode.BadRequest, Provider, innerException: ex);
        }
        if (string.IsNullOrWhiteSpace(_credential.ClientId) || string.IsNullOrWhiteSpace(_credential.ClientSecret)
            || _credential.ClientId.Any(char.IsControl) || _credential.ClientSecret.Any(char.IsControl))
            throw new ProviderApiException("Azure credentials require a client ID and client secret without control characters.", HttpStatusCode.BadRequest, Provider);
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var resourceName = ResourceName(serverName);
        var vm = await GetVmAsync(resourceName, cancellationToken);
        if (vm is null) return null;
        EnsureOwned(vm, resourceName);
        return await MapServerAsync(vm, serverName, resourceName, cancellationToken);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Region);
        if (Encoding.UTF8.GetByteCount(request.UserDataCloudInit ?? string.Empty) > 65535)
            throw new ArgumentException("Azure cloud-init custom data must not exceed 64 KiB.", nameof(request));
        if (request.SshKeyReferences.Count > 0)
            throw new ProviderApiException("Azure does not support provider-level SSH key references. Use the configured MiniCloud bootstrap key.", HttpStatusCode.BadRequest, Provider);
        var image = ParseImage(request.Image);

        var vmName = ResourceName(request.Name);
        var existing = await GetVmAsync(vmName, cancellationToken);
        if (existing is not null)
        {
            EnsureOwned(existing, vmName);
            return await MapServerAsync(existing, request.Name, vmName, cancellationToken);
        }

        var names = ResourceNames(vmName);
        var tags = BuildTags(request, vmName);
        var nsgId = ResourceId("Microsoft.Network", "networkSecurityGroups", names.Nsg);
        var vnetId = ResourceId("Microsoft.Network", "virtualNetworks", names.Vnet);
        var subnetId = $"{vnetId}/subnets/{names.Subnet}";
        var pipId = ResourceId("Microsoft.Network", "publicIPAddresses", names.PublicIp);
        var nicId = ResourceId("Microsoft.Network", "networkInterfaces", names.Nic);
        var resourceTag = ResourceTags(vmName, tags);

        try
        {
        await EnsureResourceAsync(ResourcePath("Microsoft.Network", "networkSecurityGroups", names.Nsg, NetworkApiVersion),
            resourceTag, new
            {
                location = request.Region,
                tags = resourceTag,
                properties = new
                {
                    securityRules = new[]
                    {
                        SecurityRule("minicloud-ssh", 100, 22),
                        SecurityRule("minicloud-web-http", 110, 80),
                        SecurityRule("minicloud-web-https", 120, 443)
                    }
                }
            }, cancellationToken);

        await EnsureResourceAsync(ResourcePath("Microsoft.Network", "virtualNetworks", names.Vnet, NetworkApiVersion),
            resourceTag, new
            {
                location = request.Region,
                tags = resourceTag,
                properties = new
                {
                    addressSpace = new { addressPrefixes = new[] { "10.218.0.0/16" } },
                    subnets = new[]
                    {
                        new
                        {
                            name = names.Subnet,
                            properties = new
                            {
                                addressPrefix = "10.218.1.0/24",
                                networkSecurityGroup = new { id = nsgId }
                            }
                        }
                    }
                }
            }, cancellationToken);

        await EnsureResourceAsync(ResourcePath("Microsoft.Network", "publicIPAddresses", names.PublicIp, NetworkApiVersion),
            resourceTag, new
            {
                location = request.Region,
                sku = new { name = "Standard" },
                tags = resourceTag,
                properties = new { publicIPAllocationMethod = "Static", publicIPAddressVersion = "IPv4" }
            }, cancellationToken);

        await EnsureResourceAsync(ResourcePath("Microsoft.Network", "networkInterfaces", names.Nic, NetworkApiVersion),
            resourceTag, new
            {
                location = request.Region,
                tags = resourceTag,
                properties = new
                {
                    networkSecurityGroup = new { id = nsgId },
                    ipConfigurations = new[]
                    {
                        new
                        {
                            name = "primary",
                            properties = new
                            {
                                primary = true,
                                privateIPAllocationMethod = "Dynamic",
                                subnet = new { id = subnetId },
                                publicIPAddress = new { id = pipId },
                                deleteOption = "Delete"
                            }
                        }
                    }
                }
            }, cancellationToken);
        }
        catch (Exception allocationError)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await CleanupNetworkResourcesAsync(vmName, cleanup.Token);
            }
            catch (Exception cleanupError)
            {
                throw new ProviderApiException("Azure network allocation failed and partial-resource cleanup did not complete; retry provisioning or delete the owned resources.", null, Provider,
                    innerException: new AggregateException(allocationError, cleanupError));
            }
            throw;
        }

        var vmBody = new
        {
            location = request.Region,
            tags = resourceTag,
            properties = new
            {
                hardwareProfile = new { vmSize = request.Plan },
                storageProfile = new
                {
                    imageReference = image,
                    osDisk = new { createOption = "FromImage", deleteOption = "Delete" }
                },
                osProfile = new
                {
                    computerName = vmName,
                    adminUsername = "minicloud",
                    customData = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit ?? string.Empty)),
                    linuxConfiguration = new { disablePasswordAuthentication = true }
                },
                networkProfile = new
                {
                    networkInterfaces = new[] { new { id = nicId, properties = new { primary = true, deleteOption = "Delete" } } }
                },
                diagnosticsProfile = new { bootDiagnostics = new { enabled = true } }
            }
        };
        try
        {
            await PutResourceAsync(ResourcePath("Microsoft.Compute", "virtualMachines", vmName, ComputeApiVersion),
                vmBody, cancellationToken, ifNoneMatch: true);
        }
        catch (ProviderApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var raced = await GetVmAsync(vmName, cancellationToken);
            if (raced is null) throw;
            EnsureOwned(raced, vmName);
        }
        catch (ProviderApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests)
        {
            using var check = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var allocation = await GetVmAsync(vmName, check.Token);
            if (allocation is null) await CleanupNetworkResourcesAsync(vmName, check.Token);
            else EnsureOwned(allocation, vmName);
            throw;
        }

        var created = await GetVmAsync(vmName, cancellationToken);
        if (created is null)
            throw new ProviderApiException("Azure completed VM creation but the VM could not be read back.", HttpStatusCode.NotFound, Provider);
        EnsureOwned(created, vmName);
        _logger?.LogInformation("Created Azure VM {VmName} in subscription {SubscriptionId}", vmName, _options.SubscriptionId);
        return await MapServerAsync(created, request.Name, vmName, cancellationToken);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var identity = ParseVmId(serverId);
        var vm = await GetVmAsync(identity.Name, cancellationToken);
        if (vm is null)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "404");
        EnsureOwned(vm, identity.Name);

        var vmPath = ResourcePath("Microsoft.Compute", "virtualMachines", identity.Name, ComputeApiVersion);
        var queryAt = vmPath.IndexOf('?');
        var instanceViewPath = vmPath[..queryAt] + "/instanceView" + vmPath[queryAt..];
        using var view = await SendArmAsync(HttpMethod.Get, instanceViewPath, null, cancellationToken, allowNotFound: true);
        var statusCodes = view is null ? [] : GetStatusCodes(view.RootElement);
        var power = statusCodes.FirstOrDefault(x => x.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase));
        var provisioning = statusCodes.FirstOrDefault(x => x.StartsWith("ProvisioningState/", StringComparison.OrdinalIgnoreCase));
        var ip = await GetPublicIpAsync(vm, identity.Name, cancellationToken);
        var running = string.Equals(power, "PowerState/running", StringComparison.OrdinalIgnoreCase)
            && string.Equals(provisioning, "ProvisioningState/succeeded", StringComparison.OrdinalIgnoreCase);
        return new ProviderServerStatus(serverId, power?.Split('/').LastOrDefault() ?? "unknown",
            ip, null, running, $"{provisioning ?? "unknown"}/{power ?? "unknown"}");
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var identity = ParseVmId(serverId);
        var vm = await GetVmAsync(identity.Name, cancellationToken);
        if (vm is not null) EnsureOwned(vm, identity.Name);
        var names = ResourceNames(identity.Name);
        var nicIds = vm is null ? new List<string>() : GetNetworkInterfaceIds(vm);
        if (nicIds.Count == 0)
        {
            var nic = await GetResourceAsync(ResourcePath("Microsoft.Network", "networkInterfaces", names.Nic, NetworkApiVersion), cancellationToken);
            if (nic is not null && HasOwnerTag(nic.RootElement, identity.Name)) nicIds.Add(ReadString(nic.RootElement, "id") ?? ResourceId("Microsoft.Network", "networkInterfaces", names.Nic));
        }

        var pipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vnetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nsgIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nicId in nicIds)
        {
            var nic = await GetResourceByIdAsync(nicId, NetworkApiVersion, cancellationToken);
            if (nic is null) continue;
            EnsureOwned(nic.RootElement, identity.Name);
            CollectNetworkResourceIds(nic.RootElement, pipIds, vnetIds, nsgIds);
        }

        if (vm is not null)
            await DeleteResourceAsync(ResourcePath("Microsoft.Compute", "virtualMachines", identity.Name, ComputeApiVersion), cancellationToken);
        foreach (var nicId in nicIds)
            await DeleteOwnedResourceByIdAsync(nicId, NetworkApiVersion, identity.Name, cancellationToken);
        foreach (var id in pipIds)
            await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, identity.Name, cancellationToken);
        foreach (var id in vnetIds)
            await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, identity.Name, cancellationToken);
        foreach (var id in nsgIds)
            await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, identity.Name, cancellationToken);

        // Clean up dependencies left by a partial create if discovery never saw the VM.
        if (vm is null)
        {
            await DeleteOwnedNamedResourceAsync("Microsoft.Network", "networkInterfaces", names.Nic, identity.Name, cancellationToken);
            await DeleteOwnedNamedResourceAsync("Microsoft.Network", "publicIPAddresses", names.PublicIp, identity.Name, cancellationToken);
            await DeleteOwnedNamedResourceAsync("Microsoft.Network", "virtualNetworks", names.Vnet, identity.Name, cancellationToken);
            await DeleteOwnedNamedResourceAsync("Microsoft.Network", "networkSecurityGroups", names.Nsg, identity.Name, cancellationToken);
        }
    }

    private async Task EnsureResourceAsync(string path, IReadOnlyDictionary<string, string> expectedTags, object body, CancellationToken ct)
    {
        using var existing = await SendArmAsync(HttpMethod.Get, path, null, ct, allowNotFound: true);
        if (existing is not null)
        {
            if (!HasOwnerTag(existing.RootElement, expectedTags["minicloud-server"]))
                throw new ProviderApiException("An Azure network resource with the expected name is not owned by MiniCloud.", HttpStatusCode.Conflict, Provider);
            return;
        }
        try { await PutResourceAsync(path, body, ct, ifNoneMatch: true); }
        catch (ProviderApiException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            using var raced = await SendArmAsync(HttpMethod.Get, path, null, ct, allowNotFound: true);
            if (raced is null || !HasOwnerTag(raced.RootElement, expectedTags["minicloud-server"])) throw;
        }
    }

    private async Task PutResourceAsync(string path, object body, CancellationToken ct, bool ifNoneMatch = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_managementBase, path));
        request.Content = JsonContent.Create(body, options: JsonOptions);
        if (ifNoneMatch) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("*"));
        using var response = await SendArmAsync(request, ct);
    }

    private async Task DeleteResourceAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_managementBase, path));
        using var response = await SendArmAsync(request, ct, allowNotFound: true);
    }

    private async Task DeleteResourceByIdAsync(string id, string apiVersion, CancellationToken ct)
    {
        var uri = ValidateResourceUri(id);
        var builder = new UriBuilder(uri);
        builder.Query = $"api-version={Uri.EscapeDataString(apiVersion)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, builder.Uri);
        using var response = await SendArmAsync(request, ct, allowNotFound: true);
    }

    private async Task DeleteOwnedResourceByIdAsync(string id, string apiVersion, string serverName, CancellationToken ct)
    {
        using var resource = await GetResourceByIdAsync(id, apiVersion, ct);
        if (resource is null) return;
        if (!HasOwnerTag(resource.RootElement, serverName))
            throw new ProviderApiException("An Azure network dependency is not owned by MiniCloud; refusing to delete it.", HttpStatusCode.Conflict, Provider);
        await DeleteResourceByIdAsync(id, apiVersion, ct);
    }

    private async Task DeleteOwnedNamedResourceAsync(string provider, string type, string name, string serverName, CancellationToken ct)
    {
        var path = ResourcePath(provider, type, name, NetworkApiVersion);
        using var resource = await SendArmAsync(HttpMethod.Get, path, null, ct, allowNotFound: true);
        if (resource is null) return;
        if (!HasOwnerTag(resource.RootElement, serverName)) return;
        await DeleteResourceAsync(path, ct);
    }

    private async Task CleanupNetworkResourcesAsync(string vmName, CancellationToken ct)
    {
        var names = ResourceNames(vmName);
        var nicPath = ResourcePath("Microsoft.Network", "networkInterfaces", names.Nic, NetworkApiVersion);
        using var nic = await GetResourceAsync(nicPath, ct);
        if (nic is not null && HasOwnerTag(nic.RootElement, vmName))
        {
            var publicIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vnets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nsgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNetworkResourceIds(nic, publicIps, vnets, nsgs);
            var nicId = ReadString(nic.RootElement, "id") ?? ResourceId("Microsoft.Network", "networkInterfaces", names.Nic);
            await DeleteOwnedResourceByIdAsync(nicId, NetworkApiVersion, vmName, ct);
            foreach (var id in publicIps) await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, vmName, ct);
            foreach (var id in vnets) await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, vmName, ct);
            foreach (var id in nsgs) await DeleteOwnedResourceByIdAsync(id, NetworkApiVersion, vmName, ct);
            return;
        }
        await DeleteOwnedNamedResourceAsync("Microsoft.Network", "networkInterfaces", names.Nic, vmName, ct);
        await DeleteOwnedNamedResourceAsync("Microsoft.Network", "publicIPAddresses", names.PublicIp, vmName, ct);
        await DeleteOwnedNamedResourceAsync("Microsoft.Network", "virtualNetworks", names.Vnet, vmName, ct);
        await DeleteOwnedNamedResourceAsync("Microsoft.Network", "networkSecurityGroups", names.Nsg, vmName, ct);
    }

    private async Task<JsonDocument?> GetVmAsync(string name, CancellationToken ct) =>
        await SendArmAsync(HttpMethod.Get, ResourcePath("Microsoft.Compute", "virtualMachines", name, ComputeApiVersion),
            null, ct, allowNotFound: true);

    private Task<JsonDocument?> GetResourceAsync(string path, CancellationToken ct) =>
        SendArmAsync(HttpMethod.Get, path, null, ct, allowNotFound: true);

    private async Task<JsonDocument?> GetResourceByIdAsync(string id, string apiVersion, CancellationToken ct)
    {
        var uri = ValidateResourceUri(id);
        var builder = new UriBuilder(uri) { Query = $"api-version={Uri.EscapeDataString(apiVersion)}" };
        return await SendArmAsync(HttpMethod.Get, builder.Uri, null, ct, allowNotFound: true);
    }

    private async Task<ProviderServerInfo> MapServerAsync(JsonDocument vm, string displayName, string resourceName, CancellationToken ct)
    {
        var ip = await GetPublicIpAsync(vm, resourceName, ct);
        return new ProviderServerInfo(ReadString(vm.RootElement, "id") ?? VmResourceId(resourceName), displayName,
            ip ?? string.Empty, ReadString(vm.RootElement, "properties", "provisioningState") ?? "unknown",
            Labels: ReadTags(vm.RootElement));
    }

    private async Task<string?> GetPublicIpAsync(JsonDocument vm, string vmName, CancellationToken ct)
    {
        foreach (var id in GetNetworkInterfaceIds(vm))
        {
            using var nic = await GetResourceByIdAsync(id, NetworkApiVersion, ct);
            if (nic is null) continue;
            EnsureOwned(nic.RootElement, vmName);
            var config = GetArray(nic.RootElement, "properties", "ipConfigurations").FirstOrDefault();
            var pipId = config is { } item ? ReadString(item, "properties", "publicIPAddress", "id") : null;
            if (string.IsNullOrWhiteSpace(pipId)) continue;
            using var pip = await GetResourceByIdAsync(pipId, NetworkApiVersion, ct);
            if (pip is null) continue;
            EnsureOwned(pip.RootElement, vmName);
            var address = ReadString(pip.RootElement, "properties", "ipAddress");
            if (!string.IsNullOrWhiteSpace(address)) return address;
        }
        return null;
    }

    private async Task EnsureScopeAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(_options.TenantId, out _) || !Guid.TryParse(_options.SubscriptionId, out _)
            || string.IsNullOrWhiteSpace(_options.ResourceGroupName))
            throw new ProviderApiException("Azure tenant, subscription, and resource group are required.", HttpStatusCode.BadRequest, Provider);
        var subscriptionPath = $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}?api-version={ResourceApiVersion}";
        using var subscription = await SendArmWithoutScopeCheckAsync(HttpMethod.Get, subscriptionPath, ct);
        var tenant = ReadString(subscription!.RootElement, "tenantId");
        if (string.IsNullOrWhiteSpace(tenant) || !string.Equals(tenant, _options.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("Azure service principal tenant does not match the configured tenant.", HttpStatusCode.Forbidden, Provider);
        using var group = await SendArmWithoutScopeCheckAsync(HttpMethod.Get,
            $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(_options.ResourceGroupName)}?api-version=2021-04-01", ct);
        using var vms = await SendArmWithoutScopeCheckAsync(HttpMethod.Get,
            ResourcePath("Microsoft.Compute", "virtualMachines", null, ComputeApiVersion, collection: true), ct);
    }

    private async Task<JsonDocument?> SendArmWithoutScopeCheckAsync(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_managementBase, path));
        return await SendArmAsync(request, ct);
    }

    private async Task<JsonDocument?> SendArmAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        var uri = new Uri(_managementBase, path);
        return await SendArmAsync(method, uri, body, ct, allowNotFound);
    }

    private async Task<JsonDocument?> SendArmAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        await EnsureScopeAsyncOnce(ct);
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return await SendArmAsync(request, ct, allowNotFound);
    }

    private int _scopeChecked;
    private async Task EnsureScopeAsyncOnce(CancellationToken ct)
    {
        if (Volatile.Read(ref _scopeChecked) == 1) return;
        await _scopeLock.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _scopeChecked) == 1) return;
            await EnsureScopeAsync(ct);
            Volatile.Write(ref _scopeChecked, 1);
        }
        finally { _scopeLock.Release(); }
    }

    private async Task<JsonDocument?> SendArmAsync(HttpRequestMessage request, CancellationToken ct, bool allowNotFound = false)
    {
        var requestUri = request.RequestUri ?? throw new InvalidOperationException("Azure request URI is missing.");
        EnsureTrustedArmUri(requestUri);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        using var response = await SendWithTokenAsync(request.Method, requestUri, body, ct);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            await ThrowApiErrorAsync(response, ct);
        await WaitForLongOperationAsync(response, ct);
        if (response.Content.Headers.ContentLength == 0) return null;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonDocument.Parse(text);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(HttpMethod method, Uri uri, string? content, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAccessTokenAsync(ct, forceRefresh: attempt > 0);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (content is not null) request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt != 0) return response;
            response.Dispose();
        }
        throw new InvalidOperationException("Azure authentication retry was exhausted.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(_credential.ClientId) || string.IsNullOrWhiteSpace(_credential.ClientSecret))
            throw new ProviderApiException("Azure service-principal credentials are not configured.", HttpStatusCode.Unauthorized, Provider);
        if (!forceRefresh && _accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return _accessToken;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return _accessToken;
            var tokenUri = new Uri(_authorityBase, $"{Uri.EscapeDataString(_options.TenantId)}/oauth2/v2.0/token");
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _credential.ClientId,
                    ["client_secret"] = _credential.ClientSecret,
                    ["scope"] = "https://management.azure.com/.default",
                    ["grant_type"] = "client_credentials"
                })
            };
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                await ThrowApiErrorAsync(response, ct, "Azure Entra ID token request failed");
            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
                ?? throw new ProviderApiException("Azure Entra ID returned an empty token response.", response.StatusCode, Provider);
            if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || tokenResponse.ExpiresIn <= 0)
                throw new ProviderApiException("Azure Entra ID returned an invalid token response.", response.StatusCode, Provider);
            _accessToken = tokenResponse.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            return _accessToken;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task WaitForLongOperationAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var operation = response.Headers.TryGetValues("Azure-AsyncOperation", out var asyncValues)
            ? asyncValues.FirstOrDefault()
            : response.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(operation)) return;
        var uri = Uri.TryCreate(operation, UriKind.Absolute, out var absolute) ? absolute : new Uri(_managementBase, operation);
        EnsureTrustedArmUri(uri);
        var deadline = DateTimeOffset.UtcNow + LongOperationTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var delay = RetryDelay(response.Headers.RetryAfter);
            await Task.Delay(delay, ct);
            using var poll = await SendWithTokenAsync(HttpMethod.Get, uri, null, ct);
            if (!poll.IsSuccessStatusCode) await ThrowApiErrorAsync(poll, ct);
            var body = await poll.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) continue;
            using var status = JsonDocument.Parse(body);
            var value = ReadString(status.RootElement, "status");
            if (string.Equals(value, "Succeeded", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(value, "Failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "Canceled", StringComparison.OrdinalIgnoreCase))
                throw new ProviderApiException("Azure resource operation failed: " + ReadString(status.RootElement, "error", "message"),
                    HttpStatusCode.BadRequest, Provider, TokenRedactor.Redact(body, _credential.ClientSecret, _accessToken));
            if (poll.Headers.RetryAfter is not null) response.Headers.RetryAfter = poll.Headers.RetryAfter;
        }
        throw new ProviderApiException("Azure resource operation exceeded the 30-minute completion window.", HttpStatusCode.RequestTimeout, Provider);
    }

    private async Task ThrowApiErrorAsync(HttpResponseMessage response, CancellationToken ct, string? prefix = null)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var code = string.Empty;
        var message = string.Empty;
        try
        {
            using var parsed = JsonDocument.Parse(body);
            code = ReadString(parsed.RootElement, "error", "code") ?? string.Empty;
            message = ReadString(parsed.RootElement, "error", "message") ?? string.Empty;
        }
        catch (JsonException) { }
        var summary = prefix ?? "Azure Resource Manager request failed";
        if (!string.IsNullOrWhiteSpace(code)) summary += $" ({code})";
        summary += $" with HTTP {(int)response.StatusCode}.";
        if (!string.IsNullOrWhiteSpace(message)) summary += " " + message;
        summary = TokenRedactor.Redact(summary, _credential.ClientSecret, _accessToken);
        var safeBody = TokenRedactor.Redact(body, _credential.ClientSecret, _accessToken);
        _logger?.LogWarning("Azure API request failed: {Message}", summary);
        throw new ProviderApiException(summary, response.StatusCode, Provider, safeBody);
    }

    private string ResourcePath(string provider, string type, string? name, string apiVersion, bool collection = false)
    {
        var path = $"subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/resourceGroups/{Uri.EscapeDataString(_options.ResourceGroupName)}/providers/{provider}/{type}";
        if (!collection && name is not null) path += "/" + Uri.EscapeDataString(name);
        return path + "?api-version=" + Uri.EscapeDataString(apiVersion);
    }

    private string ResourceId(string provider, string type, string name) =>
        $"/subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroupName}/providers/{provider}/{type}/{name}";

    private string VmResourceId(string name) => ResourceId("Microsoft.Compute", "virtualMachines", name);

    private Uri ValidateResourceUri(string id)
    {
        var uri = new Uri(_managementBase, id.TrimStart('/'));
        EnsureTrustedArmUri(uri);
        var expectedPrefix = $"/subscriptions/{_options.SubscriptionId}/resourceGroups/{_options.ResourceGroupName}/providers/";
        if (!uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("Azure resource identity is outside the configured subscription and resource group.", HttpStatusCode.Forbidden, Provider);
        return uri;
    }

    private void EnsureTrustedArmUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(_managementBase.Scheme == Uri.UriSchemeHttp && uri.Scheme == Uri.UriSchemeHttp))
            throw new ProviderApiException("Azure returned a non-HTTPS operation URL.", HttpStatusCode.BadGateway, Provider);
        if (uri.Port != _managementBase.Port
            || !string.Equals(uri.Host, _managementBase.Host, StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("Azure returned an operation URL outside the configured Resource Manager endpoint.", HttpStatusCode.BadGateway, Provider);
    }

    private static IReadOnlyDictionary<string, string> BuildTags(CreateServerRequest request, string serverName)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["managed-by"] = "minicloud",
            ["minicloud-server"] = serverName
        };
        if (request.Labels is not null)
            foreach (var pair in request.Labels)
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    tags[pair.Key[..Math.Min(pair.Key.Length, 512)]] = pair.Value[..Math.Min(pair.Value.Length, 256)];
        return tags;
    }

    private static IReadOnlyDictionary<string, string> ResourceTags(string serverName, IReadOnlyDictionary<string, string> tags)
    {
        var result = tags.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        result["minicloud-server"] = serverName;
        result["managed-by"] = "minicloud";
        return result;
    }

    private static bool HasOwnerTag(JsonElement resource, string serverName) =>
        string.Equals(ReadString(resource, "tags", "managed-by"), "minicloud", StringComparison.Ordinal)
        && string.Equals(ReadString(resource, "tags", "minicloud-server"), serverName, StringComparison.Ordinal);

    private static void EnsureOwned(JsonDocument resource, string serverName)
    {
        if (!HasOwnerTag(resource.RootElement, serverName))
            throw new ProviderApiException("The Azure VM name is already in use by a resource not owned by MiniCloud.", HttpStatusCode.Conflict, CloudProvider.Azure);
    }

    private static object ParseImage(string? image)
    {
        var parts = (string.IsNullOrWhiteSpace(image) ? DefaultImage : image.Trim()).Split(':');
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(x => x.Any(char.IsControl)))
            throw new ArgumentException("Azure image must be a marketplace URN in publisher:offer:sku:version form.", nameof(image));
        return new { publisher = parts[0], offer = parts[1], sku = parts[2], version = parts[3] };
    }

    private static object SecurityRule(string name, int priority, int port) => new
    {
        name,
        properties = new
        {
            priority,
            protocol = "Tcp",
            access = "Allow",
            direction = "Inbound",
            sourceAddressPrefix = "*",
            sourcePortRange = "*",
            destinationAddressPrefix = "*",
            destinationPortRange = port.ToString(CultureInfo.InvariantCulture)
        }
    };

    private (string Nsg, string Vnet, string Subnet, string PublicIp, string Nic) ResourceNames(string vmName) =>
        ($"{vmName}-nsg", $"{vmName}-vnet", "subnet", $"{vmName}-pip", $"{vmName}-nic");

    private static string ResourceName(string value)
    {
        var safe = new string(value.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');
        if (safe.Length > 48) safe = safe[..48].TrimEnd('-');
        if (string.IsNullOrWhiteSpace(safe)) safe = "minicloud";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
        return $"{safe}-{suffix}";
    }

    private (string Name, string ResourceGroup) ParseVmId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var parts = id.Trim('/').Split('/');
        if (parts.Length != 8 || !parts[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase)
            || !parts[4].Equals("providers", StringComparison.OrdinalIgnoreCase)
            || !parts[5].Equals("Microsoft.Compute", StringComparison.OrdinalIgnoreCase)
            || !parts[6].Equals("virtualMachines", StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("Azure VM resource ID is invalid.", HttpStatusCode.BadRequest, CloudProvider.Azure);
        if (!string.Equals(parts[1], _options.SubscriptionId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[3], _options.ResourceGroupName, StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("Azure VM resource ID is outside the configured subscription and resource group.", HttpStatusCode.Forbidden, CloudProvider.Azure);
        return (parts[7], parts[3]);
    }

    private static List<string> GetNetworkInterfaceIds(JsonDocument vm) =>
        GetArray(vm.RootElement, "properties", "networkProfile", "networkInterfaces")
            .Select(x => ReadString(x, "id"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToList();

    private static void CollectNetworkResourceIds(JsonDocument nic, ISet<string> publicIps, ISet<string> vnets, ISet<string> nsgs)
    {
        var nsgId = ReadString(nic.RootElement, "properties", "networkSecurityGroup", "id");
        if (nsgId is not null) nsgs.Add(nsgId);
        foreach (var config in GetArray(nic.RootElement, "properties", "ipConfigurations"))
        {
            var pipId = ReadString(config, "properties", "publicIPAddress", "id");
            var subnetId = ReadString(config, "properties", "subnet", "id");
            if (pipId is not null) publicIps.Add(pipId);
            if (subnetId is not null)
            {
                var separator = subnetId.IndexOf("/subnets/", StringComparison.OrdinalIgnoreCase);
                if (separator > 0) vnets.Add(subnetId[..separator]);
            }
        }
    }

    private static string[] GetStatusCodes(JsonElement root) => GetArray(root, "statuses")
        .Select(x => ReadString(x, "code")).Where(x => x is not null).Cast<string>().ToArray();

    private static IEnumerable<JsonElement> GetArray(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return [];
            current = next;
        }
        return current.ValueKind == JsonValueKind.Array ? current.EnumerateArray().ToArray() : [];
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static IReadOnlyDictionary<string, string>? ReadTags(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return null;
        return tags.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(x => x.Name, x => x.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        var value = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (value is null || value.Value < TimeSpan.Zero) return TimeSpan.FromSeconds(2);
        return value.Value > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : value.Value;
    }

    private sealed class ServicePrincipalCredential
    {
        [JsonPropertyName("clientId")] public string ClientId { get; set; } = string.Empty;
        [JsonPropertyName("clientSecret")] public string ClientSecret { get; set; } = string.Empty;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
