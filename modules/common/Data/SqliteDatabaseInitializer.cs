using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Minicloud.Common.Data;

public static class SqliteDatabaseInitializer
{
    public static async Task InitializeAsync(MinicloudDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite() || db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("SQLite initialization requires a SQLite database.");

        // The historical migrations and snapshot belong to PostgreSQL. SQLite supports
        // empty initialization and reuse of the exact current EF-created schema, not upgrades.
        var script = db.Database.GenerateCreateScript();
        await using var reference = new SqliteConnection("Data Source=:memory:");
        await reference.OpenAsync(cancellationToken);
        await ExecuteAsync(reference, null, script, cancellationToken);
        var expected = await ReadSchemaAsync(reference, null, cancellationToken);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Reserve the writer before inspecting state so concurrent initializers cannot
            // both observe an empty database. DDL and seed inserts commit together.
            await using var transaction = connection.BeginTransaction(deferred: false);
            var actual = await ReadSchemaAsync(connection, transaction, cancellationToken);
            if (actual.Any(entry => entry.Name == "__EFMigrationsHistory"))
                throw new InvalidOperationException(
                    "SQLite database contains migration history. Historical SQLite upgrades are unsupported; " +
                    "refusing to apply PostgreSQL migrations or rebuild existing data.");

            if (actual.Count == 0)
            {
                await ExecuteAsync(connection, transaction, script, cancellationToken);
            }
            else if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    "SQLite schema does not match the current EF-created schema. SQLite supports empty initialization " +
                    "and matching-schema reuse only; refusing to upgrade or rebuild existing data.");
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<SchemaObject>> ReadSchemaAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT type, name, sql FROM sqlite_schema
            WHERE name NOT GLOB 'sqlite_*'
            ORDER BY type, name
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schema = new List<SchemaObject>();
        while (await reader.ReadAsync(cancellationToken))
            schema.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return schema;
    }

    private sealed record SchemaObject(string Type, string Name, string? Sql);
}
