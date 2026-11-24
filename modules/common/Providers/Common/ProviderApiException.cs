using System.Net;
using Minicloud.Common.Providers.Extensibility;

namespace Minicloud.Common.Providers.Common;

public class ProviderApiException : HttpRequestException
{
    public CloudProvider? Provider { get; }
    public string? ProviderWireType { get; }
    public string? ResponseBody { get; }
    public ProviderErrorCategory? Category { get; }
    public string? ProviderErrorCode { get; }
    public bool? IsAmbiguousOutcome { get; }

    public ProviderApiException(
        string message,
        HttpStatusCode? statusCode = null,
        CloudProvider? provider = null,
        string? responseBody = null,
        Exception? innerException = null,
        ProviderErrorCategory? category = null,
        string? providerWireType = null,
        string? providerErrorCode = null,
        bool? isAmbiguousOutcome = null)
        : base(TokenRedactor.Redact(message), innerException, statusCode)
    {
        Provider = provider;
        ProviderWireType = providerWireType;
        ResponseBody = TokenRedactor.Redact(responseBody);
        Category = category;
        ProviderErrorCode = string.IsNullOrWhiteSpace(providerErrorCode) ? null : TokenRedactor.Redact(providerErrorCode);
        IsAmbiguousOutcome = isAmbiguousOutcome;
    }
}
