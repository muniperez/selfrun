namespace Selfrun.Common.Providers.Extensibility;

[Flags]
public enum ProviderComputeFamily
{
    None = 0,
    VirtualServer = 1 << 0,
    BareMetal = 1 << 1
}

public enum ProviderLocationGranularity
{
    Region,
    Zone,
    Datacenter
}

public enum ProviderQualificationState
{
    Experimental,
    DraftGated,
    Qualified,
    Deprecated
}

/// <summary>
/// Declarative capability matrix for a hosting/VPS provider adapter.
/// Core services query capabilities rather than branching on hardcoded provider types.
/// </summary>
public sealed record ProviderCapabilities(
    ProviderComputeFamily ComputeFamilies,
    IReadOnlyList<string> SupportedArchitectures,
    ProviderLocationGranularity LocationGranularity,
    bool SupportsSshKeyInventory,
    bool SupportsCloudInit,
    bool SupportsDurableVolumes,
    bool SupportsVolumeResize,
    bool SupportsSnapshots,
    bool SupportsPrivateNetworks,
    bool SupportsPublicIpv4,
    bool SupportsPublicIpv6,
    bool SupportsLabelsOrTags,
    bool SupportsExactLabelOwnershipVerification,
    bool SupportsScopeIdentityDiscovery,
    bool SupportsQuotaDiscovery,
    bool SupportsCatalogDiscovery,
    bool SupportsAsynchronousOperations,
    bool SupportsIdempotencyTokens)
{
    public bool SupportsVirtualServers => (ComputeFamilies & ProviderComputeFamily.VirtualServer) != 0;
    public bool SupportsBareMetal => (ComputeFamilies & ProviderComputeFamily.BareMetal) != 0;

    public bool SupportsArchitecture(string buildPlatform) =>
        SupportedArchitectures.Any(x => string.Equals(x, buildPlatform, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ComputeFamilyWireNames()
    {
        var names = new List<string>(2);
        if (SupportsVirtualServers) names.Add("virtual-server");
        if (SupportsBareMetal) names.Add("bare-metal");
        return names;
    }

    public static string ToWireValue(ProviderLocationGranularity granularity) => granularity switch
    {
        ProviderLocationGranularity.Region => "region",
        ProviderLocationGranularity.Zone => "zone",
        ProviderLocationGranularity.Datacenter => "datacenter",
        _ => throw new ArgumentOutOfRangeException(nameof(granularity))
    };

    public static string ToWireValue(ProviderQualificationState state) => state switch
    {
        ProviderQualificationState.Experimental => "experimental",
        ProviderQualificationState.DraftGated => "draft-gated",
        ProviderQualificationState.Qualified => "qualified",
        ProviderQualificationState.Deprecated => "deprecated",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

public sealed record ProviderLocationInfo(
    string Code,
    string DisplayName,
    ProviderLocationGranularity Granularity,
    string? ParentRegion = null,
    string? NetworkZone = null,
    bool Available = true);

public sealed record ProviderComputeOfferInfo(
    string Sku,
    string DisplayName,
    ProviderComputeFamily ComputeFamily,
    IReadOnlyList<string> Architectures,
    int VcpuCount,
    int MemoryMb,
    int DiskGb,
    IReadOnlyList<string>? AvailableLocations = null,
    TimeSpan? EstimatedProvisioningLeadTime = null);

public sealed record ProviderImageInfo(
    string Id,
    string Name,
    string Architecture,
    string? Distribution = null,
    bool SupportsCloudInit = true,
    IReadOnlyList<string>? AvailableLocations = null);

public sealed record ProviderQuotaInfo(
    string Resource,
    long Limit,
    long Used,
    string? Location = null,
    string? Unit = null)
{
    public long Remaining => Math.Max(0, Limit - Used);
}

/// <summary>
/// Optional read-only catalog and quota discovery contract for provider adapters
/// that can enumerate locations, compute offers (virtual servers and/or bare metal),
/// images, and account quotas without allocating billable resources.
/// </summary>
public interface IVpsProviderCatalogDiscoveryClient
{
    Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderComputeOfferInfo>> ListComputeOffersAsync(
        string? location = null,
        ProviderComputeFamily? computeFamily = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
        string? location = null,
        string? architecture = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(
        string? location = null,
        CancellationToken cancellationToken = default);
}
