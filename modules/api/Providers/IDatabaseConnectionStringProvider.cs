namespace Selfrun.Api.Providers;

public interface IDatabaseConnectionStringProvider
{
    string GetProviderName();

    string? GetConnectionString();
}
