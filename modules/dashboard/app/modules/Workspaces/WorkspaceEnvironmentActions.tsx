import { useEffect, useRef, useState } from "react";
import { workspaceOperation } from "@/api/workspacesApi";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { message, phaseLabel, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  canManage: boolean;
};

export function WorkspaceEnvironmentActions({ item, organizationId, refreshAfterMutation, canManage }: Props) {
  const vms = item.vms ?? [];
  const [stopConfirmation, setStopConfirmation] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [stopError, setStopError] = useState<string | null>(null);
  const stopPending = useRef(false);
  const mounted = useRef(true);
  const stopSnapshot = JSON.stringify(vms.map(vm => [vm.id, vm.generation, vm.phase, vm.gitStatus]).sort());
  const stopOpen = canManage && stopConfirmation === stopSnapshot;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { setStopConfirmation(null); setStopError(null); }, [stopSnapshot, canManage]);

  const stopAll = async () => {
    if (!canManage || !stopOpen || stopPending.current || !vms.length) return;
    stopPending.current = true;
    setStopping(true);
    setStopError(null);
    try {
      const observation = await workspaceOperation(item.id, organizationId, "stop-all");
      if (!mounted.current) return;
      setStopConfirmation(null);
      await refreshAfterMutation(observation);
    } catch (reason) { if (mounted.current) setStopError(message(reason)); }
    finally { stopPending.current = false; if (mounted.current) setStopping(false); }
  };
  return <Dialog open={stopOpen} onOpenChange={open => {
    if (stopping) return;
    setStopError(null);
    setStopConfirmation(open && canManage ? stopSnapshot : null);
  }}>
    {canManage && <DialogTrigger asChild>
      <Button variant="outline" disabled={!vms.length || stopping}>Stop all VMs</Button>
    </DialogTrigger>}
    <DialogContent onEscapeKeyDown={event => { if (stopping) event.preventDefault(); }}
      onInteractOutside={event => { if (stopping) event.preventDefault(); }}>
      <DialogHeader>
        <DialogTitle>Stop all VMs?</DialogTitle>
        <DialogDescription>Each VM stops separately. Files on the primary VM's storage are kept, and storage charges continue. A temporary VM stops only after its tasks finish and its Git work is saved; a VM that can't stop keeps its server and may keep charging.</DialogDescription>
      </DialogHeader>
      <ul className="max-h-64 space-y-2 overflow-auto text-sm">
        {vms.map(vm => <li key={vm.id} className="break-words rounded-md border p-3">
          <strong>{vm.kind === "primary" ? "Primary VM" : vm.workBranch ?? "Temporary VM"}</strong> · {phaseLabel(vm)} · Git save: {vm.gitStatus.replaceAll("_", " ")}
          <span className="block text-xs text-muted-foreground">{vm.id} · generation {vm.generation}</span>
        </li>)}
      </ul>
      <p className="text-sm text-muted-foreground">Stopping doesn't discard unsaved work or affect other dev environments. A temporary VM's disk is deleted after it stops safely; files ignored by Git aren't saved.</p>
      {stopError && <p role="alert" className="text-sm text-destructive">{stopError}</p>}
      <DialogFooter className="gap-2">
        <Button variant="outline" disabled={stopping} onClick={() => setStopConfirmation(null)}>Cancel stop all</Button>
        <Button disabled={stopping || !canManage} onClick={() => void stopAll()}>{stopping ? "Stopping…" : "Confirm stop all"}</Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>;
}
