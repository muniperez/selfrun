using Selfrun.Common.Models;
using Selfrun.Common.Providers.Models;

namespace Selfrun.Common.Providers.Extensibility;

public enum ProviderCredentialFormat
{
    ApiToken,
    KeyPairOrStructuredJson,
    WorkloadIdentity
}

public enum ProviderScopeBoundaryKind
{
    Account,
    Project,
    AccountAndRole,
    TenantAndSubscription,
    ServiceAndProject
}

public sealed record ProviderSettingField(
    string Key,
    string DisplayName,
    bool Required,
    bool ImmutableAfterReservation,
    string? Description = null);

public sealed record ProviderCredentialValidationResult(
    bool IsValid,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ProviderCredentialValidationResult Valid() => new(true);

    public static ProviderCredentialValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

/// <summary>
/// Declares the write-only OpenBao credential contract and explicit non-secret
/// scope boundary settings for a provider adapter.
/// </summary>
public sealed record ProviderCredentialContract(
    ProviderCredentialFormat Format,
    ProviderScopeBoundaryKind ScopeBoundary,
    IReadOnlyList<string> EnvironmentVariableNames,
    IReadOnlyList<ProviderSettingField> NonSecretSettings,
    bool SupportsReadOnlyScopeIdentity,
    bool EnforcesWriteOnlyStorage = true,
    Func<ProviderAccount, string, ProviderCredentialValidationResult>? CredentialFormatValidator = null)
{
    public const int MaxCredentialLength = 8192;

    public ProviderCredentialValidationResult ValidateCredential(ProviderAccount account, string credential)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(credential) ||
            credential.Length > MaxCredentialLength ||
            credential.Any(char.IsControl))
        {
            return ProviderCredentialValidationResult.Invalid(
                "provider_credential_invalid",
                "Enter a provider API credential without control characters.");
        }

        return CredentialFormatValidator is not null
            ? CredentialFormatValidator(account, credential)
            : ProviderCredentialValidationResult.Valid();
    }
}

/// <summary>
/// Canonical, non-secret cloud scope identity observed through read-only provider
/// verification (e.g. AWS account ID, Azure tenant+subscription, GCP project ID,
/// OVHcloud service/project, Linode account, Scaleway project).
/// </summary>
public sealed record ProviderScopeIdentity(
    string ProviderWireType,
    string ScopeKind,
    string ScopeId,
    string? SecondaryScopeId = null,
    string? ObservedPrincipal = null)
{
    public bool MatchesScope(ProviderScopeIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(ProviderWireType, other.ProviderWireType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ScopeKind, other.ScopeKind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ScopeId, other.ScopeId, StringComparison.Ordinal)
            && string.Equals(SecondaryScopeId ?? string.Empty, other.SecondaryScopeId ?? string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>
/// Optional read-only scope verification contract for provider adapters capable of
/// proving their underlying cloud account/tenant/subscription/project identity without
/// allocating billable resources.
/// </summary>
public interface IVpsProviderScopeClient
{
    Task<ProviderScopeIdentity> VerifyScopeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic Selfrun resource ownership metadata attached during allocation and
/// checked during discovery, reconciliation, legacy import, and cleanup.
/// </summary>
public sealed record ProviderOwnershipEvidence(
    string OrganizationSlug,
    string AppSlug,
    string EnvironmentName = "production",
    string? OrganizationId = null,
    string? ProviderAccountId = null,
    string? AppId = null)
{
    public string ManagedServerName => Selfrun.Common.Services.ManagedServerName.ForApp(OrganizationSlug, AppSlug);

    public IReadOnlyDictionary<string, string> ToLabels() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["managed-by"] = "selfrun",
        ["customer"] = OrganizationSlug,
        ["app"] = AppSlug,
        ["env"] = EnvironmentName
    };

    public IReadOnlyList<string> ToTags() =>
    [
        "selfrun",
        $"selfrun-customer-{OrganizationSlug}",
        $"selfrun-app-{AppSlug}",
        $"selfrun-env-{EnvironmentName}"
    ];
}

public static class ProviderOwnershipVerifier
{
    public static bool VerifyDiscoveredServer(
        ProviderDescriptor descriptor,
        ProviderServerInfo discovered,
        string expectedServerName,
        ProviderOwnershipEvidence ownership)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerName);
        ArgumentNullException.ThrowIfNull(ownership);

        if (!string.Equals(discovered.Name, expectedServerName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!descriptor.Capabilities.SupportsExactLabelOwnershipVerification)
        {
            return true;
        }

        return HasExpectedLabels(discovered.Labels, ownership.ToLabels());
    }

    public static bool HasExpectedLabels(
        IReadOnlyDictionary<string, string>? observed,
        IReadOnlyDictionary<string, string> expected)
    {
        if (observed is null)
        {
            return false;
        }

        foreach (var (key, value) in expected)
        {
            if (!observed.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
