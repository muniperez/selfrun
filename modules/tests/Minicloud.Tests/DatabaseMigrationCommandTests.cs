using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Tests;

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
    public async Task Sqlite_historical_migrations_are_rejected_without_changing_existing_data()
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
        Assert.Contains("Historical SQLite upgrades are unsupported", error.Message);
        Assert.Equal(before, await files.SchemaVersionAsync());
        Assert.Equal("Legacy organization", await files.ScalarAsync("SELECT name FROM organizations WHERE id = 'legacy'"));
        Assert.Equal(1L, await files.ScalarAsync("SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
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
        Assert.Equal("Data Source=minicloud-dev.db",
            MinicloudDatabase.ResolveConnectionString(DatabaseMigrationCommand.ResolveOptions(configuration, environment)));
    }

    [Fact]
    public void Migration_command_uses_configured_postgres_password_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-migration-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "synthetic-password\n");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Postgres",
                ["Database:PasswordFile"] = path,
                ["ConnectionStrings:Minicloud"] = "Host=postgres;Database=minicloud;Username=minicloud;Password=old",
                ["Database:RunMigrationsOnStartup"] = "false"
            }).Build();

            var options = DatabaseMigrationCommand.ResolveOptions(configuration, "Production");
            var connectionString = MinicloudDatabase.ResolveConnectionString(options);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("minicloud", parsed.Database);
            Assert.Equal("minicloud", parsed.Username);
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
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-db-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "p@ss;word\r\n");
            var result = MinicloudDatabase.ApplyPasswordFile(
                "Host=postgres;Database=minicloud;Username=minicloud;Password=old;Pooling=true",
                path);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(result);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("minicloud", parsed.Database);
            Assert.Equal("minicloud", parsed.Username);
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
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-db-password-{Guid.NewGuid():N}");
        Assert.Throws<FileNotFoundException>(() =>
            MinicloudDatabase.ApplyPasswordFile("Host=postgres;Database=minicloud", path));
        File.WriteAllText(path, "\r\n");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                MinicloudDatabase.ApplyPasswordFile("Host=postgres;Database=minicloud", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task SeedAsync(MinicloudDbContext db)
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

        public MinicloudDbContext Context() =>
            new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connectionString).Options);

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
                builder.ConfigureTestServices(services => services.RemoveMinicloudHostedServices());
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
                ["ConnectionStrings:Minicloud"] = connectionString
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
