import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { HardDrive, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { createDevelopmentWorkspace } from "@/api/environmentsApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";

type Props = { organizationId: string; onCreated: (workspace: DevelopmentWorkspace) => void; canManage: boolean };
const emptySelection: WorkspaceSelection = { imageId: "", profile: "", planId: "" };

export function CreateWorkspaceDialog({ organizationId, onCreated, canManage }: Props) {
  if (!canManage) return null;
  return <AuthorizedCreateWorkspaceDialog key={organizationId} organizationId={organizationId} onCreated={onCreated} />;
}

function AuthorizedCreateWorkspaceDialog({ organizationId, onCreated }: Omit<Props, "canManage">) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [selection, setSelection] = useState(emptySelection);
  const [selectionReady, setSelectionReady] = useState(false);
  const [volumeSize, setVolumeSize] = useState("20");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submission = useRef(false);
  const mounted = useRef(true);
  const previous = useRef<{ payload: string; key: string } | null>(null);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const validName = !!name.trim() && name.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(name);
  const volumeSizeGb = Number(volumeSize);
  const validVolume = !!volumeSize.trim() && Number.isInteger(volumeSizeGb) && volumeSizeGb >= 10 && volumeSizeGb <= 10000;
  const reset = () => {
    setName(""); setSelection(emptySelection); setSelectionReady(false); setVolumeSize("20"); setError(null);
  };
  const changeOpen = (next: boolean) => {
    if (submission.current) return;
    setOpen(next);
    if (!next) reset();
  };
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (submission.current) return;
    if (!validName || !validVolume || !selectionReady || !event.currentTarget.checkValidity()) {
      setError("Enter a name of 1–128 characters without control characters, choose compatible options, and use 10–10,000 GB of storage.");
      return;
    }
    const request = { organizationId, name: name.trim(), ...selection, volumeSizeGb };
    const payload = JSON.stringify(request);
    if (previous.current?.payload !== payload) previous.current = { payload, key: crypto.randomUUID() };
    submission.current = true;
    setBusy(true);
    setError(null);
    let workspace: DevelopmentWorkspace;
    try {
      workspace = await createDevelopmentWorkspace({ ...request, idempotencyKey: previous.current.key });
    } catch (reason) {
      if (mounted.current) setError(reason instanceof Error ? reason.message : "Workspace creation failed. Your entries are kept; retry with the same choices.");
      return;
    } finally {
      submission.current = false;
      if (mounted.current) setBusy(false);
    }
    if (!mounted.current) return;
    previous.current = null;
    setOpen(false);
    reset();
    onCreated(workspace);
  }
  return <Dialog open={open} onOpenChange={changeOpen}>
    <DialogTrigger asChild><Button><Plus className="mr-2 h-4 w-4" aria-hidden="true" />Create workspace</Button></DialogTrigger>
    <DialogContent className="gap-0 overflow-y-auto p-0 sm:max-w-xl">
      <DialogHeader className="px-6 pb-5 pt-6 text-left">
        <DialogTitle>Create workspace</DialogTitle>
        <DialogDescription>Set up the primary VM. You choose when to start it.</DialogDescription>
      </DialogHeader>
      <form aria-label="Create stopped workspace" onSubmit={event => void submit(event)}>
        <div className="space-y-5 px-6 pb-6">
          {error && <p role="alert" className="break-words text-sm text-destructive">{error}</p>}
          <div className="space-y-2">
            <Label htmlFor={`${id}-name`}>Workspace name</Label>
            <Input id={`${id}-name`} required maxLength={128} value={name} disabled={busy} autoComplete="off"
              placeholder="api-sandbox" aria-describedby={`${id}-name-help`} aria-invalid={!!name && !validName}
              onChange={event => setName(event.target.value)} />
            <p id={`${id}-name-help`} className="text-xs text-muted-foreground">1–128 characters, without control characters.</p>
          </div>
          {open && <WorkspaceSelectionFields organizationId={organizationId} value={selection} onChange={setSelection}
            disabled={busy} onReadyChange={setSelectionReady} />}
          <div className="space-y-2">
            <Label htmlFor={`${id}-volume`}>Retained storage (GB)</Label>
            <Input id={`${id}-volume`} type="number" required min={10} max={10000} step={1} value={volumeSize}
              disabled={busy} aria-describedby={`${id}-storage-help`} onChange={event => setVolumeSize(event.target.value)} />
            <p id={`${id}-storage-help`} className="flex items-start gap-2 text-sm text-muted-foreground">
              <HardDrive className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              Files on this volume survive VM stops. Storage charges continue while the volume is retained.
            </p>
          </div>
        </div>
        <DialogFooter className="gap-2 border-t px-6 py-4 sm:gap-0">
          <DialogClose asChild><Button type="button" variant="outline" disabled={busy}>Cancel</Button></DialogClose>
          <Button type="submit" disabled={busy || !validName || !validVolume || !selectionReady}>
            {busy ? "Creating…" : "Create stopped workspace"}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>;
}
