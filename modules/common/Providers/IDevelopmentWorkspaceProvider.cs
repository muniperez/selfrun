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
}
