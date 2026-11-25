using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;
using Xunit;

namespace Selfrun.Worker.Tests.Providers;

public sealed class ProviderCapabilityAndPlacementContractTests
{
    [Fact]
    public void BuiltInDescriptorsPreserveExistingCapabilitiesAndDefaults()
    {
        var hetzner = BuiltInProviderDescriptors.Hetzner;
        Assert.True(hetzner.Capabilities.SupportsVirtualServers);
        Assert.False(hetzner.Capabilities.SupportsBareMetal);
        Assert.True(hetzner.Capabilities.SupportsDurableVolumes);
        Assert.True(hetzner.Capabilities.SupportsVolumeResize);
        Assert.True(hetzner.Capabilities.SupportsPrivateNetworks);
        Assert.True(hetzner.Capabilities.SupportsExactLabelOwnershipVerification);
        Assert.Equal("ubuntu-24.04", hetzner.Placement.DefaultImage);

        var digitalOcean = BuiltInProviderDescriptors.DigitalOcean;
        Assert.False(digitalOcean.Capabilities.SupportsDurableVolumes);
        Assert.False(digitalOcean.Capabilities.SupportsPrivateNetworks);
        Assert.False(digitalOcean.Capabilities.SupportsExactLabelOwnershipVerification);
        Assert.Equal("ubuntu-24-04-x64", digitalOcean.Placement.DefaultImage);
        Assert.Contains(digitalOcean.Credentials.NonSecretSettings, x => x.Key == "projectId" && x.ImmutableAfterReservation);

        var vultr = BuiltInProviderDescriptors.Vultr;
        Assert.False(vultr.Capabilities.SupportsDurableVolumes);
        Assert.Equal(ProviderImageFormat.PositiveIntegerId, vultr.Placement.ImageFormat);
        Assert.Null(vultr.Placement.DefaultImage);
        Assert.Equal(2284, vultr.Placement.DefaultOsId);
    }

    [Theory]
    [InlineData("ami-0123456789abcdef0", true)]
    [InlineData("resolve:ssm:/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id", true)]
    [InlineData("ubuntu-24.04", false)]
    public void AwsAmiPlacementContractValidatesAmiOrSsmAliasFormat(string image, bool expectedValid)
    {
        var aws = ProviderExtensionRegistryTests.CreateSyntheticRegistration(
            "aws",
            "AWS EC2",
            ProviderQualificationState.DraftGated,
            placement: new ProviderPlacementContract(
                ProviderLocationGranularity.Region,
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64", "linux/arm64"],
                ProviderImageFormat.AmiOrAlias,
                ImageRequired: true)).Descriptor;

        var result = ProviderPlacementValidator.Validate(
            aws,
            new ProviderPlacement("aws", "acc_1", "us-east-1", "t3.small", image, "linux/amd64"));

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid) Assert.Equal("plan_mapping_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("Canonical:ubuntu-24_04-lts:server:latest", true)]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Compute/images/img", true)]
    [InlineData("ubuntu-24.04", false)]
    public void AzurePlacementContractValidatesUrnOrResourceId(string image, bool expectedValid)
    {
        var azure = ProviderExtensionRegistryTests.CreateSyntheticRegistration(
            "azure",
            "Azure VMs",
            ProviderQualificationState.DraftGated,
            placement: new ProviderPlacementContract(
                ProviderLocationGranularity.Region,
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64", "linux/arm64"],
                ProviderImageFormat.UrnOrResourceId,
                ImageRequired: true)).Descriptor;

        var result = ProviderPlacementValidator.Validate(
            azure,
            new ProviderPlacement("azure", "acc_2", "eastus", "Standard_B2s", image, "linux/amd64"));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData("projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-amd64", true)]
    [InlineData("ubuntu-2404-lts", false)]
    public void GcpPlacementContractValidatesZoneImageFamilyFormat(string image, bool expectedValid)
    {
        var capabilities = new ProviderCapabilities(
            ProviderComputeFamily.VirtualServer,
            ["linux/amd64", "linux/arm64"],
            ProviderLocationGranularity.Zone,
            true, true, true, true, false, true, true, true, true, true, true, true, true, true, true);
        var gcp = ProviderExtensionRegistryTests.CreateSyntheticRegistration(
            "gcp",
            "GCP Compute Engine",
            ProviderQualificationState.DraftGated,
            capabilities: capabilities,
            placement: new ProviderPlacementContract(
                ProviderLocationGranularity.Zone,
                ProviderComputeFamily.VirtualServer,
                ["linux/amd64", "linux/arm64"],
                ProviderImageFormat.ImageFamilyOrSelfLink,
                ImageRequired: true)).Descriptor;

        var result = ProviderPlacementValidator.Validate(
            gcp,
            new ProviderPlacement("gcp", "acc_3", "us-central1-a", "e2-small", image, "linux/amd64"));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void BareMetalComputeFamilyIsGatedByProviderCapabilities()
    {
        var hetznerResult = ProviderPlacementValidator.Validate(
            BuiltInProviderDescriptors.Hetzner,
            new ProviderPlacement("hetzner", "acc_h", "nbg1", "cx23", "ubuntu-24.04", "linux/amd64", ProviderComputeFamily.BareMetal));
        Assert.False(hetznerResult.IsValid);
        Assert.Equal("plan_compute_family_unsupported", hetznerResult.ErrorCode);

        var ovhCapabilities = new ProviderCapabilities(
            ProviderComputeFamily.VirtualServer | ProviderComputeFamily.BareMetal,
            ["linux/amd64"],
            ProviderLocationGranularity.Datacenter,
            SupportsSshKeyInventory: true,
            SupportsCloudInit: false,
            SupportsDurableVolumes: false,
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
            SupportsIdempotencyTokens: false);
        var ovh = ProviderExtensionRegistryTests.CreateSyntheticRegistration(
            "ovh-baremetal",
            "OVHcloud Bare Metal",
            ProviderQualificationState.DraftGated,
            capabilities: ovhCapabilities,
            placement: new ProviderPlacementContract(
                ProviderLocationGranularity.Datacenter,
                ProviderComputeFamily.BareMetal,
                ["linux/amd64"],
                ProviderImageFormat.SlugOrId,
                DefaultImage: "ubuntu2404-server_64")).Descriptor;

        Assert.Equal(["virtual-server", "bare-metal"], ovhCapabilities.ComputeFamilyWireNames());

        var validBareMetal = ProviderPlacementValidator.Validate(
            ovh,
            new ProviderPlacement("ovh-baremetal", "acc_ovh", "rbx", "rise-1", "ubuntu2404-server_64", "linux/amd64", ProviderComputeFamily.BareMetal));
        Assert.True(validBareMetal.IsValid);

        var unsupportedArm = ProviderPlacementValidator.Validate(
            ovh,
            new ProviderPlacement("ovh-baremetal", "acc_ovh", "rbx", "rise-1", "ubuntu2404-server_64", "linux/arm64", ProviderComputeFamily.BareMetal));
        Assert.False(unsupportedArm.IsValid);
    }

    [Fact]
    public void PlacementEquivalenceComparesAllImmutableFieldsIncludingMetadata()
    {
        var left = new ProviderPlacement("aws", "acc_1", "us-east-1", "t3.small", "ami-1234", "linux/amd64",
            ProviderComputeFamily.VirtualServer, new Dictionary<string, string> { ["subnetId"] = "subnet-1" });
        var same = new ProviderPlacement("AWS", "acc_1", "us-east-1", "t3.small", "ami-1234", "linux/amd64",
            ProviderComputeFamily.VirtualServer, new Dictionary<string, string> { ["subnetId"] = "subnet-1" });
        var diffMeta = left with { PlacementMetadata = new Dictionary<string, string> { ["subnetId"] = "subnet-2" } };
        var diffFamily = left with { ComputeFamily = ProviderComputeFamily.BareMetal };

        Assert.True(ProviderPlacementValidator.IsEquivalent(left, same));
        Assert.False(ProviderPlacementValidator.IsEquivalent(left, diffMeta));
        Assert.False(ProviderPlacementValidator.IsEquivalent(left, diffFamily));
    }

    [Fact]
    public void CredentialContractAndScopeIdentityEnforceWriteOnlyValidationAndScopeMatching()
    {
        var gcpContract = new ProviderCredentialContract(
            Format: ProviderCredentialFormat.KeyPairOrStructuredJson,
            ScopeBoundary: ProviderScopeBoundaryKind.ServiceAndProject,
            EnvironmentVariableNames: ["GOOGLE_APPLICATION_CREDENTIALS_JSON"],
            NonSecretSettings: [new ProviderSettingField("projectId", "GCP Project ID", Required: true, ImmutableAfterReservation: true)],
            SupportsReadOnlyScopeIdentity: true,
            CredentialFormatValidator: (_, cred) =>
                cred.TrimStart().StartsWith('{')
                    ? ProviderCredentialValidationResult.Valid()
                    : ProviderCredentialValidationResult.Invalid("provider_credential_invalid", "Expected structured JSON credential."));

        var account = new ProviderAccount { Id = "acc_gcp", OrganizationId = "org_1", Name = "GCP", ProviderType = CloudProvider.Hetzner };
        Assert.False(gcpContract.ValidateCredential(account, "").IsValid);
        Assert.False(gcpContract.ValidateCredential(account, "bad\ncontrol").IsValid);
        Assert.False(gcpContract.ValidateCredential(account, "plain-token").IsValid);
        Assert.True(gcpContract.ValidateCredential(account, """{"type":"service_account","project_id":"demo"}""").IsValid);

        var scopeA = new ProviderScopeIdentity("azure", "subscription", "sub-1", SecondaryScopeId: "tenant-1", ObservedPrincipal: "sp-a");
        var scopeASame = new ProviderScopeIdentity("azure", "subscription", "sub-1", SecondaryScopeId: "tenant-1", ObservedPrincipal: "sp-b");
        var scopeDifferentSub = new ProviderScopeIdentity("azure", "subscription", "sub-2", SecondaryScopeId: "tenant-1");
        var scopeDifferentTenant = new ProviderScopeIdentity("azure", "subscription", "sub-1", SecondaryScopeId: "tenant-2");

        Assert.True(scopeA.MatchesScope(scopeASame));
        Assert.False(scopeA.MatchesScope(scopeDifferentSub));
        Assert.False(scopeA.MatchesScope(scopeDifferentTenant));
    }

    [Fact]
    public void OwnershipVerifierEnforcesExactLabelsWhenSupportedByDescriptor()
    {
        var ownership = new ProviderOwnershipEvidence("acme", "web-api", "production");
        var expectedName = ownership.ManagedServerName;

        var matchingServer = new ProviderServerInfo("1", expectedName, "203.0.113.10", "running", Labels: ownership.ToLabels());
        var wrongLabelsServer = new ProviderServerInfo("2", expectedName, "203.0.113.11", "running",
            Labels: new Dictionary<string, string> { ["managed-by"] = "other" });
        var wrongNameServer = new ProviderServerInfo("3", "unrelated-host", "203.0.113.12", "running", Labels: ownership.ToLabels());

        Assert.True(ProviderOwnershipVerifier.VerifyDiscoveredServer(BuiltInProviderDescriptors.Hetzner, matchingServer, expectedName, ownership));
        Assert.False(ProviderOwnershipVerifier.VerifyDiscoveredServer(BuiltInProviderDescriptors.Hetzner, wrongLabelsServer, expectedName, ownership));
        Assert.False(ProviderOwnershipVerifier.VerifyDiscoveredServer(BuiltInProviderDescriptors.Hetzner, wrongNameServer, expectedName, ownership));

        // DigitalOcean / Vultr do not declare exact key-value label ownership verification; exact server name still required.
        Assert.True(ProviderOwnershipVerifier.VerifyDiscoveredServer(BuiltInProviderDescriptors.Vultr, wrongLabelsServer, expectedName, ownership));
        Assert.False(ProviderOwnershipVerifier.VerifyDiscoveredServer(BuiltInProviderDescriptors.Vultr, wrongNameServer, expectedName, ownership));
    }

    [Fact]
    public async Task CatalogDiscoveryContractSupportsLocationsOffersBareMetalLeadTimeImagesAndQuotas()
    {
        IVpsProviderCatalogDiscoveryClient discovery = new SyntheticCatalogDiscoveryClient();

        var locations = await discovery.ListLocationsAsync();
        Assert.Contains(locations, x => x.Code == "rbx" && x.Granularity == ProviderLocationGranularity.Datacenter);

        var bareMetalOffers = await discovery.ListComputeOffersAsync("rbx", ProviderComputeFamily.BareMetal);
        var offer = Assert.Single(bareMetalOffers);
        Assert.Equal(ProviderComputeFamily.BareMetal, offer.ComputeFamily);
        Assert.Equal(TimeSpan.FromMinutes(25), offer.EstimatedProvisioningLeadTime);

        var armImages = await discovery.ListImagesAsync(architecture: "linux/arm64");
        Assert.Single(armImages);

        var quotas = await discovery.GetQuotasAsync("rbx");
        var quota = Assert.Single(quotas);
        Assert.Equal(7, quota.Remaining);
    }

    private sealed class SyntheticCatalogDiscoveryClient : IVpsProviderCatalogDiscoveryClient
    {
        public Task<IReadOnlyList<ProviderLocationInfo>> ListLocationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderLocationInfo>>(
            [
                new("gra11", "Gravelines 11", ProviderLocationGranularity.Region, "eu-west", "eu-west"),
                new("rbx", "Roubaix", ProviderLocationGranularity.Datacenter, "fr")
            ]);

        public Task<IReadOnlyList<ProviderComputeOfferInfo>> ListComputeOffersAsync(
            string? location = null,
            ProviderComputeFamily? computeFamily = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderComputeOfferInfo> all =
            [
                new("d2-4", "Discovery 4GB", ProviderComputeFamily.VirtualServer, ["linux/amd64"], 2, 4096, 50, ["gra11"]),
                new("rise-1", "Rise 1 Bare Metal", ProviderComputeFamily.BareMetal, ["linux/amd64"], 8, 32768, 1000, ["rbx"], TimeSpan.FromMinutes(25))
            ];
            return Task.FromResult<IReadOnlyList<ProviderComputeOfferInfo>>(all
                .Where(x => (location is null || x.AvailableLocations?.Contains(location) == true)
                         && (computeFamily is null || x.ComputeFamily == computeFamily.Value))
                .ToArray());
        }

        public Task<IReadOnlyList<ProviderImageInfo>> ListImagesAsync(
            string? location = null,
            string? architecture = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ProviderImageInfo> all =
            [
                new("ubuntu-24.04-amd64", "Ubuntu 24.04 amd64", "linux/amd64", "ubuntu"),
                new("ubuntu-24.04-arm64", "Ubuntu 24.04 arm64", "linux/arm64", "ubuntu")
            ];
            return Task.FromResult<IReadOnlyList<ProviderImageInfo>>(all
                .Where(x => architecture is null || x.Architecture == architecture)
                .ToArray());
        }

        public Task<IReadOnlyList<ProviderQuotaInfo>> GetQuotasAsync(string? location = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderQuotaInfo>>([new("servers", Limit: 10, Used: 3, Location: location, Unit: "count")]);
    }
}
