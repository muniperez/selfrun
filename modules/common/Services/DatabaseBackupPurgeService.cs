using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Auth;
using Selfrun.Common.Contracts;
using Selfrun.Common.Data;
using Selfrun.Common.Models;

namespace Selfrun.Common.Services;

public sealed class DatabaseBackupPurgeService(SelfrunDbContext db, DatabaseManagementService management)
{
    public async Task<DatabaseOperation> QueueAsync(string databaseId, DatabaseBackupPurgeRequest request, ActorIdentity actor, CancellationToken ct)
    {
        var database = await management.GetAsync(databaseId, actor, true, ct);
        if (!string.Equals(database.Name, request.Confirmation, StringComparison.Ordinal))
            throw new ArgumentException("Type the exact database name to confirm permanent backup removal.");
        var policy = await db.DatabaseBackupPolicies.SingleOrDefaultAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct)
            ?? throw new InvalidOperationException("Backup policy is unavailable.");
        if (policy.Enabled || policy.KeepLastCopies)
            throw new InvalidOperationException("Disable scheduled backups and indefinite retention before purging backups.");
        if (await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId &&
            (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct))
            throw new InvalidOperationException("Wait for active database operations to finish before purging backups.");
        database.Revision++;
        policy.Revision++;
        var operation = management.Queue(database.OrganizationId, database.Id, database.ServerId, "purge-backups", new { confirmation = request.Confirmation }, actor);
        await db.SaveChangesAsync(ct);
        return operation;
    }
}
