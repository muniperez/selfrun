using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class MigrationConsistencyTests
{
    private static MinicloudDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_validation")
            .Options);

    [Fact]
    public void PostgresModelMatchesMigrationSnapshot()
    {
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task WorkspaceIdleTimeoutMigrationLeavesExistingRowsUnset()
    {
        const string previous = "20261117100000_DevelopmentWorkspaceVms";
        const string current = "20261119145527_DevelopmentWorkspaceIdleTimeout";
        using var postgres = CreateContext();
        var script = postgres.GetService<IMigrator>().GenerateScript(previous, current);
        Assert.Contains("ALTER TABLE development_workspaces ADD idle_timeout_minutes integer;", script);
        Assert.DoesNotContain("DEFAULT", script);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
            CREATE TABLE development_workspaces (id TEXT PRIMARY KEY);
            INSERT INTO development_workspaces (id) VALUES ('old-workspace');
            """);
        foreach (var migration in db.Database.GetMigrations().TakeWhile(id => id != current))
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, '10.0.5')");

        await db.Database.MigrateAsync(current);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT idle_timeout_minutes FROM development_workspaces WHERE id = 'old-workspace'";
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
    }

    [Fact]
    public void WorkspaceKeepRunningMigrationDefaultsExistingRowsToIdleStop()
    {
        using var postgres = CreateContext();
        var script = postgres.GetService<IMigrator>().GenerateScript(
            "20261119152838_DevelopmentWorkspaceResourceTelemetry", "20261119155230_DevelopmentWorkspaceKeepRunning");
        Assert.Contains("ALTER TABLE development_workspaces ADD keep_running boolean NOT NULL DEFAULT FALSE;", script);
    }

    [Fact]
    public void WorkspaceDeletionMigrationReleasesNameAndKeyAndRollsBackSafely()
    {
        const string previous = "20261119155230_DevelopmentWorkspaceKeepRunning";
        const string current = "20261119160815_DevelopmentWorkspaceDeletion";
        using var postgres = CreateContext();
        var migrator = postgres.GetService<IMigrator>();
        var up = migrator.GenerateScript(previous, current);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_development_workspaces_organization_id_idempotency_key\" ON development_workspaces (organization_id, idempotency_key) WHERE volume_status <> 'deleted';", up);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_development_workspaces_organization_id_name\" ON development_workspaces (organization_id, name) WHERE volume_status <> 'deleted';", up);
        var down = migrator.GenerateScript(current, previous);
        var rename = down.IndexOf("SET name = id, idempotency_key = id WHERE volume_status = 'deleted'", StringComparison.Ordinal);
        Assert.InRange(rename, 0, down.IndexOf("CREATE UNIQUE INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void DomainMigrationCreatesColumnOnceAndReconciliationPreservesIt()
    {
        using var db = CreateContext();
        var migrator = db.GetService<IMigrator>();
        const string original = "20260914143000_AddDomainIsPrimary";
        const string repair = "20260918084933_ReconcileDomainModelSnapshot";
        var upgrade = migrator.GenerateScript("20260914121500_AddDeploymentServiceSha256", repair);
        Assert.Equal(1, upgrade.Split("ADD is_primary").Length - 1);
        var reconciliation = migrator.GenerateScript(original, repair);
        Assert.DoesNotContain("ALTER TABLE", reconciliation);
        var rollback = migrator.GenerateScript(repair, original);
        Assert.DoesNotContain("DROP COLUMN", rollback);
    }
}
