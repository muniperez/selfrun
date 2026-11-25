using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Selfrun.Common.Providers.Aws;

/// <summary>
/// SDK-independent AWS Signature Version 4 (AWS4-HMAC-SHA256) request signer.
/// Adds per-request authorization headers without mutating shared HttpClient defaults.
/// </summary>
public static class AwsSigV4Signer
{
    public static void SignRequest(
        HttpRequestMessage request,
        string payload,
        string accessKeyId,
        string secretAccessKey,
        string region,
        string service,
        DateTimeOffset timestamp,
        string? sessionToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("AWS request signing requires an absolute request URI.");
        }

        var amzDate = timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Sha256Hex(payload ?? string.Empty);

        var host = request.RequestUri.IsDefaultPort
            ? request.RequestUri.Host
            : $"{request.RequestUri.Host}:{request.RequestUri.Port}";

        request.Headers.Remove("X-Amz-Date");
        request.Headers.Remove("X-Amz-Content-Sha256");
        request.Headers.Remove("X-Amz-Security-Token");
        request.Headers.Remove("Authorization");

        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Content-Sha256", payloadHash);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate
        };

        if (!string.IsNullOrWhiteSpace(sessionToken))
        {
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", sessionToken);
            headers["x-amz-security-token"] = sessionToken.Trim();
        }

        var canonicalUri = string.IsNullOrEmpty(request.RequestUri.AbsolutePath) ? "/" : request.RequestUri.AbsolutePath;
        var canonicalQuery = BuildCanonicalQuery(request.RequestUri.Query);
        var canonicalHeaders = new StringBuilder();
        foreach (var pair in headers)
        {
            canonicalHeaders.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
        }

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join('\n',
            request.Method.Method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Sha256Hex(canonicalRequest));

        var signingKey = DeriveSigningKey(secretAccessKey, dateStamp, region, service);
        var signature = Convert.ToHexString(HmacSha256(signingKey, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

        var authorization = $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private static string BuildCanonicalQuery(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var raw = query.StartsWith('?') ? query[1..] : query;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var pairs = raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var eq = segment.IndexOf('=');
                var name = eq >= 0 ? segment[..eq] : segment;
                var value = eq >= 0 ? segment[(eq + 1)..] : string.Empty;
                return (Name: Uri.EscapeDataString(Uri.UnescapeDataString(name)),
                        Value: Uri.EscapeDataString(Uri.UnescapeDataString(value)));
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}");

        return string.Join('&', pairs);
    }

    private static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        var kDate = HmacSha256(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
