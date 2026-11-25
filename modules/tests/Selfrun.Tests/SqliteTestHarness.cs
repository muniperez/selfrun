using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Data;

namespace Selfrun.Tests;

public sealed class SqliteTestHarness : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<SelfrunDbContext>? _options;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<SelfrunDbContext>()
            .UseSqlite(_connection)
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

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
