using System.Net;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Aws;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests.Providers;

public sealed class AwsClientTests
{
    private static (AwsClient Client, TestHttpMessageHandler Handler) CreateClient(string? accountId = "123456789012")
    {
        var handler = new TestHttpMessageHandler();
        var options = new AwsOptions
        {
            Enabled = true,
            BaseUrl = "https://ec2.{region}.example.test/",
            DefaultRegion = "us-east-1",
            AccessKeyId = "AKIATESTACCESSKEY",
            SecretAccessKey = "super-secret-aws-signing-key-value",
            SessionToken = "synthetic-session-token-123456",
            AccountId = accountId
        };
        return (new AwsClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task SigV4HeadersAreAttachedPerRequestAndDiscoveryReturnsCatalog()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"accountId":"123456789012","regions":[{"regionName":"us-east-1","zones":["us-east-1a","us-east-1b"]}]}""");
        handler.QueueJsonResponse("""{"accountId":"123456789012","regions":[{"regionName":"us-east-1","zones":["us-east-1a","us-east-1b"]}]}""");
        handler.QueueJsonResponse("""{"instanceTypes":[{"instanceType":"t3.small","vcpus":2,"memoryMb":2048,"diskGb":20,"architectures":["linux/amd64"]},{"instanceType":"t4g.small","vcpus":2,"memoryMb":2048,"diskGb":20,"architectures":["linux/arm64"]}]}""");
        handler.QueueJsonResponse("""{"images":[{"imageId":"ami-ubuntu-24.04-lts","name":"Ubuntu 24.04 amd64","architecture":"x86_64","osFamily":"ubuntu"},{"imageId":"ami-ubuntu-24.04-arm64","name":"Ubuntu 24.04 arm64","architecture":"arm64","osFamily":"ubuntu"}]}""");
        handler.QueueJsonResponse("""{"accountId":"123456789012","quotas":[{"resource":"vcpu","used":4,"limit":64}]}""");

        var scope = await client.VerifyScopeAsync();
        Assert.Equal("aws", scope.ProviderWireType);
        Assert.Equal("123456789012", scope.ScopeId);

        var locations = await client.ListLocationsAsync();
        var offers = await client.ListComputeOffersAsync("us-east-1");
        var images = await client.ListImagesAsync("us-east-1");
        var quotas = await client.GetQuotasAsync("us-east-1");

        Assert.Single(locations);
        Assert.Equal("us-east-1", locations[0].Code);
        Assert.Equal(2, offers.Count);
        Assert.Contains(images, i => i.Architecture == "linux/arm64");
        Assert.Single(quotas);
        Assert.All(handler.RecordedRequests, req =>
        {
            Assert.NotNull(req.AuthorizationHeader);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIATESTACCESSKEY/", req.AuthorizationHeader);
        });
    }

    [Fact]
    public async Task NormalLifecycleCreateFindStatusAndIdempotentDelete()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"instance":{"instanceId":"i-0abc123","name":"mc-app-1","state":"pending","region":"us-east-1","accountId":"123456789012","publicIpAddress":"198.51.100.10","tags":{"Name":"mc-app-1","minicloud-managed":"true"}}}""");
        handler.QueueJsonResponse("""{"instances":[{"instanceId":"i-0abc123","name":"mc-app-1","state":"running","region":"us-east-1","accountId":"123456789012","publicIpAddress":"198.51.100.10"}]}""");
        handler.QueueJsonResponse("""{"instances":[{"instanceId":"i-0abc123","name":"mc-app-1","state":"running","region":"us-east-1","accountId":"123456789012","publicIpAddress":"198.51.100.10"}]}""");
        handler.QueueJsonResponse("""{"Error":{"Code":"InvalidInstanceID.NotFound"}}""", HttpStatusCode.BadRequest);

        var created = await client.CreateServerAsync(new CreateServerRequest("mc-app-1", "t3.small", "us-east-1", ["my-key"], "#cloud-config\n"));
        Assert.Equal("us-east-1/i-0abc123", created.Id);
        Assert.Equal("198.51.100.10", created.Ipv4Address);

        var found = await client.FindServerAsync("mc-app-1");
        Assert.NotNull(found);
        Assert.Equal("us-east-1/i-0abc123", found!.Id);

        var status = await client.GetServerStatusAsync(created.Id);
        Assert.True(status.IsRunning);
        Assert.Equal("198.51.100.10", status.Ipv4Address);

        await client.DeleteServerAsync(created.Id);
        Assert.Equal(4, handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task RejectsDuplicateActiveInstancesAndCrossAccountBoundaryViolations()
    {
        var (client, handler) = CreateClient("123456789012");
        handler.QueueJsonResponse("""{"instances":[{"instanceId":"i-1","name":"dup-app","state":"running","accountId":"123456789012"},{"instanceId":"i-2","name":"dup-app","state":"running","accountId":"123456789012"}]}""");

        await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("dup-app"));

        handler.QueueJsonResponse("""{"instances":[{"instanceId":"i-3","name":"other-app","state":"running","accountId":"999999999999"}]}""");
        var boundaryEx = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("other-app"));
        Assert.Contains("different AWS account", boundaryEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuotaErrorIsReportedWithRedactedCredentials()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse(
            """{"error":"VcpuLimitExceeded secret=super-secret-aws-signing-key-value token=synthetic-session-token-123456"}""",
            HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.CreateServerAsync(new CreateServerRequest("mc-quota", "t3.xlarge", "us-east-1", [], "")));

        Assert.Equal(CloudProvider.Aws, ex.Provider);
        Assert.DoesNotContain("super-secret-aws-signing-key-value", ex.Message);
        Assert.DoesNotContain("synthetic-session-token-123456", ex.Message);
    }

    [Fact]
    public async Task SshKeysAndEbsVolumesWorkThroughContracts()
    {
        var (client, handler) = CreateClient();
        handler.QueueJsonResponse("""{"keyPairs":[{"keyPairId":"key-1","keyName":"ops-key","publicKey":"ssh-ed25519 AAAAC3Nza"}]}""");
        handler.QueueJsonResponse("""{"keyPairId":"key-2","keyName":"new-key","publicKey":"ssh-ed25519 AAAAC3Nzb"}""");
        handler.QueueJsonResponse("""{"volumeId":"vol-123","name":"mc-vol","size":20,"region":"us-east-1","tags":{"app-id":"app-1"}}""");
        handler.QueueJsonResponse("""{"state":"attaching"}""");
        handler.QueueJsonResponse("""{"state":"modifying"}""");

        var keys = await client.ListSshKeysAsync();
        Assert.Single(keys);
        var added = await client.RegisterSshKeyAsync("new-key", "ssh-ed25519 AAAAC3Nzb");
        Assert.Equal("key-2", added.Id);

        var vol = await client.CreateVolumeAsync("mc-vol", 20, "us-east-1", new Dictionary<string, string> { ["app-id"] = "app-1" }, default);
        Assert.Equal("vol-123", vol.Id);
        await client.AttachVolumeAsync("us-east-1/vol-123", "us-east-1/i-0abc123", default);
        await client.ResizeVolumeAsync("us-east-1/vol-123", 40, default);
    }

    [Fact]
    public void CredentialParserEnforcesAccountBoundary()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AwsParsedCredentials.Parse("""{"accessKeyId":"AKIA1","secretAccessKey":"SEC1","accountId":"999999999999"}""", "123456789012"));
        Assert.Contains("accountId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
