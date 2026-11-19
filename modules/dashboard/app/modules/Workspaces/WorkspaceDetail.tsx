import { Fragment, useEffect, useId, useRef, useState, type FormEvent } from "react";
import { ChevronDown, ChevronRight, GitBranch, LoaderCircle, Plus } from "lucide-react";
import { Link } from "react-router-dom";
import { deleteWorkspace, launchEphemeralVm, workspaceOperation } from "@/api/workspacesApi";
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
import { permanentDeletion } from "./workspaceCollection";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: (observation?: DevelopmentWorkspace) => Promise<void>;
  canManage: boolean;
};

const initialSelection: WorkspaceSelection = { imageId: "", profile: "Standard", planId: "" };
const lossDisclosure = "A temporary VM has its own disk and can't see the primary VM's storage. Untracked files, files ignored by Git and unpushed work can be lost permanently; there is no backup.";

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "Something went wrong. Refresh status and try again.";
}

function phaseLabel(vm: DevelopmentWorkspaceVm) {
  const { starting, failed, ready } = vmStartup(vm);
  if (vm.phase === "awaiting_git_save") return "Git save blocked";
  if (failed) return "VM startup failed";
  if (starting) return "Starting VM…";
  if (vm.errorCode) return "Needs attention";
  if (ready) return "Running";
  if (vm.phase === "stopped") return "Stopped";
  return vm.phase.replaceAll("_", " ");
}

export function WorkspaceDetail(props: Props) {
  return <ScopedWorkspaceDetail key={`${props.organizationId}:${props.item.id}`} {...props} />;
}

function ScopedWorkspaceDetail({ item, organizationId, refresh, canManage: canManageRole }: Props) {
  const { profile, selectedOrganization } = useAuth();
  const deletion = permanentDeletion(item);
  // Once permanent deletion is requested, only retrying deletion remains available.
  const canManage = canManageRole && !deletion;
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
    {item.errorCode && !(primary && (vmStartup(primary).failed || primary.errorCode === item.errorCode)) && <p role="alert" className="text-sm text-destructive">{item.errorCode}: {item.errorMessage}</p>}
    {refreshError && <p role="alert" className="text-sm text-destructive">{refreshError}</p>}
    {deletion === "deleting" && <p role="status" className="rounded-md border border-warning bg-warning-surface p-3 text-sm text-warning">
      Deleting this dev environment and its saved files. Refresh to check progress.</p>}
    {deletion === "failed" && <p className="rounded-md border border-destructive p-3 text-sm text-destructive">
      Deleting this dev environment failed. Fix the reported problem, then retry from Delete environment below.</p>}
    {deletion === "deleted" && <p role="status" className="rounded-md border p-3 text-sm">
      This dev environment and its saved files were permanently deleted. <Link className="font-medium text-primary underline" to="/workspaces">Back to dev environments</Link></p>}
    {!canManageRole && <p className="text-sm text-muted-foreground">You can view this dev environment but can't change it.</p>}
    <Tabs defaultValue="overview" className="min-w-0">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
        <TabsList aria-label="Dev environment details" className="h-auto gap-6 rounded-none bg-transparent p-0">
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
        </Dialog>
      </div>
      <TabsContent value="overview" className="space-y-8 pt-4">
        <section aria-label="Primary VM">
          {primary ? <WorkspaceVmCard key={`${primary.id}:${primary.generation}:${profile?.id ?? ""}`} workspaceId={item.id} organizationId={organizationId}
            vm={primary} currency={item.currency} refresh={refresh} canManage={canManage} canManageConnect={canManageConnect} variant="primary"
            retainedStorage={{ sizeGb: item.volumeSizeGb, status: item.volumeStatus, estimatedMonthlyStorage: item.estimatedMonthlyStorage }} />
            : <Card><CardContent className="space-y-2 p-6 text-sm">
            <p className="font-medium">No primary VM reported.</p>
            <p className="text-muted-foreground">Select Refresh status to load it. A temporary VM is never used instead. Storage: {item.volumeSizeGb} GB · {item.volumeStatus ?? "status unavailable"}.</p>
          </CardContent></Card>}
        </section>
        {!!item.agents?.length && <section aria-labelledby="agents-heading" className="space-y-2">
          <h2 id="agents-heading" className="text-lg font-semibold">Coding agents</h2>
          <ul className="flex flex-wrap gap-2 text-sm">
            {item.agents.map(agent => <li key={agent.id} className="rounded-md border px-3 py-1.5">
              <span className="font-medium">{agent.name}</span> <span className="text-muted-foreground">{agent.version}</span>
            </li>)}
          </ul>
          <p className="text-sm text-muted-foreground">Installed in this dev environment's image and available in T3. Sign in to each agent from T3 before first use.</p>
        </section>}
        <section aria-labelledby="ephemeral-heading" className="min-w-0 space-y-4">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div>
              <h2 id="ephemeral-heading" className="text-lg font-semibold">Temporary VMs</h2>
              <p className="text-sm text-muted-foreground">Each temporary VM works on its own Git branch with its own disk, and starts and stops on its own.</p>
            </div>
            <LaunchEphemeralDialog item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
          </div>
          <EphemeralTable vms={ephemeral} workspaceId={item.id} organizationId={organizationId}
            currency={item.currency} refresh={refresh} canManage={canManage} actorId={profile?.id} />
        </section>
        {canManageRole && deletion !== "deleted" && <DeleteEnvironment item={item} organizationId={organizationId}
          vms={vms} failed={deletion === "failed"} deleting={deletion === "deleting"} refresh={refresh} />}
      </TabsContent>
      <TabsContent value="storage" className="space-y-6 pt-4">
        <Card>
          <CardHeader><CardTitle>Storage</CardTitle></CardHeader>
          <CardContent className="space-y-4">
            <dl className="grid gap-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
              <Metric label="Size" value={`${item.volumeSizeGb} GB`} />
              <Metric label="Status" value={item.volumeStatus ?? "Status unavailable"} />
              <Metric label="Estimated cost per month" value={`${item.estimatedMonthlyStorage.toFixed(2)} ${item.currency}`} />
              <Metric label="Prices last updated" value={item.pricingUpdatedAt ?? "Unavailable"} />
            </dl>
            <p className="text-sm text-muted-foreground">Storage is charged even when all VMs are stopped. Only files on this storage are kept when the primary VM is replaced; other disks, including every temporary VM's disk, are deleted. Storage isn't a backup.</p>
          </CardContent>
        </Card>
        <section aria-label="VM costs" className="space-y-3">
          <h2 className="text-lg font-semibold">VM costs</h2>
          <div className="overflow-hidden rounded-lg border">
            <Table>
              <TableHeader><TableRow>
                <TableHead scope="col">VM</TableHead><TableHead scope="col">Status</TableHead>
                <TableHead scope="col">Server</TableHead><TableHead scope="col">Per hour</TableHead>
                <TableHead scope="col">Per month if running</TableHead>
              </TableRow></TableHeader>
              <TableBody>
                {[...(primary ? [primary] : []), ...vms.filter(vm => vm.id !== primary?.id)].map(vm => <TableRow key={vm.id}>
                  <TableCell className="break-words font-medium">{vm.kind === "primary" ? "Primary VM" : "Temporary VM"}<span className="block text-xs text-muted-foreground">{vm.id}</span></TableCell>
                  <TableCell>{phaseLabel(vm)}</TableCell>
                  <TableCell>{vm.computeReserved ? "Allocated · charges may continue" : "Not allocated"}</TableCell>
                  <TableCell className="whitespace-nowrap">{vm.computeHourlyRate.toFixed(4)} {item.currency}</TableCell>
                  <TableCell className="whitespace-nowrap">{vm.estimatedMonthlyCompute.toFixed(2)} {item.currency}</TableCell>
                </TableRow>)}
                {!vms.length && <TableRow><TableCell colSpan={5} className="py-6 text-center text-muted-foreground">No VMs reported.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </div>
          <p className="text-sm text-muted-foreground">These are estimates based on your operator's prices, not invoices or a billing guarantee. Currency: {item.currency}. Prices last updated: {item.pricingUpdatedAt ?? "unavailable"}.</p>
          <p className="text-sm text-muted-foreground">Cloud providers charge for a VM's server until it's deleted, not just powered off, and may round up to whole hours. Repeated short sessions may each cost an hour. Backups, snapshots, IPs, traffic and the control plane may cost extra.</p>
        </section>
      </TabsContent>
      <TabsContent value="settings" className="pt-4">
        <WorkspaceIdleTimeoutSettings item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
      </TabsContent>
    </Tabs>
  </div>;
}

function DeleteEnvironment({ item, organizationId, vms, failed, deleting, refresh }: {
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

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0 space-y-1"><dt className="text-muted-foreground">{label}</dt><dd className="break-words font-medium">{value}</dd></div>;
}

function EphemeralTable({ vms, workspaceId, organizationId, currency, refresh, canManage, actorId }: {
  vms: DevelopmentWorkspaceVm[]; workspaceId: string; organizationId: string; currency: string;
  refresh: Props["refresh"]; canManage: boolean; actorId?: string;
}) {
  return <div className="overflow-hidden rounded-lg border">
    <Table aria-label="Temporary VMs">
      <TableHeader><TableRow>
        <TableHead scope="col">Work branch / repository</TableHead>
        <TableHead scope="col">Status</TableHead><TableHead scope="col">Git save</TableHead>
        <TableHead scope="col"><span className="sr-only">Details</span></TableHead>
      </TableRow></TableHeader>
      <TableBody>
        {vms.map(vm => <EphemeralRow key={`${vm.id}:${vm.generation}:${actorId ?? ""}`} vm={vm} workspaceId={workspaceId}
          organizationId={organizationId} currency={currency} refresh={refresh} canManage={canManage} />)}
        {!vms.length && <TableRow><TableCell colSpan={4} className="space-y-1 py-10 text-center">
          <p className="font-medium">No temporary VMs</p>
          <p className="text-muted-foreground">Launch a separate Git-only VM when you need parallel work. Viewing this page never starts a VM.</p>
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
    {canManage && <DialogTrigger asChild><Button variant="outline"><Plus aria-hidden="true" className="mr-2 h-4 w-4" />Launch temporary VM</Button></DialogTrigger>}
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
