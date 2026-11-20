import { useEffect, useRef, useState, type FormEvent } from "react";
import { LoaderCircle, Plus } from "lucide-react";
import { launchEphemeralVm } from "@/api/workspacesApi";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";
import { message, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = { item: DevelopmentWorkspace; organizationId: string; refresh: WorkspaceRefresh; canManage: boolean };

const initialSelection: WorkspaceSelection = { imageId: "", profile: "Standard", planId: "" };
const lossDisclosure = "A temporary VM has its own disk and can't see the primary VM's storage. Untracked files, files ignored by Git and unpushed work can be lost permanently; there is no backup.";

export function WorkspaceLaunchEphemeralDialog({ item, organizationId, refresh, canManage }: Props) {
  const [open, setOpen] = useState(false);
  const [selection, setSelection] = useState<WorkspaceSelection>(initialSelection);
  const [selectionReady, setSelectionReady] = useState(false);
  const [repository, setRepository] = useState("");
  const [baseRef, setBaseRef] = useState("");
  const [baseCommit, setBaseCommit] = useState("");
  const [credentialRef, setCredentialRef] = useState("");
  const [pushAuthorized, setPushAuthorized] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestKey = useRef<{ payload: string; key: string } | null>(null);
  const pending = useRef(false);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { if (!canManage) setOpen(false); }, [canManage]);
  const valid = selectionReady && !!selection.imageId && !!selection.planId && !!selection.profile && !!repository.trim()
    && !!baseRef.trim() && /^(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})$/.test(baseCommit.trim())
    && pushAuthorized && acknowledged;
  const reset = () => {
    setSelection(initialSelection); setSelectionReady(false); setRepository(""); setBaseRef(""); setBaseCommit(""); setCredentialRef("");
    setPushAuthorized(false); setAcknowledged(false); setError(null);
  };
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!canManage || !open || !valid || pending.current) return;
    pending.current = true;
    setBusy(true);
    setError(null);
    const request = {
      ...selection, repository: repository.trim(), baseRef: baseRef.trim(), baseCommit: baseCommit.trim(),
      pushAuthorized, acknowledgeIgnoredDataLoss: acknowledged,
      ...(credentialRef.trim() ? { repositoryCredentialRef: credentialRef.trim() } : {}),
    };
    const payload = JSON.stringify(request);
    if (requestKey.current?.payload !== payload) requestKey.current = { payload, key: crypto.randomUUID() };
    try {
      const observation = await launchEphemeralVm(item.id, organizationId, { ...request, idempotencyKey: requestKey.current.key });
      if (!mounted.current) return;
      requestKey.current = null;
      reset();
      setOpen(false);
      await refresh(observation);
    } catch (reason) { if (mounted.current) setError(message(reason)); }
    finally { pending.current = false; if (mounted.current) setBusy(false); }
  };
  return <Dialog open={open && canManage} onOpenChange={next => { if (!busy) { setOpen(next && canManage); setError(null); } }}>
    {canManage && <DialogTrigger asChild><Button size="icon" variant="ghost" className="size-8" aria-label="Launch temporary VM" title="Launch temporary VM"><Plus aria-hidden="true" className="size-4" /></Button></DialogTrigger>}
    <DialogContent className="max-w-2xl" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
      onInteractOutside={event => { if (busy) event.preventDefault(); }}>
      <DialogHeader>
        <DialogTitle>Launch temporary VM</DialogTitle>
        <DialogDescription>{lossDisclosure} If the VM is lost unexpectedly, you may lose everything since your last push.</DialogDescription>
      </DialogHeader>
      <form aria-label={`Launch temporary VM in ${item.name}`} onSubmit={event => void submit(event)} className="space-y-4">
        <fieldset disabled={busy} className="grid min-w-0 gap-4">
          <WorkspaceSelectionFields organizationId={organizationId} workspaceId={item.id} value={selection} onChange={setSelection} onReadyChange={setSelectionReady} disabled={busy} />
          <label className="space-y-1 text-sm font-medium">Repository URL<Input required value={repository} onChange={event => setRepository(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Base ref<Input required value={baseRef} onChange={event => setBaseRef(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Resolved base commit SHA<Input required pattern="[a-fA-F0-9]{40}|[a-fA-F0-9]{64}" value={baseCommit} onChange={event => setBaseCommit(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Repository credential reference (optional; not a token)<Input value={credentialRef} onChange={event => setCredentialRef(event.target.value)} /></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" required className="mt-1 accent-primary" checked={pushAuthorized} onChange={event => setPushAuthorized(event.target.checked)} /><span>I authorize task-scoped pushes to this repository's isolated work branch.</span></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" required className="mt-1 accent-primary" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} /><span>I understand that untracked files and files ignored by Git are deleted with this VM.</span></label>
        </fieldset>
        <p className="text-sm text-muted-foreground">Launching doesn't push, force-push, open a PR or merge. Files, credentials and storage from other VMs aren't copied. Launching starts a VM right away, and it costs money while it runs.</p>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <DialogFooter className="gap-2">
          <Button type="button" variant="outline" disabled={busy} onClick={() => setOpen(false)}>Cancel launch</Button>
          <Button disabled={!canManage || !valid || busy}>
            {busy && <LoaderCircle aria-hidden="true" className="mr-2 size-4 motion-safe:animate-spin" />}
            {busy ? "Starting VM…" : "Launch temporary VM"}
          </Button>
        </DialogFooter>
        <p role="status" className="sr-only">{busy ? "Starting VM…" : ""}</p>
      </form>
    </DialogContent>
  </Dialog>;
}
