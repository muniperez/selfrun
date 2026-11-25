using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Selfrun.Api.Services;
using Selfrun.Common.Data;
using Selfrun.Common.Models;

namespace Selfrun.Tests;

public sealed class DatabaseMigrationCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sqlite_initialization_preserves_data_and_is_idempotent(bool startup, bool existing)
    {
        using var files = new SqliteFiles();
        if (existing)
        {
            await using var db = files.Context();
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
        }

        await files.InitializeAsync(startup);
        if (!existing)
        {
            await using var db = files.Context();
            Assert.Empty(await db.Organizations.ToListAsync());
            Assert.Single(await db.MaintenanceAuthorities.ToListAsync());
            await SeedAsync(db);
        }

        var before = await files.SchemaVersionAsync();
        await files.InitializeAsync(startup);
        Assert.Equal(before, await files.SchemaVersionAsync());
        await using var check = files.Context();
        var member = await check.OrganizationMembers.Include(x => x.User).Include(x => x.Organization).SingleAsync();
        Assert.Equal("existing-member", member.Id);
        Assert.Equal("existing-organization", member.Organization.Id);
        Assert.Equal("Preserved organization", member.Organization.Name);
        Assert.Equal("operator@example.test", member.User.Email);
        Assert.Equal("Preserved operator", member.User.DisplayName);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), member.User.CreatedAt);
        Assert.Equal(OrganizationRole.Owner, member.Role);
        Assert.Equal(9, (await check.MaintenanceAuthorities.SingleAsync()).Epoch);
        Assert.Equal(0L, await files.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = '__EFMigrationsHistory'"));
    }

    [Fact]
    public async Task Sqlite_unrecognized_old_migration_schema_is_rejected_without_changing_existing_data()
    {
        using var files = new SqliteFiles();
        await using (var db = files.Context())
        {
            await db.Database.MigrateAsync("20260430111924_InitialControlPlane");
            db.Organizations.Add(new Organization { Id = "legacy", Name = "Legacy organization", Slug = "legacy" });
            await db.SaveChangesAsync();
        }

        var before = await files.SchemaVersionAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => files.InitializeAsync(startup: false));
        Assert.Contains("not a recognized upgrade prerequisite", error.Message);
        Assert.Equal(before, await files.SchemaVersionAsync());
        Assert.Equal("Legacy organization", await files.ScalarAsync("SELECT name FROM organizations WHERE id = 'legacy'"));
        Assert.Equal(1L, await files.ScalarAsync("SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Sqlite_full_historical_model_upgrades_populated_data_and_retries_idempotently(bool startup, bool history)
    {
        using var files = new SqliteFiles();
        await files.CreateHistoricalAsync(history);
        var original = await files.SnapshotAsync();

        await files.InitializeAsync(startup);
        Assert.Equal(original, await files.SnapshotAsync(original.Keys));
        await using (var db = files.Context())
        {
            var user = await db.Users.SingleAsync();
            Assert.Equal("existing-user", user.Id);
            Assert.Null(user.KratosIdentityId);
            Assert.False(user.KeepSignedIn);
            Assert.Single(await db.OrganizationMembers.ToListAsync());
            Assert.Empty(await db.BootstrapRecords.ToListAsync());
            Assert.Empty(await db.BrowserSessions.ToListAsync());
            Assert.Equal("hash-preserved", (await db.ApiKeys.SingleAsync()).KeyHash);
            Assert.Equal("runtime-hash-preserved", (await db.RuntimeTokens.SingleAsync()).TokenHash);
            Assert.Equal(3, await db.Apps.CountAsync());
            Assert.Equal("existing-application", (await db.Apps.SingleAsync(x => x.Id == "existing-app")).ApplicationId);
            Assert.All(await db.Apps.ToListAsync(), app =>
            {
                Assert.Equal("none", app.SshAccessMode);
                Assert.Equal("not_registered", app.SshRegistrationState);
                Assert.Equal(0, app.SshRevision);
            });
            Assert.Equal("20261110.1", (await db.Deployments.SingleAsync(x => x.Id == "deployment-z")).Number);
            Assert.Equal("20261110.2", (await db.Deployments.SingleAsync(x => x.Id == "deployment-a")).Number);
            Assert.Equal("20261110.3", (await db.Deployments.SingleAsync(x => x.Id == "deployment-b")).Number);
            Assert.Equal("20261110.1", (await db.Deployments.SingleAsync(x => x.Id == "other-org-deployment")).Number);
            Assert.Equal("20261111.1", (await db.Deployments.SingleAsync(x => x.Id == "next-day-deployment")).Number);
            Assert.Equal(3, await db.DeploymentNumberCounters.CountAsync());
            Assert.Equal(3, (await db.DeploymentNumberCounters.SingleAsync(x =>
                x.OrganizationId == "existing-organization" && x.UtcDate == new DateOnly(2026, 11, 10))).LastValue);
            Assert.Single(await db.MaintenanceAuthorities.ToListAsync());
        }
        var upgraded = await files.SnapshotAsync();
        var version = await files.SchemaVersionAsync();
        // Retry both entry points against the upgraded schema, including retained history.
        await files.InitializeAsync(!startup);
        await files.InitializeAsync(startup);
        Assert.Equal(version, await files.SchemaVersionAsync());
        Assert.Equal(upgraded, await files.SnapshotAsync(upgraded.Keys));
        Assert.Equal(history ? 1L : 0L, await files.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = '__EFMigrationsHistory'"));
    }

    [Fact]
    public async Task Sqlite_historical_conversion_failure_rolls_back_and_can_be_retried()
    {
        using var files = new SqliteFiles();
        await files.CreateHistoricalAsync(history: false);
        await using (var db = files.Context())
            await db.Database.ExecuteSqlRawAsync("UPDATE deployments SET created_at = 'invalid-date' WHERE id = 'deployment-a'");
        var original = await files.SnapshotAsync();
        var version = await files.SchemaVersionAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => files.InitializeAsync(startup: false));
        Assert.Contains("invalid creation timestamp", error.Message);
        Assert.Equal(version, await files.SchemaVersionAsync());
        Assert.Equal(original, await files.SnapshotAsync(original.Keys));
        Assert.Equal(0L, await files.ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('maintenance_authority', 'browser_sessions', '__sqlite_upgrade_numbers')"));
        await using (var db = files.Context())
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE deployments SET created_at = '2026-11-10 00:00:00.0000001+00:00' WHERE id = 'deployment-a'");
        await files.InitializeAsync(startup: false);
        await files.InitializeAsync(startup: false);
        Assert.Equal("20261110.2", await files.ScalarAsync("SELECT number FROM deployments WHERE id = 'deployment-a'"));
    }

    [Fact]
    public async Task Concurrent_sqlite_historical_initializers_upgrade_once()
    {
        using var files = new SqliteFiles();
        await files.CreateHistoricalAsync(history: false);
        var original = await files.SnapshotAsync();
        await Task.WhenAll(files.InitializeAsync(startup: false), files.InitializeAsync(startup: false));
        Assert.Equal(original, await files.SnapshotAsync(original.Keys));
        Assert.Equal(3L, await files.ScalarAsync("SELECT COUNT(*) FROM deployment_number_counters"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_historical_upgrade_restores_connection_foreign_key_enforcement(bool failure)
    {
        using var files = new SqliteFiles();
        await files.CreateHistoricalAsync(history: false);
        await using var connection = files.Connection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        await command.ExecuteNonQueryAsync();
        await using var db = new SelfrunDbContext(new DbContextOptionsBuilder<SelfrunDbContext>().UseSqlite(connection).Options);
        if (failure)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE deployments SET created_at = 'invalid-date' WHERE id = 'deployment-a'");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteDatabaseInitializer.InitializeAsync(db));
        }
        else
        {
            await SqliteDatabaseInitializer.InitializeAsync(db);
        }
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_history_without_tables_only_initializes_when_no_migrations_were_applied(bool applied)
    {
        using var files = new SqliteFiles();
        await using (var db = files.Context())
        {
            await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateScript());
            if (applied)
                await db.Database.ExecuteSqlRawAsync(
                    """INSERT INTO "__EFMigrationsHistory" VALUES ('20260430111924_InitialControlPlane', '10.0.5')""");
        }
        if (applied)
        {
            var original = await files.SnapshotAsync();
            var version = await files.SchemaVersionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => files.InitializeAsync(startup: false));
            Assert.Equal(version, await files.SchemaVersionAsync());
            Assert.Equal(original, await files.SnapshotAsync(original.Keys));
        }
        else
        {
            await files.InitializeAsync(startup: false);
            await files.InitializeAsync(startup: false);
            await using var db = files.Context();
            Assert.Single(await db.MaintenanceAuthorities.ToListAsync());
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
        }
    }

    [Theory]
    [InlineData("DROP INDEX \"IX_api_keys_key_prefix\"")]
    [InlineData("CREATE TABLE unexpected (id TEXT PRIMARY KEY)")]
    [InlineData("UPDATE organization_members SET user_id = 'missing-user'")]
    [InlineData("INSERT INTO \"__EFMigrationsHistory\" VALUES ('99999999999999_Unknown', '10.0.5')")]
    public async Task Sqlite_historical_unknown_schema_history_or_invalid_relationship_is_unchanged(string change)
    {
        using var files = new SqliteFiles();
        await files.CreateHistoricalAsync(history: true);
        await using (var connection = files.Connection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF; " + change;
            await command.ExecuteNonQueryAsync();
        }
        var original = await files.SnapshotAsync();
        var version = await files.SchemaVersionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.InitializeAsync(startup: false));
        Assert.Equal(version, await files.SchemaVersionAsync());
        Assert.Equal(original, await files.SnapshotAsync(original.Keys));
        Assert.Equal("old-firebase-id", await files.ScalarAsync("SELECT firebase_uid FROM users WHERE id = 'existing-user'"));
    }

    [Theory]
    [InlineData("ALTER TABLE users DROP COLUMN display_name")]
    [InlineData("DROP INDEX \"IX_organizations_slug\"")]
    [InlineData("CREATE TABLE unknown_state (id TEXT PRIMARY KEY)")]
    public async Task Sqlite_incompatible_schema_is_rejected_without_repair_or_data_loss(string change)
    {
        using var files = new SqliteFiles();
        await using (var db = files.Context())
        {
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
            await db.Database.ExecuteSqlRawAsync(change);
        }

        var before = await files.SchemaVersionAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => files.InitializeAsync(startup: false));
        Assert.Contains("refusing to upgrade or rebuild existing data", error.Message);
        Assert.Equal(before, await files.SchemaVersionAsync());
        Assert.Equal("operator@example.test", await files.ScalarAsync("SELECT email FROM users WHERE id = 'existing-user'"));
        Assert.Equal(1L, await files.ScalarAsync("SELECT COUNT(*) FROM organization_members"));
    }

    [Fact]
    public async Task Sqlite_startup_with_initialization_disabled_does_not_create_schema()
    {
        using var files = new SqliteFiles();
        await files.StartApiAsync(runMigrations: false);
        Assert.Equal(0L, await files.ScalarAsync("SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*'"));
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", "sQLite")]
    public void Standalone_sqlite_default_matches_the_API(string environment, string? provider)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Provider"] = provider }).Build();
        Assert.Equal("Data Source=selfrun-dev.db",
            SelfrunDatabase.ResolveConnectionString(DatabaseMigrationCommand.ResolveOptions(configuration, environment)));
    }

    [Fact]
    public void Migration_command_uses_configured_postgres_password_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"selfrun-migration-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "synthetic-password\n");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Postgres",
                ["Database:PasswordFile"] = path,
                ["ConnectionStrings:Selfrun"] = "Host=postgres;Database=selfrun;Username=selfrun;Password=old",
                ["Database:RunMigrationsOnStartup"] = "false"
            }).Build();

            var options = DatabaseMigrationCommand.ResolveOptions(configuration, "Production");
            var connectionString = SelfrunDatabase.ResolveConnectionString(options);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("selfrun", parsed.Database);
            Assert.Equal("selfrun", parsed.Username);
            Assert.Equal("synthetic-password", parsed.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Database_password_file_overrides_only_the_password_and_preserves_connection_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"selfrun-db-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "p@ss;word\r\n");
            var result = SelfrunDatabase.ApplyPasswordFile(
                "Host=postgres;Database=selfrun;Username=selfrun;Password=old;Pooling=true",
                path);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(result);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("selfrun", parsed.Database);
            Assert.Equal("selfrun", parsed.Username);
            Assert.Equal("p@ss;word", parsed.Password);
            Assert.True(parsed.Pooling);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Configured_missing_or_empty_database_password_file_fails_explicitly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"selfrun-db-password-{Guid.NewGuid():N}");
        Assert.Throws<FileNotFoundException>(() =>
            SelfrunDatabase.ApplyPasswordFile("Host=postgres;Database=selfrun", path));
        File.WriteAllText(path, "\r\n");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                SelfrunDatabase.ApplyPasswordFile("Host=postgres;Database=selfrun", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task SeedAsync(SelfrunDbContext db)
    {
        var created = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = "existing-member",
            Organization = new Organization
            {
                Id = "existing-organization",
                Name = "Preserved organization",
                Slug = "existing",
                CreatedAt = created,
                UpdatedAt = created
            },
            User = new User
            {
                Id = "existing-user",
                Email = "operator@example.test",
                DisplayName = "Preserved operator",
                CreatedAt = created,
                UpdatedAt = created
            },
            Role = OrganizationRole.Owner,
            CreatedAt = created,
            UpdatedAt = created
        });
        (await db.MaintenanceAuthorities.SingleAsync()).Epoch = 9;
        await db.SaveChangesAsync();
    }

    private sealed class SqliteFiles : IDisposable
    {
        private readonly string directory;
        private readonly string connectionString;

        public SqliteFiles()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, ".git")) &&
                   !File.Exists(Path.Combine(root.FullName, ".git")))
                root = root.Parent;
            if (root is null)
                throw new InvalidOperationException("Cannot locate repository artifact directory.");
            directory = Path.Combine(root.FullName, ".local", "artifacts", "issue-255", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            connectionString = $"Data Source={Path.Combine(directory, "state.db")};Pooling=False";
        }

        public SelfrunDbContext Context() =>
            new(new DbContextOptionsBuilder<SelfrunDbContext>().UseSqlite(connectionString).Options);

        public SqliteConnection Connection() => new(connectionString);

        public async Task CreateHistoricalAsync(bool history)
        {
            await using var db = Context();
            await using var stream = typeof(DatabaseMigrationCommandTests).Assembly.GetManifestResourceStream(
                "Selfrun.Tests.Fixtures.Sqlite-feb8073.sql")
                ?? throw new InvalidOperationException("Historical SQLite fixture is missing.");
            using var reader = new StreamReader(stream);
            await db.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync());
            if (history)
            {
                // Metadata can remain from a previous EF migration attempt. The schema itself,
                // not these rows, identifies the full historical EnsureCreated prerequisite.
                await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateScript());
                await db.Database.ExecuteSqlRawAsync(
                    """INSERT INTO "__EFMigrationsHistory" VALUES ('20260430111924_InitialControlPlane', '10.0.5')""");
            }
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO users VALUES ('existing-user', 'old-firebase-id', 'operator@example.test', 'Preserved operator',
                    '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO organizations VALUES
                    ('existing-organization', 'Preserved organization', 'existing', '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00'),
                    ('other-organization', 'Other organization', 'other', '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO organization_members VALUES ('existing-member', 'existing-organization', 'existing-user', 'Owner',
                    '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO api_keys VALUES ('existing-key', 'existing-organization', 'deploy', 'prefix', 'hash-preserved', '["deploy"]',
                    NULL, NULL, NULL, '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO cli_login_sessions VALUES ('existing-cli-session', 'existing-organization', 'existing-user', 'exchanged',
                    '["deploy"]', 'existing-key', NULL, '2026-01-02 03:04:05+00:00', '2026-01-02 04:04:05+00:00',
                    '2026-01-02 03:05:05+00:00', '2026-01-02 03:06:05+00:00');
                INSERT INTO applications (id, organization_id, name, slug, normalized_slug, default_environment_id, legacy_root_app_id, created_at, updated_at)
                    VALUES ('existing-application', 'existing-organization', 'Preserved application', 'sample', 'sample', 'existing-app',
                        'existing-app', '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO apps (id, organization_id, application_id, name, slug, parent_app_id, branch_name, environment_name,
                    normalized_environment_name, environment_kind, runtime_parent_slug, source_branch, source_environment_id,
                    environment_key, plan, database, created_at, updated_at)
                    VALUES
                    ('existing-app', 'existing-organization', 'existing-application', 'Production', 'sample', NULL, NULL, 'production',
                        'production', 'persistent', NULL, NULL, NULL, 'sample:production', 'P0', 'Sqlite', '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00'),
                    ('preview-app', 'existing-organization', 'existing-application', 'Preview', 'sample-preview', 'existing-app', 'feature',
                        'preview', 'preview', 'preview', 'sample', 'feature', 'existing-app', 'sample:preview', 'P0', 'Sqlite',
                        '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00'),
                    ('other-app', 'other-organization', NULL, 'Other app', 'other', NULL, NULL, 'production', 'production', 'persistent',
                        NULL, NULL, NULL, 'other:production', 'P0', 'None', '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO app_secrets VALUES ('existing-secret', 'existing-organization', 'existing-app', 'TOKEN', 'synthetic-ciphertext',
                    '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO runtime_servers (id, organization_id, app_id, provider, hostname, created_at, updated_at)
                    VALUES ('existing-runtime', 'existing-organization', 'existing-app', 'Hetzner', 'runtime.example.test',
                        '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO runtime_tokens VALUES ('existing-runtime-token', 'existing-runtime', 'runtime-prefix', 'runtime-hash-preserved',
                    NULL, NULL, NULL, '2026-01-02 03:04:05+00:00', '2026-01-02 03:04:05+00:00');
                INSERT INTO deployments (id, provider_creation_attempted, organization_id, app_id, database, status, ownership_token,
                    created_at, updated_at)
                    VALUES
                    ('deployment-z', 0, 'existing-organization', 'existing-app', 'Sqlite', 'Succeeded', 'synthetic-owner-token',
                        '2026-11-10 02:00:00+02:00', '2026-11-10 02:00:00+02:00'),
                    ('deployment-b', 0, 'existing-organization', 'preview-app', 'Sqlite', 'Succeeded', NULL,
                        '2026-11-10 00:00:00.0000001+00:00', '2026-11-10 00:00:00.0000001+00:00'),
                    ('deployment-a', 0, 'existing-organization', 'existing-app', 'Sqlite', 'Succeeded', NULL,
                        '2026-11-10 00:00:00.0000001+00:00', '2026-11-10 00:00:00.0000001+00:00'),
                    ('other-org-deployment', 0, 'other-organization', 'other-app', 'None', 'Succeeded', NULL,
                        '2026-11-10 00:00:00+00:00', '2026-11-10 00:00:00+00:00'),
                    ('next-day-deployment', 0, 'existing-organization', 'existing-app', 'Sqlite', 'Succeeded', NULL,
                        '2026-11-10 22:00:00-02:00', '2026-11-10 22:00:00-02:00');
                INSERT INTO deployment_events VALUES ('existing-event', 'deployment-z', 'Info', 'Preserved deployment event',
                    '{{"synthetic":true}}', '2026-11-10 02:00:00+02:00');
                """);
        }

        public async Task<SortedDictionary<string, string>> SnapshotAsync(IEnumerable<string>? projections = null)
        {
            await using var connection = Connection();
            await connection.OpenAsync();
            var queries = projections?.ToList() ?? [];
            if (projections is null)
            {
                var tables = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT GLOB 'sqlite_*' ORDER BY name";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        tables.Add(reader.GetString(0));
                }
                foreach (var table in tables)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"PRAGMA table_info(\"{table}\")";
                    await using var reader = await command.ExecuteReaderAsync();
                    var columns = new List<string>();
                    while (await reader.ReadAsync())
                        if (reader.GetString(1) != "firebase_uid")
                            columns.Add("\"" + reader.GetString(1) + "\"");
                    queries.Add($"SELECT {string.Join(", ", columns)} FROM \"{table}\" ORDER BY {string.Join(", ", columns)}");
                }
            }
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var query in queries)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<object[]>();
                while (await reader.ReadAsync())
                {
                    var row = new object[reader.FieldCount];
                    reader.GetValues(row);
                    rows.Add(row);
                }
                result[query] = System.Text.Json.JsonSerializer.Serialize(rows);
            }
            return result;
        }

        public async Task InitializeAsync(bool startup)
        {
            if (startup)
                await StartApiAsync(runMigrations: true);
            else
                await DatabaseMigrationCommand.RunAsync(Configuration(), "Development");
        }

        public async Task StartApiAsync(bool runMigrations)
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(Configuration())
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Database:RunMigrationsOnStartup"] = runMigrations.ToString(),
                        ["Database:PasswordFile"] = "",
                        ["ServerRelease:ManifestPath"] = null,
                        ["OpenBao:Address"] = ""
                    }));
                builder.ConfigureTestServices(services => services.RemoveSelfrunHostedServices());
            });
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://api.example.test")
            });
            using var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:Selfrun"] = connectionString
            }).Build();

        public Task<object?> SchemaVersionAsync() => ScalarAsync("PRAGMA schema_version");

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
