import { useEffect, useId, useRef, useState } from "react";
import { deleteWorkspace } from "@/api/workspacesApi";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { message } from "./workspaceVmLabels";

export function WorkspaceDeleteEnvironment({ item, organizationId, vms, failed, deleting, refresh }: {
  item: DevelopmentWorkspace; organizationId: string; vms: DevelopmentWorkspaceVm[];
  failed: boolean; deleting: boolean; refresh: () => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [typedName, setTypedName] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const mounted = useRef(true);
  const nameId = useId();
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const allStopped = vms.every(vm => vm.phase === "stopped" && vm.desiredState === "stopped" && !vm.computeReserved);
  const available = failed || (!deleting && allStopped);
  const confirmed = typedName.trim() === item.name;
  const remove = async () => {
    if (!available || !confirmed || pending) return;
    setPending(true);
    setError(null);
    try {
      await deleteWorkspace(item.id, organizationId);
      if (!mounted.current) return;
      setOpen(false);
      try { await refresh(); } catch { /* The page keeps its last state; the user can refresh manually. */ }
    } catch (reason) { if (mounted.current) setError(message(reason)); }
    finally { if (mounted.current) setPending(false); }
  };
  return <section aria-labelledby="delete-environment-heading" className="space-y-3 rounded-lg border border-destructive/50 p-4">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0 max-w-prose space-y-1">
        <h2 id="delete-environment-heading" className="text-lg font-semibold">Delete environment</h2>
        <p className="text-sm text-muted-foreground">Permanently delete this dev environment and the files saved on its primary volume. Stopping VMs keeps your files; deleting the environment does not.</p>
        {!available && !deleting && <p className="text-sm text-muted-foreground">Stop all VMs and wait until they finish stopping before deleting.</p>}
      </div>
      <Dialog open={open} onOpenChange={next => {
        if (pending) return;
        setOpen(next && available); setTypedName(""); setError(null);
      }}>
        <DialogTrigger asChild>
          <Button variant="destructive" disabled={!available || pending}>{failed ? "Retry delete environment" : "Delete environment"}</Button>
        </DialogTrigger>
        <DialogContent onEscapeKeyDown={event => { if (pending) event.preventDefault(); }}
          onInteractOutside={event => { if (pending) event.preventDefault(); }}>
          <DialogHeader>
            <DialogTitle>Delete {item.name} permanently?</DialogTitle>
            <DialogDescription>This deletes the dev environment and its primary volume. Files that are not pushed to Git are lost for good. This cannot be undone.</DialogDescription>
          </DialogHeader>
          <div className="space-y-2 text-sm">
            <label htmlFor={nameId} className="block">Type <strong className="break-all">{item.name}</strong> to confirm</label>
            <Input id={nameId} autoComplete="off" value={typedName} disabled={pending} onChange={event => setTypedName(event.target.value)} />
          </div>
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
          <DialogFooter className="gap-2">
            <Button variant="outline" disabled={pending} onClick={() => setOpen(false)}>Cancel</Button>
            <Button variant="destructive" disabled={!confirmed || pending} onClick={() => void remove()}>
              {pending ? "Deleting…" : "Delete permanently"}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  </section>;
}
