import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { ChevronDown, ChevronRight, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { createDevelopmentWorkspace } from "@/api/environmentsApi";
import { getWorkspaceSelections, type WorkspaceSelectionOption, type WorkspaceSelections } from "@/api/workspacesApi";
import { ApiError, type DevelopmentWorkspace } from "@/models/controlPlane";

type Props = { organizationId: string; onCreated: (workspace: DevelopmentWorkspace) => void; canManage: boolean };
type Catalog = { state: "loading" } | { state: "error"; message: string } | { state: "ready"; value: WorkspaceSelections };

const defaultStorageGb = "20";
const hoursPerMonth = 720;
const staleCodes = ["workspace_image_invalid", "workspace_profile_invalid", "workspace_plan_invalid", "workspace_placement_invalid"];
const selectClass = "h-10 w-full min-w-0 rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50";

const optionKey = (option: WorkspaceSelectionOption) => JSON.stringify([option.imageId, option.profile, option.planId]);

export function formatPrice(amount: number | null | undefined, currency: string | null | undefined) {
  if (typeof amount !== "number" || !Number.isFinite(amount)) return null;
  try {
    return new Intl.NumberFormat(undefined, {
      style: "currency", currency: currency || "USD", minimumFractionDigits: 2, maximumFractionDigits: amount > 0 && amount < 1 ? 4 : 2,
    }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency ?? ""}`.trim();
  }
}

function sizeLabel(option: WorkspaceSelectionOption) {
  const specs = [option.cpuCount ? `${option.cpuCount} vCPU` : null, option.memoryGiB ? `${option.memoryGiB} GB RAM` : null].filter(Boolean);
  return specs.length ? `${option.profile} · ${specs.join(", ")}` : option.profile;
}

function optionLabel(option: WorkspaceSelectionOption, currency: string | null | undefined, showImage: boolean) {
  const hourly = formatPrice(option.computeHourlyRate, currency);
  return [sizeLabel(option), `${option.planName} (${option.region})`, hourly ? `${hourly}/hour while running` : null,
    showImage ? option.imageName : null].filter(Boolean).join(" · ");
}

export function CreateWorkspaceDialog({ organizationId, onCreated, canManage }: Props) {
  if (!canManage) return null;
  return <AuthorizedCreateWorkspaceDialog key={organizationId} organizationId={organizationId} onCreated={onCreated} />;
}

function AuthorizedCreateWorkspaceDialog({ organizationId, onCreated }: Omit<Props, "canManage">) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [choice, setChoice] = useState("");
  const [volumeSize, setVolumeSize] = useState(defaultStorageGb);
  const [customizing, setCustomizing] = useState(false);
  const [catalog, setCatalog] = useState<Catalog>({ state: "loading" });
  const [revision, setRevision] = useState(0);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submission = useRef(false);
  const mounted = useRef(true);
  const previous = useRef<{ payload: string; key: string } | null>(null);
  const chosen = useRef("");
  chosen.current = choice;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setCatalog({ state: "loading" });
    void getWorkspaceSelections(organizationId, undefined, controller.signal).then(value => {
      if (controller.signal.aborted) return;
      const options = value.available ? value.options : [];
      const current = chosen.current;
      const defaults = options.filter(option => option.isDefault);
      const preferred = defaults.length === 1 ? defaults[0] : options.length === 1 ? options[0] : undefined;
      if (current && options.some(option => optionKey(option) === current)) {
        setNotice(null);
      } else if (current) {
        setChoice("");
        setCustomizing(true);
        setNotice(options.length ? "The size and location you picked is no longer available. Choose another option." : null);
      } else {
        setChoice(preferred ? optionKey(preferred) : "");
      }
      setCatalog({ state: "ready", value });
    }).catch(reason => {
      if (!controller.signal.aborted) setCatalog({ state: "error", message: reason instanceof Error ? reason.message : "" });
    });
    return () => controller.abort();
  }, [open, organizationId, revision]);

  const validName = !!name.trim() && name.length <= 128 && !/[\u0000-\u001f\u007f-\u009f]/.test(name);
  const volumeSizeGb = Number(volumeSize);
  const validVolume = !!volumeSize.trim() && Number.isInteger(volumeSizeGb) && volumeSizeGb >= 10 && volumeSizeGb <= 10000;
  const ready = catalog.state === "ready" ? catalog.value : null;
  const options = ready?.available ? ready.options : [];
  const selected = options.find(option => optionKey(option) === choice);
  const currency = ready?.currency;
  const showImage = new Set(options.map(option => option.imageId)).size > 1;
  const choiceRequired = !!ready?.available && options.length > 0 && !selected;
  const canSubmit = !busy && validName && validVolume && !!selected;
  const storageMonthly = validVolume && typeof ready?.volumeMonthlyRatePerGb === "number"
    ? formatPrice(ready.volumeMonthlyRatePerGb * volumeSizeGb, currency) : null;
  const vmHourly = formatPrice(selected?.computeHourlyRate, currency);
  const vmMonthly = typeof selected?.computeHourlyRate === "number" ? formatPrice(selected.computeHourlyRate * hoursPerMonth, currency) : null;

  const reset = () => {
    setName(""); setChoice(""); setVolumeSize(defaultStorageGb); setCustomizing(false); setNotice(null); setError(null);
    setCatalog({ state: "loading" }); previous.current = null;
  };
  const changeOpen = (next: boolean) => {
    if (submission.current) return;
    setOpen(next);
    if (!next) reset();
  };
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (submission.current) return;
    if (!validName || !validVolume || !selected) {
      setError("Enter a name of 1–128 characters, choose an available option, and use 10–10,000 GB of storage.");
      return;
    }
    const request = { organizationId, name: name.trim(), imageId: selected.imageId, profile: selected.profile, planId: selected.planId,
      volumeSizeGb, startPrimaryVm: true };
    const payload = JSON.stringify(request);
    if (previous.current?.payload !== payload) previous.current = { payload, key: crypto.randomUUID() };
    submission.current = true;
    setBusy(true);
    setError(null);
    let workspace: DevelopmentWorkspace;
    try {
      workspace = await createDevelopmentWorkspace({ ...request, idempotencyKey: previous.current.key });
    } catch (reason) {
      if (mounted.current) {
        setError(reason instanceof Error && reason.message ? reason.message
          : "Couldn't create the dev environment. Your entries are kept; try again.");
        if (reason instanceof ApiError && staleCodes.includes(reason.code)) setRevision(value => value + 1);
      }
      return;
    } finally {
      submission.current = false;
      if (mounted.current) setBusy(false);
    }
    if (!mounted.current) return;
    setOpen(false);
    reset();
    onCreated(workspace);
  }

  const choiceField = <div className="space-y-2">
    <Label htmlFor={`${id}-option`}>Size and location</Label>
    <select id={`${id}-option`} className={selectClass} required disabled={busy} value={choice}
      aria-describedby={choiceRequired ? `${id}-option-help` : undefined} onChange={event => { setChoice(event.target.value); setNotice(null); }}>
      <option value="">Choose an option</option>
      {options.map(option => <option key={optionKey(option)} value={optionKey(option)}>{optionLabel(option, currency, showImage)}</option>)}
    </select>
    {choiceRequired && <p id={`${id}-option-help`} className="text-xs text-muted-foreground">
      {notice ?? "There's no default for your organization. Choose the size and location to use; prices differ."}
    </p>}
  </div>;

  return <Dialog open={open} onOpenChange={changeOpen}>
    <DialogTrigger asChild><Button><Plus className="mr-2 h-4 w-4" aria-hidden="true" />Create dev environment</Button></DialogTrigger>
    <DialogContent className="gap-0 overflow-y-auto p-0 sm:max-w-xl">
      <DialogHeader className="px-6 pb-5 pt-6 text-left">
        <DialogTitle>Create dev environment</DialogTitle>
        <DialogDescription>Its primary VM starts automatically after you create it.</DialogDescription>
      </DialogHeader>
      <form aria-label="Create dev environment" onSubmit={event => void submit(event)}>
        <div className="space-y-5 px-6 pb-6">
          {error && <p role="alert" className="break-words text-sm text-destructive">{error}</p>}
          <div className="space-y-2">
            <Label htmlFor={`${id}-name`}>Name</Label>
            <Input id={`${id}-name`} required maxLength={128} value={name} disabled={busy} autoComplete="off"
              placeholder="api-sandbox" aria-describedby={`${id}-name-help`} aria-invalid={!!name && !validName}
              onChange={event => setName(event.target.value)} />
            <p id={`${id}-name-help`} className="text-xs text-muted-foreground">Up to 128 characters.</p>
          </div>
          {catalog.state === "loading" && <p role="status" className="text-sm text-muted-foreground">Loading options…</p>}
          {catalog.state === "error" && <div role="alert" className="space-y-2 text-sm text-destructive">
            <p>Couldn't load the available options, so you can't create an environment yet. {catalog.message}</p>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setRevision(value => value + 1)}>Retry</Button>
          </div>}
          {ready && !ready.available && <p role="status" className="text-sm text-muted-foreground">
            You can't create dev environments right now. {ready.unavailableReason || "Ask an administrator to turn them on."}
          </p>}
          {ready?.available && !options.length && <div role="status" className="space-y-2 text-sm text-muted-foreground">
            <p>No approved setup is available for new dev environments. Ask an administrator to approve a T3 Connect image and a matching plan.</p>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setRevision(value => value + 1)}>Retry</Button>
          </div>}
          {choiceRequired && !customizing && choiceField}
          {selected && <section aria-label="Summary" className="space-y-2 rounded-md border bg-muted/20 p-4 text-sm">
            <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[auto_minmax(0,1fr)]">
              <dt className="text-muted-foreground">Size</dt><dd className="break-words">{sizeLabel(selected)}</dd>
              <dt className="text-muted-foreground">Location</dt><dd className="break-words">{selected.planName} · {selected.region}</dd>
              <dt className="text-muted-foreground">Storage</dt><dd>{validVolume ? `${volumeSizeGb} GB` : "Enter 10–10,000 GB"}</dd>
              <dt className="text-muted-foreground">Image</dt><dd className="break-words">{selected.imageName}</dd>
            </dl>
            <p>{vmHourly ? `VM: ${vmHourly}/hour while running (about ${vmMonthly}/month if never stopped). ` : "VM: charged while running. "}
              {storageMonthly ? `Storage: ${storageMonthly}/month, ` : "Storage is charged "}until you delete the environment, even when the VM is stopped.</p>
            <p role="note" aria-label="Automatic start">Creating starts the primary VM automatically. It's charged {vmHourly ? `${vmHourly}/hour` : "by the hour"} while it runs, until you stop it.</p>
            <p className="text-xs text-muted-foreground">Creating doesn't link a T3 account. Storage keeps your files between starts but isn't a backup.</p>
          </section>}
          {ready?.available && options.length > 0 && <div className="space-y-4">
            <Button type="button" variant="ghost" size="sm" className="-ml-2" aria-expanded={customizing}
              aria-controls={`${id}-customize`} onClick={() => setCustomizing(value => !value)}>
              {customizing ? <ChevronDown aria-hidden="true" className="mr-1 h-4 w-4" /> : <ChevronRight aria-hidden="true" className="mr-1 h-4 w-4" />}
              Customize
            </Button>
            {customizing && <div id={`${id}-customize`} className="space-y-4">
              {choiceField}
              <div className="space-y-2">
                <Label htmlFor={`${id}-volume`}>Storage (GB)</Label>
                <Input id={`${id}-volume`} type="number" required min={10} max={10000} step={1} value={volumeSize}
                  disabled={busy} aria-describedby={`${id}-storage-help`} aria-invalid={!validVolume}
                  onChange={event => setVolumeSize(event.target.value)} />
                <p id={`${id}-storage-help`} className="text-xs text-muted-foreground">10–10,000 GB. Files here are kept when the VM stops.</p>
              </div>
            </div>}
          </div>}
        </div>
        <DialogFooter className="gap-2 border-t px-6 py-4 sm:gap-0">
          <DialogClose asChild><Button type="button" variant="outline" disabled={busy}>Cancel</Button></DialogClose>
          <Button type="submit" disabled={!canSubmit}>{busy ? "Creating…" : "Create environment"}</Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>;
}
