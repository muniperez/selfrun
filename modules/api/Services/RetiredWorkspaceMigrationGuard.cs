// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Api.Services;

internal enum UnmatchedMigrationStatus
{
    None,
    UnknownMigrations,
    IncompleteRetiredWorkspaceHistory,
    CompleteRetiredWorkspaceHistory,
}

internal sealed record UnmatchedMigrationEvaluation(
    UnmatchedMigrationStatus Status,
    IReadOnlyList<string> UnknownMigrations);

/// <summary>
/// Isolates read-only PostgreSQL upgrade compatibility for databases upgraded in place
/// from the last workspace-bearing release (server-v0.0.1-rc.4).
/// These migrations are recognized only as already applied, never created or executed by app-only Minicloud.
/// </summary>
internal static class RetiredWorkspaceMigrationGuard
{
    internal static readonly IReadOnlyList<string> RetiredWorkspaceMigrations =
    [
        "20261117100000_DevelopmentWorkspaceVms",
        "20261119145527_DevelopmentWorkspaceIdleTimeout",
        "20261119152838_DevelopmentWorkspaceResourceTelemetry",
        "20261119155230_DevelopmentWorkspaceKeepRunning",
        "20261119160815_DevelopmentWorkspaceDeletion",
        "20261120020000_DevelopmentWorkspaceTerminalDeletionIdentity",
        "20261120134056_DevelopmentWorkspaceVmResourceHistory",
    ];

    internal static UnmatchedMigrationEvaluation EvaluateUnmatchedMigrations(IEnumerable<string> unmatchedMigrations)
    {
        var unmatched = unmatchedMigrations.ToHashSet(StringComparer.Ordinal);
        var unknown = unmatched.Except(RetiredWorkspaceMigrations, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length > 0)
        {
            return new(UnmatchedMigrationStatus.UnknownMigrations, unknown);
        }

        if (unmatched.Count == 0)
        {
            return new(UnmatchedMigrationStatus.None, []);
        }

        if (!unmatched.SetEquals(RetiredWorkspaceMigrations))
        {
            return new(UnmatchedMigrationStatus.IncompleteRetiredWorkspaceHistory, []);
        }

        return new(UnmatchedMigrationStatus.CompleteRetiredWorkspaceHistory, []);
    }

    internal static async Task ValidateUnmatchedMigrationsAsync(
        MinicloudDbContext db,
        IEnumerable<string> unmatchedMigrations,
        CancellationToken cancellationToken)
    {
        var evaluation = EvaluateUnmatchedMigrations(unmatchedMigrations);
        switch (evaluation.Status)
        {
            case UnmatchedMigrationStatus.None:
                return;
            case UnmatchedMigrationStatus.UnknownMigrations:
                throw new InvalidOperationException(
                    $"The Minicloud database has migrations unknown to this release ({string.Join(", ", evaluation.UnknownMigrations)}); refusing to downgrade.");
            case UnmatchedMigrationStatus.IncompleteRetiredWorkspaceHistory:
                throw new InvalidOperationException(
                    "The Minicloud database has an incomplete workspace migration history. Finish upgrading it with the workspace-bearing release before retrying.");
            case UnmatchedMigrationStatus.CompleteRetiredWorkspaceHistory:
                await EnsureAllWorkspacesRetiredAsync(db, cancellationToken);
                return;
        }
    }

    private static async Task EnsureAllWorkspacesRetiredAsync(MinicloudDbContext db, CancellationToken cancellationToken)
    {
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
