// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class SelfrunCompatibilityTests
{
    [Fact]
    public void Environment_variables_prefer_SELFRUN_and_fall_back_to_MINICLOUD()
    {
        var tempXdg = Directory.CreateTempSubdirectory("selfrun-compat-env-");
        try
        {
            var both = new Dictionary<string, string?>
            {
                ["SELFRUN_API_URL"] = "https://api.selfrun.example",
                ["MINICLOUD_API_URL"] = "https://api.legacy.example",
                ["SELFRUN_APP_DOMAIN"] = "apps.selfrun.example",
                ["MINICLOUD_APP_DOMAIN"] = "apps.legacy.example",
                ["SELFRUN_LOCAL_ORGANIZATION_SLUG"] = "selfrun-org",
                ["MINICLOUD_LOCAL_ORGANIZATION_SLUG"] = "legacy-org",
                ["SELFRUN_TOKEN"] = "selfrun-token",
                ["MINICLOUD_TOKEN"] = "legacy-token",
                ["XDG_CONFIG_HOME"] = tempXdg.FullName
            };

            var envBoth = CliEnvironment.FromEnvironment(k => both.GetValueOrDefault(k), () => tempXdg.FullName);
            Assert.Equal("https://api.selfrun.example", envBoth.ApiBaseUrl);
            Assert.Equal("apps.selfrun.example", envBoth.AppDomain);
            Assert.Equal("selfrun-org", envBoth.LocalOrganizationSlug);
            Assert.Equal("selfrun-token", CliEnvironment.GetTokenEnvironmentVariable(k => both.GetValueOrDefault(k)));

            var legacyOnly = new Dictionary<string, string?>
            {
                ["MINICLOUD_API_URL"] = "https://api.legacy.example",
                ["MINICLOUD_APP_DOMAIN"] = "apps.legacy.example",
                ["MINICLOUD_LOCAL_ORGANIZATION_SLUG"] = "legacy-org",
                ["MINICLOUD_TOKEN"] = "legacy-token",
                ["XDG_CONFIG_HOME"] = tempXdg.FullName
            };

            var envLegacy = CliEnvironment.FromEnvironment(k => legacyOnly.GetValueOrDefault(k), () => tempXdg.FullName);
            Assert.Equal("https://api.legacy.example", envLegacy.ApiBaseUrl);
            Assert.Equal("apps.legacy.example", envLegacy.AppDomain);
            Assert.Equal("legacy-org", envLegacy.LocalOrganizationSlug);
            Assert.Equal("legacy-token", CliEnvironment.GetTokenEnvironmentVariable(k => legacyOnly.GetValueOrDefault(k)));
        }
        finally
        {
            tempXdg.Delete(recursive: true);
        }
    }

    [Fact]
    public void Config_directory_uses_selfrun_by_default_and_falls_back_to_legacy_minicloud_when_present()
    {
        var tempXdg = Directory.CreateTempSubdirectory("selfrun-compat-xdg-");
        try
        {
            // Clean directory: defaults to selfrun
            Assert.Equal(Path.Combine(tempXdg.FullName, "selfrun"), CliEnvironment.ResolveConfigDirectory(tempXdg.FullName));

            // Legacy minicloud exists, selfrun does not: falls back to minicloud
            Directory.CreateDirectory(Path.Combine(tempXdg.FullName, "minicloud"));
            Assert.Equal(Path.Combine(tempXdg.FullName, "minicloud"), CliEnvironment.ResolveConfigDirectory(tempXdg.FullName));

            // Both exist: prefers selfrun
            Directory.CreateDirectory(Path.Combine(tempXdg.FullName, "selfrun"));
            Assert.Equal(Path.Combine(tempXdg.FullName, "selfrun"), CliEnvironment.ResolveConfigDirectory(tempXdg.FullName));
        }
        finally
        {
            tempXdg.Delete(recursive: true);
        }
    }

    [Fact]
    public void Default_config_path_resolves_selfrun_yml_then_minicloud_yml_then_minicloudconfig_yml()
    {
        var dir = Directory.CreateTempSubdirectory("selfrun-compat-cfg-");
        try
        {
            Assert.Equal("selfrun.yml", MinicloudConfigLoader.ResolveDefaultPath(dir.FullName));

            File.WriteAllText(Path.Combine(dir.FullName, "minicloudconfig.yml"), "app: demo\n");
            Assert.Equal("minicloudconfig.yml", MinicloudConfigLoader.ResolveDefaultPath(dir.FullName));

            File.WriteAllText(Path.Combine(dir.FullName, "minicloud.yml"), "app: demo\n");
            Assert.Equal("minicloud.yml", MinicloudConfigLoader.ResolveDefaultPath(dir.FullName));

            File.WriteAllText(Path.Combine(dir.FullName, "selfrun.yml"), "app: demo\n");
            Assert.Equal("selfrun.yml", MinicloudConfigLoader.ResolveDefaultPath(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Local_secrets_file_prefers_selfrun_secrets_env_and_falls_back_to_minicloud_secrets_env()
    {
        var dir = Directory.CreateTempSubdirectory("selfrun-compat-secrets-");
        try
        {
            Assert.Null(LocalSecretsFile.ResolvePath(dir.FullName));

            var legacy = Path.Combine(dir.FullName, "minicloud.secrets.env");
            File.WriteAllText(legacy, "API_KEY=legacy\n");
            Assert.Equal(legacy, LocalSecretsFile.ResolvePath(dir.FullName));

            var primary = Path.Combine(dir.FullName, "selfrun.secrets.env");
            File.WriteAllText(primary, "API_KEY=selfrun\n");
            Assert.Equal(primary, LocalSecretsFile.ResolvePath(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Artifact_bundler_excludes_both_selfrun_and_minicloud_secret_files_and_dot_directories()
    {
        var sourceDir = Directory.CreateTempSubdirectory("selfrun-compat-bundle-src-");
        var outputDir = Directory.CreateTempSubdirectory("selfrun-compat-bundle-out-");
        try
        {
            File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\nCOPY app.txt /app.txt\n");
            File.WriteAllText(Path.Combine(sourceDir.FullName, "app.txt"), "hello\n");
            File.WriteAllText(Path.Combine(sourceDir.FullName, "selfrun.secrets.env"), "SECRET=1\n");
            File.WriteAllText(Path.Combine(sourceDir.FullName, "minicloud.secrets.env"), "SECRET=2\n");
            Directory.CreateDirectory(Path.Combine(sourceDir.FullName, ".selfrun"));
            File.WriteAllText(Path.Combine(sourceDir.FullName, ".selfrun", "local.txt"), "hidden\n");
            Directory.CreateDirectory(Path.Combine(sourceDir.FullName, ".minicloud"));
            File.WriteAllText(Path.Combine(sourceDir.FullName, ".minicloud", "local.txt"), "hidden\n");

            var service = new MinicloudServiceConfig(
                SourcePath: sourceDir.FullName,
                Dockerfile: "Dockerfile",
                Image: null,
                Port: 8080,
                Public: true,
                Path: "/",
                HealthPath: "/");

            var bundle = DeploymentArtifactBundler.Create("app_1", "web", service, "abc123", outputDir.FullName);
            using var archive = ZipFile.OpenRead(bundle.ZipPath);
            var entries = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);

            Assert.Contains("app.txt", entries);
            Assert.DoesNotContain("selfrun.secrets.env", entries);
            Assert.DoesNotContain("minicloud.secrets.env", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith(".selfrun/", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.StartsWith(".minicloud/", StringComparison.Ordinal));
        }
        finally
        {
            sourceDir.Delete(recursive: true);
            outputDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("minicloud")]
    [InlineData("selfrun")]
    public async Task Instance_discovery_accepts_both_minicloud_and_selfrun_product_identifiers(string product)
    {
        var tempDir = Directory.CreateTempSubdirectory("selfrun-compat-api-");
        try
        {
            var handler = new StubHandler(product);
            using var http = new HttpClient(handler);
            var env = CliEnvironment.ForTests("https://api.example.com", tempDir.FullName);
            var tokens = new TokenStore(env);
            var client = new MinicloudApiClient(env, tokens, http);

            var info = await client.GetInstanceInfoAsync("https://api.example.com", CancellationToken.None);
            Assert.Equal(product, info.Product);
            Assert.Equal("apps.example.com", info.AppDomain);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class StubHandler(string product) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    product,
                    protocolVersion = 1,
                    appDomain = "apps.example.com"
                })
            };
            return Task.FromResult(response);
        }
    }
}
