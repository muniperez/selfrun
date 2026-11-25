using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers;

public interface IVpsProviderSshKeyClient
{
    Task<IReadOnlyList<ProviderSshKeyInfo>> ListSshKeysAsync(CancellationToken cancellationToken = default);
    Task<ProviderSshKeyInfo> RegisterSshKeyAsync(string name, string publicKey, CancellationToken cancellationToken = default);
}
