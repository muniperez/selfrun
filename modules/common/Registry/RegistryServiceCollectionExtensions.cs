using Microsoft.Extensions.DependencyInjection;
using Selfrun.Common.Services;

namespace Selfrun.Common.Registry;

public static class RegistryServiceCollectionExtensions
{
    public static IServiceCollection AddSelfrunRegistry(this IServiceCollection services, Action<RegistryOptions> configure, Action<ProviderPlanOptions> configurePlans)
    {
        services.Configure(configure);
        services.Configure(configurePlans);
        services.AddSingleton<RegistryTokens>();
        services.AddScoped<ImageBuildTargetService>();
        services.AddScoped<ImagePublicationLifecycleService>();
        services.AddScoped<ImagePublicationService>();
        services.AddScoped<RegistryMaintenanceService>();
        services.AddHttpClient<RegistryClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
