import { useCallback, useEffect, useRef, useState } from "react";
import { AlertTriangle, Clock3, Terminal } from "lucide-react";
import { getWorkspaceLaunchUrl, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceLaunchLink } from "./WorkspaceLaunchLink";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceT3Connect } from "./WorkspaceT3Connect";
import { WorkspaceDiskUsage } from "./WorkspaceDiskUsage";
import { WorkspaceVmResources } from "./WorkspaceVmResources";
import { vmStartup, WorkspaceVmStartup } from "./WorkspaceVmStartup";

type Props = {
  workspaceId: string;
  organizationId: string;
  vm: DevelopmentWorkspaceVm;
  currency: string;
  refresh: (observation?: DevelopmentWorkspace) => Promise<void>;
  canManage: boolean;
  canManageConnect?: boolean;
  variant?: "primary" | "embedded";
  retainedStorage?: { sizeGb: number; status?: string | null; estimatedMonthlyStorage: number };
  /** Whether the card is on screen; resource history refreshes only while active. */
  active?: boolean;
};

const lossDisclosure = "This VM has its own Git-only disk and can't see the primary VM's storage. Untracked files, files ignored by Git and unpushed work can be lost permanently. There is no backup.";
const message = (reason: unknown) => reason instanceof Error ? reason.message : "Something went wrong. Refresh status and try again.";
const startingLabel = "Starting VM…";
type Launch = { context: string; service: string } & (
  { state: "active"; url: string; deadline: number } | { state: "expired" }
);

export function WorkspaceVmCard(props: Props) {
  return <VmGenerationCard key={`${props.organizationId}:${props.workspaceId}:${props.vm.id}:${props.vm.generation}`} {...props} />;
}

function VmGenerationCard({ workspaceId, organizationId, vm, currency, refresh, canManage, canManageConnect = false, variant = "primary", retainedStorage, active = true }: Props) {
  const [pending, setPending] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<WorkspaceSelection>({
    imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId,
  });
  const [selectionReady, setSelectionReady] = useState(false);
  const [taskId, setTaskId] = useState("");
  const [leaseResult, setLeaseResult] = useState<string | null>(null);
  const [previewSlug, setPreviewSlug] = useState("");
  const [previewOpen, setPreviewOpen] = useState(false);
  const [service, setService] = useState("t3");
  const [launch, setLaunch] = useState<Launch | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const [discardText, setDiscardText] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const request = useRef(0);
  const { ready, starting, failed } = vmStartup(vm);
  const context = JSON.stringify([vm.revision, vm.generation, ready, canManage, service, previewSlug]);
  const currentContext = useRef(context);
  currentContext.current = context;
  const permission = useRef(canManage);
  permission.current = canManage;
  const busy = pending !== null;
  const blocked = busy || !canManage;
  const awaitingGit = vm.phase === "awaiting_git_save";
  const stopping = ["draining", "deleting", "awaiting_git_save"].includes(vm.phase);
  const confirmText = `discard ${vm.id} ${vm.generation}`;
  const embedded = variant === "embedded";
  const imageChanges = !!selection.imageId && selection.imageId !== vm.imageId;
  const startingRequest = pending === startingLabel;
  const visibleLaunch = canManage && launch?.context === context ? launch : null;
  const expireLaunch = useCallback(() => {
    setLaunch(current => current?.state === "active" && current === launch
      ? { context: current.context, service: current.service, state: "expired" } : current);
  }, [launch]);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; ++request.current; };
  }, []);
  useEffect(() => {
    setSelection({ imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId });
  }, [vm.nextImageId, vm.nextProfile, vm.nextPlanId, vm.imageId, vm.profile, vm.planId]);
  useEffect(() => {
    if (!canManage) { ++request.current; setDiscardOpen(false); setDiscardText(""); setAcknowledged(false); setLaunch(null); }
  }, [canManage]);
  useEffect(() => {
    setLaunch(previous => previous?.context === context ? previous : null);
  }, [context]);

  const run = async (label: string, operation: () => Promise<DevelopmentWorkspace | void>, refreshAfter = true) => {
    if (inFlight.current || !permission.current) return;
    inFlight.current = true;
    setPending(label); setError(null);
    if (refreshAfter) { ++request.current; setLaunch(null); }
    try {
      const observation = await operation();
      if (mounted.current && refreshAfter) await refresh(observation || undefined);
    } catch (reason) {
      if (mounted.current) setError(message(reason));
    } finally {
      inFlight.current = false;
      if (mounted.current) setPending(null);
    }
  };
  const action = (operation: "start" | "stop" | "cancel-idle-stop", label: string) =>
    run(label, () => mutateWorkspaceVm(workspaceId, vm.id, organizationId, operation, { generation: vm.generation }));
  // Stopping keeps the generation, so the follow-up start is queued to run once the failed attempt is cleaned up.
  const retryStart = () => run(startingLabel, async () => {
    await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "stop", { generation: vm.generation });
    return mutateWorkspaceVm(workspaceId, vm.id, organizationId, "start", { generation: vm.generation });
  });
  const task = (complete: boolean, cancelled = false) => {
    if (!taskId.trim() || (!complete && !ready)) return;
    void run(cancelled ? "Cancelling task…" : complete ? "Completing task…" : "Renewing lease…", async () => {
      const lease = await workspaceTask(workspaceId, vm.id, organizationId, taskId.trim(), vm.generation, complete, cancelled);
      if (mounted.current) setLeaseResult(`${lease.taskId}: ${lease.status}; generation ${lease.generation}; expires ${lease.expiresAt}`);
    });
  };
  const authorize = (target: string) => {
    if (!ready || !target.trim() || inFlight.current || !canManage) return;
    setService(target);
    setLaunch(null);
    const expectedContext = JSON.stringify([vm.revision, vm.generation, ready, canManage, target, previewSlug]);
    currentContext.current = expectedContext;
    const sequence = ++request.current;
    void run("Getting a secure link…", async () => {
      const current = () => mounted.current && sequence === request.current && currentContext.current === expectedContext && permission.current;
      const report = (error: string) => {
        if (current()) setError(`${error} This didn't start the VM. Check that the VM is running and the preview name is registered, then try again or contact your operator.`);
      };
      let result: Awaited<ReturnType<typeof getWorkspaceLaunchUrl>>;
      try {
        result = await getWorkspaceLaunchUrl(workspaceId, vm.id, organizationId, vm.generation, target);
      } catch {
        report("Couldn't get a secure link.");
        return;
      }
      if (!current()) return;
      let url: URL;
      try { url = new URL(result?.url); }
      catch { report("The link returned was invalid."); return; }
      if (url.protocol !== "https:" || !url.hostname || url.username || url.password) {
        report("The link returned was unsafe.");
        return;
      }
      const deadline = Date.parse(result.expiresAt);
      if (!Number.isFinite(deadline)) { report("The link returned had an invalid expiry."); return; }
      const scope = { context: expectedContext, service: target };
      setLaunch(deadline > Date.now() ? { ...scope, state: "active", url: url.href, deadline } : { ...scope, state: "expired" });
    }, false);
  };
  const closeDiscard = (open: boolean) => {
    setDiscardOpen(open); setDiscardText(""); setAcknowledged(false);
  };

  const startControl = vm.desiredState === "stopped" && <Button disabled={blocked || Boolean(vm.startAfterStop)}
    onClick={() => void action("start", stopping ? "Queueing start…" : startingLabel)}>{stopping ? "Start VM after it stops" : vm.errorCode ? "Retry start" : "Start VM"}</Button>;
  const retryControl = failed && vm.kind === "primary" && canManage && <Button disabled={blocked}
    onClick={() => void retryStart()}>Retry start</Button>;
  const stopControl = (vm.desiredState !== "stopped" || awaitingGit || vm.startAfterStop) && <Button variant="outline" disabled={blocked}
    onClick={() => void action("stop", awaitingGit ? "Retrying stop…" : "Stopping VM…")}>{awaitingGit ? "Retry stop" : "Stop VM"}</Button>;
  const discardControl = vm.kind === "ephemeral" && <Button variant="ghost" className="text-destructive hover:text-destructive" disabled={blocked || vm.phase === "stopped"}
    onClick={() => closeDiscard(true)}>Discard local data…</Button>;
  const Surface = embedded ? "div" : Card;

  return <Surface role="article" aria-label={`VM ${vm.id}`} className={embedded ? "min-w-0" : "overflow-hidden"}>
    {!embedded && <CardHeader className="flex-row items-center justify-between gap-3 space-y-0 p-5 pb-4">
      <div className="flex min-w-0 items-start gap-3">
        <div className="rounded-lg border bg-muted/30 p-3"><Terminal className="size-5 text-muted-foreground" aria-hidden="true" /></div>
        <div className="min-w-0 space-y-1">
          <CardTitle><h3>{vm.kind === "primary" ? "Primary VM" : "Temporary VM"}</h3></CardTitle>
          <p className="text-sm text-muted-foreground">{vm.profile} · {vm.region} · {vm.sku}</p>
        </div>
      </div>
      <Badge variant="secondary" className={`shrink-0 ${awaitingGit ? "border-warning/25 bg-warning-surface text-warning" : ""}`}>
        {startingRequest || starting ? "Starting" : failed ? "Startup failed" : awaitingGit ? "Git save blocked" : ready ? "Running"
          : vm.phase === "ready" ? "Not ready" : vm.phase === "stopped" ? "Stopped" : vm.phase.replaceAll("_", " ")}
      </Badge>
    </CardHeader>}
    {!embedded && <dl className="grid gap-4 px-5 pb-4 text-sm sm:grid-cols-3">
      <div className="space-y-1"><dt className="text-muted-foreground">Storage</dt><dd className="font-medium">{retainedStorage ? `${retainedStorage.sizeGb} GB` : vm.kind === "primary" ? "Not reported" : "None · Git only"}</dd></div>
      <div className="space-y-1"><dt className="text-muted-foreground">Storage cost</dt><dd className="font-medium">{retainedStorage ? `${retainedStorage.estimatedMonthlyStorage.toFixed(2)} ${currency} / month` : "Not reported"}</dd></div>
      <div className="space-y-1"><dt className="text-muted-foreground">VM cost</dt><dd className="font-medium">{vm.computeHourlyRate.toFixed(4)} {currency} / hour while running</dd>
        <dd className="text-xs text-muted-foreground">{vm.computeReserved ? "Server allocated · charges may continue" : "No server allocated"}</dd></div>
    </dl>}
    {awaitingGit && <section aria-label={`Git save recovery for VM ${vm.id}`} className="border-y border-warning/20 bg-warning-surface p-4 text-sm">
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 size-5 shrink-0 text-warning" aria-hidden="true" />
        <div className="min-w-0 space-y-2">
          <h4 className="font-medium">Git save needs attention</h4>
          <p className="break-words text-muted-foreground">Reported reason: {vm.errorMessage ?? vm.errorCode ?? "No reason reported. Ask your operator to inspect this generation's Git-save operation."}</p>
          <p className="text-muted-foreground">Restore repository access, then retry stop. {vm.computeReserved ? "The VM's server is still allocated and may keep charging." : "No server is allocated, but this doesn't confirm the provider deleted it."}</p>
          <div className="flex flex-wrap gap-2 pt-1">{stopControl}{discardControl}</div>
        </div>
      </div>
    </section>}
    <WorkspaceVmStartup vm={vm} pending={startingRequest} />
    {vm.startAfterStop && <p role="status" className="px-5 py-2 text-sm">This VM will start again after it finishes stopping. Select Stop VM to cancel.</p>}
    {pending && !startingRequest && <p role="status" className="px-5 py-2 text-sm">{pending}</p>}
    {!canManage && <p className="px-5 py-2 text-xs text-muted-foreground">You can view this VM but can't start, stop or open it.</p>}
    {vm.errorCode && !awaitingGit && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{vm.errorCode}: {vm.errorMessage ?? "No additional details reported."}</p>}
    {error && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{error}</p>}
    {(!awaitingGit || !embedded) && <div className={`flex flex-wrap items-center justify-between gap-3 px-5 py-3 ${embedded ? "" : "border-t bg-muted/20"}`}>
      {ready ? <section aria-label={`Connections for VM ${vm.id}`} className="flex flex-wrap items-center gap-2">
        <Button disabled={blocked} onClick={() => authorize("t3")}>Connect to T3</Button>
        <Button variant="outline" disabled={blocked} aria-expanded={previewOpen} onClick={() => {
          ++request.current; setLaunch(null); setPreviewOpen(previous => !previous);
        }}>Open preview</Button>
      </section> : !stopping ? startControl || retryControl : <span className="text-xs text-muted-foreground">{awaitingGit ? "Waiting for Git save" : "Stopping…"}</span>}
      <p className="flex items-center gap-2 text-xs text-muted-foreground"><Clock3 className="size-3.5" aria-hidden="true" />
        {vm.lastActivityAt ? `Last activity: ${vm.lastActivityAt}` : "Waiting for first activity"}</p>
    </div>}
    {ready && previewOpen && <form aria-label={`Open preview for VM ${vm.id}`} className="flex flex-wrap items-end gap-2 border-t px-5 py-3" onSubmit={event => { event.preventDefault(); authorize(previewSlug.trim()); }}>
      <label className="min-w-0 space-y-1 text-sm">Preview name<Input className="w-full sm:w-48" value={previewSlug} disabled={blocked} placeholder="my-preview" required onChange={event => { ++request.current; setLaunch(null); setPreviewSlug(event.target.value); }} /></label>
      <Button variant="outline" disabled={blocked || !previewSlug.trim()}>Get preview link</Button>
    </form>}
    {visibleLaunch?.state === "active" && <WorkspaceLaunchLink url={visibleLaunch.url} deadline={visibleLaunch.deadline}
      label={visibleLaunch.service === "t3" ? "Open T3" : "Open preview"} onExpire={expireLaunch} />}
    <div className={visibleLaunch?.state === "expired" ? "flex flex-wrap items-center gap-2 px-5 py-2 text-sm" : undefined}>
      <p role="status">{visibleLaunch?.state === "expired" ? "Link expired" : ""}</p>
      {visibleLaunch?.state === "expired" && <Button variant="outline" disabled={blocked} onClick={() => authorize(visibleLaunch.service)}>Get a new link</Button>}
    </div>
    {vm.kind === "primary" && canManage && canManageConnect && !starting && !startingRequest && !failed && <div className="border-t px-5 py-4">
      <WorkspaceT3Connect workspaceId={workspaceId} vmId={vm.id} organizationId={organizationId}
        generation={vm.generation} revision={vm.revision} ready={ready && !busy} />
    </div>}
    <WorkspaceVmResources workspaceId={workspaceId} organizationId={organizationId} vm={vm} active={active} />
    {canManage && vm.resources && vm.resources.state !== "unsupported" && <WorkspaceDiskUsage workspaceId={workspaceId}
      organizationId={organizationId} vm={vm} ready={ready && !busy} refresh={() => refresh()} />}
    <details className="min-w-0 border-t px-5 py-2 text-sm">
      <summary className="w-fit cursor-pointer text-xs text-muted-foreground hover:text-foreground">Advanced VM controls</summary>
      <div className="space-y-5 py-4">
        <div className="flex flex-wrap gap-2">
          {stopping && startControl}
          {!awaitingGit && stopControl}
          {vm.shutdownDeadline && <Button variant="outline" disabled={blocked || !["ready", "draining"].includes(vm.phase)}
            onClick={() => void action("cancel-idle-stop", "Cancelling idle stop…")}>Cancel idle stop</Button>}
          {!awaitingGit && discardControl}
        </div>
        <dl className="grid gap-2 break-words text-xs text-muted-foreground sm:grid-cols-2">
          <div><dt>VM / generation</dt><dd>{vm.id} · Generation {vm.generation} · desired {vm.desiredState} · phase {vm.phase}</dd></div>
          <div><dt>Placement</dt><dd>{vm.profile} · {vm.sku} · {vm.region} · provider account {vm.providerAccountId}</dd></div>
          <div><dt>Cost estimates</dt><dd>{vm.computeHourlyRate.toFixed(4)} {currency}/hour · {vm.estimatedMonthlyCompute.toFixed(2)} {currency}/month</dd></div>
          <div><dt>Server</dt><dd>{vm.computeReserved ? "Allocated — charges may continue until it's deleted." : "Not allocated. This doesn't confirm provider deletion or a final bill."}</dd></div>
          <div><dt>Activity</dt><dd>{vm.lastActivityAt ? `Last activity: ${vm.lastActivityAt}` : "Waiting for first activity"} · Idle stop deadline: {vm.shutdownDeadline ?? "none reported"}</dd></div>
          <div><dt>Image / plan / server</dt><dd>{vm.imageId} · {vm.planId} · {vm.providerServerId ?? "none reported"}{vm.lastReadyAt && ` · Last ready: ${vm.lastReadyAt}`}</dd></div>
          {retainedStorage && <div><dt>Storage status</dt><dd>{retainedStorage.status ?? "status unavailable"}</dd></div>}
        </dl>
        <div className="space-y-2 break-words text-xs text-muted-foreground">
          {vm.kind === "primary" ? <p>Only files on this dev environment's storage are kept when the VM is replaced; other disk files are deleted. Storage is charged while the VM is stopped and isn't a backup.</p> : <>
            <p>{lossDisclosure}</p>
            <p>Repository: {vm.repository ?? "unreported"} · {vm.baseRef ?? "unreported"}@{vm.baseCommit ?? "unreported"}</p>
            <p>Work branch: {vm.workBranch ?? "pending"} · Git save: {vm.gitStatus}</p>
            {vm.finalHead && <p>Final local HEAD: {vm.finalHead}</p>}
            {vm.verifiedRemoteCommit && <p>Verified remote: {vm.verifiedRemoteRef ?? "unreported"}@{vm.verifiedRemoteCommit}</p>}
          </>}
          <p>{ready ? "The VM is running. Opening T3 or a preview never starts a VM." : "T3 and previews open once the VM is running and ready. Viewing this VM never starts it."}</p>
        </div>
        {awaitingGit && <div className="space-y-2 text-xs text-muted-foreground">
          <p>Awaiting Git save · generation {vm.generation} · {vm.errorCode ?? vm.gitStatus}</p>
          <ol className="list-inside list-decimal space-y-1">
            <li>Restore repository access and authentication; check remote availability and resolve any reported Git conflicts.</li>
            <li>Verify the isolated work branch can be pushed, then choose Retry stop for this VM generation.</li>
            <li>Keep the VM while saving, or explicitly discard this generation's local data. Other VMs are not affected.</li>
          </ol>
        </div>}
        <form aria-label={`Resize VM ${vm.id}`} className="grid gap-3" onSubmit={event => {
          event.preventDefault();
          if (!selectionReady || !selection.imageId || !selection.planId) return;
          void run("Saving next-start settings…", () => mutateWorkspaceVm(workspaceId, vm.id, organizationId, "resize", { generation: vm.generation, ...selection }));
        }}>
          <WorkspaceSelectionFields workspaceId={workspaceId} organizationId={organizationId} value={selection} onChange={setSelection} onReadyChange={setSelectionReady} disabled={blocked} />
          <p className="text-xs text-muted-foreground">Applies the next time this VM starts; a running VM isn't resized.{imageChanges ? " This also changes the VM's image." : ""} Next start: size {vm.nextProfile ?? vm.profile} · image {vm.nextImageId ?? vm.imageId} · plan {vm.nextPlanId ?? vm.planId}.</p>
          <Button variant="outline" disabled={blocked || !selectionReady || !selection.imageId || !selection.planId}>{imageChanges ? "Use this size and image at next start" : "Use this size at next start"}</Button>
        </form>
        <form aria-label={`Task lease for VM ${vm.id}`} className="space-y-3" onSubmit={event => { event.preventDefault(); task(false); }}>
          <label>Task ID<Input required maxLength={128} value={taskId} disabled={blocked} onChange={event => setTaskId(event.target.value)} /></label>
          <p className="text-xs text-muted-foreground">Keep-awake leases are bound to VM {vm.id}, generation {vm.generation}, and expire unless renewed. Completing or cancelling a task releases its lease, not the VM.</p>
          {(vm.taskLeases ?? []).map(lease => <p key={`${lease.taskId}:${lease.generation}`}>{lease.taskId}: {lease.status} · generation {lease.generation} · expires {lease.expiresAt}</p>)}
          {!vm.taskLeases?.length && <p className="text-muted-foreground">No task leases reported.</p>}
          {leaseResult && <p role="status">{leaseResult}</p>}
          <div className="flex flex-wrap gap-2">
            <Button disabled={blocked || !taskId.trim() || !ready}>Renew keep-awake lease</Button>
            <Button type="button" variant="outline" disabled={blocked || !taskId.trim()} onClick={() => task(true)}>Complete task</Button>
            <Button type="button" variant="outline" disabled={blocked || !taskId.trim()} onClick={() => task(true, true)}>Cancel task</Button>
          </div>
        </form>
      </div>
    </details>
    <Dialog open={discardOpen && canManage} onOpenChange={open => { if (!inFlight.current) closeDiscard(open); }}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Discard VM {vm.id} generation {vm.generation}</DialogTitle>
          <DialogDescription>Irreversibly delete this ephemeral generation's local data without a verified Git save. Ignored, untracked and unpushed data can be lost permanently. Sibling VMs and the primary retained volume are not affected.</DialogDescription>
        </DialogHeader>
        <form aria-label={`Discard VM ${vm.id} generation ${vm.generation}`} className="space-y-4" onSubmit={event => {
          event.preventDefault();
          if (!discardOpen || discardText !== confirmText || !acknowledged) return;
          void run("Discarding this generation…", async () => {
            await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "discard", { generation: vm.generation, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
            if (mounted.current) closeDiscard(false);
          });
        }}>
          <label className="block space-y-2 text-sm">Type {confirmText}<Input autoComplete="off" disabled={blocked} value={discardText} onChange={event => setDiscardText(event.target.value)} /></label>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" disabled={blocked} checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />I acknowledge irreversible ignored, untracked and unpushed local data loss for VM {vm.id}, generation {vm.generation}.</label>
          {pending && <p role="status">{pending}</p>}
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={busy} onClick={() => closeDiscard(false)}>Cancel discard</Button>
            <Button variant="destructive" disabled={blocked || discardText !== confirmText || !acknowledged}>Confirm irreversible discard</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  </Surface>;
}
