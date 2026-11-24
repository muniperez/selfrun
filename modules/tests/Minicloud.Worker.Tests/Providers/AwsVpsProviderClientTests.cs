using System.Reflection;
using System.Text;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Aws;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests.Providers;

public sealed class AwsVpsProviderClientTests
{
    private const string SubnetId = "subnet-0123456789abcdef0";
    private const string SecurityGroupId = "sg-0123456789abcdef0";
    private const string AmiId = "ami-0123456789abcdef0";

    [Fact]
    public async Task ValidatesAccountScopeAndConfigurationUsingReadOnlyCalls()
    {
        var mutations = 0;
        var ec2 = Fake<IAmazonEC2>((method, _) =>
        {
            if (method.Name.Contains("RunInstances", StringComparison.Ordinal)
                || method.Name.Contains("CreateVolume", StringComparison.Ordinal)
                || method.Name.Contains("TerminateInstances", StringComparison.Ordinal))
                mutations++;
            return method.Name switch
            {
                nameof(IAmazonEC2.DescribeSubnetsAsync) => new DescribeSubnetsResponse
                    { Subnets = [new Subnet { SubnetId = SubnetId, AvailabilityZone = "us-east-1a", VpcId = "vpc-abc12345" }] },
                nameof(IAmazonEC2.DescribeSecurityGroupsAsync) => new DescribeSecurityGroupsResponse
                    { SecurityGroups = [new SecurityGroup { GroupId = SecurityGroupId, VpcId = "vpc-abc12345" }] },
                nameof(IAmazonEC2.DescribeInstancesAsync) => new DescribeInstancesResponse(),
                _ => throw new InvalidOperationException($"Unexpected AWS API call {method.Name}.")
            };
        });
        var sts = Fake<IAmazonSecurityTokenService>((method, _) =>
            method.Name == nameof(IAmazonSecurityTokenService.GetCallerIdentityAsync)
                ? new GetCallerIdentityResponse { Account = "123456789012" }
                : throw new InvalidOperationException($"Unexpected AWS STS call {method.Name}."));
        var client = new AwsVpsProviderClient(ec2, sts, Settings());

        await client.ValidateAccountConfigurationAsync(default);
        Assert.Equal("123456789012", await client.GetScopeIdentityAsync(default));
        Assert.Null(await client.FindServerAsync("mc-credential-probe"));
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task RefusesToTerminateAnInstanceWithoutMinicloudOwnershipTags()
    {
        var terminations = 0;
        var ec2 = Fake<IAmazonEC2>((method, _) => method.Name switch
        {
            nameof(IAmazonEC2.DescribeInstancesAsync) => new DescribeInstancesResponse
            {
                Reservations =
                [
                    new Reservation
                    {
                        Instances =
                        [
                            new Instance
                            {
                                InstanceId = "i-0123456789abcdef0",
                                State = new InstanceState { Name = InstanceStateName.Running },
                                Tags = [new Amazon.EC2.Model.Tag { Key = "Name", Value = "unmanaged-instance" }]
                            }
                        ]
                    }
                ]
            },
            nameof(IAmazonEC2.TerminateInstancesAsync) => CountTermination(ref terminations),
            _ => throw new InvalidOperationException($"Unexpected AWS API call {method.Name}.")
        });
        var client = new AwsVpsProviderClient(ec2, FakeSts(), Settings());

        var error = await Assert.ThrowsAsync<ProviderApiException>(() => client.DeleteServerAsync("i-0123456789abcdef0"));

        Assert.Contains("not tagged as a Minicloud resource", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, terminations);
    }

    [Fact]
    public async Task LaunchUsesPinnedNetworkTaggedIdentityAndStableClientToken()
    {
        var requests = new List<RunInstancesRequest>();
        var ec2 = Fake<IAmazonEC2>((method, args) => method.Name switch
        {
            nameof(IAmazonEC2.DescribeImagesAsync) => new DescribeImagesResponse
            {
                Images = [new Image { ImageId = AmiId, State = ImageState.Available, Architecture = ArchitectureValues.X86_64 }]
            },
            nameof(IAmazonEC2.RunInstancesAsync) => CaptureLaunch(args, requests),
            _ => throw new InvalidOperationException($"Unexpected AWS API call {method.Name}.")
        });
        var client = new AwsVpsProviderClient(ec2, FakeSts(), Settings());
        var request = new CreateServerRequest("mc-test-app", "t3.micro", "us-east-1", [],
            "#cloud-config\nusers: []\n",
            new Dictionary<string, string> { ["app-id"] = "app-test", ["managed-by"] = "minicloud" },
            Image: AmiId, BuildPlatform: "linux/amd64");

        var created = await client.CreateServerAsync(request);
        var retried = await client.CreateServerAsync(request);

        Assert.Equal("i-0123456789abcdef0", created.Id);
        Assert.Equal(created.Id, retried.Id);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].ClientToken, requests[1].ClientToken);
        Assert.Equal(64, requests[0].ClientToken.Length);
        Assert.Equal(AmiId, requests[0].ImageId);
        Assert.Equal("t3.micro", requests[0].InstanceType);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit)), requests[0].UserData);
        var network = Assert.Single(requests[0].NetworkInterfaces);
        Assert.Equal(SubnetId, network.SubnetId);
        Assert.Equal(SecurityGroupId, Assert.Single(network.Groups));
        Assert.True(network.AssociatePublicIpAddress);
        Assert.Contains(requests[0].TagSpecifications,
            tags => tags.ResourceType == ResourceType.Instance && tags.Tags.Any(t => t.Key == "app-id" && t.Value == "app-test"));
    }

    [Fact]
    public async Task RejectsAmiArchitectureMismatchBeforeAllocating()
    {
        var launches = 0;
        var ec2 = Fake<IAmazonEC2>((method, _) => method.Name switch
        {
            nameof(IAmazonEC2.DescribeImagesAsync) => new DescribeImagesResponse
            {
                Images = [new Image { ImageId = AmiId, State = ImageState.Available, Architecture = ArchitectureValues.X86_64 }]
            },
            nameof(IAmazonEC2.RunInstancesAsync) => CountLaunch(ref launches),
            _ => throw new InvalidOperationException($"Unexpected AWS API call {method.Name}.")
        });
        var client = new AwsVpsProviderClient(ec2, FakeSts(), Settings());
        var request = new CreateServerRequest("mc-test-app", "t4g.micro", "us-east-1", [],
            "#cloud-config\n", Image: AmiId, BuildPlatform: "linux/arm64");

        var error = await Assert.ThrowsAsync<ProviderApiException>(() => client.CreateServerAsync(request));

        Assert.Contains("architecture", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task CreatesEncryptedGp3VolumeInPinnedSubnetZoneAndRejectsCrossZoneAttachment()
    {
        CreateVolumeRequest? createdVolume = null;
        var ec2 = Fake<IAmazonEC2>((method, args) => method.Name switch
        {
            nameof(IAmazonEC2.DescribeSubnetsAsync) => new DescribeSubnetsResponse
                { Subnets = [new Subnet { SubnetId = SubnetId, AvailabilityZone = "us-east-1a", VpcId = "vpc-abc12345" }] },
            nameof(IAmazonEC2.CreateVolumeAsync) => CaptureVolume(args, request => createdVolume = request),
            nameof(IAmazonEC2.DescribeVolumesAsync) => new DescribeVolumesResponse
            {
                Volumes = [new Volume
                {
                    VolumeId = "vol-0123456789abcdef0",
                    AvailabilityZone = "us-east-1a",
                    Size = 20,
                    State = VolumeState.Available,
                    Tags = [new Amazon.EC2.Model.Tag { Key = "Name", Value = "mc-volume" },
                        new Amazon.EC2.Model.Tag { Key = "managed-by", Value = "minicloud" },
                        new Amazon.EC2.Model.Tag { Key = "app-id", Value = "app-test" }]
                }]
            },
            nameof(IAmazonEC2.DescribeInstancesAsync) => new DescribeInstancesResponse
            {
                Reservations =
                [
                    new Reservation
                    {
                        Instances =
                        [
                            new Instance
                            {
                                InstanceId = "i-0123456789abcdef0",
                                Placement = new Placement { AvailabilityZone = "us-east-1b" },
                                Tags =
                                [
                                    new Amazon.EC2.Model.Tag { Key = "Name", Value = "mc-test-app" },
                                    new Amazon.EC2.Model.Tag { Key = "managed-by", Value = "minicloud" }
                                ]
                            }
                        ]
                    }
                ]
            },
            _ => throw new InvalidOperationException($"Unexpected AWS API call {method.Name}.")
        });
        var client = new AwsVpsProviderClient(ec2, FakeSts(), Settings());
        var labels = new Dictionary<string, string>
        {
            ["app-id"] = "app-test",
            ["storage-name"] = "uploads",
            ["managed-by"] = "minicloud"
        };

        var volume = await client.CreateVolumeAsync("mc-volume", 20, "us-east-1", labels, default);
        Assert.Equal("us-east-1", volume.Region);
        Assert.Equal(20, volume.SizeGb);
        Assert.Equal("gp3", createdVolume!.VolumeType.Value);
        Assert.True(createdVolume.Encrypted);
        Assert.Equal("us-east-1a", createdVolume.AvailabilityZone);
        Assert.Equal(64, createdVolume.ClientToken.Length);

        var error = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.AttachVolumeAsync(volume.Id, "i-0123456789abcdef0", default));
        Assert.Contains("same Availability Zone", error.Message, StringComparison.Ordinal);
    }

    private static RunInstancesResponse CaptureLaunch(object?[] args, List<RunInstancesRequest> requests)
    {
        requests.Add((RunInstancesRequest)args[0]!);
        return new RunInstancesResponse
        {
            Reservation = new Reservation
            {
                Instances =
                [
                    new Instance
                    {
                        InstanceId = "i-0123456789abcdef0",
                        PublicIpAddress = "198.51.100.10",
                        State = new InstanceState { Name = InstanceStateName.Pending },
                        Tags = [new Amazon.EC2.Model.Tag { Key = "Name", Value = "mc-test-app" }]
                    }
                ]
            }
        };
    }

    private static CreateVolumeResponse CaptureVolume(object?[] args, Action<CreateVolumeRequest> capture)
    {
        var request = (CreateVolumeRequest)args[0]!;
        capture(request);
        return new CreateVolumeResponse
        {
            Volume = new Volume
            {
                VolumeId = "vol-0123456789abcdef0",
                AvailabilityZone = request.AvailabilityZone,
                Size = request.Size,
                State = VolumeState.Available
            }
        };
    }

    private static RunInstancesResponse CountLaunch(ref int launches)
    {
        launches++;
        return new RunInstancesResponse();
    }

    private static TerminateInstancesResponse CountTermination(ref int terminations)
    {
        terminations++;
        return new TerminateInstancesResponse();
    }

    private static AwsProviderSettings Settings() => new("us-east-1", SubnetId, SecurityGroupId);

    private static IAmazonSecurityTokenService FakeSts() => Fake<IAmazonSecurityTokenService>((method, _) =>
        method.Name == nameof(IAmazonSecurityTokenService.GetCallerIdentityAsync)
            ? new GetCallerIdentityResponse { Account = "123456789012" }
            : throw new InvalidOperationException($"Unexpected AWS STS call {method.Name}."));

    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, AwsServiceProxy>();
        ((AwsServiceProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class AwsServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => throw new NotImplementedException();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var result = Handler(targetMethod, args ?? []);
            if (targetMethod.ReturnType == typeof(Task)) return Task.CompletedTask;
            if (targetMethod.ReturnType.IsGenericType
                && targetMethod.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = targetMethod.ReturnType.GenericTypeArguments[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType)
                    .Invoke(null, [result]);
            }
            return result;
        }
    }
}
