using System.Security.Cryptography;
using System.Text;

namespace Minicloud.Common.Providers;

public sealed record ProviderStorageVolume(
    string Id,
    string Name,
    int SizeGb,
    string Region,
    string? ServerId,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string>? Tags = null,
    string? FilesystemPath = null,
    bool IoReady = true,
    string? Status = null,
    bool FilesystemInitializationPending = false)
{
    public static string OwnershipTag(IReadOnlyDictionary<string, string> labels)
    {
        var identity = string.Join('\n', labels.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
        return "mc-owner-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
    }
}

public interface IStorageVolumeProvider
{
    CloudProvider Provider { get; }
    Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct);
    Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct);
    Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
    Task AttachVolumeAsync(string id, string serverId, CancellationToken ct);
    Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct);
}

public interface IStorageVolumeInitializationProvider
{
    Task MarkFilesystemInitializedAsync(string id, string ownershipTag, CancellationToken ct);
}
