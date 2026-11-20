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
