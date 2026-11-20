import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { vmStartup } from "./WorkspaceVmStartup";

export type WorkspaceRefresh = (observation?: DevelopmentWorkspace) => Promise<void>;

export function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "Something went wrong. Refresh status and try again.";
}

export function phaseLabel(vm: DevelopmentWorkspaceVm) {
  const { starting, failed, ready } = vmStartup(vm);
  if (vm.phase === "awaiting_git_save") return "Git save blocked";
  if (failed) return "VM startup failed";
  if (starting) return "Starting VM…";
  if (vm.errorCode) return "Needs attention";
  if (ready) return "Running";
  if (vm.phase === "stopped") return "Stopped";
  return vm.phase.replaceAll("_", " ");
}

export function vmName(vm: DevelopmentWorkspaceVm) {
  return vm.kind === "primary" ? "Primary VM" : vm.workBranch ?? "Work branch pending";
}

export function gitSaveLabel(vm: DevelopmentWorkspaceVm) {
  return vm.gitStatus.replaceAll("_", " ").replace(/^./, character => character.toUpperCase());
}

export function phaseTone(vm: DevelopmentWorkspaceVm): "warning" | "destructive" | "success" | "neutral" {
  const { failed, ready } = vmStartup(vm);
  if (vm.phase === "awaiting_git_save") return "warning";
  if (failed || vm.errorCode) return "destructive";
  return ready ? "success" : "neutral";
}

export function relativeTime(value: string, now = Date.now()) {
  const time = Date.parse(value);
  if (!Number.isFinite(time)) return value;
  const minutes = Math.floor(Math.max(0, now - time) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  return hours < 24 ? `${hours}h ago` : `${Math.floor(hours / 24)}d ago`;
}
