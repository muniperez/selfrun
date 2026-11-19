import type { DevelopmentWorkspace } from "@/models/controlPlane";

export function primaryVm(workspace: DevelopmentWorkspace) {
  return workspace.vms?.find(vm => vm.id === workspace.primaryVmId && vm.kind === "primary")
    ?? workspace.vms?.find(vm => vm.kind === "primary");
}

// Ordinary compute deletion also reports a "deleting" VM phase; only volume status marks permanent deletion.
export function permanentDeletion(workspace: DevelopmentWorkspace) {
  if (workspace.volumeStatus === "deleted") return "deleted";
  if (workspace.volumeStatus !== "deleting") return null;
  return primaryVm(workspace)?.phase === "delete_failed" || workspace.phase === "delete_failed" ? "failed" : "deleting";
}

export function workspacePhase(phase?: string | null) {
  if (phase === "ready") return { value: "ready", label: "Running" };
  if (phase === "stopped") return { value: "stopped", label: "Stopped" };
  if (["provisioning", "attaching", "bootstrapping", "enrolling", "starting"].includes(phase ?? ""))
    return { value: "starting", label: "Starting" };
  if (["draining", "deleting", "stopping"].includes(phase ?? "")) return { value: "stopping", label: "Stopping" };
  if (["awaiting_git_save", "blocked", "failed", "recovering"].includes(phase ?? ""))
    return { value: "attention", label: phase === "awaiting_git_save" ? "Git save blocked" : "Needs attention" };
  return { value: "unavailable", label: "Unavailable" };
}

export function primaryState(workspace: DevelopmentWorkspace) {
  const deletion = permanentDeletion(workspace);
  if (deletion === "deleting") return { value: "stopping", label: "Deleting environment" };
  if (deletion === "failed") return { value: "attention", label: "Deletion failed" };
  return workspacePhase(primaryVm(workspace)?.phase);
}

export function ephemeralSummary(workspace: DevelopmentWorkspace) {
  if (!Array.isArray(workspace.vms)) return { state: "unavailable", label: "Unavailable" };
  const siblings = workspace.vms.filter(vm => vm.kind === "ephemeral");
  if (!siblings.length) return { state: "none", label: "None" };
  const unavailable = siblings.filter(vm => workspacePhase(vm.phase).value === "unavailable").length;
  const unknown = unavailable ? ` · ${unavailable} unavailable` : "";
  const attention = siblings.filter(vm => workspacePhase(vm.phase).value === "attention" || Boolean(vm.errorCode)).length;
  if (attention) return { state: "attention", label: `${attention} needs attention${unknown}` };
  const running = siblings.filter(vm => vm.phase === "ready").length;
  if (running) return { state: "running", label: `${running} running${unknown}${siblings.length > running ? ` · ${siblings.length} total` : ""}` };
  const transitioning = siblings.filter(vm => ["starting", "stopping"].includes(workspacePhase(vm.phase).value)).length;
  if (transitioning) return { state: "transitioning", label: `${transitioning} starting or stopping${unknown}` };
  if (unavailable) return { state: "unavailable", label: "Unavailable" };
  return { state: "stopped", label: `${siblings.length} stopped` };
}

export type LastActivity = { at: number; source: "activity" | "created" };

// Falls back to the creation time until the workspace or any VM records activity.
export function lastActivity(workspace: DevelopmentWorkspace): LastActivity | null {
  const dates = [workspace.lastActivityAt, ...(workspace.vms ?? []).map(vm => vm.lastActivityAt)]
    .filter((value): value is string => Boolean(value)).map(Date.parse).filter(Number.isFinite);
  if (dates.length) return { at: Math.max(...dates), source: "activity" };
  const created = workspace.createdAt ? Date.parse(workspace.createdAt) : NaN;
  return Number.isFinite(created) ? { at: created, source: "created" } : null;
}
