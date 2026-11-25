using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Selfrun.Common.Providers;

namespace Selfrun.Worker.Tests.Providers;

public sealed class LoopbackProviderContractTests
{
    [Theory]
    [InlineData(CloudProvider.Hetzner)]
    [InlineData(CloudProvider.DigitalOcean)]
    [InlineData(CloudProvider.Vultr)]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.GoogleCloud)]
    [InlineData(CloudProvider.Ovhcloud)]
    [InlineData(CloudProvider.Linode)]
    [InlineData(CloudProvider.Scaleway)]
    public async Task ProductionRegistrationUsesConfiguredLocalHttpEndpoint(CloudProvider cloud)
    {
        using var portPicker = new TcpListener(IPAddress.Loopback, 0);
        portPicker.Start();
        var port = ((IPEndPoint)portPicker.LocalEndpoint).Port;
        portPicker.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var response = cloud switch
        {
            CloudProvider.Hetzner => """{"servers":[]}""",
            CloudProvider.DigitalOcean => """{"droplets":[]}""",
            CloudProvider.Vultr => """{"instances":[]}""",
            CloudProvider.Aws => """{"instances":[]}""",
            CloudProvider.Azure => """{"value":[]}""",
            CloudProvider.GoogleCloud => """{"items":{}}""",
            CloudProvider.Ovhcloud => """[]""",
            CloudProvider.Linode => """{"data":[]}""",
            CloudProvider.Scaleway => """{"servers":[]}""",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud))
        };
        var expectedPath = cloud switch
        {
            CloudProvider.Hetzner => "/v1/servers?name=integration-probe",
            CloudProvider.DigitalOcean => "/v2/droplets?name=integration-probe",
            CloudProvider.Vultr => "/v2/instances?label=integration-probe",
            CloudProvider.Aws => "/ec2/?Action=DescribeInstances&Version=2016-11-15&Filter.1.Name=tag%3AName&Filter.1.Value.1=integration-probe",
            CloudProvider.Azure => "/arm/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01",
            CloudProvider.GoogleCloud => "/gcp/projects/proj-1/aggregated/instances?filter=name%20%3D%20%22integration-probe%22",
            CloudProvider.Ovhcloud => "/ovh/cloud/project/ovh-proj/instance",
            CloudProvider.Linode => "/linode/linode/instances",
            CloudProvider.Scaleway => "/scw/instance/v1/zones/fr-par-1/servers?project=scw-proj&name=integration-probe",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud))
        };

        var received = listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hetzner:BaseUrl"] = $"http://127.0.0.1:{port}/v1/",
            ["Hetzner:ApiToken"] = "test-only",
            ["DigitalOcean:BaseUrl"] = $"http://127.0.0.1:{port}/v2/",
            ["DigitalOcean:ApiToken"] = "test-only",
            ["Vultr:BaseUrl"] = $"http://127.0.0.1:{port}/v2/",
            ["Vultr:ApiKey"] = "test-only",
            ["Aws:BaseUrl"] = $"http://127.0.0.1:{port}/ec2/",
            ["Aws:AccessKeyId"] = "AKIATEST",
            ["Aws:SecretAccessKey"] = "test-secret",
            ["Azure:BaseUrl"] = $"http://127.0.0.1:{port}/arm/",
            ["Azure:SubscriptionId"] = "sub-1",
            ["Azure:ResourceGroup"] = "rg-1",
            ["Azure:AccessToken"] = "test-only",
            ["GoogleCloud:BaseUrl"] = $"http://127.0.0.1:{port}/gcp/",
            ["GoogleCloud:ProjectId"] = "proj-1",
            ["GoogleCloud:AccessToken"] = "test-only",
            ["Ovhcloud:BaseUrl"] = $"http://127.0.0.1:{port}/ovh/",
            ["Ovhcloud:ProjectId"] = "ovh-proj",
            ["Ovhcloud:ApplicationKey"] = "app-key",
            ["Ovhcloud:ApplicationSecret"] = "app-sec",
            ["Ovhcloud:ConsumerKey"] = "cons-key",
            ["Linode:BaseUrl"] = $"http://127.0.0.1:{port}/linode/",
            ["Linode:ApiToken"] = "test-only",
            ["Scaleway:BaseUrl"] = $"http://127.0.0.1:{port}/scw/",
            ["Scaleway:ProjectId"] = "scw-proj",
            ["Scaleway:SecretKey"] = "test-only"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSelfrunProviders(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ProviderServiceCollectionExtensions.VpsProviderClientResolver>();

        var findTask = resolver(cloud).FindServerAsync("integration-probe");
        var request = await received;
        Assert.Equal("GET", request.Request.HttpMethod);
        Assert.Equal(expectedPath, request.Request.RawUrl);
        if (cloud is CloudProvider.Hetzner or CloudProvider.DigitalOcean or CloudProvider.Vultr or CloudProvider.Azure or CloudProvider.GoogleCloud or CloudProvider.Linode)
        {
            Assert.Equal("Bearer test-only", request.Request.Headers["Authorization"]);
        }
        else if (cloud == CloudProvider.Aws)
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 ", request.Request.Headers["Authorization"]);
        }
        else if (cloud == CloudProvider.Ovhcloud)
        {
            Assert.Equal("app-key", request.Request.Headers["X-Ovh-Application"]);
            Assert.StartsWith("$1$", request.Request.Headers["X-Ovh-Signature"]);
        }
        else if (cloud == CloudProvider.Scaleway)
        {
            Assert.Equal("test-only", request.Request.Headers["X-Auth-Token"]);
        }

        var bytes = Encoding.UTF8.GetBytes(response);
        request.Response.ContentType = "application/json";
        request.Response.ContentLength64 = bytes.Length;
        await request.Response.OutputStream.WriteAsync(bytes);
        request.Response.Close();
        Assert.Null(await findTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
