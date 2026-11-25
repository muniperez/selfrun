using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers;

public sealed record BareMetalOfferInfo(
    string OfferCode,
    string Name,
    string Datacenter,
    string Architecture,
    int Cores,
    int MemoryGb,
    string ProvisioningLeadTime,
    bool OrderingEnabled);

public sealed record BareMetalServerInfo(
    string ServiceName,
    string DisplayName,
    string Datacenter,
    string State,
    string? Ipv4Address,
    string? Ipv6Address,
    string? OsImage,
    bool CommercialOrderingGated);

/// <summary>
/// Dedicated / bare-metal provider contract separating bare-metal offers, inventory,
/// lead times, and gated commercial ordering/cancellation from virtual-server lifecycles.
/// </summary>
public interface IBareMetalProviderClient
{
    CloudProvider Provider { get; }
    bool IsCommercialOrderingEnabled { get; }
    Task<IReadOnlyList<BareMetalOfferInfo>> ListBareMetalOffersAsync(string? datacenter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BareMetalServerInfo>> ListBareMetalServersAsync(CancellationToken cancellationToken = default);
    Task<BareMetalServerInfo?> FindBareMetalServerAsync(string serverName, CancellationToken cancellationToken = default);
    Task<BareMetalServerInfo> ProvisionBareMetalServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default);
    Task<ProviderServerStatus> GetBareMetalServerStatusAsync(string serviceName, CancellationToken cancellationToken = default);
    Task DeleteBareMetalServerAsync(string serviceName, CancellationToken cancellationToken = default);
}
