using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;

namespace Minicloud.Common.Providers.Extensibility;

public sealed record ProviderClientCreationContext(
    ProviderAccount Account,
    string Credential,
    HttpClient HttpClient,
    ILoggerFactory LoggerFactory);

public sealed record ProviderDescriptor(
    string WireType,
    IReadOnlyList<string> Aliases,
    string DisplayName,
    CloudProvider? BuiltInProvider,
    ProviderQualificationState QualificationState,
    ProviderCapabilities Capabilities,
    ProviderPlacementContract Placement,
    ProviderCredentialContract Credentials,
    ProviderRetryPolicy RetryPolicy);

public interface IVpsProviderRegistration
{
    ProviderDescriptor Descriptor { get; }
    IProviderErrorClassifier ErrorClassifier { get; }
    IVpsProviderClient CreateClient(ProviderClientCreationContext context);
}

public sealed class ProviderRegistryOptions
{
    public const string SectionName = "Providers:Registry";

    /// <summary>
    /// Optional explicit allowlist of gated/experimental provider wire types enabled
    /// for local testing or disposable qualification runs. Empty in production so
    /// unqualified providers are never advertised or selectable.
    /// </summary>
    public HashSet<string> EnabledGatedProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IVpsProviderRegistry
{
    IReadOnlyList<ProviderDescriptor> GetAll();
    IReadOnlyList<ProviderDescriptor> GetSupported();
    bool TryGet(CloudProvider provider, out IVpsProviderRegistration registration);
    bool TryGetByWireType(string wireType, out IVpsProviderRegistration registration);
    bool IsSupported(CloudProvider provider);
    bool IsSupported(string wireType);
    IVpsProviderRegistration GetRequired(CloudProvider provider);
    IVpsProviderRegistration GetRequiredByWireType(string wireType);
    bool TryParseBuiltIn(string wireType, out CloudProvider provider);
    string GetWireType(CloudProvider provider);
}

public sealed class VpsProviderRegistry : IVpsProviderRegistry
{
    private readonly IReadOnlyList<IVpsProviderRegistration> _registrations;
    private readonly Dictionary<CloudProvider, IVpsProviderRegistration> _byEnum = [];
    private readonly Dictionary<string, IVpsProviderRegistration> _byWireType = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledGated;

    public VpsProviderRegistry(
        IEnumerable<IVpsProviderRegistration> registrations,
        IOptions<ProviderRegistryOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _enabledGated = new HashSet<string>(
            options?.Value.EnabledGatedProviders ?? [],
            StringComparer.OrdinalIgnoreCase);

        var ordered = new List<IVpsProviderRegistration>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var descriptor = registration.Descriptor ?? throw new InvalidOperationException("Provider registration descriptor is required.");
            if (string.IsNullOrWhiteSpace(descriptor.WireType))
            {
                throw new InvalidOperationException("Provider descriptor WireType is required.");
            }

            if (!_byWireType.TryAdd(descriptor.WireType.Trim(), registration))
            {
                throw new InvalidOperationException($"Duplicate VPS provider registration for wire type '{descriptor.WireType}'.");
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                if (!_byWireType.TryAdd(alias.Trim(), registration))
                {
                    throw new InvalidOperationException($"Duplicate VPS provider alias '{alias}' for '{descriptor.WireType}'.");
                }
            }

            if (descriptor.BuiltInProvider is { } builtIn)
            {
                if (!_byEnum.TryAdd(builtIn, registration))
                {
                    throw new InvalidOperationException($"Duplicate VPS provider registration for enum value '{builtIn}'.");
                }
            }

            ordered.Add(registration);
        }

        _registrations = ordered;
    }

    public IReadOnlyList<ProviderDescriptor> GetAll() =>
        _registrations.Select(x => x.Descriptor).ToArray();

    public IReadOnlyList<ProviderDescriptor> GetSupported() =>
        _registrations
            .Where(IsRegistrationSupported)
            .Select(x => x.Descriptor)
            .ToArray();

    public bool TryGet(CloudProvider provider, out IVpsProviderRegistration registration) =>
        _byEnum.TryGetValue(provider, out registration!);

    public bool TryGetByWireType(string wireType, out IVpsProviderRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(wireType))
        {
            registration = null!;
            return false;
        }

        return _byWireType.TryGetValue(wireType.Trim(), out registration!);
    }

    public bool IsSupported(CloudProvider provider) =>
        _byEnum.TryGetValue(provider, out var registration) && IsRegistrationSupported(registration);

    public bool IsSupported(string wireType) =>
        TryGetByWireType(wireType, out var registration) && IsRegistrationSupported(registration);

    public IVpsProviderRegistration GetRequired(CloudProvider provider) =>
        TryGet(provider, out var registration)
            ? registration
            : throw new ProviderCatalogException("provider_type_unsupported", $"VPS provider '{provider}' is not registered.");

    public IVpsProviderRegistration GetRequiredByWireType(string wireType) =>
        TryGetByWireType(wireType, out var registration)
            ? registration
            : throw new ProviderCatalogException("provider_type_unsupported", $"VPS provider '{wireType}' is not registered.");

    public bool TryParseBuiltIn(string wireType, out CloudProvider provider)
    {
        if (TryGetByWireType(wireType, out var registration) &&
            IsRegistrationSupported(registration) &&
            registration.Descriptor.BuiltInProvider is { } builtIn)
        {
            provider = builtIn;
            return true;
        }

        provider = (CloudProvider)(-1);
        return false;
    }

    public string GetWireType(CloudProvider provider) =>
        GetRequired(provider).Descriptor.WireType;

    private bool IsRegistrationSupported(IVpsProviderRegistration registration)
    {
        var descriptor = registration.Descriptor;
        if (descriptor.QualificationState == ProviderQualificationState.Qualified)
        {
            return true;
        }

        if (descriptor.QualificationState is ProviderQualificationState.DraftGated or ProviderQualificationState.Experimental)
        {
            return _enabledGated.Contains(descriptor.WireType)
                || descriptor.Aliases.Any(alias => _enabledGated.Contains(alias));
        }

        return false;
    }

    public static VpsProviderRegistry CreateDefault(
        IOptions<HetznerOptions>? hetzner = null,
        IOptions<DigitalOceanOptions>? digitalOcean = null,
        IOptions<VultrOptions>? vultr = null,
        IOptions<ProviderRegistryOptions>? options = null)
    {
        return new VpsProviderRegistry(
        [
            new HetznerProviderRegistration(hetzner ?? Options.Create(new HetznerOptions())),
            new DigitalOceanProviderRegistration(digitalOcean ?? Options.Create(new DigitalOceanOptions())),
            new VultrProviderRegistration(vultr ?? Options.Create(new VultrOptions()))
        ], options);
    }
}

public static class BuiltInProviderDescriptors
{
    public static ProviderDescriptor Hetzner { get; } = new(
        WireType: "hetzner",
        Aliases: [],
        DisplayName: "Hetzner",
        BuiltInProvider: CloudProvider.Hetzner,
        QualificationState: ProviderQualificationState.Qualified,
        Capabilities: new ProviderCapabilities(
            ComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedArchitectures: ["linux/amd64", "linux/arm64"],
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportsSshKeyInventory: true,
            SupportsCloudInit: true,
            SupportsDurableVolumes: true,
            SupportsVolumeResize: true,
            SupportsSnapshots: false,
            SupportsPrivateNetworks: true,
            SupportsPublicIpv4: true,
            SupportsPublicIpv6: true,
            SupportsLabelsOrTags: true,
            SupportsExactLabelOwnershipVerification: true,
            SupportsScopeIdentityDiscovery: false,
            SupportsQuotaDiscovery: false,
            SupportsCatalogDiscovery: false,
            SupportsAsynchronousOperations: true,
            SupportsIdempotencyTokens: false),
        Placement: new ProviderPlacementContract(
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportedComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedBuildPlatforms: ["linux/amd64", "linux/arm64"],
            ImageFormat: ProviderImageFormat.SlugOrId,
            DefaultImage: "ubuntu-24.04",
            DefaultOsId: null,
            ImageRequired: false,
            LocationExample: "nbg1",
            SkuExample: "cx23"),
        Credentials: new ProviderCredentialContract(
            Format: ProviderCredentialFormat.ApiToken,
            ScopeBoundary: ProviderScopeBoundaryKind.Project,
            EnvironmentVariableNames: ["HCLOUD_TOKEN", "HETZNER_API_TOKEN"],
            NonSecretSettings: [],
            SupportsReadOnlyScopeIdentity: false),
        RetryPolicy: ProviderRetryPolicy.Default);

    public static ProviderDescriptor DigitalOcean { get; } = new(
        WireType: "digital-ocean",
        Aliases: ["digitalocean"],
        DisplayName: "DigitalOcean",
        BuiltInProvider: CloudProvider.DigitalOcean,
        QualificationState: ProviderQualificationState.Qualified,
        Capabilities: new ProviderCapabilities(
            ComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedArchitectures: ["linux/amd64", "linux/arm64"],
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportsSshKeyInventory: true,
            SupportsCloudInit: true,
            SupportsDurableVolumes: false,
            SupportsVolumeResize: false,
            SupportsSnapshots: false,
            SupportsPrivateNetworks: false,
            SupportsPublicIpv4: true,
            SupportsPublicIpv6: true,
            SupportsLabelsOrTags: true,
            SupportsExactLabelOwnershipVerification: false,
            SupportsScopeIdentityDiscovery: false,
            SupportsQuotaDiscovery: false,
            SupportsCatalogDiscovery: false,
            SupportsAsynchronousOperations: false,
            SupportsIdempotencyTokens: false),
        Placement: new ProviderPlacementContract(
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportedComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedBuildPlatforms: ["linux/amd64", "linux/arm64"],
            ImageFormat: ProviderImageFormat.SlugOrId,
            DefaultImage: "ubuntu-24-04-x64",
            DefaultOsId: null,
            ImageRequired: false,
            LocationExample: "nyc3",
            SkuExample: "s-1vcpu-1gb"),
        Credentials: new ProviderCredentialContract(
            Format: ProviderCredentialFormat.ApiToken,
            ScopeBoundary: ProviderScopeBoundaryKind.Project,
            EnvironmentVariableNames: ["DIGITALOCEAN_ACCESS_TOKEN", "DIGITALOCEAN_TOKEN"],
            NonSecretSettings:
            [
                new ProviderSettingField("projectId", "Project ID", Required: false, ImmutableAfterReservation: true,
                    Description: "Optional DigitalOcean project to which created droplets are assigned.")
            ],
            SupportsReadOnlyScopeIdentity: false),
        RetryPolicy: ProviderRetryPolicy.Default);

    public static ProviderDescriptor Vultr { get; } = new(
        WireType: "vultr",
        Aliases: [],
        DisplayName: "Vultr",
        BuiltInProvider: CloudProvider.Vultr,
        QualificationState: ProviderQualificationState.Qualified,
        Capabilities: new ProviderCapabilities(
            ComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedArchitectures: ["linux/amd64", "linux/arm64"],
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportsSshKeyInventory: true,
            SupportsCloudInit: true,
            SupportsDurableVolumes: false,
            SupportsVolumeResize: false,
            SupportsSnapshots: false,
            SupportsPrivateNetworks: false,
            SupportsPublicIpv4: true,
            SupportsPublicIpv6: true,
            SupportsLabelsOrTags: true,
            SupportsExactLabelOwnershipVerification: false,
            SupportsScopeIdentityDiscovery: false,
            SupportsQuotaDiscovery: false,
            SupportsCatalogDiscovery: false,
            SupportsAsynchronousOperations: false,
            SupportsIdempotencyTokens: false),
        Placement: new ProviderPlacementContract(
            LocationGranularity: ProviderLocationGranularity.Region,
            SupportedComputeFamilies: ProviderComputeFamily.VirtualServer,
            SupportedBuildPlatforms: ["linux/amd64", "linux/arm64"],
            ImageFormat: ProviderImageFormat.PositiveIntegerId,
            DefaultImage: null,
            DefaultOsId: 2284,
            ImageRequired: false,
            LocationExample: "ewr",
            SkuExample: "vc2-1c-1gb"),
        Credentials: new ProviderCredentialContract(
            Format: ProviderCredentialFormat.ApiToken,
            ScopeBoundary: ProviderScopeBoundaryKind.Account,
            EnvironmentVariableNames: ["VULTR_API_KEY", "VULTR_KEY"],
            NonSecretSettings:
            [
                new ProviderSettingField("defaultOsId", "Default OS ID", Required: false, ImmutableAfterReservation: false,
                    Description: "Optional numeric Vultr OS ID used when a plan omits an image override.")
            ],
            SupportsReadOnlyScopeIdentity: false),
        RetryPolicy: ProviderRetryPolicy.Default);

    public static ProviderDescriptor ForBuiltIn(CloudProvider provider) => provider switch
    {
        CloudProvider.Hetzner => Hetzner,
        CloudProvider.DigitalOcean => DigitalOcean,
        CloudProvider.Vultr => Vultr,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

public sealed class HetznerProviderRegistration(IOptions<HetznerOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Hetzner;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context) =>
        new HetznerClient(
            context.HttpClient,
            new HetznerOptions { ApiToken = context.Credential, BaseUrl = options.Value.BaseUrl },
            context.LoggerFactory.CreateLogger<HetznerClient>());
}

public sealed class DigitalOceanProviderRegistration(IOptions<DigitalOceanOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.DigitalOcean;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context) =>
        new DigitalOceanClient(
            context.HttpClient,
            new DigitalOceanOptions
            {
                ApiToken = context.Credential,
                BaseUrl = options.Value.BaseUrl,
                ProjectId = context.Account.ProjectId
            },
            context.LoggerFactory.CreateLogger<DigitalOceanClient>());
}

public sealed class VultrProviderRegistration(IOptions<VultrOptions> options) : IVpsProviderRegistration
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Vultr;
    public IProviderErrorClassifier ErrorClassifier => DefaultProviderErrorClassifier.Instance;

    public IVpsProviderClient CreateClient(ProviderClientCreationContext context) =>
        new VultrClient(
            context.HttpClient,
            new VultrOptions
            {
                ApiKey = context.Credential,
                BaseUrl = options.Value.BaseUrl,
                DefaultOsId = context.Account.DefaultOsId
            },
            context.LoggerFactory.CreateLogger<VultrClient>());
}
