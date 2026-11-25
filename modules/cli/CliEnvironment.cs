// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using Selfrun.Cli.Config;

namespace Selfrun.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "SELFRUN_TOKEN";
    public const string ApiUrlEnvironmentVariable = "SELFRUN_API_URL";
    public const string LocalOrganizationSlugEnvironmentVariable = "SELFRUN_LOCAL_ORGANIZATION_SLUG";
    public const string AppDomainEnvironmentVariable = "SELFRUN_APP_DOMAIN";
    public const string TimingTraceOutputEnvironmentVariable = "SELFRUN_CLI_TIMING_TRACE_OUTPUT";

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

    internal static string? ReadEnvironmentVariable(string name, Func<string, string?>? reader = null)
    {
        reader ??= Environment.GetEnvironmentVariable;
        var value = reader(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string? GetTokenEnvironmentVariable(Func<string, string?>? reader = null) =>
        ReadEnvironmentVariable(TokenEnvironmentVariable, reader);

    public static CliEnvironment FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable,
            () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));

    internal static CliEnvironment FromEnvironment(Func<string, string?> reader, Func<string> defaultXdgConfigHome)
    {
        var apiBaseUrl = ReadEnvironmentVariable(ApiUrlEnvironmentVariable, reader) ?? "";

        var localOrganizationSlug = ReadEnvironmentVariable(LocalOrganizationSlugEnvironmentVariable, reader) ?? "local";

        var configHome = reader("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = defaultXdgConfigHome();
        }

        var appDomain = ReadEnvironmentVariable(AppDomainEnvironmentVariable, reader) ?? "";
        return new CliEnvironment(apiBaseUrl, localOrganizationSlug, Path.Combine(configHome, "selfrun"), appDomain)
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
