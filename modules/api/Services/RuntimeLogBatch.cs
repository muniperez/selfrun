using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Redaction;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

internal static class RuntimeLogBatch
{
    public static async Task<List<int>> FindNewAsync(
        MinicloudDbContext db, string serverId, IReadOnlyList<RuntimeLogIngestRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var parameters = new List<object> { serverId };
        var rows = string.Join(", ", records.Select((record, index) =>
            AddValues(parameters, index, record.Source, record.Cursor, record.ObservedAt)));

        // Use database equality (including timestamp precision) for both new and stored keys.
        const string sql = """
            WITH batch(input_index, source, cursor, observed_at) AS (VALUES __ROWS__)
            SELECT MIN(input_index) AS "Value"
            FROM batch
            WHERE NOT EXISTS (
                SELECT 1 FROM runtime_log_records AS stored
                WHERE stored.runtime_server_id = {0} AND stored.source = batch.source
                    AND stored.cursor = batch.cursor AND stored.observed_at = batch.observed_at
            )
            GROUP BY source, cursor, observed_at
            ORDER BY MIN(input_index)
            """;
        return await db.Database.SqlQueryRaw<int>(sql.Replace("__ROWS__", rows), parameters.ToArray()).ToListAsync(cancellationToken);
    }

    public static async Task<int> SaveAsync(
        MinicloudDbContext db, RuntimeServer server, IReadOnlyList<RuntimeLogIngestRecord> records,
        IReadOnlyList<int> pending, IIdGenerator ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        var parameters = new List<object> { server.OrganizationId, server.AppId, server.Id, now };
        var rows = string.Join(", ", pending.Select(index =>
        {
            var record = records[index];
            var stream = string.Equals(record.Stream, "stderr", StringComparison.OrdinalIgnoreCase) ? "stderr" : "stdout";
            return AddValues(parameters, ids.NewId("rtlog"), record.Source, record.Service, stream, record.Cursor,
                LogRedactor.Redact(record.Message), record.ObservedAt);
        }));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // Concurrent batches can pass the lookup together. Insert in key order and count only successful writes.
        // WHERE true disambiguates ON CONFLICT from a join condition in SQLite.
        const string sql = """
            WITH batch(id, source, service, stream, cursor, content, observed_at) AS (VALUES __ROWS__)
            INSERT INTO runtime_log_records
                (id, organization_id, app_id, runtime_server_id, source, service, stream, cursor, content, observed_at, received_at)
            SELECT id, {0}, {1}, {2}, source, service, stream, cursor, content, observed_at, {3}
            FROM batch WHERE true
            ORDER BY source, cursor, observed_at
            ON CONFLICT (runtime_server_id, source, cursor, observed_at) DO NOTHING
            """;
        var accepted = await db.Database.ExecuteSqlRawAsync(sql.Replace("__ROWS__", rows), parameters, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private static string AddValues(List<object> parameters, params object?[] values) =>
        "(" + string.Join(", ", values.Select(value =>
        {
            if (value is null)
                return "NULL";
            var placeholder = $"{{{parameters.Count}}}";
            parameters.Add(value);
            return placeholder;
        })) + ")";
}
