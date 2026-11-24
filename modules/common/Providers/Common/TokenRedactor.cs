using System.Text.RegularExpressions;

namespace Minicloud.Common.Providers.Common;

public static partial class TokenRedactor
{
    private static readonly Regex BearerRegex = new(@"Bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AwsAuthRegex = new(@"AWS4-HMAC-SHA256\s+Credential=[^,\s]+,\s*SignedHeaders=[^,\s]+,\s*Signature=[A-Fa-f0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PemPrivateKeyRegex = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex OvhSignatureRegex = new(@"\$1\$[a-f0-9]{40}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KeyPatternRegex = new(@"(?i)(token|key|secret|password|authorization|x-auth-token|x-ovh-consumer|x-ovh-signature|x-amz-security-token|client_secret|clientSecret|secretAccessKey|applicationSecret|consumerKey|private_key|root_pass)[\"":= ]+([A-Za-z0-9_\-\./+=]{8,})", RegexOptions.Compiled);

    public static string Redact(string? input, params string?[] explicitTokens)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var result = input;

        foreach (var token in explicitTokens)
        {
            if (!string.IsNullOrWhiteSpace(token) && token.Length > 3)
            {
                result = result.Replace(token, "[REDACTED]");
            }
        }

        result = PemPrivateKeyRegex.Replace(result, "[REDACTED_PRIVATE_KEY]");
        result = AwsAuthRegex.Replace(result, "AWS4-HMAC-SHA256 [REDACTED]");
        result = OvhSignatureRegex.Replace(result, "$1$[REDACTED]");
        result = BearerRegex.Replace(result, "Bearer [REDACTED]");
        result = KeyPatternRegex.Replace(result, "$1=[REDACTED]");

        return result;
    }
}
