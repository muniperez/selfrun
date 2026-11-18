using System.Text.Json;

namespace Minicloud.Worker.DevelopmentWorkspaces;

internal sealed record WorkspaceRuntimeRejection(
    string Action, string State, string ReasonCode, string Stage, string FailureType,
    string? Tool, string? ToolAction, int? ExitCode, int? Errno, DateTimeOffset CapturedAt)
{
    public string Summary => $"Runtime {Action} refused: {ReasonCode}; stage {Stage}; failure {FailureType}" +
        (Tool is null ? "" : $"; tool {Tool}") +
        (ToolAction is null ? "" : $" {ToolAction}") +
        (ExitCode is null ? "" : $"; exit {ExitCode}") +
        (Errno is null ? "." : $"; errno {Errno}.");

    internal static WorkspaceRuntimeRejection Capture(string action, JsonElement response, DateTimeOffset now)
    {
        var detail = response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("diagnostic", out var value) && value.ValueKind == JsonValueKind.Object
                ? value : default;
        return new(
            Allowed(action, ["git-check", "git-save", "quiesce", "discard", "status", "resume",
                "cancel-drain", "receipt-validate"]) ?? "unknown",
            Allowed(Text(response, "state"), ["Blocked", "AwaitingGitSave"]) ?? "Unknown",
            CodeForReason(Text(response, "reason")),
            Allowed(Text(detail, "stage"), ["request", "git-preflight", "freeze-guard",
                "idle-before-freeze", "kernel-freeze", "idle-after-freeze", "t3-snapshot",
                "stop-services", "idle-after-stop", "t3-final-snapshot", "remount-readonly",
                "verify-fence", "git-inspect", "git-reinspect", "resume", "cancel-drain"]) ?? "unknown",
            Allowed(Text(detail, "failureType"), ["blocked", "tool_exit", "tool_timeout",
                "permission_denied", "file_missing", "filesystem_error", "sqlite_error",
                "missing_field", "invalid_type", "invalid_value", "missing_attribute", "adapter_error"]) ?? "unknown",
            Allowed(Text(detail, "tool"), ["systemctl", "mount", "findmnt", "umount", "git", "runuser", "age"]),
            Allowed(Text(detail, "toolAction"), ["show", "stop", "start", "freeze", "thaw"]),
            Number(detail, "exitCode", -255, 255), Number(detail, "errno", 1, 4095), now);
    }

    private static string? Text(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 256 } text ? text : null;

    private static int? Number(JsonElement json, string name, int min, int max) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
            ? number : null;

    private static string? Allowed(string? value, string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : null;

    private static string CodeForReason(string? reason)
    {
        foreach (var table in new[] { "runs", "run_attempts", "nodes", "runtime_requests", "provider_sessions",
                     "provider_threads", "provider_turns", "turn_items", "subagents" })
            if (reason == "T3 has active/pending work or an unknown status in orchestration_v2_projection_" + table)
                return "t3_work_not_idle_" + table;
        if (reason == "T3 has active/pending work or an unknown status in orchestration_v2_effect_outbox")
            return "t3_work_not_idle_effect_outbox";
        return CodeForStaticReason(reason);
    }

    private static string CodeForStaticReason(string? reason) => reason switch
    {
        "T3 thread state changed after the HTTP idle snapshot" => "t3_snapshot_changed",
        "T3 application state changed after the HTTP idle snapshot" => "t3_snapshot_changed",
        "Pinned T3 state database is unavailable" => "t3_database_unavailable",
        "Unknown pinned T3 database schema" => "t3_schema_unknown",
        "Unknown pinned T3 database columns/types" => "t3_columns_unknown",
        "Unknown pinned T3 projection table" => "t3_projection_unknown",
        "Pinned T3 thread projection is unknown or not caught up to the global V2 cursor" => "t3_projection_not_current",
        "Pinned T3 thread projection is unknown or not caught up to the V2 thread cursor" => "t3_projection_not_current",
        "Pinned T3 read-only state inspection failed; keep compute running" => "t3_database_inspection_failed",
        "Frozen T3 state changed after quiescence" => "t3_frozen_state_changed",
        "T3 state database escapes the workspace" => "t3_database_not_owned",
        "Unexpected T3 process without a state snapshot" => "t3_process_without_snapshot",
        "Work cgroup did not freeze" => "cgroup_freeze_failed",
        "Work cgroup is no longer frozen" => "cgroup_fence_lost",
        "Workspace no longer read-only" => "filesystem_fence_lost",
        "Workspace did not become read-only" => "filesystem_readonly_failed",
        "Designated service did not stop cleanly" => "service_stop_unconfirmed",
        "PostgreSQL still running; refuse quiescence" => "postgres_still_running",
        "Registered task still running; lease expiry is not completion" => "registered_task_running",
        "Coding process escaped supervised work slice" => "coding_process_outside_slice",
        "Unregistered live coding process; terminal input observation is not qualified" => "unregistered_coding_process",
        "Coding process inventory changed during inspection" => "process_inventory_changed",
        "Shell acquired children during idle inspection" => "shell_children_changed",
        "Cannot enumerate all live work" => "process_inventory_unavailable",
        "Unparseable process identity" => "process_identity_unparseable",
        "Process ownership unavailable" => "process_ownership_unavailable",
        "PID changed during inventory" => "process_identity_changed",
        "Shell is not a T3 descendant" => "shell_not_t3_owned",
        "Shell stdin/stderr PTY identity differs" => "shell_pty_identity_mismatch",
        "Shell descriptor is not a PTY slave" => "shell_not_pty_slave",
        "Shell descriptor is not a character device" => "shell_not_character_device",
        "Idle terminal requires a single-threaded coding-owned shell" => "shell_identity_unsupported",
        "Shell is not sleeping in its foreground controlling terminal" => "shell_not_foreground_idle",
        "Interactive shell has children or stopped/background jobs" => "shell_has_jobs",
        "Shell executable is not an approved root-owned image binary" => "shell_binary_not_trusted",
        "Incomplete shell command line" => "shell_invocation_incomplete",
        "Shell scripts, -c commands and unknown invocation flags remain active work" => "shell_invocation_active",
        "Native terminal adapter required: no proven blocking stdin syscall" => "shell_stdin_wait_unproven",
        "Native terminal adapter required: pselect is not an infinite stdin-only wait" => "shell_pselect_not_idle",
        "Native terminal adapter required: no proven sleeping stdin-only pselect" => "shell_pselect_wait_unproven",
        "Native terminal adapter required: no proven blocking read(0) syscall" => "shell_read_not_idle",
        "Native terminal adapter required: no proven sleeping PTY read" => "shell_read_wait_unproven",
        "PTY device/session/foreground group changed" => "shell_pty_changed",
        "Terminal evidence changed during inspection" => "shell_evidence_changed",
        "Terminal stdin wait evidence changed during inspection" => "shell_stdin_wait_changed",
        "Cannot prove idle terminal from kernel evidence" => "shell_kernel_idle_unproven",
        "T3 service identity is unknown or outside its owned cgroup" => "t3_process_not_owned",
        "Preview service identity is unknown or outside its owned cgroup" => "preview_process_not_owned",
        "Preview service has unregistered children or leftover cgroup processes" => "preview_children_unregistered",
        "Resolve tracked modifications and untracked files" => "git_dirty",
        "Registered checkout inventory differs from filesystem" => "git_checkout_inventory_changed",
        "Checkout changed during verification" => "git_checkout_changed",
        "Authorized remote ref unavailable" => "git_remote_ref_unavailable",
        "Remote ref changed during verification; retry" => "git_remote_ref_changed",
        "Repository origin changed" => "git_origin_changed",
        "Work branch changed" => "git_branch_changed",
        "Cannot inspect Git configuration" => "git_config_inspection_failed",
        "No registered checkouts" => "git_no_registered_checkouts",
        "Unregistered bare repository" => "git_bare_repository_unregistered",
        "Checkout outside workspace" => "git_checkout_outside_workspace",
        "Symlink checkout is unsupported" => "git_checkout_symlink",
        "Explicit push authority is required" => "git_push_not_authorized",
        "Invalid exact remote ref" => "git_remote_ref_invalid",
        "Only the authorized non-default work branch is allowed" => "git_branch_not_authorized",
        "An exact base SHA is required" => "git_base_invalid",
        "Checkout root changed" => "git_checkout_root_changed",
        "Git metadata outside workspace" => "git_metadata_outside_workspace",
        "External filters or alternate push URLs are unsupported" => "git_config_unsupported",
        "Submodules require separate verified support" => "git_submodule_unsupported",
        "Git LFS content is unsupported" or "Git LFS is unsupported" => "git_lfs_unsupported",
        "Unregistered Git worktree" => "git_worktree_unregistered",
        "Partial/shallow clones are unsupported" => "git_partial_clone_unsupported",
        "Independent Git verification directory required" => "git_verification_cache_missing",
        "Default-branch save or unavailable remote default identity is forbidden" => "git_remote_default_unconfirmed",
        "Stale VM/generation" => "runtime_identity_stale",
        "Another operation owns the freeze; stale operation" => "freeze_operation_stale",
        "Stale freeze snapshot sequence" => "freeze_snapshot_stale",
        "Tool unavailable or failed: systemctl" => "systemctl_failed",
        "Tool unavailable or failed: mount" => "mount_failed",
        "Tool unavailable or failed: findmnt" => "findmnt_failed",
        "Tool unavailable or failed: umount" => "umount_failed",
        "Tool unavailable or failed: git" => "git_failed",
        "Tool unavailable or failed: runuser" => "runuser_failed",
        "Invalid request or adapter failure" => "runtime_adapter_failed",
        _ => "unrecognized_runtime_rejection"
    };
}
