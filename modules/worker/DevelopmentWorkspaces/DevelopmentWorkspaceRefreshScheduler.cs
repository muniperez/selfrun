using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Worker.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceRefreshScheduler
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? lastVmId;

    internal async Task<DevelopmentWorkspaceVm?> NextAsync(MinicloudDbContext db, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var cursor = lastVmId;
            var vm = await Candidates(db, cursor).SingleOrDefaultAsync(ct);
            if (vm is null && cursor is not null)
                vm = await Candidates(db, null).SingleOrDefaultAsync(ct);
            // Advance before external I/O, including failures. No per-generation cache
            // can suppress a replacement generation, and scoped processors share one cursor.
            lastVmId = vm?.Id;
            return vm;
        }
        finally { gate.Release(); }
    }

    private static IQueryable<DevelopmentWorkspaceVm> Candidates(MinicloudDbContext db, string? cursor)
    {
        var eligible = db.DevelopmentWorkspaceVms.AsNoTracking()
            .Where(x => x.Phase == "ready" && x.DesiredState == "running");
        if (cursor is not null) eligible = eligible.Where(x => string.Compare(x.Id, cursor) > 0);
        return eligible.OrderBy(x => x.Id).Take(1);
    }
}
