using System.Net;
using System.Security.Cryptography;
using System.Text;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Azure;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests.Providers;

public sealed class AzureClientTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string Subscription = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ResourceGroup = "minicloud-test";
    private const string ServerName = "mc-example-api";
    private static readonly string VmName = AzureResourceName(ServerName);
    private static readonly string VmId = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{VmName}";
    private static readonly string NicId = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Network/networkInterfaces/{VmName}-nic";
    private static readonly string PipId = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Network/publicIPAddresses/{VmName}-pip";
    private static readonly string VnetId = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Network/virtualNetworks/{VmName}-vnet";
    private static readonly string NsgId = $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.Network/networkSecurityGroups/{VmName}-nsg";
    private const string Secret = "synthetic-azure-secret-value";

    [Fact]
    public async Task ValidateAndFindOnlyPerformReadRequestsWithinConfiguredScope()
    {
        var handler = new TestHttpMessageHandler();
        QueueTokenAndScope(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Create(handler);

        var result = await client.FindServerAsync(ServerName);

        Assert.Null(result);
        Assert.Equal(5, handler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.RecordedRequests[0].Method);
        Assert.Contains("/11111111-2222-3333-4444-555555555555/oauth2/v2.0/token", handler.RecordedRequests[0].Uri);
        Assert.Contains("grant_type=client_credentials", handler.RecordedRequests[0].Body);
        Assert.Equal("Bearer synthetic-access-token", handler.RecordedRequests[1].AuthorizationHeader);
        Assert.Contains($"/subscriptions/{Subscription}?api-version=", handler.RecordedRequests[1].Uri);
        Assert.Contains($"/resourceGroups/{ResourceGroup}?", handler.RecordedRequests[2].Uri);
        Assert.Contains("/virtualMachines?api-version=", handler.RecordedRequests[3].Uri);
        Assert.Equal(HttpMethod.Get, handler.RecordedRequests[4].Method);
        Assert.All(handler.RecordedRequests.Skip(1), request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task CreateAndDeleteOwnVmAndAllDedicatedNetworkResources()
    {
        var handler = new TestHttpMessageHandler();
        QueueTokenAndScope(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("{}");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("{}");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("{}");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("{}");
        handler.QueueJsonResponse("{}");
        handler.QueueJsonResponse(VmJson());
        handler.QueueJsonResponse(NicJson());
        handler.QueueJsonResponse(PipJson());

        var client = Create(handler);
        var server = await client.CreateServerAsync(new CreateServerRequest(
            ServerName,
            "Standard_B2s",
            "eastus",
            [],
            "#cloud-config\nruncmd: []",
            new Dictionary<string, string> { ["app"] = "example-api" },
            Image: "Canonical:0001-com-ubuntu-server-jammy:22_04-lts:latest"));

        Assert.Equal(VmId, server.Id);
        Assert.Equal(ServerName, server.Name);
        Assert.Equal("203.0.113.11", server.Ipv4Address);
        var writes = handler.RecordedRequests.Where(x => x.Method == HttpMethod.Put).ToArray();
        Assert.Equal(5, writes.Length);
        Assert.Contains(writes, x => x.Uri.Contains("Microsoft.Compute/virtualMachines/", StringComparison.Ordinal));
        var vmWrite = Assert.Single(writes.Where(x => x.Uri.Contains("Microsoft.Compute/virtualMachines/", StringComparison.Ordinal)));
        Assert.Contains("\"deleteOption\":\"Delete\"", vmWrite.Body);
        Assert.Contains("\"vmSize\":\"Standard_B2s\"", vmWrite.Body);
        Assert.Contains("\"customData\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("#cloud-config\nruncmd: []")) + "\"", vmWrite.Body);
        Assert.DoesNotContain(Secret, string.Join('\n', handler.RecordedRequests.Select(x => x.AuthorizationHeader)));

        handler.QueueJsonResponse(VmJson());
        handler.QueueJsonResponse(NicJson());
        handler.QueueJsonResponse(NicJson());
        handler.QueueJsonResponse(PipJson());
        handler.QueueJsonResponse(OwnedResourceJson(VnetId));
        handler.QueueJsonResponse(OwnedResourceJson(NsgId));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        await client.DeleteServerAsync(server.Id);

        var deletes = handler.RecordedRequests.Where(x => x.Method == HttpMethod.Delete).Select(x => x.Uri).ToArray();
        Assert.Equal(5, deletes.Length);
        Assert.Contains(deletes, x => x.Contains("Microsoft.Compute/virtualMachines/", StringComparison.Ordinal));
        Assert.Contains(deletes, x => x.Contains("Microsoft.Network/networkInterfaces/", StringComparison.Ordinal));
        Assert.Contains(deletes, x => x.Contains("Microsoft.Network/publicIPAddresses/", StringComparison.Ordinal));
        Assert.Contains(deletes, x => x.Contains("Microsoft.Network/virtualNetworks/", StringComparison.Ordinal));
        Assert.Contains(deletes, x => x.Contains("Microsoft.Network/networkSecurityGroups/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task QuotaOrPermissionFailureIsReportedWithoutLeakingClientSecret()
    {
        var handler = new TestHttpMessageHandler();
        QueueTokenAndScope(handler);
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("""{"error":{"code":"AuthorizationFailed","message":"synthetic denied"}}""", HttpStatusCode.Forbidden);
        var client = Create(handler);

        var error = await Assert.ThrowsAsync<Minicloud.Common.Providers.Common.ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest(ServerName, "Standard_B2s", "eastus", [], "")));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("AuthorizationFailed", error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Single(handler.RecordedRequests.Where(x => x.Method == HttpMethod.Put));
    }

    [Fact]
    public async Task RejectsVmIdFromAnotherSubscriptionBeforeSendingRequests()
    {
        var client = Create(new TestHttpMessageHandler());

        await Assert.ThrowsAsync<Minicloud.Common.Providers.Common.ProviderApiException>(() =>
            client.DeleteServerAsync("/subscriptions/99999999-2222-3333-4444-555555555555/resourceGroups/minicloud-test/providers/Microsoft.Compute/virtualMachines/some-vm"));
    }

    [Fact]
    public async Task DeleteRefusesAnUnownedNetworkDependency()
    {
        var handler = new TestHttpMessageHandler();
        QueueTokenAndScope(handler);
        handler.QueueJsonResponse(VmJson());
        handler.QueueJsonResponse(NicJson());
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueJsonResponse(NicJson());
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
        handler.QueueJsonResponse("""{"tags":{"managed-by":"another-owner","minicloud-server":"other-vm"}}""");
        var client = Create(handler);

        var error = await Assert.ThrowsAsync<Minicloud.Common.Providers.Common.ProviderApiException>(() =>
            client.DeleteServerAsync(VmId));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        var deletes = handler.RecordedRequests.Where(x => x.Method == HttpMethod.Delete).Select(x => x.Uri).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.DoesNotContain(deletes, x => x.Contains("publicIPAddresses/", StringComparison.Ordinal));
    }

    private static AzureClient Create(TestHttpMessageHandler handler)
    {
        var options = new AzureOptions
        {
            TenantId = Tenant,
            SubscriptionId = Subscription,
            ResourceGroupName = ResourceGroup,
            ManagementBaseUrl = "https://management.azure.test/",
            AuthorityBaseUrl = "https://management.azure.test/"
        };
        var http = new HttpClient(handler);
        return new AzureClient(http, options, $$"""{"clientId":"synthetic-client-id","clientSecret":"{{Secret}}"}""");
    }

    private static void QueueTokenAndScope(TestHttpMessageHandler handler)
    {
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"synthetic-access-token","expires_in":3600}""", Encoding.UTF8, "application/json")
        });
        handler.QueueJsonResponse($$"""{"tenantId":"{{Tenant}}"}""");
        handler.QueueJsonResponse("""{"name":"minicloud-test"}""");
        handler.QueueJsonResponse("""{"value":[]}""");
    }

    private static string VmJson() => $$"""
        {
          "id": "{{VmId}}",
          "name": "{{VmName}}",
          "tags": { "managed-by": "minicloud", "minicloud-server": "{{VmName}}" },
          "properties": {
            "provisioningState": "Succeeded",
            "networkProfile": { "networkInterfaces": [{ "id": "{{NicId}}" }] }
          }
        }
        """;

    private static string NicJson() => $$"""
        {
          "id": "{{NicId}}",
          "tags": { "managed-by": "minicloud", "minicloud-server": "{{VmName}}" },
          "properties": {
            "networkSecurityGroup": { "id": "{{NsgId}}" },
            "ipConfigurations": [{
              "properties": {
                "publicIPAddress": { "id": "{{PipId}}" },
                "subnet": { "id": "{{VnetId}}/subnets/subnet" }
              }
            }]
          }
        }
        """;

    private static string PipJson() => $$"""
        {
          "id": "{{PipId}}",
          "tags": { "managed-by": "minicloud", "minicloud-server": "{{VmName}}" },
          "properties": { "ipAddress": "203.0.113.11" }
        }
        """;

    private static string OwnedResourceJson(string id) => $$"""
        {
          "id": "{{id}}",
          "tags": { "managed-by": "minicloud", "minicloud-server": "{{VmName}}" }
        }
        """;

    private static string AzureResourceName(string name) =>
        $"{new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-')}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant()}";
}
