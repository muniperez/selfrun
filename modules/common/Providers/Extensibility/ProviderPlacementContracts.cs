using System.Globalization;

namespace Minicloud.Common.Providers.Extensibility;

public enum ProviderImageFormat
{
    SlugOrId,
    PositiveIntegerId,
    AmiOrAlias,
    UrnOrResourceId,
    ImageFamilyOrSelfLink
}

public sealed record ProviderPlacementContract(
    ProviderLocationGranularity LocationGranularity,
    ProviderComputeFamily SupportedComputeFamilies,
    IReadOnlyList<string> SupportedBuildPlatforms,
    ProviderImageFormat ImageFormat,
    string? DefaultImage = null,
    int? DefaultOsId = null,
    bool ImageRequired = false,
    string? LocationExample = null,
    string? SkuExample = null,
    Func<ProviderPlacement, ProviderPlacementValidationResult>? CustomValidator = null);

/// <summary>
/// Immutable placement snapshot bound to an organization plan and captured at
/// provisioning reservation time. Placement never changes silently across retries
/// or switches accounts on quota failure.
/// </summary>
public sealed record ProviderPlacement(
    string ProviderWireType,
    string ProviderAccountId,
    string Region,
    string Sku,
    string? Image,
    string BuildPlatform,
    ProviderComputeFamily ComputeFamily = ProviderComputeFamily.VirtualServer,
    IReadOnlyDictionary<string, string>? PlacementMetadata = null);

public sealed record ProviderPlacementValidationResult(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ProviderPlacementValidationResult Valid() => new(true);

    public static ProviderPlacementValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

public static class ProviderPlacementValidator
{
    public const int MaxIdentifierLength = 128;

    public static ProviderPlacementValidationResult Validate(ProviderDescriptor descriptor, ProviderPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(placement);

        var contract = descriptor.Placement;

        if (placement.ComputeFamily == ProviderComputeFamily.None ||
            (contract.SupportedComputeFamilies & placement.ComputeFamily) != placement.ComputeFamily ||
            (descriptor.Capabilities.ComputeFamilies & placement.ComputeFamily) != placement.ComputeFamily)
        {
            return ProviderPlacementValidationResult.Invalid(
                "plan_compute_family_unsupported",
                $"Provider '{descriptor.DisplayName}' does not support compute family '{placement.ComputeFamily}'.");
        }

        if (string.IsNullOrWhiteSpace(placement.Region) ||
            placement.Region.Trim().Length > MaxIdentifierLength ||
            placement.Region.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(placement.Sku) ||
            placement.Sku.Trim().Length > MaxIdentifierLength ||
            placement.Sku.Any(char.IsControl))
        {
            return ProviderPlacementValidationResult.Invalid(
                "plan_mapping_invalid",
                "Specify a region, provider SKU, supported image and linux/amd64 or linux/arm64 platform.");
        }

        if (string.IsNullOrWhiteSpace(placement.BuildPlatform) ||
            !contract.SupportedBuildPlatforms.Contains(placement.BuildPlatform, StringComparer.Ordinal) ||
            !descriptor.Capabilities.SupportsArchitecture(placement.BuildPlatform))
        {
            return ProviderPlacementValidationResult.Invalid(
                "plan_mapping_invalid",
                "Specify a region, provider SKU, supported image and linux/amd64 or linux/arm64 platform.");
        }

        var trimmedImage = string.IsNullOrWhiteSpace(placement.Image) ? null : placement.Image.Trim();
        if (trimmedImage is not null && (trimmedImage.Length > MaxIdentifierLength || trimmedImage.Any(char.IsControl)))
        {
            return ProviderPlacementValidationResult.Invalid(
                "plan_mapping_invalid",
                "Specify a region, provider SKU, supported image and linux/amd64 or linux/arm64 platform.");
        }

        if (trimmedImage is null && contract.ImageRequired && string.IsNullOrWhiteSpace(contract.DefaultImage) && contract.DefaultOsId is null)
        {
            return ProviderPlacementValidationResult.Invalid(
                "plan_mapping_invalid",
                $"Provider '{descriptor.DisplayName}' requires an explicit image identifier.");
        }

        if (trimmedImage is not null)
        {
            var imageResult = ValidateImageFormat(descriptor, contract.ImageFormat, trimmedImage);
            if (!imageResult.IsValid)
            {
                return imageResult;
            }
        }

        if (contract.CustomValidator is not null)
        {
            var custom = contract.CustomValidator(placement);
            if (!custom.IsValid)
            {
                return custom;
            }
        }

        return ProviderPlacementValidationResult.Valid();
    }

    public static bool IsEquivalent(ProviderPlacement left, ProviderPlacement right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!string.Equals(left.ProviderWireType, right.ProviderWireType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.ProviderAccountId, right.ProviderAccountId, StringComparison.Ordinal) ||
            !string.Equals(left.Region, right.Region, StringComparison.Ordinal) ||
            !string.Equals(left.Sku, right.Sku, StringComparison.Ordinal) ||
            !string.Equals(left.Image, right.Image, StringComparison.Ordinal) ||
            !string.Equals(left.BuildPlatform, right.BuildPlatform, StringComparison.Ordinal) ||
            left.ComputeFamily != right.ComputeFamily)
        {
            return false;
        }

        var leftMeta = left.PlacementMetadata;
        var rightMeta = right.PlacementMetadata;
        if (leftMeta is null || leftMeta.Count == 0)
        {
            return rightMeta is null || rightMeta.Count == 0;
        }

        if (rightMeta is null || leftMeta.Count != rightMeta.Count)
        {
            return false;
        }

        foreach (var (key, value) in leftMeta)
        {
            if (!rightMeta.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ProviderPlacementValidationResult ValidateImageFormat(
        ProviderDescriptor descriptor,
        ProviderImageFormat format,
        string image)
    {
        switch (format)
        {
            case ProviderImageFormat.PositiveIntegerId:
                if (!int.TryParse(image, NumberStyles.None, CultureInfo.InvariantCulture, out var osId) || osId <= 0)
                {
                    return ProviderPlacementValidationResult.Invalid(
                        "plan_mapping_invalid",
                        $"{descriptor.DisplayName} plan OS overrides must be positive numeric OS IDs.");
                }
                return ProviderPlacementValidationResult.Valid();

            case ProviderImageFormat.AmiOrAlias:
                if (!image.StartsWith("ami-", StringComparison.Ordinal) &&
                    !image.StartsWith("resolve:ssm:", StringComparison.Ordinal))
                {
                    return ProviderPlacementValidationResult.Invalid(
                        "plan_mapping_invalid",
                        $"{descriptor.DisplayName} images must be an AMI ID (ami-...) or SSM alias (resolve:ssm:...).");
                }
                return ProviderPlacementValidationResult.Valid();

            case ProviderImageFormat.UrnOrResourceId:
                var urnParts = image.Split(':', StringSplitOptions.RemoveEmptyEntries);
                if (urnParts.Length != 4 && !image.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                {
                    return ProviderPlacementValidationResult.Invalid(
                        "plan_mapping_invalid",
                        $"{descriptor.DisplayName} images must be a publisher:offer:sku:version URN or /subscriptions/... image resource ID.");
                }
                return ProviderPlacementValidationResult.Valid();

            case ProviderImageFormat.ImageFamilyOrSelfLink:
                if (!image.StartsWith("projects/", StringComparison.Ordinal))
                {
                    return ProviderPlacementValidationResult.Invalid(
                        "plan_mapping_invalid",
                        $"{descriptor.DisplayName} images must reference a projects/<project>/global/images/... family or image path.");
                }
                return ProviderPlacementValidationResult.Valid();

            default:
                return ProviderPlacementValidationResult.Valid();
        }
    }
}
