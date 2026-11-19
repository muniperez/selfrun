import { LoaderCircle } from "lucide-react";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";

const stages = [
  { id: "preparing_vm", label: "Preparing VM" },
  { id: "preparing_storage", label: "Preparing storage" },
  { id: "setting_up", label: "Setting up VM" },
  { id: "checking_readiness", label: "Checking readiness" },
  { id: "ready", label: "Ready" },
];

export function vmStartup(vm: DevelopmentWorkspaceVm) {
  const ready = vm.phase === "ready" && vm.desiredState === "running" && !vm.errorCode;
  const stopping = ["draining", "detaching", "deleting", "awaiting_git_save", "stopping"].includes(vm.phase);
  const failed = vm.desiredState === "running" && ["blocked", "failed"].includes(vm.phase);
  const starting = vm.desiredState === "running" && !vm.errorCode && !ready && !failed && !stopping && vm.phase !== "stopped";
  const applicable = stages.filter(stage => vm.kind === "primary" || stage.id !== "preparing_storage");
  const current = applicable.findIndex(stage => stage.id !== "ready" && stage.id === vm.startupStage);
  return { ready, failed, starting, stages: applicable, current };
}

export function WorkspaceVmStartup({ vm, pending = false }: { vm: DevelopmentWorkspaceVm; pending?: boolean }) {
  const { ready, starting, failed, stages, current } = vmStartup(vm);
  const stage = stages[current];
  const status = pending || starting ? (starting && stage ? `${stage.label}…` : "Starting VM…")
    : failed ? "VM startup failed" : ready ? "Ready" : "";

  return <div className={pending || starting || failed ? "space-y-2 border-t px-5 py-3 text-sm" : undefined}>
    <p role="status" className={ready ? "sr-only" : failed ? "font-medium text-destructive" : "flex items-center gap-2 font-medium"}>
      {(pending || starting) && <LoaderCircle aria-hidden="true" className="size-4 motion-safe:animate-spin" />}
      {status}
    </p>
    {(starting || failed) && stage && <ol aria-label="VM startup stages" className="flex flex-wrap gap-2">
      {stages.map(({ id, label }, index) => <li key={id} aria-current={index === current ? "step" : undefined}
        className={`rounded-md border px-2 py-1 text-xs ${index < current ? "bg-success-surface text-success"
          : index === current ? "border-current font-medium" : "text-muted-foreground"}`}>
        <span className="sr-only">{index < current ? "Completed: " : index > current ? "Upcoming: " : failed ? "Last reported: " : "Current: "}</span>
        {label}
      </li>)}
    </ol>}
    {failed && <p className="text-muted-foreground">
      {stage ? `Last reported stage: ${stage.label}. ` : ""}
      Select Refresh status to check again, or contact your operator if startup stays blocked. Checking status doesn't start another VM.
    </p>}
    {(pending || starting) && <p className="text-muted-foreground">T3 and previews become available when the VM is ready.</p>}
  </div>;
}
