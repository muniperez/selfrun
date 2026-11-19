using Microsoft.Extensions.Configuration;
using Minicloud.Common.Extensions;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

[Collection("Development workspace configuration environment")]
public sealed class DevelopmentWorkspaceConfigurationTests
{
    [Fact]
    public void Unset_file_preserves_existing_configuration_sources()
    {
        WithEnvironment(null, () =>
        {
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Existing"] = "unchanged" });
            var count = ((IConfigurationBuilder)configuration).Sources.Count;

            configuration.AddDevelopmentWorkspaceConfiguration();

            Assert.Equal(count, ((IConfigurationBuilder)configuration).Sources.Count);
            Assert.Equal("unchanged", configuration["Existing"]);
            Assert.Null(configuration["DevelopmentWorkspaces:Enabled"]);
        });
    }

    [Fact]
    public void Explicit_file_is_loaded_legacy_workspace_counts_are_ignored_and_environment_still_wins()
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, ".local", "artifacts", "issue110", "packaging",
            "configuration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "workspace.json");
        File.WriteAllText(file, """
            {"DevelopmentWorkspaces":{"Enabled":true,"MaxActiveVms":2,
              "MaxActiveWorkspaces":"obsolete","MaxWorkspacesPerOrganization":"obsolete"}}
            """);
        var priorLimit = Environment.GetEnvironmentVariable("DevelopmentWorkspaces__MaxActiveVms");
        try
        {
            Environment.SetEnvironmentVariable("DevelopmentWorkspaces__MaxActiveVms", "1");
            WithEnvironment(file, () =>
            {
                var configuration = new ConfigurationManager();
                configuration.AddDevelopmentWorkspaceConfiguration();
                var options = configuration.GetRequiredSection(DevelopmentWorkspaceOptions.SectionName)
                    .Get<DevelopmentWorkspaceOptions>();
                Assert.NotNull(options);
                Assert.True(options.Enabled);
                Assert.Equal(1, options.MaxActiveVms);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("DevelopmentWorkspaces__MaxActiveVms", priorLimit);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Relative_or_missing_explicit_file_fails_instead_of_silently_disabling()
    {
        WithEnvironment("workspace.json", () =>
            Assert.Throws<InvalidOperationException>(() =>
                new ConfigurationManager().AddDevelopmentWorkspaceConfiguration()));
        var missing = Path.Combine(FindRepositoryRoot(), ".local", "artifacts", "issue110",
            "packaging", $"missing-{Guid.NewGuid():N}.json");
        WithEnvironment(missing, () =>
            Assert.Throws<FileNotFoundException>(() =>
                new ConfigurationManager().AddDevelopmentWorkspaceConfiguration()));
    }

    private static void WithEnvironment(string? path, Action action)
    {
        const string key = "DevelopmentWorkspaces__ConfigurationFile";
        var prior = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, path);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, prior);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

[CollectionDefinition("Development workspace configuration environment", DisableParallelization = true)]
public sealed class DevelopmentWorkspaceConfigurationEnvironment;
