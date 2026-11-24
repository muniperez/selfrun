using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Common.Providers.Aws;
using Minicloud.Common.Providers.Azure;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.GoogleCloud;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Linode;
using Minicloud.Common.Providers.Ovhcloud;
using Minicloud.Common.Providers.Scaleway;
using Minicloud.Common.Providers.Vultr;
using System.Net.Http;

namespace Minicloud.Common.Providers;

public static class ProviderServiceCollectionExtensions
{
    public delegate IVpsProviderClient VpsProviderClientResolver(CloudProvider provider);

    public static IServiceCollection AddMinicloudProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProviderRegistryOptions>(configuration.GetSection(ProviderRegistryOptions.SectionName));
        services.Configure<HetznerOptions>(configuration.GetSection(HetznerOptions.SectionName));
        services.Configure<DigitalOceanOptions>(configuration.GetSection(DigitalOceanOptions.SectionName));
        services.Configure<VultrOptions>(configuration.GetSection(VultrOptions.SectionName));
        services.Configure<AwsOptions>(configuration.GetSection(AwsOptions.SectionName));
        services.Configure<AzureOptions>(configuration.GetSection(AzureOptions.SectionName));
        services.Configure<GoogleCloudOptions>(configuration.GetSection(GoogleCloudOptions.SectionName));
        services.Configure<OvhcloudOptions>(configuration.GetSection(OvhcloudOptions.SectionName));
        services.Configure<LinodeOptions>(configuration.GetSection(LinodeOptions.SectionName));
        services.Configure<ScalewayOptions>(configuration.GetSection(ScalewayOptions.SectionName));
        services.Configure<CloudflareOptions>(configuration.GetSection(CloudflareOptions.SectionName));

        services.TryAddTransient<ResilientHttpRetryHandler>();
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddHttpClient(ProviderAccountClientFactory.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ProviderAccountClientFactory>();

        // Built-in qualified provider registrations
        services.AddVpsProvider<HetznerProviderRegistration>();
        services.AddVpsProvider<DigitalOceanProviderRegistration>();
        services.AddVpsProvider<VultrProviderRegistration>();

        // Built-in experimental provider registrations (disabled by default until live-qualified)
        services.AddVpsProvider<AwsProviderRegistration>();
        services.AddVpsProvider<AzureProviderRegistration>();
        services.AddVpsProvider<GoogleCloudProviderRegistration>();
        services.AddVpsProvider<OvhcloudProviderRegistration>();
        services.AddVpsProvider<LinodeProviderRegistration>();
        services.AddVpsProvider<ScalewayProviderRegistration>();

        services.TryAddSingleton<IVpsProviderRegistry, VpsProviderRegistry>();

        // Hetzner
        services.AddHttpClient<HetznerClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, HetznerClient>(CloudProvider.Hetzner);

        // DigitalOcean
        services.AddHttpClient<DigitalOceanClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, DigitalOceanClient>(CloudProvider.DigitalOcean);

        // Vultr
        services.AddHttpClient<VultrClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, VultrClient>(CloudProvider.Vultr);

        // AWS
        services.AddHttpClient<AwsClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, AwsClient>(CloudProvider.Aws);

        // Microsoft Azure
        services.AddHttpClient<AzureClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, AzureClient>(CloudProvider.Azure);

        // Google Cloud Platform
        services.AddHttpClient<GoogleCloudClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, GoogleCloudClient>(CloudProvider.GoogleCloud);

        // OVHcloud (Public Cloud + gated Bare Metal)
        services.AddHttpClient<OvhcloudClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, OvhcloudClient>(CloudProvider.Ovhcloud);
        services.AddScoped<IBareMetalProviderClient>(sp => sp.GetRequiredService<OvhcloudClient>());

        // Akamai Connected Cloud (Linode)
        services.AddHttpClient<LinodeClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, LinodeClient>(CloudProvider.Linode);

        // Scaleway
        services.AddHttpClient<ScalewayClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, ScalewayClient>(CloudProvider.Scaleway);

        services.AddHttpClient<ICustomDomainDnsResolver, CustomDomainDnsResolver>(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<Minicloud.Common.Services.CustomDomainVerificationService>();
        services.AddScoped<Minicloud.Common.Services.CustomDomainRegistrationService>();

        // Cloudflare DNS
        services.AddHttpClient<ICloudflareDnsClient, CloudflareDnsClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();

        // Resolver delegate
        services.AddScoped<VpsProviderClientResolver>(sp => provider => provider switch
        {
            CloudProvider.Hetzner => sp.GetRequiredService<HetznerClient>(),
            CloudProvider.DigitalOcean => sp.GetRequiredService<DigitalOceanClient>(),
            CloudProvider.Vultr => sp.GetRequiredService<VultrClient>(),
            CloudProvider.Aws => sp.GetRequiredService<AwsClient>(),
            CloudProvider.Azure => sp.GetRequiredService<AzureClient>(),
            CloudProvider.GoogleCloud => sp.GetRequiredService<GoogleCloudClient>(),
            CloudProvider.Ovhcloud => sp.GetRequiredService<OvhcloudClient>(),
            CloudProvider.Linode => sp.GetRequiredService<LinodeClient>(),
            CloudProvider.Scaleway => sp.GetRequiredService<ScalewayClient>(),
            _ => sp.GetKeyedService<IVpsProviderClient>(provider)
                ?? throw new NotSupportedException($"VPS provider {provider} is not supported.")
        });

        return services;
    }

    public static IServiceCollection AddVpsProvider<TRegistration>(this IServiceCollection services)
        where TRegistration : class, IVpsProviderRegistration
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVpsProviderRegistration, TRegistration>());
        return services;
    }

    public static IServiceCollection AddVpsProvider(this IServiceCollection services, IVpsProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        services.AddSingleton<IVpsProviderRegistration>(registration);
        return services;
    }
}
