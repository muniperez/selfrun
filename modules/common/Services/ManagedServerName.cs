using System.Security.Cryptography;
using System.Text;

namespace Selfrun.Common.Services;

public static class ManagedServerName
{
    public static string ForApp(string organizationSlug, string appSlug) =>
        Bound($"selfrun-{organizationSlug}-production-{appSlug}".ToLowerInvariant());

    public static string ForHostname(string organizationSlug, string appSlug) =>
        Bound($"selfrun-{organizationSlug}-{appSlug}");

    private static string Bound(string canonicalName)
    {
        if (canonicalName.Length <= 63)
        {
            return canonicalName;
        }

        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName)))[..20];
        return canonicalName[..42].TrimEnd('-') + "-" + suffix;
    }
}
