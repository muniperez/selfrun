// SPDX-License-Identifier: MPL-2.0

using System.IO.Compression;
using Selfrun.Cli;
using Selfrun.Cli.Commands;
using Selfrun.Cli.Config;

namespace Selfrun.Tests;

public sealed class CliNamingTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(".local", "artifacts", "cli-naming-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Reads_selfrun_environment_variables_and_ignores_minicloud_variables()
    {
        var settings = new Dictionary<string, string?>
        {
            ["MINICLOUD_API_URL"] = "https://api.old.example",
            ["MINICLOUD_APP_DOMAIN"] = "apps.old.example",
            ["MINICLOUD_LOCAL_ORGANIZATION_SLUG"] = "old-org",
            ["MINICLOUD_TOKEN"] = "old-token"
        };

        var onlyOld = CliEnvironment.FromEnvironment(settings.GetValueOrDefault, () => "/config");
        Assert.Empty(onlyOld.ApiBaseUrl);
        Assert.Empty(onlyOld.AppDomain);
        Assert.Equal("local", onlyOld.LocalOrganizationSlug);
        Assert.Null(CliEnvironment.GetTokenEnvironmentVariable(settings.GetValueOrDefault));

        settings["SELFRUN_API_URL"] = "https://api.selfrun.example";
        settings["SELFRUN_APP_DOMAIN"] = "apps.selfrun.example";
        settings["SELFRUN_LOCAL_ORGANIZATION_SLUG"] = "selfrun-org";
        settings["SELFRUN_TOKEN"] = "selfrun-token";

        var selfrun = CliEnvironment.FromEnvironment(settings.GetValueOrDefault, () => "/config");
        Assert.Equal("https://api.selfrun.example", selfrun.ApiBaseUrl);
        Assert.Equal("apps.selfrun.example", selfrun.AppDomain);
        Assert.Equal("selfrun-org", selfrun.LocalOrganizationSlug);
        Assert.Equal("selfrun-token", CliEnvironment.GetTokenEnvironmentVariable(settings.GetValueOrDefault));
    }

    [Fact]
    public void Config_directory_ignores_existing_minicloud_directory()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "minicloud"));

        var settings = CliEnvironment.FromEnvironment(name => name == "XDG_CONFIG_HOME" ? _directory : null,
            () => throw new InvalidOperationException());

        Assert.Equal(Path.Combine(_directory, "selfrun"), settings.ConfigHome);
    }

    [Fact]
    public void Local_secrets_ignores_minicloud_file()
    {
        File.WriteAllText(Path.Combine(_directory, "minicloud.secrets.env"), "API_KEY=old\n");
        Assert.Null(LocalSecretsFile.ResolvePath(_directory));

        var path = Path.Combine(_directory, "selfrun.secrets.env");
        File.WriteAllText(path, "API_KEY=selfrun\n");
        Assert.Equal(path, LocalSecretsFile.ResolvePath(_directory));
    }

    [Fact]
    public void Artifact_bundler_excludes_both_selfrun_and_minicloud_secret_files_and_dot_directories()
    {
        var sourceDir = Directory.CreateTempSubdirectory("selfrun-bundle-src-");
        var outputDir = Directory.CreateTempSubdirectory("selfrun-bundle-out-");
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

            var service = new SelfrunServiceConfig(
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
}
