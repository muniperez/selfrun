using System.Text.Json;

namespace Minicloud.Worker.DevelopmentWorkspaces;

internal static class T3WorkspaceEvidence
{
    private static readonly HashSet<string> TerminalStates = ["idle", "completed", "interrupted", "failed", "cancelled", "rolled_back"];

    public static bool IsIdle(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt64(out var schema) || schema < 1 ||
            !snapshot.TryGetProperty("snapshotSequence", out var sequence) || !sequence.TryGetInt64(out var seq) || seq < 0 ||
            !snapshot.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array ||
            !snapshot.TryGetProperty("archivedThreads", out var archived) || archived.ValueKind != JsonValueKind.Array)
            return false;
        return threads.EnumerateArray().Concat(archived.EnumerateArray()).All(ThreadIdle);
    }

    private static bool ThreadIdle(JsonElement thread)
    {
        if (thread.ValueKind != JsonValueKind.Object || !thread.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String || !TerminalStates.Contains(status.GetString()!) ||
            !thread.TryGetProperty("activeRunId", out var run) || run.ValueKind != JsonValueKind.Null ||
            !thread.TryGetProperty("pendingRuntimeRequest", out var pending) || pending.ValueKind != JsonValueKind.Null)
            return false;
        if (thread.TryGetProperty("activityRunStatus", out var activity) && activity.ValueKind != JsonValueKind.Null) return false;
        if (thread.TryGetProperty("blockedOnThreadWait", out var wait) && wait.ValueKind != JsonValueKind.False) return false;
        if (thread.TryGetProperty("pendingGuestMessageCount", out var guests) && (!guests.TryGetInt64(out var count) || count != 0)) return false;
        if (thread.TryGetProperty("pendingBackgroundTasks", out var tasks) && (tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() != 0)) return false;
        return true;
    }
}
