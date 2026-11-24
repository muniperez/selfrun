using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Common.Providers.Linode;

public sealed partial class LinodeClient
{
    private const string FilesystemPendingTag = "mc-format-pending";

    public async Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct)
    {
        var label = ToVolumeLabel(name);
        var matches = new List<LinodeVolumeDto>();
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(HttpMethod.Get, $"volumes?page={page}&page_size={PageSize}");
            request.Headers.TryAddWithoutValidation("X-Filter", JsonSerializer.Serialize(new { label }));
            using var response = await SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
            var result = await response.Content.ReadFromJsonAsync<LinodePage<LinodeVolumeDto>>(cancellationToken: ct);
            var items = result?.Data ?? [];
            matches.AddRange(items.Where(x => string.Equals(x.Label, label, StringComparison.Ordinal)));
            if (result is null || page >= result.Pages || items.Count == 0) break;
        }
        if (matches.Count > 1) throw new InvalidOperationException("Multiple Linode volumes match the reserved storage label.");
        return matches.Count == 0 ? null : MapVolume(matches[0]);
    }

    public async Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct)
    {
        var volumeId = ParseResourceId(id, "Linode volume");
        using var request = CreateRequest(HttpMethod.Get, $"volumes/{volumeId}");
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        var volume = await response.Content.ReadFromJsonAsync<LinodeVolumeDto>(cancellationToken: ct);
        return volume is null ? null : MapVolume(volume);
    }

    public async Task<ProviderStorageVolume> CreateVolumeAsync(
        string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        if (sizeGb <= 0) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        var payload = new LinodeCreateVolumePayload
        {
            Label = ToVolumeLabel(name),
            Size = sizeGb,
            Region = region,
            Tags = ["minicloud", ProviderStorageVolume.OwnershipTag(labels), FilesystemPendingTag]
        };
        using var request = CreateRequest(HttpMethod.Post, "volumes");
        request.Content = JsonContent.Create(payload);
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var created = await response.Content.ReadFromJsonAsync<LinodeVolumeDto>(cancellationToken: ct);
        if (created is null || created.Id <= 0)
            throw new ProviderApiException("Linode API returned an empty Block Storage volume in the create response.", response.StatusCode, Provider);
        return await WaitForVolumeAsync(created.Id.ToString(CultureInfo.InvariantCulture), sizeGb, null, ct);
    }

    public async Task AttachVolumeAsync(string id, string serverId, CancellationToken ct)
    {
        var volumeId = ParseResourceId(id, "Linode volume");
        var linodeId = ParseResourceId(serverId, "Linode instance");
        var current = await GetVolumeAsync(id, ct) ?? throw new InvalidOperationException("Linode volume disappeared before attachment.");
        EnsureManagedVolume(current);
        if (current.ServerId is not null && current.ServerId != serverId)
            throw new InvalidOperationException("Linode volume is attached to another instance.");
        if (current.ServerId == serverId)
        {
            await WaitForVolumeAsync(id, current.SizeGb, serverId, ct);
            return;
        }
        using var request = CreateRequest(HttpMethod.Post, $"volumes/{volumeId}/attach");
        request.Content = JsonContent.Create(new LinodeAttachVolumePayload { LinodeId = linodeId });
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await WaitForVolumeAsync(id, current.SizeGb, serverId, ct);
    }

    public async Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct)
    {
        var volumeId = ParseResourceId(id, "Linode volume");
        if (sizeGb <= 0) throw new ArgumentOutOfRangeException(nameof(sizeGb));
        var current = await GetVolumeAsync(id, ct)
            ?? throw new InvalidOperationException("Linode volume disappeared before resize.");
        EnsureManagedVolume(current);
        if (sizeGb < current.SizeGb)
            throw new InvalidOperationException("Linode Block Storage volumes can only grow.");
        if (sizeGb == current.SizeGb) return;
        using var request = CreateRequest(HttpMethod.Post, $"volumes/{volumeId}/resize");
        request.Content = JsonContent.Create(new LinodeResizeVolumePayload { Size = sizeGb });
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await WaitForVolumeAsync(id, sizeGb, null, ct);
    }

    public async Task MarkFilesystemInitializedAsync(string id, string ownershipTag, CancellationToken ct)
    {
        var volumeId = ParseResourceId(id, "Linode volume");
        var current = await GetVolumeAsync(id, ct)
            ?? throw new InvalidOperationException("Linode volume disappeared after filesystem initialization.");
        var tags = current.Tags ?? [];
        if (!tags.Contains(ownershipTag, StringComparer.Ordinal))
            throw new InvalidOperationException("Linode volume ownership does not match this app.");
        if (!tags.Contains(FilesystemPendingTag, StringComparer.Ordinal)) return;

        using var request = CreateRequest(HttpMethod.Put, $"volumes/{volumeId}");
        request.Content = JsonContent.Create(new { tags = tags.Where(x => x != FilesystemPendingTag).ToArray() });
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var updated = await response.Content.ReadFromJsonAsync<LinodeVolumeDto>(cancellationToken: ct);
        if (updated?.Tags is null || updated.Tags.Contains(FilesystemPendingTag, StringComparer.Ordinal))
            throw new ProviderApiException("Linode did not confirm filesystem initialization.", response.StatusCode, Provider);
    }

    private async Task<ProviderStorageVolume> WaitForVolumeAsync(string id, int minimumSize, string? serverId, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - started < OperationTimeout)
        {
            ct.ThrowIfCancellationRequested();
            var volume = await GetVolumeAsync(id, ct) ?? throw new InvalidOperationException("Linode volume disappeared during an operation.");
            if (volume.SizeGb < minimumSize)
            {
                await Task.Delay(PollInterval, ct);
                continue;
            }
            if (serverId is not null && volume.ServerId != serverId)
            {
                if (volume.ServerId is not null) throw new InvalidOperationException("Linode volume attached to an unexpected instance.");
                await Task.Delay(PollInterval, ct);
                continue;
            }
            if (volume.ServerId == serverId && volume.FilesystemPath is not null && volume.IoReady)
                return volume;
            if (serverId is null && volume.FilesystemPath is not null && volume.Status == "active")
                return volume;
            await Task.Delay(PollInterval, ct);
        }
        throw new TimeoutException("Linode Block Storage did not become ready within five minutes.");
    }

    private static ProviderStorageVolume MapVolume(LinodeVolumeDto volume)
    {
        var tags = volume.Tags ?? [];
        return new ProviderStorageVolume(volume.Id.ToString(CultureInfo.InvariantCulture), volume.Label ?? string.Empty,
            volume.Size, volume.Region ?? string.Empty, volume.LinodeId?.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string>(), tags, volume.FilesystemPath, volume.IoReady, volume.Status,
            tags.Contains(FilesystemPendingTag, StringComparer.Ordinal));
    }

    private static void EnsureManagedVolume(ProviderStorageVolume volume)
    {
        var tags = volume.Tags ?? [];
        if (!tags.Contains("minicloud", StringComparer.Ordinal)
            || !tags.Any(x => x.StartsWith("mc-owner-", StringComparison.Ordinal) && x.Length == 41
                && x.AsSpan(9).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0))
            throw new InvalidOperationException("Refusing to modify a Linode volume not marked as an owned Minicloud resource.");
    }

    private static string ToVolumeLabel(string name)
    {
        if (name.Length <= 32) return name;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        return "mc-" + suffix[..29];
    }

    private sealed class LinodeVolumeDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("region")] public string? Region { get; set; }
        [JsonPropertyName("linode_id")] public long? LinodeId { get; set; }
        [JsonPropertyName("filesystem_path")] public string? FilesystemPath { get; set; }
        [JsonPropertyName("io_ready")] public bool IoReady { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    }

    private sealed class LinodeCreateVolumePayload
    {
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("size")] public int Size { get; set; }
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
        [JsonPropertyName("tags")] public string[] Tags { get; set; } = [];
    }

    private sealed class LinodeAttachVolumePayload
    {
        [JsonPropertyName("linode_id")] public long LinodeId { get; set; }
    }

    private sealed class LinodeResizeVolumePayload
    {
        [JsonPropertyName("size")] public int Size { get; set; }
    }
}
