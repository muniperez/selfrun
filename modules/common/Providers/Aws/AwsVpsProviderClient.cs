using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Aws;

public sealed class AwsVpsProviderClient : IVpsProviderClient, IStorageVolumeProvider,
    IProviderScopeIdentity, IAwsAccountValidator
{
    private readonly IAmazonEC2 _ec2;
    private readonly IAmazonSecurityTokenService _sts;
    private readonly AwsProviderSettings _settings;

    public CloudProvider Provider => CloudProvider.Aws;

    public AwsVpsProviderClient(AwsCredentialBundle credentials, AwsProviderSettings settings)
        : this(CreateCredentials(credentials), settings)
    {
    }

    private AwsVpsProviderClient(AWSCredentials credentials, AwsProviderSettings settings)
        : this(new AmazonEC2Client(credentials, RegionEndpoint.GetBySystemName(settings.Region)),
            new AmazonSecurityTokenServiceClient(credentials, RegionEndpoint.GetBySystemName(settings.Region)), settings)
    {
    }

    public AwsVpsProviderClient(IAmazonEC2 ec2, IAmazonSecurityTokenService sts, AwsProviderSettings settings)
    {
        _ec2 = ec2 ?? throw new ArgumentNullException(nameof(ec2));
        _sts = sts ?? throw new ArgumentNullException(nameof(sts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (!IsRegion(settings.Region) || !IsSubnetId(settings.SubnetId) || !IsSecurityGroupId(settings.SecurityGroupId))
            throw new ArgumentException("AWS region, subnet, or security group is invalid.", nameof(settings));
    }

    public async Task ValidateAccountConfigurationAsync(CancellationToken cancellationToken)
    {
        var subnetResponse = await SendAsync(() => _ec2.DescribeSubnetsAsync(
            new DescribeSubnetsRequest { SubnetIds = [_settings.SubnetId] }, cancellationToken));
        var subnet = subnetResponse.Subnets.SingleOrDefault()
            ?? throw new ProviderApiException("The configured AWS subnet was not found.", provider: Provider);
        if (!subnet.AvailabilityZone.StartsWith(_settings.Region, StringComparison.Ordinal))
            throw new ProviderApiException("The configured AWS subnet is not in the selected region.", provider: Provider);

        var groupsResponse = await SendAsync(() => _ec2.DescribeSecurityGroupsAsync(
            new DescribeSecurityGroupsRequest { GroupIds = [_settings.SecurityGroupId] }, cancellationToken));
        var group = groupsResponse.SecurityGroups.SingleOrDefault()
            ?? throw new ProviderApiException("The configured AWS security group was not found.", provider: Provider);
        if (!string.Equals(group.VpcId, subnet.VpcId, StringComparison.Ordinal))
            throw new ProviderApiException("The configured AWS security group and subnet belong to different VPCs.", provider: Provider);
    }

    public async Task<string> GetScopeIdentityAsync(CancellationToken cancellationToken)
    {
        var identity = await SendAsync(() => _sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken));
        if (string.IsNullOrWhiteSpace(identity.Account) || identity.Account.Length != 12 || identity.Account.Any(c => !char.IsAsciiDigit(c)))
            throw new ProviderApiException("AWS did not return a valid account identity.", provider: Provider);
        return identity.Account;
    }

    public async Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var response = await SendAsync(() => _ec2.DescribeInstancesAsync(new DescribeInstancesRequest
        {
            Filters =
            [
                new Filter { Name = "tag:Name", Values = [serverName] },
                new Filter { Name = "tag:managed-by", Values = ["minicloud"] },
                new Filter { Name = "instance-state-name", Values = ["pending", "running", "stopping", "stopped"] }
            ]
        }, cancellationToken));
        var matches = Instances(response).Where(x => x.Tags.Any(t => t.Key == "Name" && t.Value == serverName)).ToArray();
        if (matches.Length > 1)
            throw new ProviderApiException("Multiple AWS instances match this Minicloud server identity.", provider: Provider);
        return matches.Length == 0 ? null : MapServer(matches[0]);
    }

    public async Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Plan);
        if (!string.Equals(request.Region, _settings.Region, StringComparison.Ordinal))
            throw new ProviderApiException("AWS plan region does not match the provider account's configured region.", provider: Provider);
        if (request.SshKeyReferences.Count > 0)
            throw new NotSupportedException("AWS provider key-pair selection is not supported. Use the Minicloud cloud-init SSH key.");
        if (string.IsNullOrWhiteSpace(request.Image) || !IsAmiId(request.Image))
            throw new ProviderApiException("AWS plans require a valid regional AMI ID.", provider: Provider);

        var imageResponse = await SendAsync(() => _ec2.DescribeImagesAsync(
            new DescribeImagesRequest { ImageIds = [request.Image] }, cancellationToken));
        var image = imageResponse.Images.SingleOrDefault()
            ?? throw new ProviderApiException("The selected AWS AMI was not found or is not accessible.", provider: Provider);
        if (!string.Equals(image.State?.Value, "available", StringComparison.OrdinalIgnoreCase))
            throw new ProviderApiException("The selected AWS AMI is not available.", provider: Provider);
        var imagePlatform = image.Architecture?.Value switch
        {
            "x86_64" => "linux/amd64",
            "arm64" => "linux/arm64",
            _ => null
        };
        if (imagePlatform is null || imagePlatform != request.BuildPlatform)
            throw new ProviderApiException("The selected AWS AMI architecture does not match the plan build platform.", provider: Provider);

        var tags = new Dictionary<string, string>(request.Labels ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["Name"] = request.Name,
            ["managed-by"] = "minicloud"
        };
        var tagSpecifications = new List<TagSpecification>
        {
            new() { ResourceType = ResourceType.Instance, Tags = ToTags(tags) },
            new() { ResourceType = ResourceType.Volume, Tags = ToTags(tags) }
        };
        var token = IdempotencyToken(request.Name, request.Region, request.Plan, request.Image);
        var response = await SendAsync(() => _ec2.RunInstancesAsync(new RunInstancesRequest
        {
            ImageId = request.Image,
            InstanceType = request.Plan,
            MinCount = 1,
            MaxCount = 1,
            ClientToken = token,
            UserData = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.UserDataCloudInit ?? string.Empty)),
            NetworkInterfaces =
            [
                new InstanceNetworkInterfaceSpecification
                {
                    DeviceIndex = 0,
                    SubnetId = _settings.SubnetId,
                    Groups = [_settings.SecurityGroupId],
                    AssociatePublicIpAddress = true
                }
            ],
            TagSpecifications = tagSpecifications
        }, cancellationToken));
        var instance = response.Reservation?.Instances?.SingleOrDefault()
            ?? throw new ProviderApiException("AWS returned no instance after a successful launch request.", provider: Provider);
        return MapServer(instance);
    }

    public async Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var response = await SendAsync(() => _ec2.DescribeInstancesAsync(
            new DescribeInstancesRequest { InstanceIds = [serverId] }, cancellationToken));
        var instance = Instances(response).SingleOrDefault(x => x.InstanceId == serverId);
        if (instance is null)
            return new ProviderServerStatus(serverId, "not_found", null, null, false, "not_found");
        var state = instance.State?.Name?.Value ?? "unknown";
        return new ProviderServerStatus(serverId, state, instance.PublicIpAddress, null,
            string.Equals(state, "running", StringComparison.OrdinalIgnoreCase), state);
    }

    public async Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        try
        {
            var response = await _ec2.DescribeInstancesAsync(
                new DescribeInstancesRequest { InstanceIds = [serverId] }, cancellationToken);
            var instance = Instances(response).SingleOrDefault(x => x.InstanceId == serverId);
            if (instance is null) return;
            if (!HasMinicloudIdentity(MapServer(instance).Labels))
                throw new ProviderApiException("Refusing to terminate an AWS instance not tagged as a Minicloud resource.", provider: Provider);
            if (string.Equals(instance.State?.Name?.Value, "terminated", StringComparison.OrdinalIgnoreCase)) return;
            await _ec2.TerminateInstancesAsync(new TerminateInstancesRequest { InstanceIds = [serverId] }, cancellationToken);
        }
        catch (AmazonEC2Exception exception) when (exception.ErrorCode == "InvalidInstanceID.NotFound")
        {
        }
        catch (AmazonServiceException exception)
        {
            throw ToProviderException(exception);
        }
    }

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var response = await SendAsync(() => _ec2.DescribeVolumesAsync(new DescribeVolumesRequest
        {
            Filters =
            [
                new Filter { Name = "tag:Name", Values = [name] },
                new Filter { Name = "tag:managed-by", Values = ["minicloud"] }
            ]
        }, ct));
        if (response.Volumes.Count > 1)
            throw new ProviderApiException("Multiple AWS EBS volumes match this Minicloud storage identity.", provider: Provider);
        return response.Volumes.Count == 0 ? null : MapVolume(response.Volumes[0]);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        try
        {
            var response = await _ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = [id] }, ct);
            return response.Volumes.SingleOrDefault() is { } volume ? MapVolume(volume) : null;
        }
        catch (AmazonEC2Exception exception) when (exception.ErrorCode == "InvalidVolume.NotFound")
        {
            return null;
        }
        catch (AmazonServiceException exception)
        {
            throw ToProviderException(exception);
        }
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region,
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sizeGb < 1 || region != _settings.Region)
            throw new ProviderApiException("AWS EBS size or region is invalid.", provider: Provider);
        var subnet = await SendAsync(() => _ec2.DescribeSubnetsAsync(
            new DescribeSubnetsRequest { SubnetIds = [_settings.SubnetId] }, ct));
        var zone = subnet.Subnets.SingleOrDefault()?.AvailabilityZone
            ?? throw new ProviderApiException("The configured AWS subnet has no availability zone.", provider: Provider);
        var response = await SendAsync(() => _ec2.CreateVolumeAsync(new CreateVolumeRequest
        {
            AvailabilityZone = zone,
            Size = sizeGb,
            VolumeType = VolumeType.Gp3,
            Encrypted = true,
            ClientToken = IdempotencyToken(name, region, "gp3", sizeGb.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            TagSpecifications =
            [
                new TagSpecification
                {
                    ResourceType = ResourceType.Volume,
                    Tags = ToTags(new Dictionary<string, string>(labels, StringComparer.Ordinal)
                    {
                        ["Name"] = name,
                        ["managed-by"] = "minicloud"
                    })
                }
            ]
        }, ct));
        var volume = response.Volume ?? throw new ProviderApiException("AWS returned no EBS volume after creation.", provider: Provider);
        await WaitForVolumeAsync(volume.VolumeId, "available", ct);
        return await GetVolumeAsync(volume.VolumeId, ct)
            ?? throw new ProviderApiException("AWS EBS volume disappeared after creation.", provider: Provider);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        var volume = await GetVolumeAsync(id, ct)
            ?? throw new ProviderApiException("AWS EBS volume was not found.", provider: Provider);
        if (!HasMinicloudIdentity(volume.Labels))
            throw new ProviderApiException("Refusing to attach an EBS volume not tagged as a Minicloud resource.", provider: Provider);
        var instanceResponse = await SendAsync(() => _ec2.DescribeInstancesAsync(
            new DescribeInstancesRequest { InstanceIds = [serverId] }, ct));
        var instance = Instances(instanceResponse).SingleOrDefault(x => x.InstanceId == serverId)
            ?? throw new ProviderApiException("AWS instance was not found for EBS attachment.", provider: Provider);
        if (!HasMinicloudIdentity(MapServer(instance).Labels))
            throw new ProviderApiException("Refusing to attach an EBS volume to an instance not tagged as a Minicloud resource.", provider: Provider);
        var instanceZone = instance.Placement?.AvailabilityZone;
        var volumeDetail = await SendAsync(() => _ec2.DescribeVolumesAsync(
            new DescribeVolumesRequest { VolumeIds = [id] }, ct));
        var volumeZone = volumeDetail.Volumes.SingleOrDefault()?.AvailabilityZone;
        if (string.IsNullOrWhiteSpace(instanceZone) || instanceZone != volumeZone)
            throw new ProviderApiException("AWS EBS volumes can only attach to instances in the same Availability Zone.", provider: Provider);
        if (volume.ServerId == serverId)
        {
            await WaitForVolumeAsync(id, "in-use", ct, serverId);
            return;
        }
        if (volume.ServerId is not null)
            throw new ProviderApiException("AWS EBS volume is already attached to another instance.", provider: Provider);
        await SendAsync(() => _ec2.AttachVolumeAsync(new AttachVolumeRequest
        {
            VolumeId = id,
            InstanceId = serverId,
            Device = "/dev/sdf"
        }, ct));
        await WaitForVolumeAsync(id, "in-use", ct, serverId);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        if (sizeGb < 1) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        var volume = await GetVolumeAsync(id, ct) ?? throw new ProviderApiException("AWS EBS volume was not found.", provider: Provider);
        if (!HasMinicloudIdentity(volume.Labels))
            throw new ProviderApiException("Refusing to resize an EBS volume not tagged as a Minicloud resource.", provider: Provider);
        if (sizeGb < volume.SizeGb)
            throw new ProviderApiException("AWS EBS volumes cannot be shrunk.", provider: Provider);
        if (sizeGb == volume.SizeGb) return;
        await WaitForExistingModificationAsync(id, ct);
        volume = await GetVolumeAsync(id, ct) ?? throw new ProviderApiException("AWS EBS volume disappeared before resize.", provider: Provider);
        if (sizeGb < volume.SizeGb)
            throw new ProviderApiException("AWS EBS volume size changed while resize was being prepared.", provider: Provider);
        if (sizeGb == volume.SizeGb) return;
        await SendAsync(() => _ec2.ModifyVolumeAsync(new ModifyVolumeRequest { VolumeId = id, Size = sizeGb }, ct));
        await WaitForExistingModificationAsync(id, ct, sizeGb);
    }

    private async Task WaitForExistingModificationAsync(string id, CancellationToken ct, int? requiredTargetSize = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var changes = await SendAsync(() => _ec2.DescribeVolumesModificationsAsync(
                new DescribeVolumesModificationsRequest { VolumeIds = [id] }, ct));
            var modification = changes.VolumesModifications.SingleOrDefault(x => x.VolumeId == id);
            if (modification is null) return;
            if (modification.ModificationState?.Value == "completed")
            {
                if (requiredTargetSize is null || modification.TargetSize == requiredTargetSize.Value) return;
                throw new ProviderApiException("AWS completed a volume resize with a different target size.", provider: Provider);
            }
            if (modification.ModificationState?.Value == "failed")
                throw new ProviderApiException("AWS EBS volume resize failed.", provider: Provider);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("AWS EBS volume resize did not complete within ten minutes.");
    }

    private async Task WaitForVolumeAsync(string id, string desiredState, CancellationToken ct, string? serverId = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var volume = await GetVolumeAsync(id, ct);
            if (volume is null) throw new ProviderApiException("AWS EBS volume disappeared during an operation.", provider: Provider);
            var stateResponse = await SendAsync(() => _ec2.DescribeVolumesAsync(
                new DescribeVolumesRequest { VolumeIds = [id] }, ct));
            var detail = stateResponse.Volumes.SingleOrDefault();
            var state = detail?.State?.Value;
            var attached = serverId is null || detail?.Attachments?.SingleOrDefault()?.State?.Value == "attached";
            if (state == desiredState && attached && (serverId is null || volume.ServerId == serverId)) return;
            if (state is "deleting" or "deleted" or "error")
                throw new ProviderApiException($"AWS EBS volume entered the unexpected '{state}' state.", provider: Provider);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException($"AWS EBS volume did not reach '{desiredState}' within five minutes.");
    }

    private ProviderStorageVolume MapVolume(Volume volume)
    {
        var zone = volume.AvailabilityZone ?? string.Empty;
        if (!zone.StartsWith(_settings.Region, StringComparison.Ordinal))
            throw new ProviderApiException("AWS EBS volume is outside the configured region.", provider: Provider);
        var attachments = volume.Attachments ?? [];
        if (attachments.Count > 1)
            throw new ProviderApiException("AWS EBS volume has ambiguous attachments.", provider: Provider);
        var labels = (volume.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal);
        var sizeGb = volume.Size.GetValueOrDefault();
        if (sizeGb < 1) throw new ProviderApiException("AWS returned an invalid EBS volume size.", provider: Provider);
        return new ProviderStorageVolume(volume.VolumeId, labels.GetValueOrDefault("Name") ?? string.Empty,
            sizeGb, _settings.Region, attachments.SingleOrDefault()?.InstanceId, labels);
    }

    private static ProviderServerInfo MapServer(Instance instance)
    {
        var labels = (instance.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .ToDictionary(x => x.Key, x => x.Value ?? string.Empty, StringComparer.Ordinal);
        return new ProviderServerInfo(instance.InstanceId, labels.GetValueOrDefault("Name") ?? string.Empty,
            instance.PublicIpAddress ?? string.Empty, instance.State?.Name?.Value ?? "unknown", Labels: labels);
    }

    private static IEnumerable<Instance> Instances(DescribeInstancesResponse response) =>
        (response.Reservations ?? []).SelectMany(x => x.Instances ?? []);

    private static List<Amazon.EC2.Model.Tag> ToTags(IReadOnlyDictionary<string, string> tags) =>
        tags.Select(x => new Amazon.EC2.Model.Tag { Key = x.Key, Value = x.Value }).ToList();

    private static string IdempotencyToken(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();

    private static bool HasMinicloudIdentity(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null && labels.TryGetValue("managed-by", out var managedBy) && managedBy == "minicloud"
        && labels.TryGetValue("Name", out var name) && !string.IsNullOrWhiteSpace(name);

    public static bool IsRegion(string value) =>
        value.Length is >= 9 and <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && value.Contains('-');

    public static bool IsSubnetId(string value) =>
        value.StartsWith("subnet-", StringComparison.Ordinal) && value.Length is >= 15 and <= 24
        && value[7..].All(Uri.IsHexDigit);

    public static bool IsSecurityGroupId(string value) =>
        value.StartsWith("sg-", StringComparison.Ordinal) && value.Length is >= 11 and <= 20
        && value[3..].All(Uri.IsHexDigit);

    public static bool IsAmiId(string value) =>
        value.StartsWith("ami-", StringComparison.Ordinal) && value.Length is >= 12 and <= 21
        && value[4..].All(Uri.IsHexDigit);

    private static AWSCredentials CreateCredentials(AwsCredentialBundle credentials) =>
        string.IsNullOrWhiteSpace(credentials.SessionToken)
            ? new BasicAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey)
            : new SessionAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken);

    private async Task<T> SendAsync<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (AmazonServiceException exception)
        {
            throw ToProviderException(exception);
        }
    }

    private ProviderApiException ToProviderException(AmazonServiceException exception) =>
        new($"AWS API request failed ({exception.ErrorCode ?? "unknown"}).",
            exception.StatusCode, Provider, exception.ErrorCode, exception);
}
