// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using Minicloud.Cli.Config;

namespace Minicloud.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "SELFRUN_TOKEN";
    public const string LegacyTokenEnvironmentVariable = "MINICLOUD_TOKEN";
    public const string ApiUrlEnvironmentVariable = "SELFRUN_API_URL";
    public const string LegacyApiUrlEnvironmentVariable = "MINICLOUD_API_URL";
    public const string LocalOrganizationSlugEnvironmentVariable = "SELFRUN_LOCAL_ORGANIZATION_SLUG";
    public const string LegacyLocalOrganizationSlugEnvironmentVariable = "MINICLOUD_LOCAL_ORGANIZATION_SLUG";
    public const string AppDomainEnvironmentVariable = "SELFRUN_APP_DOMAIN";
    public const string LegacyAppDomainEnvironmentVariable = "MINICLOUD_APP_DOMAIN";
    public const string TimingTraceOutputEnvironmentVariable = "SELFRUN_CLI_TIMING_TRACE_OUTPUT";
    public const string LegacyTimingTraceOutputEnvironmentVariable = "MINICLOUD_CLI_TIMING_TRACE_OUTPUT";

    private CliEnvironment(string apiBaseUrl, string localOrganizationSlug, string configHome, string appDomain = "")
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        LocalOrganizationSlug = localOrganizationSlug.Trim().ToLowerInvariant();
        ConfigHome = configHome;
        AppDomain = appDomain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    public string AppDomain { get; private set; }
    public string ApiBaseUrl { get; private set; }
    public string LocalOrganizationSlug { get; }
    public string ConfigHome { get; }
    public string? ActiveInstanceName { get; private set; }
    public string? InstanceUrl { get; private set; }
    public bool HasApiOverride { get; private set; }
    public InstanceStore Instances => new(ConfigHome);
    public string TokenFilePath => Path.Combine(ConfigHome, "tokens",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiBaseUrl))) + ".token");

    public void SetAppDomain(string appDomain) => AppDomain = appDomain;

    public void SelectInstance(InstanceProfile profile)
    {
        ActiveInstanceName = profile.Name;
        InstanceUrl = profile.Url;
        ApiBaseUrl = InstanceStore.DeriveApiUrl(profile.Url);
        AppDomain = profile.AppDomain;
        HasApiOverride = false;
    }

    public void LoadActiveInstance()
    {
        if (!string.IsNullOrWhiteSpace(ApiBaseUrl)) return;
        var state = Instances.Load();
        var active = state.Instances.SingleOrDefault(x => x.Name == state.Active);
        if (active is not null) SelectInstance(active);
    }

    public static string? GetEnvironmentVariable(string primaryName, string legacyName, Func<string, string?>? reader = null)
    {
        reader ??= Environment.GetEnvironmentVariable;
        var primary = reader(primaryName);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary;
        }

        var legacy = reader(legacyName);
        return string.IsNullOrWhiteSpace(legacy) ? null : legacy;
    }

    public static string? GetTokenEnvironmentVariable(Func<string, string?>? reader = null) =>
        GetEnvironmentVariable(TokenEnvironmentVariable, LegacyTokenEnvironmentVariable, reader);

    public static string ResolveConfigDirectory(string xdgConfigHome)
    {
        var primary = Path.Combine(xdgConfigHome, "selfrun");
        var legacy = Path.Combine(xdgConfigHome, "minicloud");
        if (!Directory.Exists(primary) && Directory.Exists(legacy))
        {
            return legacy;
        }

        return primary;
    }

    public static CliEnvironment FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable,
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));

    internal static CliEnvironment FromEnvironment(Func<string, string?> reader, Func<string> defaultXdgConfigHome)
    {
        var apiBaseUrl = GetEnvironmentVariable(ApiUrlEnvironmentVariable, LegacyApiUrlEnvironmentVariable, reader) ?? "";

        var localOrganizationSlug = GetEnvironmentVariable(
            LocalOrganizationSlugEnvironmentVariable,
            LegacyLocalOrganizationSlugEnvironmentVariable,
            reader) ?? "local";

        var configHome = reader("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = defaultXdgConfigHome();
        }

        var appDomain = GetEnvironmentVariable(AppDomainEnvironmentVariable, LegacyAppDomainEnvironmentVariable, reader) ?? "";
        return new CliEnvironment(apiBaseUrl, localOrganizationSlug, ResolveConfigDirectory(configHome), appDomain)
        { HasApiOverride = !string.IsNullOrWhiteSpace(apiBaseUrl) };
    }

    public static CliEnvironment ForTests(string apiBaseUrl, string configHome) => new(apiBaseUrl, "local", configHome, "app.example.com");

    internal static CliEnvironment ForInstance(InstanceProfile profile, string configHome)
    {
        var result = new CliEnvironment("", "local", configHome);
        result.SelectInstance(profile);
        return result;
    }
}
