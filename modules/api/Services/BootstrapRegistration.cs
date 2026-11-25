using Selfrun.Common.Services;

namespace Selfrun.Api.Services;

public static class BootstrapRegistration
{
    public static IServiceCollection AddSelfrunBootstrap(this IServiceCollection services)
    {
        services.AddScoped<BootstrapService>();
        services.AddHttpClient<IBootstrapIdentityStore, BootstrapKratosIdentityStore>(client =>
                client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false
            })
            .RemoveAllLoggers();
        return services;
    }
}
