using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ProviderExtensionRegistryTests
{
    [Fact]
    public void DefaultRegistryExposesOnlyQualifiedBuiltInProvidersAndResolvesAliases()
    {
        var registry = VpsProviderRegistry.CreateDefault();

        var supported = registry.GetSupported();
        Assert.Equal(["hetzner", "digital-ocean", "vultr"], supported.Select(x => x.WireType).ToArray());
        Assert.All(supported, x => Assert.Equal(ProviderQualificationState.Qualified, x.QualificationState));

        Assert.True(registry.IsSupported(CloudProvider.Hetzner));
        Assert.True(registry.IsSupported("hetzner"));
        Assert.True(registry.IsSupported("digital-ocean"));
        Assert.True(registry.IsSupported("digitalocean"));
        Assert.True(registry.IsSupported("vultr"));
        Assert.False(registry.IsSupported("aws"));

        Assert.True(registry.TryParseBuiltIn("digitalocean", out var parsed));
        Assert.Equal(CloudProvider.DigitalOcean, parsed);
        Assert.Equal("digital-ocean", registry.GetWireType(CloudProvider.DigitalOcean));
    }

    [Fact]
    public void DraftGatedAndExperimentalProvidersAreHiddenUntilAllowlisted()
    {
        var aws = CreateSyntheticRegistration("aws", "AWS EC2", ProviderQualificationState.DraftGated, aliases: ["amazon-ec2"]);
        var azure = CreateSyntheticRegistration("azure", "Azure VMs", ProviderQualificationState.Experimental);
        var deprecated = CreateSyntheticRegistration("legacy-cloud", "Legacy Cloud", ProviderQualificationState.Deprecated);

        var defaultRegistry = new VpsProviderRegistry(
        [
            new HetznerProviderRegistration(Options.Create(new HetznerOptions())),
            aws,
            azure,
            deprecated
        ]);

        Assert.Equal(["hetzner", "aws", "azure", "legacy-cloud"], defaultRegistry.GetAll().Select(x => x.WireType).ToArray());
        Assert.Equal(["hetzner"], defaultRegistry.GetSupported().Select(x => x.WireType).ToArray());
        Assert.False(defaultRegistry.IsSupported("aws"));
        Assert.False(defaultRegistry.IsSupported("amazon-ec2"));
        Assert.False(defaultRegistry.IsSupported("azure"));
        Assert.False(defaultRegistry.IsSupported("legacy-cloud"));
        Assert.True(defaultRegistry.TryGetByWireType("aws", out _));
        Assert.False(defaultRegistry.TryParseBuiltIn("aws", out _));

        var allowlistedRegistry = new VpsProviderRegistry(
            [
                new HetznerProviderRegistration(Options.Create(new HetznerOptions())),
                aws,
                azure,
                deprecated
            ],
            Options.Create(new ProviderRegistryOptions
            {
                EnabledGatedProviders = new HashSet<string>(["aws", "legacy-cloud"], StringComparer.OrdinalIgnoreCase)
            }));

        Assert.Equal(["hetzner", "aws"], allowlistedRegistry.GetSupported().Select(x => x.WireType).ToArray());
        Assert.True(allowlistedRegistry.IsSupported("aws"));
        Assert.True(allowlistedRegistry.IsSupported("amazon-ec2"));
        Assert.False(allowlistedRegistry.IsSupported("azure"));
        Assert.False(allowlistedRegistry.IsSupported("legacy-cloud"));
    }

    [Fact]
    public void RegistryRejectsDuplicateWireTypesAliasesAndBuiltInBindings()
    {
        var first = CreateSyntheticRegistration("scaleway", "Scaleway", ProviderQualificationState.DraftGated, aliases: ["scw"]);
        var dupWire = CreateSyntheticRegistration("SCALEWAY", "Scaleway Dup", ProviderQualificationState.DraftGated);
        var dupAlias = CreateSyntheticRegistration("other", "Other", ProviderQualificationState.DraftGated, aliases: ["scw"]);

        Assert.Throws<InvalidOperationException>(() => new VpsProviderRegistry([first, dupWire]));
        Assert.Throws<InvalidOperationException>(() => new VpsProviderRegistry([first, dupAlias]));
        Assert.Throws<InvalidOperationException>(() => new VpsProviderRegistry(
        [
            new HetznerProviderRegistration(Options.Create(new HetznerOptions())),
            new HetznerProviderRegistration(Options.Create(new HetznerOptions()))
        ]));
    }

    [Fact]
    public void DiServiceCollectionExtensionRegistersBuiltInsAndAdditionalGatedProviders()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMinicloudProviders(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddVpsProvider(CreateSyntheticRegistration("ovh", "OVHcloud", ProviderQualificationState.DraftGated));
        services.AddVpsProvider(CreateSyntheticRegistration("linode", "Linode", ProviderQualificationState.DraftGated));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IVpsProviderRegistry>();

        Assert.Equal(["hetzner", "digital-ocean", "vultr"], registry.GetSupported().Select(x => x.WireType).ToArray());
        Assert.Contains(registry.GetAll(), x => x.WireType == "ovh");
        Assert.Contains(registry.GetAll(), x => x.WireType == "linode");
        Assert.Throws<ProviderCatalogException>(() => registry.GetRequiredByWireType("unknown-provider"));
    }

    internal static IVpsProviderRegistration CreateSyntheticRegistration(
        string wireType,
        string displayName,
        ProviderQualificationState state,
        IReadOnlyList<string>? aliases = null,
        ProviderCapabilities? capabilities = null,
        ProviderPlacementContract? placement = null,
        ProviderCredentialContract? credentials = null,
        Func<ProviderClientCreationContext, IVpsProviderClient>? clientFactory = null)
    {
        var effectiveCapabilities = capabilities ?? new ProviderCapabilities(
            ComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedArchitectures: ["linux/amd64", "linux/arm64"],
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportsSshKeyInventory: true,
            SupportsCloudInit: true,
            SupportsDurableVolumes: true,
            SupportsVolumeResize: false,
            SupportsSnapshots: false,
            SupportsPrivateNetworks: true,
            SupportsPublicIpv4: true,
            SupportsPublicIpv6: true,
            SupportsLabelsOrTags: true,
            SupportsExactLabelOwnershipVerification: true,
            SupportsScopeIdentityDiscovery: true,
            SupportsQuotaDiscovery: true,
            SupportsCatalogDiscovery: true,
            SupportsAsynchronousOperations: true,
            SupportsIdempotencyTokens: true);

        var effectivePlacement = placement ?? new ProviderPlacementContract(
            LocationGranularity: effectiveCapabilities.LocationGranularity,
            SupportedComputeFamilies: effectiveCapabilities.ComputeFamilies,
            SupportedBuildPlatforms: effectiveCapabilities.SupportedArchitectures,
            ImageFormat: ProviderImageFormat.SlugOrId,
            DefaultImage: "ubuntu-24.04");

        var effectiveCredentials = credentials ?? new ProviderCredentialContract(
            Format: ProviderCredentialFormat.ApiToken,
            ScopeBoundary: ProviderScopeBoundaryKind.Project,
            EnvironmentVariableNames: [$"{wireType.ToUpperInvariant().Replace('-', '_')}_TOKEN"],
            NonSecretSettings: [],
            SupportsReadOnlyScopeIdentity: effectiveCapabilities.SupportsScopeIdentityDiscovery);

        var descriptor = new ProviderDescriptor(
            WireType: wireType,
            Aliases: aliases ?? [],
            DisplayName: displayName,
            BuiltInProvider: null,
            QualificationState: state,
            Capabilities: effectiveCapabilities,
            Placement: effectivePlacement,
            Credentials: effectiveCredentials,
            RetryPolicy: ProviderRetryPolicy.Default);

        return new SyntheticProviderRegistration(
            descriptor,
            clientFactory ?? (_ => new SyntheticVpsProviderClient()));
    }

    private sealed class SyntheticProviderRegistration(
        ProviderDescriptor descriptor,
        Func<ProviderClientCreationContext, IVpsProviderClient> factory) : IVpsProviderRegistration
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;
        public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;
        public IVpsProviderClient CreateClient(ProviderClientCreationContext context) => factory(context);
    }
}
