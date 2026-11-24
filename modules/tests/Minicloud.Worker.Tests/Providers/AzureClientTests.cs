using System.Net;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Azure;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests.Providers;

public sealed class AzureClientTests
{
    private const string SubId = "00000000-0000-0000-0000-000000000001";
    private const string RgName = "rg-minicloud";

    private static (AzureClient Client, TestHttpMessageHandler Handler) CreateClient(bool useServicePrincipal = true)
    {
        var handler = new TestHttpMessageHandler();
        var options = new AzureOptions
        {
            Enabled = true,
            BaseUrl = "https://management.azure.test/",
            TokenEndpoint = "https://login.azure.test/{tenantId}/oauth2/v2.0/token",
            SubscriptionId = SubId,
            ResourceGroup = RgName,
            TenantId = useServicePrincipal ? "tenant-1" : string.Empty,
            ClientId = useServicePrincipal ? "client-1" : string.Empty,
            ClientSecret = useServicePrincipal ? "azure-super-secret-client-value" : string.Empty,
            AccessToken = useServicePrincipal ? null : "pre-minted-managed-identity-token"
        };
        return (new AzureClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task AcquiresAndCachesOAuthTokenAndDiscoversCatalog()
    {
        var (client, handler) = CreateClient(useServicePrincipal: true);
        handler.QueueJsonResponse("""{"access_token":"cached-azure-jwt-token","expires_in":3600}""");
        handler.QueueJsonResponse("""{"name":"rg-minicloud","location":"eastus"}""");
        handler.QueueJsonResponse("""{"value":[{"name":"eastus","displayName":"East US","zones":["1","2","3"]}]}""");
        handler.QueueJsonResponse("""{"value":[{"name":"Standard_B2s","numberOfCores":2,"memoryInMB":4096,"osDiskSizeInMB":30720}]}""");
        handler.QueueJsonResponse("""{"value":[{"currentValue":2,"limit":20,"name":{"value":"cores"}}]}""");

        var scope = await client.VerifyScopeAsync();
        var locations = await client.ListLocationsAsync();
        var offers = await client.ListComputeOffersAsync("eastus");
        var images = await client.ListImagesAsync("eastus");
        var quotas = await client.GetQuotasAsync("eastus");

        Assert.Equal($"{SubId}/{RgName}", scope.ScopeId);
        Assert.Single(locations);
        Assert.Single(offers);
        Assert.Equal(2, images.Count);
        Assert.Single(quotas);
        // 1 token request + 4 ARM GET requests (token was cached across all 4!)
        Assert.Equal(5, handler.RecordedRequests.Count);
        Assert.Equal("Bearer cached-azure-jwt-token", handler.RecordedRequests[1].AuthorizationHeader);
        Assert.Equal("Bearer cached-azure-jwt-token", handler.RecordedRequests[4].AuthorizationHeader);
    }

    [Fact]
    public async Task NormalLifecycleCreateStatusDeleteAndPartialAllocationCleanup()
    {
        var (client, handler) = CreateClient(useServicePrincipal: false);
        // Create: PIP + NIC + VM
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Network/publicIPAddresses/mc-az-pip","properties":{"ipAddress":"203.0.113.55"}}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Network/networkInterfaces/mc-az-nic"}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Compute/virtualMachines/mc-az","name":"mc-az","properties":{"provisioningState":"Succeeded"}}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        // Status
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Compute/virtualMachines/mc-az","name":"mc-az","publicIpv4":"203.0.113.55","properties":{"provisioningState":"Succeeded","instanceView":{"statuses":[{"code":"PowerState/running"}]}}}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        // Delete: VM + NIC + PIP
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-az", "Standard_B2s", "eastus", ["ssh-ed25519 AAAAC3"], "#cloud-config\n"));
        Assert.Equal("203.0.113.55", created.Ipv4Address);

        var status = await client.GetServerStatusAsync(created.Id);
        Assert.True(status.IsRunning);

        await client.DeleteServerAsync(created.Id);
        Assert.Equal(7, handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task PartialAllocationFailureCleansUpNicAndPublicIp()
    {
        var (client, handler) = CreateClient(useServicePrincipal: false);
        // PIP succeeds, NIC succeeds, VM fails with quota error -> client cleans up NIC then PIP
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Network/publicIPAddresses/mc-fail-pip","properties":{"ipAddress":"203.0.113.60"}}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        handler.QueueJsonResponse("""{"id":"/subscriptions/__SUB__/resourceGroups/__RG__/providers/Microsoft.Network/networkInterfaces/mc-fail-nic"}""".Replace("__SUB__", SubId).Replace("__RG__", RgName));
        handler.QueueJsonResponse("""{"error":{"code":"QuotaExceeded","message":"Operation results in exceeding quota limits of Core"}}""", HttpStatusCode.BadRequest);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent)); // DELETE NIC
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent)); // DELETE PIP

        await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-fail", "Standard_D8s_v5", "eastus", [], "")));

        Assert.Equal(5, handler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Delete, handler.RecordedRequests[3].Method);
        Assert.Contains("networkInterfaces/mc-fail-nic", handler.RecordedRequests[3].Uri);
        Assert.Equal(HttpMethod.Delete, handler.RecordedRequests[4].Method);
        Assert.Contains("publicIPAddresses/mc-fail-pip", handler.RecordedRequests[4].Uri);
    }

    [Fact]
    public async Task RejectsCrossResourceGroupOwnershipAndRedactsSecrets()
    {
        var (client, handler) = CreateClient(useServicePrincipal: true);
        handler.QueueJsonResponse("""{"access_token":"secret-azure-token-999","expires_in":3600}""");
        handler.QueueJsonResponse("""{"value":[{"id":"/subscriptions/other-sub/resourceGroups/other-rg/providers/Microsoft.Compute/virtualMachines/mc-wrong","name":"mc-wrong"}]}""");

        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("mc-wrong"));
        Assert.Contains("different subscription or resource group", ex.Message, StringComparison.OrdinalIgnoreCase);

        handler.QueueJsonResponse("""{"error":"azure-super-secret-client-value secret-azure-token-999"}""", HttpStatusCode.Forbidden);
        var authEx = await Assert.ThrowsAsync<ProviderApiException>(() => client.ListSshKeysAsync());
        Assert.DoesNotContain("azure-super-secret-client-value", authEx.Message);
        Assert.DoesNotContain("secret-azure-token-999", authEx.Message);
    }

    [Fact]
    public void CredentialParserEnforcesSubscriptionAndResourceGroupScope()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AzureParsedCredentials.Parse("""{"accessToken":"tok","subscriptionId":"sub-other","resourceGroup":"rg-1"}""", "sub-1/rg-1"));
        Assert.Throws<InvalidOperationException>(() =>
            AzureParsedCredentials.Parse("""{"accessToken":"tok","subscriptionId":"sub-1","resourceGroup":"rg-other"}""", "sub-1/rg-1"));
    }
}
