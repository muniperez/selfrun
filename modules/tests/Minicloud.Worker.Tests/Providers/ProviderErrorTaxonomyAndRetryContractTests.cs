using System.Net;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Extensibility;
using Minicloud.Common.Services;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ProviderErrorTaxonomyAndRetryContractTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Authentication, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.Forbidden, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Authorization, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.BadRequest, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Validation, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Validation, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.PaymentRequired, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.QuotaExceeded, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.RateLimited, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOperationSafety.ReadOnly, ProviderErrorCategory.RateLimited, ProviderErrorDisposition.RetryableRead, true)]
    [InlineData(HttpStatusCode.NotFound, ProviderOperationSafety.ReadOnly, ProviderErrorCategory.NotFound, ProviderErrorDisposition.DefiniteRejection, false)]
    [InlineData(HttpStatusCode.NotFound, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.NotFound, ProviderErrorDisposition.AmbiguousOutcome, false)]
    [InlineData(HttpStatusCode.Conflict, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Conflict, ProviderErrorDisposition.AmbiguousOutcome, false)]
    [InlineData(HttpStatusCode.RequestTimeout, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.Timeout, ProviderErrorDisposition.AmbiguousOutcome, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, ProviderOperationSafety.ReadOnly, ProviderErrorCategory.Timeout, ProviderErrorDisposition.RetryableRead, true)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOperationSafety.NonIdempotentMutation, ProviderErrorCategory.TransientProviderError, ProviderErrorDisposition.AmbiguousOutcome, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderOperationSafety.ReadOnly, ProviderErrorCategory.TransientProviderError, ProviderErrorDisposition.RetryableRead, true)]
    public void DefaultClassifierMapsHttpStatusCodesByOperationSafety(
        HttpStatusCode statusCode,
        ProviderOperationSafety safety,
        ProviderErrorCategory expectedCategory,
        ProviderErrorDisposition expectedDisposition,
        bool expectedRetryable)
    {
        var ex = new ProviderApiException("provider error", statusCode, responseBody: "{\"error\":\"generic\"}");
        var classified = DefaultProviderErrorClassifier.Instance.Classify(ex, safety);

        Assert.Equal(expectedCategory, classified.Category);
        Assert.Equal(expectedDisposition, classified.Disposition);
        Assert.Equal(expectedRetryable, classified.IsRetryable);
        Assert.False(string.IsNullOrWhiteSpace(classified.SafeOperatorMessage));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "InsufficientInstanceCapacity")]
    [InlineData(HttpStatusCode.Forbidden, "VcpuLimitExceeded")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "resource_limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "QUOTA_EXCEEDED")]
    public void QuotaSignalsClassifyAsQuotaExceededDefiniteRejection(HttpStatusCode statusCode, string errorCode)
    {
        var ex = new ProviderApiException("limit reached", statusCode, providerErrorCode: errorCode);
        var classified = DefaultProviderErrorClassifier.Instance.Classify(ex, ProviderOperationSafety.NonIdempotentMutation);

        Assert.Equal(ProviderErrorCategory.QuotaExceeded, classified.Category);
        Assert.Equal(ProviderErrorDisposition.DefiniteRejection, classified.Disposition);
        Assert.True(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void PartialAllocationAmbiguousNeverClearsCreationMarkerEvenOn4xx(HttpStatusCode statusCode)
    {
        var ex = new ProviderApiException(
            "VM created before post-create attach failed.",
            statusCode,
            providerWireType: "azure",
            category: ProviderErrorCategory.PartialAllocationAmbiguous,
            isAmbiguousOutcome: true);

        var classified = DefaultProviderErrorClassifier.Instance.Classify(ex, ProviderOperationSafety.NonIdempotentMutation);
        Assert.Equal(ProviderErrorCategory.PartialAllocationAmbiguous, classified.Category);
        Assert.Equal(ProviderErrorDisposition.AmbiguousOutcome, classified.Disposition);
        Assert.False(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(ex));
    }

    [Fact]
    public void TransportTimeoutAndSecretStoreExceptionsClassifyCorrectlyAndRedactSecrets()
    {
        Assert.False(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(new HttpRequestException("reset")));
        Assert.False(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(new TimeoutException("timed out")));
        Assert.False(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(new TaskCanceledException("canceled")));
        Assert.True(DefaultProviderErrorClassifier.IsDefiniteAllocationRejection(new SecretStoreUnavailableException("vault down")));

        var readTimeout = DefaultProviderErrorClassifier.Instance.Classify(new TimeoutException("timed out"), ProviderOperationSafety.ReadOnly);
        Assert.Equal(ProviderErrorDisposition.RetryableRead, readTimeout.Disposition);
        Assert.True(readTimeout.IsRetryable);

        var ex = new ProviderApiException(
            "Authorization: Bearer super_secret_token_12345 failed",
            HttpStatusCode.Unauthorized,
            responseBody: "{\"token\":\"super_secret_token_12345\"}");
        Assert.DoesNotContain("super_secret_token_12345", ex.Message);
        Assert.DoesNotContain("super_secret_token_12345", ex.ResponseBody ?? "");
    }

    [Fact]
    public async Task ResilientHttpRetryHandlerHonorsProviderOperationSafetyAndNoRetryOptions()
    {
        // Read-only request retries transient 503 and succeeds.
        {
            var inner = new TestHttpMessageHandler();
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
            using var retryHandler = new ResilientHttpRetryHandler(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1), innerHandler: inner);
            using var client = new HttpClient(retryHandler);
            using var readReq = new HttpRequestMessage(HttpMethod.Get, "https://provider.example.test/v1/servers");
            readReq.Options.Set(ProviderHttpOptions.OperationSafety, ProviderOperationSafety.ReadOnly);
            var response = await client.SendAsync(readReq);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, inner.RecordedRequests.Count);
        }

        // Non-idempotent mutation MUST NOT retry even on 503 or 429.
        {
            var inner = new TestHttpMessageHandler();
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
            using var retryHandler = new ResilientHttpRetryHandler(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1), innerHandler: inner);
            using var client = new HttpClient(retryHandler);
            using var mutateReq = new HttpRequestMessage(HttpMethod.Post, "https://provider.example.test/v1/servers");
            mutateReq.Options.Set(ProviderHttpOptions.OperationSafety, ProviderOperationSafety.NonIdempotentMutation);
            var response = await client.SendAsync(mutateReq);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Single(inner.RecordedRequests);
        }

        // Explicit NoRetry option also disables retries.
        {
            var inner = new TestHttpMessageHandler();
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            inner.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK));
            using var retryHandler = new ResilientHttpRetryHandler(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(1), innerHandler: inner);
            using var client = new HttpClient(retryHandler);
            using var noRetryReq = new HttpRequestMessage(HttpMethod.Post, "https://provider.example.test/v1/volumes");
            noRetryReq.Options.Set(ProviderHttpOptions.NoRetry, true);
            var response = await client.SendAsync(noRetryReq);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Single(inner.RecordedRequests);
        }
    }
}
