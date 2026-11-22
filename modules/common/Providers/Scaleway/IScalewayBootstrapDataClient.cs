namespace Minicloud.Common.Providers.Scaleway;

public interface IScalewayBootstrapDataClient
{
    Task SetCloudInitDataAsync(string serverId, string cloudInit, CancellationToken ct);
}
