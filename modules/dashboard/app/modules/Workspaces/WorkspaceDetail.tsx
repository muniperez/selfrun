import { Fragment, useEffect, useId, useRef, useState, type FormEvent } from "react";
import { ChevronDown, ChevronRight, GitBranch, LoaderCircle, Plus } from "lucide-react";
import { launchEphemeralVm, workspaceOperation } from "@/api/workspacesApi";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useAuth } from "@/core/auth/AuthProvider";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceIdleTimeoutSettings } from "./WorkspaceIdleTimeoutSettings";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceVmCard } from "./WorkspaceVmCard";
import { vmStartup } from "./WorkspaceVmStartup";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: (observation?: DevelopmentWorkspace) => Promise<void>;
  canManage: boolean;
};

const initialSelection: WorkspaceSelection = { imageId: "", profile: "Standard", planId: "" };
const lossDisclosure = "Ephemeral VMs have independent Git-only root disks and no access to the primary retained volume. Ignored, untracked and unpushed local data can be permanently lost; there is no filesystem backup.";

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "Workspace operation failed.";
}

function phaseLabel(vm: DevelopmentWorkspaceVm) {
  const { starting, failed, ready } = vmStartup(vm);
  if (vm.phase === "awaiting_git_save") return "Git save blocked";
  if (failed) return "VM startup failed";
  if (starting) return "Starting VM…";
  if (vm.errorCode) return `Blocked · ${vm.phase.replaceAll("_", " ")}`;
  if (ready) return "Ready";
  if (vm.phase === "stopped") return "Stopped";
  return vm.phase.replaceAll("_", " ");
}

export function WorkspaceDetail(props: Props) {
  return <ScopedWorkspaceDetail key={`${props.organizationId}:${props.item.id}`} {...props} />;
}

function ScopedWorkspaceDetail({ item, organizationId, refresh, canManage }: Props) {
  const { profile, selectedOrganization } = useAuth();
  const canManageConnect = canManage && Boolean(profile?.id) && item.organizationId === organizationId &&
    selectedOrganization?.id === organizationId &&
    (selectedOrganization.role.toLowerCase() === "owner" || item.createdBy === profile?.id);
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const ephemeral = vms.filter(vm => vm.kind === "ephemeral" && vm.id !== primary?.id);
  const [stopConfirmation, setStopConfirmation] = useState<string | null>(null);
  const [stopping, setStopping] = useState(false);
  const [stopError, setStopError] = useState<string | null>(null);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const stopPending = useRef(false);
  const mounted = useRef(true);
  const stopSnapshot = JSON.stringify(vms.map(vm => [vm.id, vm.generation, vm.phase, vm.gitStatus]).sort());
  const stopOpen = canManage && stopConfirmation === stopSnapshot;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => { setStopConfirmation(null); setStopError(null); }, [stopSnapshot, canManage]);

  const refreshAfterMutation = async (observation?: DevelopmentWorkspace) => {
    try { await refresh(observation); }
    catch (reason) { if (mounted.current) setRefreshError(`Operation accepted. Refresh failed: ${message(reason)}`); }
  };
  const stopAll = async () => {
    if (!canManage || !stopOpen || stopPending.current || !vms.length) return;
    stopPending.current = true;
    setStopping(true);
    setStopError(null);
    setRefreshError(null);
    try {
      const observation = await workspaceOperation(item.id, organizationId, "stop-all");
      if (!mounted.current) return;
      setStopConfirmation(null);
      await refreshAfterMutation(observation);
    } catch (reason) { if (mounted.current) setStopError(message(reason)); }
    finally { stopPending.current = false; if (mounted.current) setStopping(false); }
  };
  return <div className="min-w-0 space-y-6">
    {item.errorCode && !(primary && vmStartup(primary).failed) && <p role="alert" className="text-sm text-destructive">{item.errorCode}: {item.errorMessage}</p>}
    {refreshError && <p role="alert" className="text-sm text-destructive">{refreshError}</p>}
    {!canManage && <p className="text-sm text-muted-foreground">Read-only access. Workspace management requires an owner or admin.</p>}
    <Tabs defaultValue="overview" className="min-w-0">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
        <TabsList aria-label="Workspace details" className="h-auto gap-6 rounded-none bg-transparent p-0">
          <TabsTrigger value="overview" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Overview</TabsTrigger>
          <TabsTrigger value="storage" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Storage &amp; costs</TabsTrigger>
          <TabsTrigger value="settings" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Settings</TabsTrigger>
        </TabsList>
        <Dialog open={stopOpen} onOpenChange={open => {
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
              <DialogDescription>Stop each listed VM independently. Primary volume data is retained. Every ephemeral VM must pass its own task and Git-save guards; blocked VMs remain allocated and may incur costs.</DialogDescription>
            </DialogHeader>
            <ul className="max-h-64 space-y-2 overflow-auto text-sm">
              {vms.map(vm => <li key={vm.id} className="break-words rounded-md border p-3">
                <strong>{vm.id}</strong> · {vm.kind} · generation {vm.generation} · {vm.phase} · Git save: {vm.gitStatus}
              </li>)}
            </ul>
            <p className="text-sm text-muted-foreground">This does not discard unsaved data or stop unrelated workspaces. Ephemeral root disks are disposable after safe deletion; ignored files are not saved by Git.</p>
            {stopError && <p role="alert" className="text-sm text-destructive">{stopError}</p>}
            <DialogFooter className="gap-2">
              <Button variant="outline" disabled={stopping} onClick={() => setStopConfirmation(null)}>Cancel stop all</Button>
              <Button disabled={stopping || !canManage} onClick={() => void stopAll()}>{stopping ? "Stopping…" : "Confirm stop all"}</Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>
      <TabsContent value="overview" className="space-y-8 pt-4">
        <section aria-label="Primary VM">
          {primary ? <WorkspaceVmCard key={`${primary.id}:${primary.generation}:${profile?.id ?? ""}`} workspaceId={item.id} organizationId={organizationId}
            vm={primary} currency={item.currency} refresh={refresh} canManage={canManage} canManageConnect={canManageConnect} variant="primary"
            retainedStorage={{ sizeGb: item.volumeSizeGb, status: item.volumeStatus, estimatedMonthlyStorage: item.estimatedMonthlyStorage }} />
            : <Card><CardContent className="space-y-2 p-6 text-sm">
            <p className="font-medium">No primary VM reported.</p>
            <p className="text-muted-foreground">Refresh workspace status to load its primary VM. An ephemeral VM is never used as a substitute. Retained volume: {item.volumeSizeGb} GB · {item.volumeStatus ?? "status unavailable"}.</p>
          </CardContent></Card>}
        </section>
        <section aria-labelledby="ephemeral-heading" className="min-w-0 space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 id="ephemeral-heading" className="text-lg font-semibold">Ephemeral VMs</h2>
              <p className="text-sm text-muted-foreground">Each VM works on its own Git branch and disposable disk. Siblings start and stop independently.</p>
            </div>
            <LaunchEphemeralDialog item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
          </div>
          <EphemeralTable vms={ephemeral} workspaceId={item.id} organizationId={organizationId}
            currency={item.currency} refresh={refresh} canManage={canManage} actorId={profile?.id} />
        </section>
      </TabsContent>
      <TabsContent value="storage" className="space-y-6 pt-4">
        <Card>
          <CardHeader><CardTitle>Primary retained storage</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <dl className="grid gap-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
              <Metric label="Volume capacity" value={`${item.volumeSizeGb} GB`} />
              <Metric label="Volume status" value={item.volumeStatus ?? "Status unavailable"} />
              <Metric label="Estimated monthly storage" value={`${item.estimatedMonthlyStorage.toFixed(2)} ${item.currency}`} />
              <Metric label="Operator rate timestamp" value={item.pricingUpdatedAt ?? "Timestamp unavailable"} />
            </dl>
            <p className="text-sm text-muted-foreground">Retained storage continues to incur charges when all VMs are stopped. Only data on the primary retained volume survives compute replacement; primary root disks and all ephemeral disks are disposable. A retained volume is not a backup.</p>
          </CardContent>
        </Card>
        <section aria-label="Per-VM compute estimates" className="space-y-3">
          <h2 className="text-lg font-semibold">Per-VM compute estimates</h2>
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader><TableRow>
                <TableHead scope="col">VM</TableHead><TableHead scope="col">Observed phase</TableHead>
                <TableHead scope="col">Reservation</TableHead><TableHead scope="col">Hourly estimate</TableHead>
                <TableHead scope="col">Monthly estimate</TableHead>
              </TableRow></TableHeader>
              <TableBody>
                {[...(primary ? [primary] : []), ...vms.filter(vm => vm.id !== primary?.id)].map(vm => <TableRow key={vm.id}>
                  <TableCell className="break-words font-medium">{vm.id}<span className="block text-xs text-muted-foreground">{vm.kind}</span></TableCell>
                  <TableCell>{vm.phase}</TableCell>
                  <TableCell>{vm.computeReserved ? "Reserved · charges may continue" : "Not reserved"}</TableCell>
                  <TableCell className="whitespace-nowrap">{vm.computeHourlyRate.toFixed(4)} {item.currency}</TableCell>
                  <TableCell className="whitespace-nowrap">{vm.estimatedMonthlyCompute.toFixed(2)} {item.currency}</TableCell>
                </TableRow>)}
                {!vms.length && <TableRow><TableCell colSpan={5} className="py-6 text-center text-muted-foreground">No VM observations reported.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </div>
          <p className="text-sm text-muted-foreground">These are operator-configured estimates and observed reservations, not provider invoices or a billing guarantee. Currency: {item.currency}. Operator rates updated: {item.pricingUpdatedAt ?? "timestamp unavailable"}.</p>
          <p className="text-sm text-muted-foreground">Hetzner compute billing ends on deletion, not power-off, and rounds up to whole hours. Repeated short sessions may each incur an hour. Backups, snapshots, IPs, traffic and the control plane may cost extra.</p>
        </section>
      </TabsContent>
      <TabsContent value="settings" className="pt-4">
        <WorkspaceIdleTimeoutSettings item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
      </TabsContent>
    </Tabs>
  </div>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0 space-y-1"><dt className="text-muted-foreground">{label}</dt><dd className="break-words font-medium">{value}</dd></div>;
}

function EphemeralTable({ vms, workspaceId, organizationId, currency, refresh, canManage, actorId }: {
  vms: DevelopmentWorkspaceVm[]; workspaceId: string; organizationId: string; currency: string;
  refresh: Props["refresh"]; canManage: boolean; actorId?: string;
}) {
  return <div className="overflow-hidden rounded-lg border">
    <Table aria-label="Ephemeral VMs">
      <TableHeader><TableRow>
        <TableHead scope="col">Work branch / repository</TableHead>
        <TableHead scope="col">VM status</TableHead><TableHead scope="col">Git save</TableHead>
        <TableHead scope="col"><span className="sr-only">Details</span></TableHead>
      </TableRow></TableHeader>
      <TableBody>
        {vms.map(vm => <EphemeralRow key={`${vm.id}:${vm.generation}:${actorId ?? ""}`} vm={vm} workspaceId={workspaceId}
          organizationId={organizationId} currency={currency} refresh={refresh} canManage={canManage} />)}
        {!vms.length && <TableRow><TableCell colSpan={4} className="space-y-1 py-10 text-center">
          <p className="font-medium">No ephemeral VMs</p>
          <p className="text-muted-foreground">Launch an independent Git-only VM when you need parallel work. Viewing this page never starts compute.</p>
        </TableCell></TableRow>}
      </TableBody>
    </Table>
  </div>;
}

function EphemeralRow({ vm, workspaceId, organizationId, currency, refresh, canManage }: {
  vm: DevelopmentWorkspaceVm; workspaceId: string; organizationId: string; currency: string;
  refresh: Props["refresh"]; canManage: boolean;
}) {
  const { starting, failed, ready } = vmStartup(vm);
  const [expanded, setExpanded] = useState(starting || failed);
  const [visited, setVisited] = useState(starting || failed);
  const detailsId = useId();
  return <Fragment>
    <TableRow>
      <TableCell className="min-w-48">
        <div className="flex items-start gap-3 py-2">
          <GitBranch aria-hidden="true" className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0">
            <p className="break-all font-medium">{vm.workBranch ?? "Work branch pending"}</p>
            <p className="break-all text-xs text-muted-foreground">{vm.repository ?? "Repository unavailable"}</p>
          </div>
        </div>
      </TableCell>
      <TableCell><Badge role={expanded ? undefined : "status"} variant={failed || vm.errorCode && vm.phase !== "awaiting_git_save" ? "destructive" : "secondary"}
        className={vm.phase === "awaiting_git_save" ? "border-warning/30 bg-warning-surface text-warning"
          : ready ? "border-success/30 bg-success-surface text-success" : undefined}>{phaseLabel(vm)}</Badge></TableCell>
      <TableCell>{vm.gitStatus.replaceAll("_", " ").replace(/^./, character => character.toUpperCase())}</TableCell>
      <TableCell>
        <Button size="icon" variant="ghost" aria-label={`${expanded ? "Collapse" : "Expand"} VM ${vm.id}`}
          aria-expanded={expanded} aria-controls={detailsId} onClick={() => { setExpanded(!expanded); setVisited(true); }}>
          {expanded ? <ChevronDown aria-hidden="true" className="h-4 w-4" /> : <ChevronRight aria-hidden="true" className="h-4 w-4" />}
        </Button>
      </TableCell>
    </TableRow>
    <TableRow hidden={!expanded}>
      <TableCell colSpan={4} className={vm.phase === "awaiting_git_save" ? "border-t border-warning/30 bg-warning-surface p-0" : "bg-muted/20 p-0"}>
        <div id={detailsId} role="region" aria-label={`Details for VM ${vm.id}`} className="min-w-0">
          {visited && <WorkspaceVmCard workspaceId={workspaceId} organizationId={organizationId} vm={vm}
            currency={currency} refresh={refresh} canManage={canManage} variant="embedded" />}
        </div>
      </TableCell>
    </TableRow>
  </Fragment>;
}

function LaunchEphemeralDialog({ item, organizationId, refresh, canManage }: Props) {
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
    {canManage && <DialogTrigger asChild><Button variant="outline"><Plus aria-hidden="true" className="mr-2 h-4 w-4" />Launch ephemeral VM</Button></DialogTrigger>}
    <DialogContent className="max-w-2xl" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
      onInteractOutside={event => { if (busy) event.preventDefault(); }}>
      <DialogHeader>
        <DialogTitle>Launch ephemeral VM</DialogTitle>
        <DialogDescription>{lossDisclosure} Abrupt VM loss may lose everything since the last verified push.</DialogDescription>
      </DialogHeader>
      <form aria-label={`Launch ephemeral VM in ${item.name}`} onSubmit={event => void submit(event)} className="space-y-4">
        <fieldset disabled={busy} className="grid min-w-0 gap-4">
          <WorkspaceSelectionFields organizationId={organizationId} workspaceId={item.id} value={selection} onChange={setSelection} onReadyChange={setSelectionReady} disabled={busy} />
          <label className="space-y-1 text-sm font-medium">Repository URL<Input required value={repository} onChange={event => setRepository(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Base ref<Input required value={baseRef} onChange={event => setBaseRef(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Resolved base commit SHA<Input required pattern="[a-fA-F0-9]{40}|[a-fA-F0-9]{64}" value={baseCommit} onChange={event => setBaseCommit(event.target.value)} /></label>
          <label className="space-y-1 text-sm font-medium">Repository credential reference (optional; not a token)<Input value={credentialRef} onChange={event => setCredentialRef(event.target.value)} /></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" required className="mt-1 accent-primary" checked={pushAuthorized} onChange={event => setPushAuthorized(event.target.checked)} /><span>I authorize task-scoped pushes to this repository's isolated work branch.</span></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" required className="mt-1 accent-primary" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} /><span>I acknowledge ignored/untracked local data loss on ephemeral root deletion.</span></label>
        </fieldset>
        <p className="text-sm text-muted-foreground">Launch does not itself push, force-push, create a PR or merge. No primary or sibling files, credentials or retained storage are inherited. Launching explicitly allocates paid compute.</p>
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        <DialogFooter className="gap-2">
          <Button type="button" variant="outline" disabled={busy} onClick={() => setOpen(false)}>Cancel launch</Button>
          <Button disabled={!canManage || !valid || busy}>
            {busy && <LoaderCircle aria-hidden="true" className="mr-2 size-4 motion-safe:animate-spin" />}
            {busy ? "Starting VM…" : "Launch ephemeral VM"}
          </Button>
        </DialogFooter>
        <p role="status" className="sr-only">{busy ? "Starting VM…" : ""}</p>
      </form>
    </DialogContent>
  </Dialog>;
}
