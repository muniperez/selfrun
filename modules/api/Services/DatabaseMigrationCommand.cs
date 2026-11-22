// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Minicloud.Common.Data;

namespace Minicloud.Api.Services;

public static class DatabaseMigrationCommand
{
    // The complete workspace history from the last workspace-bearing server release.
    // These migrations are recognized only as already applied, never run on an app-only database.
    private static readonly string[] RetiredWorkspaceMigrations =
    [
        "20261117100000_DevelopmentWorkspaceVms",
        "20261119145527_DevelopmentWorkspaceIdleTimeout",
        "20261119152838_DevelopmentWorkspaceResourceTelemetry",
        "20261119155230_DevelopmentWorkspaceKeepRunning",
        "20261119160815_DevelopmentWorkspaceDeletion",
        "20261120020000_DevelopmentWorkspaceTerminalDeletionIdentity",
        "20261120134056_DevelopmentWorkspaceVmResourceHistory",
    ];

    public static MinicloudDatabaseOptions ResolveOptions(IConfiguration configuration, string environmentName)
    {
        var options = new MinicloudDatabaseOptions
        {
            Provider = configuration["Database:Provider"] ?? (environmentName == "Development" ? "Sqlite" : "Postgres"),
            MinicloudConnectionString = configuration.GetConnectionString("Minicloud"),
            DefaultConnectionString = configuration.GetConnectionString("DefaultConnection"),
            DatabaseConnectionString = configuration.GetConnectionString("Database"),
            DatabaseUrl = configuration["DATABASE_URL"],
            PasswordFile = configuration["Database:PasswordFile"]
        };
        if (options.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            options.FallbackConnectionString = "Data Source=minicloud-dev.db";
        return options;
    }

    public static async Task RunAsync(
        IConfiguration configuration,
        string environmentName,
        CancellationToken cancellationToken = default)
    {
        var options = ResolveOptions(configuration, environmentName);
        var connectionString = MinicloudDatabase.ResolveConnectionString(options);
        var dbOptions = new DbContextOptionsBuilder<MinicloudDbContext>();
        MinicloudDatabase.ConfigureDbContext(dbOptions, options.Provider, connectionString);
        await using var db = new MinicloudDbContext(dbOptions.Options);
        if (db.Database.IsSqlite())
        {
            await SqliteDatabaseConfigurator.ConfigureDatabaseAsync(db);
            await SqliteDatabaseInitializer.InitializeAsync(db, cancellationToken);
            return;
        }

        if (!db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        await MigratePostgresAsync(db, cancellationToken);
    }

    internal static async Task MigratePostgresAsync(MinicloudDbContext db, CancellationToken cancellationToken = default)
    {
        // EF's history-table lock does not cover first-run schema creation, so concurrent
        // one-shot migrators serialize on a session advisory lock held on this connection.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(5577006791947779410)", cancellationToken);
            try
            {
                await RejectUnrecognizedStateAsync(db, cancellationToken);
                await db.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(5577006791947779410)", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    // Refuse to adopt a populated database that this release did not create, or one
    // already migrated by a newer release.
    public static async Task RejectUnrecognizedStateAsync(MinicloudDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql() || !await db.Database.CanConnectAsync(cancellationToken))
        {
            return;
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    to_regclass('public."__EFMigrationsHistory"') IS NOT NULL,
                    EXISTS (
                        SELECT 1 FROM pg_catalog.pg_class c
                        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f'))
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var hasHistory = reader.GetBoolean(0);
            var hasObjects = reader.GetBoolean(1);
            if (!hasHistory && hasObjects)
            {
                throw new InvalidOperationException(
                    "The Minicloud database contains objects without migration history; refusing to adopt unknown populated state.");
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unmatched = (await db.Database.GetAppliedMigrationsAsync(cancellationToken))
            .Where(id => !known.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var unknown = unmatched.Except(RetiredWorkspaceMigrations, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Minicloud database has migrations unknown to this release ({string.Join(", ", unknown)}); refusing to downgrade.");
        }

        if (unmatched.Count == 0)
            return;

        if (!unmatched.SetEquals(RetiredWorkspaceMigrations))
        {
            throw new InvalidOperationException(
                "The Minicloud database has an incomplete workspace migration history. Finish upgrading it with the workspace-bearing release before retrying.");
        }

        // Verify retirement without modifying legacy tables or trusting their absence.
        // Querying the tables directly also fails closed if required legacy tables are missing.
        var hasUnretiredWorkspaces = await db.Database.SqlQueryRaw<bool>("""
            SELECT (
                EXISTS (SELECT 1 FROM public.development_workspaces
                    WHERE volume_status <> 'deleted' OR provider_volume_id IS NOT NULL OR volume_attached_vm_id IS NOT NULL)
                OR EXISTS (SELECT 1 FROM public.development_workspace_vms
                    WHERE phase <> 'deleted' OR desired_state <> 'stopped' OR compute_reserved OR start_after_stop OR gateway_ready
                    OR provider_server_id IS NOT NULL OR provider_ip_id IS NOT NULL OR provider_firewall_id IS NOT NULL
                    OR runtime_credential_ref IS NOT NULL)
                OR EXISTS (SELECT 1 FROM public.development_workspace_operations WHERE status IN ('pending', 'running'))
                OR EXISTS (SELECT 1 FROM public.development_workspace_gateway_routes)
                OR EXISTS (SELECT 1 FROM public.development_workspace_gateway_sessions)
                OR EXISTS (SELECT 1 FROM public.development_workspace_task_leases)
            ) AS "Value"
            """).SingleAsync(cancellationToken);
        if (hasUnretiredWorkspaces)
        {
            throw new InvalidOperationException(
                "The Minicloud database contains workspaces that have not been retired. Permanently delete all workspaces with the workspace-bearing release before upgrading.");
        }
    }
}
