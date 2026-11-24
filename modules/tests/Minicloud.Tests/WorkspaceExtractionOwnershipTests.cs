// SPDX-License-Identifier: AGPL-3.0-only
extern alias WorkerAssembly;

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Controllers;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

/// <summary>
/// Source-level and assembly-level regression tests enforcing the Minicloud / Municode
/// ownership boundary (issue #256): coding workspaces belong to the independent workspace
/// product while Minicloud retains app hosting, managed databases, GitHub App integration,
/// public CLI/installer compatibility, and read-only retired-workspace PostgreSQL upgrade checks.
/// </summary>
public sealed class WorkspaceExtractionOwnershipTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Solution_and_repository_exclude_extracted_coding_workspace_modules()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "Minicloud.slnx"));

        Assert.DoesNotContain("workspace-gateway", solution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WorkspaceGateway", solution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevelopmentWorkspace", solution, StringComparison.OrdinalIgnoreCase);

        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "modules", "workspace-gateway")));
        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "templates", "development-workspaces")));
        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "modules", "dashboard", "app", "modules", "Workspaces")));
        Assert.False(Directory.Exists(Path.Combine(RepositoryRoot, "modules", "worker", "DevelopmentWorkspaces")));
    }

    [Fact]
    public void Api_and_worker_assemblies_exclude_coding_workspace_surfaces_and_preserve_app_hosting()
    {
        var apiTypes = typeof(ApplicationsController).Assembly.GetTypes();
        Assert.DoesNotContain(apiTypes, t =>
            (t.FullName?.Contains("DevelopmentWorkspace", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.FullName?.Contains("WorkspaceGateway", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.FullName?.Contains("WorkspaceGit", StringComparison.OrdinalIgnoreCase) ?? false));

        var controllerRoutes = apiTypes
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => r.Template))
            .ToArray();
        Assert.DoesNotContain(controllerRoutes, route => route.Contains("workspace", StringComparison.OrdinalIgnoreCase));

        // App-hosting and Minicloud's own GitHub App integration remain present.
        Assert.Contains(apiTypes, t => t == typeof(ApplicationsController));
        Assert.Contains(apiTypes, t => t == typeof(EnvironmentsController));
        Assert.Contains(apiTypes, t => t == typeof(DeploymentsController));
        Assert.Contains(apiTypes, t => t == typeof(DatabasesController));
        Assert.Contains(apiTypes, t => t == typeof(PlansController));
        Assert.Contains(apiTypes, t => t == typeof(ProvidersController));
        Assert.Contains(apiTypes, t => t == typeof(GitHubAppsController));

        var workerTypes = typeof(WorkerAssembly::Minicloud.Worker.WorkerOptions).Assembly.GetTypes();
        Assert.DoesNotContain(workerTypes, t =>
            (t.FullName?.Contains("DevelopmentWorkspace", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.FullName?.Contains("WorkspaceTunnel", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.FullName?.Contains("WorkspaceBootstrap", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (t.FullName?.Contains("T3Workspace", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public void Common_domain_model_scopes_and_actions_exclude_coding_workspaces()
    {
        using var db = new MinicloudDbContext(
            new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql("Host=localhost;Database=ownership_validation")
                .Options);

        var entityNames = db.Model.GetEntityTypes()
            .Select(e => $"{e.ClrType.Name}:{e.GetTableName()}")
            .ToArray();
        Assert.DoesNotContain(entityNames, name =>
            name.Contains("workspace", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(ApiKeyScopes.All, scope =>
            scope.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ApiKeyScopes.CliLoginDefault, scope =>
            scope.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ApiKeyScopes.AppsRead, ApiKeyScopes.All);
        Assert.Contains(ApiKeyScopes.DeploymentsCreate, ApiKeyScopes.All);
        Assert.Contains(ApiKeyScopes.GitHubRead, ApiKeyScopes.All);

        Assert.DoesNotContain(Enum.GetNames<OrganizationAction>(), name =>
            name.Contains("workspace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Retired_workspace_migration_guard_is_isolated_and_classifies_histories_strictly()
    {
        Assert.Equal(7, RetiredWorkspaceMigrationGuard.RetiredWorkspaceMigrations.Count);

        var empty = RetiredWorkspaceMigrationGuard.EvaluateUnmatchedMigrations([]);
        Assert.Equal(UnmatchedMigrationStatus.None, empty.Status);
        Assert.Empty(empty.UnknownMigrations);

        var complete = RetiredWorkspaceMigrationGuard.EvaluateUnmatchedMigrations(
            RetiredWorkspaceMigrationGuard.RetiredWorkspaceMigrations);
        Assert.Equal(UnmatchedMigrationStatus.CompleteRetiredWorkspaceHistory, complete.Status);
        Assert.Empty(complete.UnknownMigrations);

        var partial = RetiredWorkspaceMigrationGuard.EvaluateUnmatchedMigrations(
            RetiredWorkspaceMigrationGuard.RetiredWorkspaceMigrations.Take(6));
        Assert.Equal(UnmatchedMigrationStatus.IncompleteRetiredWorkspaceHistory, partial.Status);
        Assert.Empty(partial.UnknownMigrations);

        var mixedUnknown = RetiredWorkspaceMigrationGuard.EvaluateUnmatchedMigrations(
            RetiredWorkspaceMigrationGuard.RetiredWorkspaceMigrations.Append("20261201000000_FutureMigration"));
        Assert.Equal(UnmatchedMigrationStatus.UnknownMigrations, mixedUnknown.Status);
        Assert.Equal(["20261201000000_FutureMigration"], mixedUnknown.UnknownMigrations);

        var migrationCommandSource = File.ReadAllText(
            Path.Combine(RepositoryRoot, "modules", "api", "Services", "DatabaseMigrationCommand.cs"));
        Assert.DoesNotContain("development_workspace", migrationCommandSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(RetiredWorkspaceMigrationGuard), migrationCommandSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Delivery_bundle_and_public_compatibility_surfaces_match_app_only_ownership()
    {
        var bundle = Path.Combine(RepositoryRoot, "packaging", "server", "bundle");
        Assert.False(File.Exists(Path.Combine(bundle, "compose.workspaces.yml")));
        Assert.False(File.Exists(Path.Combine(bundle, "Caddyfile.workspaces")));
        Assert.False(File.Exists(Path.Combine(bundle, "development-workspaces.example.json")));
        Assert.False(File.Exists(Path.Combine(bundle, "development-workspaces.md")));

        var compose = File.ReadAllText(Path.Combine(bundle, "compose.yml"));
        Assert.DoesNotContain("workspace-gateway", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevelopmentWorkspaces", compose, StringComparison.OrdinalIgnoreCase);

        var openbaoPolicy = File.ReadAllText(Path.Combine(bundle, "openbao-policy.hcl"));
        Assert.DoesNotContain("workspace", openbaoPolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apps/+/services", openbaoPolicy, StringComparison.Ordinal);
        Assert.Contains("providers/+/credentials", openbaoPolicy, StringComparison.Ordinal);
        Assert.Contains("integrations/+/credentials", openbaoPolicy, StringComparison.Ordinal);

        // Public installer and reusable customer deployment workflow compatibility remain intact.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "install.sh")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "install.ps1")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, ".github", "workflows", "customer-deploy.yml")));
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".github", "workflows", "t3-release-candidate.yml")));
    }

    [Fact]
    public void Public_docs_design_and_site_exclude_coding_workspace_ownership()
    {
        var readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        Assert.DoesNotContain("minicloud workspaces", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coding-workspace.png", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docs/first-workspace.md", readme, StringComparison.OrdinalIgnoreCase);

        var design = File.ReadAllText(Path.Combine(RepositoryRoot, "DESIGN.md"));
        Assert.DoesNotContain("## Coding workspaces extension", design, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/workspaces/:workspaceId", design, StringComparison.OrdinalIgnoreCase);

        var siteIndex = File.ReadAllText(Path.Combine(RepositoryRoot, "site", "public", "index.html"));
        Assert.DoesNotContain("minicloud workspaces", siteIndex, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coding-workspace", siteIndex, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
