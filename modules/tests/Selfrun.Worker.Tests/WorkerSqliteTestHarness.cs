using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Contracts;
using Selfrun.Common.Services;
using Selfrun.Common.Providers;
using Microsoft.Extensions.Options;

namespace Selfrun.Worker.Tests;

public sealed class WorkerSqliteTestHarness : IAsyncLifetime
{
    private readonly string _connectionString =
        $"Data Source=worker-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection? _connection;
    private DbContextOptions<SelfrunDbContext>? _options;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<SelfrunDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public SelfrunDbContext CreateDbContext()
    {
        if (_options is null)
        {
            throw new InvalidOperationException("SQLite test harness has not been initialized.");
        }

        return new SelfrunDbContext(_options);
    }

    public IServiceScopeFactory CreateScopeFactory(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDbContext());
        services.AddScoped<IAppServiceSecretResolver, EmptySecretResolver>();
        services.AddScoped<ControlPlaneAuthorization>();
        services.AddSingleton<IIdGenerator, IdGenerator>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ProviderAccountClientFactory>(_ =>
            new ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!));
        services.AddScoped<ProviderSshKeyService>();
        services.AddSingleton(new SecretEncryptionService(Options.Create(new SecretEncryptionOptions { EncryptionKey = "synthetic-worker-test-key" })));
        configure?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class EmptySecretResolver : IAppServiceSecretResolver
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveForDeploymentAsync(
            string appId, IReadOnlyList<DeploymentServiceRequest> services, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(
                new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    public async Task<(Organization Org, SelfrunApp App)> SeedOrgAndAppAsync(string name = "test")
    {
        await using var db = CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var org = new Organization
        {
            Id = $"org_{name}",
            Name = $"Org {name}",
            Slug = $"org-{name}-{Guid.NewGuid():N}"[..20],
            CreatedAt = now,
            UpdatedAt = now
        };
        var app = new SelfrunApp
        {
            Id = $"app_{name}",
            OrganizationId = org.Id,
            Name = $"App {name}",
            Slug = $"app-{name}-{Guid.NewGuid():N}"[..20],
            Plan = AppPlan.P0V0,
            Database = DatabaseMode.None,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Organizations.Add(org);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return (org, app);
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
