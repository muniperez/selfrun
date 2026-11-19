namespace Minicloud.Common.Providers;

public sealed record WorkspaceCloudAction(string Resource, string Id, string Status);

public sealed record WorkspaceCloudServer(
    string Id, string Name, string Status, string Region, string Sku, string ImageId,
    string? Ipv4, string? PrimaryIpId, IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string>? FirewallIds = null, bool FirewallsApplied = false);

public sealed record WorkspaceCloudVolume(
    string Id, string Name, int SizeGb, string Region, string? ServerId,
    string? Filesystem, string Device, IReadOnlyDictionary<string, string> Labels);

public sealed record WorkspaceCloudImage(
    string Id, string Status, string Type, string Architecture, string? OsFlavor,
    IReadOnlyDictionary<string, string> Labels);

public sealed record WorkspaceCloudFirewall(
    string Id, string Name, int AttachedServers, IReadOnlyDictionary<string, string> Labels);

public sealed record WorkspaceCloudAllocation(WorkspaceCloudServer Server, WorkspaceCloudAction Action);
public sealed record WorkspaceCloudVolumeAllocation(
    WorkspaceCloudVolume Volume, WorkspaceCloudAction? Action, IReadOnlyList<WorkspaceCloudAction> NextActions);

public sealed record WorkspaceCloudCreate(
    string Name, string Sku, string Region, string ImageId, string UserData,
    string FirewallId, IReadOnlyDictionary<string, string> Labels);

public interface IDevelopmentWorkspaceProvider
{
    Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct);
    Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct);
    Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct);
    Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct);
    Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudAction?> AttachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct);

    /// <summary>Name of the attached volume under <c>/dev/disk/by-id</c> inside the guest.</summary>
    string GetWorkspaceVolumeDeviceId(WorkspaceCloudVolume volume) => "scsi-0HC_Volume_" + volume.Id;

    /// <summary>True when new volumes arrive without a filesystem and the guest must create ext4 once.</summary>
    bool WorkspaceVolumesFormatInGuest => false;
}

public static class WorkspaceOwnership
{
    private static readonly System.Text.RegularExpressions.Regex Label =
        new("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static void Require(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected)
    {
        Validate(expected);
        if (!expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value))
            throw new InvalidOperationException("Workspace provider ownership does not match the recorded resource identity.");
    }

    public static void RequireManagementAddresses(IReadOnlyList<string> managementAddresses)
    {
        if (managementAddresses.Count is < 1 or > 8 || managementAddresses.Any(address =>
            !System.Net.IPAddress.TryParse(address.Split('/')[0], out var ip) ||
            address != ip + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128") ||
            System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)))
            throw new ArgumentException("Workspace management rules require explicit control-plane host CIDRs.");
    }

    /// <summary>Stable digest of an ownership label set, for providers without key/value labels.</summary>
    public static string Digest(IReadOnlyDictionary<string, string> labels)
    {
        Validate(labels);
        var canonical = string.Concat(labels.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value + "\n"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))[..40];
    }

    public static bool Matches(IReadOnlyDictionary<string, string> actual, IReadOnlyDictionary<string, string> expected) =>
        expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public static void Validate(IReadOnlyDictionary<string, string> labels)
    {
        if (labels.Count < 4 || !labels.TryGetValue("managed-by", out var product) || product != "minicloud-workspaces" ||
            !labels.ContainsKey("organization") || !labels.ContainsKey("workspace") ||
            labels.Any(pair => !Label.IsMatch(pair.Key) || !Label.IsMatch(pair.Value)))
            throw new ArgumentException("Explicit workspace ownership labels are required.");
    }
}
