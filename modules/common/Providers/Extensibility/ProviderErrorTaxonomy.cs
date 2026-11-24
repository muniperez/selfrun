using System.Net;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Services;

namespace Minicloud.Common.Providers.Extensibility;

public enum ProviderErrorCategory
{
    Unknown,
    Authentication,
    Authorization,
    Validation,
    NotFound,
    Conflict,
    QuotaExceeded,
    RateLimited,
    TransientProviderError,
    Timeout,
    PartialAllocationAmbiguous,
    UnsupportedCapability
}

/// <summary>
/// Governs how the worker/control plane responds to a provider error:
/// - <see cref="DefiniteRejection"/>: mutation definitely did not create an external resource; safe to clear creation lock.
/// - <see cref="AmbiguousOutcome"/>: external resource may exist; MUST preserve reservation and reconcile in the SAME account.
/// - <see cref="RetryableRead"/>: transient error on a safe read-only probe/poll.
/// </summary>
public enum ProviderErrorDisposition
{
    DefiniteRejection,
    AmbiguousOutcome,
    RetryableRead
}

public enum ProviderOperationSafety
{
    ReadOnly,
    IdempotentWrite,
    NonIdempotentMutation
}

public sealed record ProviderRetryPolicy(
    int MaxReadRetries = 3,
    TimeSpan? BaseDelay = null,
    TimeSpan? MaxDelay = null,
    bool RespectRetryAfterHeader = true,
    bool RetryNonIdempotentMutations = false)
{
    public TimeSpan EffectiveBaseDelay => BaseDelay ?? TimeSpan.FromSeconds(1);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(30);

    public static ProviderRetryPolicy Default { get; } = new();
}

/// <summary>
/// Provider-agnostic HTTP request options inspected by <see cref="ResilientHttpRetryHandler"/>.
/// </summary>
public static class ProviderHttpOptions
{
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("minicloud.no-retry");
    public static readonly HttpRequestOptionsKey<ProviderOperationSafety> OperationSafety = new("minicloud.operation-safety");
}

public sealed record ProviderErrorClassification(
    ProviderErrorCategory Category,
    ProviderErrorDisposition Disposition,
    bool IsRetryable,
    string SafeOperatorMessage,
    string? ProviderErrorCode = null);

public interface IProviderErrorClassifier
{
    ProviderErrorClassification Classify(
        Exception exception,
        ProviderOperationSafety operationSafety = ProviderOperationSafety.NonIdempotentMutation);
}

public sealed class DefaultProviderErrorClassifier : IProviderErrorClassifier
{
    public static DefaultProviderErrorClassifier Instance { get; } = new();

    private static readonly string[] QuotaIndicators =
    [
        "quota",
        "limit_exceeded",
        "limit exceeded",
        "resource_limit",
        "insufficientinstancecapacity",
        "insufficient capacity",
        "vcpulimitexceeded",
        "addresslimitexceeded",
        "quota_exceeded",
        "operationnotallowed"
    ];

    public ProviderErrorClassification Classify(
        Exception exception,
        ProviderOperationSafety operationSafety = ProviderOperationSafety.NonIdempotentMutation)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is SecretStoreUnavailableException)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.Authentication,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "Provider account credentials are temporarily unavailable in secret storage.");
        }

        if (exception is TimeoutException)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.Timeout,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: operationSafety == ProviderOperationSafety.ReadOnly,
                "The provider request timed out before returning a confirmed outcome.");
        }

        if (exception is OperationCanceledException)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.Timeout,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider request was canceled or timed out before confirmation.");
        }

        if (exception is ProviderApiException apiEx)
        {
            return ClassifyProviderApiException(apiEx, operationSafety);
        }

        if (exception is HttpRequestException)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.TransientProviderError,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: operationSafety == ProviderOperationSafety.ReadOnly,
                "The provider transport request failed before confirming an outcome.");
        }

        return new ProviderErrorClassification(
            ProviderErrorCategory.Unknown,
            ProviderErrorDisposition.AmbiguousOutcome,
            IsRetryable: false,
            "The provider operation failed with an unclassified error; reconcile provider state before retrying.");
    }

    /// <summary>
    /// Returns true only when an allocation error proves no external resource was created.
    /// Transport failures, timeouts, 5xx errors, and post-create partial failures always return false.
    /// </summary>
    public static bool IsDefiniteAllocationRejection(Exception exception, IProviderErrorClassifier? classifier = null)
    {
        var effective = classifier ?? Instance;
        var result = effective.Classify(exception, ProviderOperationSafety.NonIdempotentMutation);
        return result.Disposition == ProviderErrorDisposition.DefiniteRejection;
    }

    private static ProviderErrorClassification ClassifyProviderApiException(
        ProviderApiException ex,
        ProviderOperationSafety operationSafety)
    {
        if (ex.IsAmbiguousOutcome == true || ex.Category == ProviderErrorCategory.PartialAllocationAmbiguous)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.PartialAllocationAmbiguous,
                ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider allocation left a partial or unconfirmed resource; reconcile the original account before retrying.",
                ex.ProviderErrorCode);
        }

        if (ex.Category is { } explicitCategory && explicitCategory != ProviderErrorCategory.Unknown)
        {
            return BuildFromCategory(explicitCategory, operationSafety, ex.ProviderErrorCode);
        }

        var statusCode = ex.StatusCode;
        if (statusCode is null)
        {
            return new ProviderErrorClassification(
                ProviderErrorCategory.Unknown,
                ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider failed without an HTTP status; reconcile the original account before retrying.",
                ex.ProviderErrorCode);
        }

        if (LooksLikeQuotaError(ex))
        {
            return BuildFromCategory(ProviderErrorCategory.QuotaExceeded, operationSafety, ex.ProviderErrorCode);
        }

        var category = statusCode.Value switch
        {
            HttpStatusCode.Unauthorized => ProviderErrorCategory.Authentication,
            HttpStatusCode.Forbidden => ProviderErrorCategory.Authorization,
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ProviderErrorCategory.Validation,
            HttpStatusCode.NotFound => ProviderErrorCategory.NotFound,
            HttpStatusCode.Conflict => ProviderErrorCategory.Conflict,
            HttpStatusCode.PaymentRequired => ProviderErrorCategory.QuotaExceeded,
            HttpStatusCode.TooManyRequests => ProviderErrorCategory.RateLimited,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ProviderErrorCategory.Timeout,
            >= HttpStatusCode.InternalServerError => ProviderErrorCategory.TransientProviderError,
            _ => ProviderErrorCategory.Unknown
        };

        return BuildFromCategory(category, operationSafety, ex.ProviderErrorCode);
    }

    private static ProviderErrorClassification BuildFromCategory(
        ProviderErrorCategory category,
        ProviderOperationSafety operationSafety,
        string? providerErrorCode)
    {
        return category switch
        {
            ProviderErrorCategory.Authentication => new(
                category,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "Provider credentials were rejected. Verify the account token or rotate credentials.",
                providerErrorCode),

            ProviderErrorCategory.Authorization => new(
                category,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "Provider credentials lack permission for this operation in the configured scope.",
                providerErrorCode),

            ProviderErrorCategory.Validation => new(
                category,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "The provider rejected the requested placement or server configuration.",
                providerErrorCode),

            ProviderErrorCategory.QuotaExceeded => new(
                category,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "The selected provider account has insufficient quota or capacity. Select another plan for an unprovisioned environment; credentials remain valid.",
                providerErrorCode),

            ProviderErrorCategory.RateLimited => new(
                category,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: operationSafety == ProviderOperationSafety.ReadOnly,
                "The provider rate-limited the request. Retry after the provider backoff window.",
                providerErrorCode),

            ProviderErrorCategory.NotFound => new(
                category,
                operationSafety == ProviderOperationSafety.NonIdempotentMutation
                    ? ProviderErrorDisposition.AmbiguousOutcome
                    : ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "The requested provider resource was not found in the configured account scope.",
                providerErrorCode),

            ProviderErrorCategory.Conflict => new(
                category,
                ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider reported a resource conflict; reconcile the account before retrying.",
                providerErrorCode),

            ProviderErrorCategory.Timeout => new(
                category,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: operationSafety == ProviderOperationSafety.ReadOnly,
                "The provider request timed out before confirming completion.",
                providerErrorCode),

            ProviderErrorCategory.TransientProviderError => new(
                category,
                operationSafety == ProviderOperationSafety.ReadOnly
                    ? ProviderErrorDisposition.RetryableRead
                    : ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: operationSafety == ProviderOperationSafety.ReadOnly,
                "The provider returned a transient server error.",
                providerErrorCode),

            ProviderErrorCategory.UnsupportedCapability => new(
                category,
                ProviderErrorDisposition.DefiniteRejection,
                IsRetryable: false,
                "The selected provider does not support this capability.",
                providerErrorCode),

            ProviderErrorCategory.PartialAllocationAmbiguous => new(
                category,
                ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider allocation left a partial or unconfirmed resource; reconcile the original account before retrying.",
                providerErrorCode),

            _ => new(
                ProviderErrorCategory.Unknown,
                ProviderErrorDisposition.AmbiguousOutcome,
                IsRetryable: false,
                "The provider returned an unclassified error; reconcile provider state before retrying.",
                providerErrorCode)
        };
    }

    private static bool LooksLikeQuotaError(ProviderApiException ex)
    {
        if (ex.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.PaymentRequired or
            HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        var corpus = $"{ex.ProviderErrorCode} {ex.ResponseBody} {ex.Message}";
        return QuotaIndicators.Any(indicator =>
            corpus.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }
}
