import { useEffect, useState } from "react";
import { Card, CardContent } from "@/components/ui/card";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceLaunchEphemeralDialog } from "./WorkspaceLaunchEphemeralDialog";
import { WorkspaceVmCard } from "./WorkspaceVmCard";
import { phaseLabel, phaseTone, vmName, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  canManage: boolean;
  canManageConnect: boolean;
  actorId?: string;
};

const dotClass = {
  warning: "bg-warning", destructive: "bg-destructive", success: "bg-success", neutral: "bg-muted-foreground/50",
} as const;

export function WorkspaceOverviewTab({ item, organizationId, refresh, refreshAfterMutation, canManage, actorId }: Props) {
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const ephemeral = vms.filter(vm => vm.kind === "ephemeral" && vm.id !== primary?.id);
  // null selects the primary entry; selection is never a side effect beyond local view state.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selectedEphemeral = selectedId ? ephemeral.find(vm => vm.id === selectedId) : undefined;
  useEffect(() => { if (selectedId && !selectedEphemeral) setSelectedId(null); }, [selectedId, selectedEphemeral]);
  const selected = selectedEphemeral ?? primary;

  return <div className="grid gap-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
    <section aria-labelledby="vm-list-heading" className="min-w-0 space-y-2">
      <div className="flex items-center justify-between gap-2">
        <h2 id="vm-list-heading" className="text-sm font-semibold">Virtual machines</h2>
        <WorkspaceLaunchEphemeralDialog item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
      </div>
      <ul className="space-y-1">
        <li><VmEntry label="Primary VM" vm={primary} selected={!selectedEphemeral} onSelect={() => setSelectedId(null)} /></li>
        {ephemeral.map(vm => <li key={vm.id}>
          <VmEntry label={vmName(vm)} detail={vm.repository} vm={vm} selected={selectedEphemeral?.id === vm.id} onSelect={() => setSelectedId(vm.id)} />
        </li>)}
      </ul>
    </section>
    <section aria-label="Selected VM" className="min-w-0">
      {selected ? <WorkspaceVmCard key={`${selected.id}:${selected.generation}:${actorId ?? ""}`} workspaceId={item.id}
        organizationId={organizationId} vm={selected} currency={item.currency} refresh={refresh} canManage={canManage} />
        : <Card><CardContent className="space-y-2 p-6 text-sm">
          <p className="font-medium">No primary VM reported.</p>
          <p className="text-muted-foreground">Select Refresh status to load it. A temporary VM is never used instead. Storage: {item.volumeSizeGb} GB · {item.volumeStatus ?? "status unavailable"}.</p>
        </CardContent></Card>}
    </section>
  </div>;
}

function VmEntry({ label, detail, vm, selected, onSelect }: {
  label: string; detail?: string | null; vm?: DevelopmentWorkspaceVm; selected: boolean; onSelect: () => void;
}) {
  const state = vm ? phaseLabel(vm) : "Not reported";
  return <button type="button" aria-pressed={selected} onClick={onSelect} aria-label={[label, detail, state].filter(Boolean).join(", ")}
    className={`flex w-full min-w-0 items-start gap-2 rounded-md border px-3 py-2 text-left text-sm transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring ${
      selected ? "border-foreground/20 bg-muted" : "border-transparent hover:bg-muted/50"}`}>
    <span aria-hidden="true" className={`mt-1.5 size-2 shrink-0 rounded-full ${vm ? dotClass[phaseTone(vm)] : dotClass.neutral}`} />
    <span className="min-w-0">
      <span className="block break-all font-medium">{label}</span>
      {detail && <span className="block break-all text-xs text-muted-foreground">{detail}</span>}
      <span className="block text-xs text-muted-foreground">{state}</span>
    </span>
  </button>;
}
