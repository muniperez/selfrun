using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Selfrun.Common.Providers.Ovhcloud;

/// <summary>
/// SDK-independent OVHcloud API v1 request signer ($1$ + SHA-1 hex signature).
/// </summary>
public static class OvhcloudRequestSigner
{
    public static void SignRequest(
        HttpRequestMessage request,
        string body,
        string applicationKey,
        string applicationSecret,
        string consumerKey,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("OVHcloud request signing requires an absolute URI.");
        }

        var unixSeconds = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var fullUrl = request.RequestUri.AbsoluteUri;
        var toSign = $"{applicationSecret}+{consumerKey}+{request.Method.Method.ToUpperInvariant()}+{fullUrl}+{body ?? string.Empty}+{unixSeconds}";
#pragma warning disable CA5350 // OVHcloud API v1 protocol requires SHA-1 request signing.
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(toSign));
#pragma warning restore CA5350
        var signature = "$1$" + Convert.ToHexString(hash).ToLowerInvariant();

        request.Headers.Remove("X-Ovh-Application");
        request.Headers.Remove("X-Ovh-Consumer");
        request.Headers.Remove("X-Ovh-Timestamp");
        request.Headers.Remove("X-Ovh-Signature");

        request.Headers.TryAddWithoutValidation("X-Ovh-Application", applicationKey);
        request.Headers.TryAddWithoutValidation("X-Ovh-Consumer", consumerKey);
        request.Headers.TryAddWithoutValidation("X-Ovh-Timestamp", unixSeconds);
        request.Headers.TryAddWithoutValidation("X-Ovh-Signature", signature);
    }
}
