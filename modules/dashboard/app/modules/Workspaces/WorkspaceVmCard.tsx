import { useEffect, useRef, useState } from "react";
import { AlertTriangle, Clock3, Terminal } from "lucide-react";
import { getWorkspaceLaunchUrl, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceT3Connect } from "./WorkspaceT3Connect";

type Props = {
  workspaceId: string;
  organizationId: string;
  vm: DevelopmentWorkspaceVm;
  currency: string;
  refresh: () => Promise<void>;
  canManage: boolean;
  canManageConnect?: boolean;
  variant?: "primary" | "embedded";
  retainedStorage?: { sizeGb: number; status?: string | null; estimatedMonthlyStorage: number };
};

const lossDisclosure = "Independent Git-only root disk; no access to the primary retained volume. Ignored, untracked and unpushed local data can be permanently lost. There is no filesystem backup.";
const message = (reason: unknown) => reason instanceof Error ? reason.message : "Workspace operation failed. Refresh status and try again.";

export function WorkspaceVmCard(props: Props) {
  return <VmGenerationCard key={`${props.organizationId}:${props.workspaceId}:${props.vm.id}:${props.vm.generation}`} {...props} />;
}

function VmGenerationCard({ workspaceId, organizationId, vm, currency, refresh, canManage, canManageConnect = false, variant = "primary", retainedStorage }: Props) {
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
  const [launch, setLaunch] = useState<{ url: string; expiresAt: string; context: string; service: string } | null>(null);
  const [discardOpen, setDiscardOpen] = useState(false);
  const [discardText, setDiscardText] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const mounted = useRef(true);
  const inFlight = useRef(false);
  const request = useRef(0);
  const ready = vm.phase === "ready" && vm.desiredState === "running" && !vm.errorCode;
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

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; ++request.current; };
  }, []);
  useEffect(() => {
    setSelection({ imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId });
  }, [vm.nextImageId, vm.nextProfile, vm.nextPlanId, vm.imageId, vm.profile, vm.planId]);
  useEffect(() => {
    if (!launch) return;
    const timer = window.setTimeout(() => setLaunch(null), Math.min(Math.max(0, Date.parse(launch.expiresAt) - Date.now()), 2_147_483_647));
    return () => window.clearTimeout(timer);
  }, [launch]);
  useEffect(() => {
    if (!canManage) { setDiscardOpen(false); setDiscardText(""); setAcknowledged(false); setLaunch(null); }
  }, [canManage]);
  useEffect(() => {
    setLaunch(previous => previous?.context === context ? previous : null);
  }, [context]);

  const run = async (label: string, operation: () => Promise<unknown>, refreshAfter = true) => {
    if (inFlight.current || !permission.current) return;
    inFlight.current = true;
    setPending(label); setError(null);
    if (refreshAfter) { ++request.current; setLaunch(null); }
    try {
      await operation();
      if (mounted.current && refreshAfter) await refresh();
    } catch (reason) {
      if (mounted.current) setError(message(reason));
    } finally {
      inFlight.current = false;
      if (mounted.current) setPending(null);
    }
  };
  const action = (operation: "start" | "stop" | "cancel-idle-stop", label: string) =>
    run(label, () => mutateWorkspaceVm(workspaceId, vm.id, organizationId, operation, { generation: vm.generation }));
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
    void run("Authorizing gateway access…", async () => {
      try {
        const result = await getWorkspaceLaunchUrl(workspaceId, vm.id, organizationId, vm.generation, target);
        let url: URL;
        try { url = new URL(result.url); }
        catch { throw new Error("Gateway returned an invalid launch URL. Request a new session or contact your operator."); }
        if (url.protocol !== "https:" || !url.hostname || url.username || url.password)
          throw new Error("Gateway returned an unsafe launch URL; an HTTPS URL without embedded credentials is required.");
        if (!Number.isFinite(Date.parse(result.expiresAt)) || Date.parse(result.expiresAt) <= Date.now())
          throw new Error("Gateway authorization has expired or has an invalid expiry. Request a new session.");
        if (mounted.current && sequence === request.current && currentContext.current === expectedContext && permission.current)
          setLaunch({ ...result, context: expectedContext, service: target });
      } catch (reason) {
        if (mounted.current && sequence === request.current && currentContext.current === expectedContext)
          setError(`${message(reason)} Compute was not started. Check gateway configuration, VM readiness and the registered preview slug, then retry or contact your operator.`);
      }
    }, false);
  };
  const closeDiscard = (open: boolean) => {
    setDiscardOpen(open); setDiscardText(""); setAcknowledged(false);
  };

  const startControl = vm.desiredState === "stopped" && <Button disabled={blocked || Boolean(vm.startAfterStop)}
    onClick={() => void action("start", stopping ? "Queueing start…" : "Starting compute…")}>{stopping ? "Queue start after stop" : "Start compute"}</Button>;
  const stopControl = (vm.desiredState !== "stopped" || awaitingGit || vm.startAfterStop) && <Button variant="outline" disabled={blocked}
    onClick={() => void action("stop", awaitingGit ? "Retrying stop…" : "Stopping compute…")}>{awaitingGit ? "Retry stop" : "Stop compute"}</Button>;
  const discardControl = vm.kind === "ephemeral" && <Button variant="ghost" className="text-destructive hover:text-destructive" disabled={blocked || vm.phase === "stopped"}
    onClick={() => closeDiscard(true)}>Discard local data…</Button>;
  const Surface = embedded ? "div" : Card;

  return <Surface role="article" aria-label={`VM ${vm.id}`} className={embedded ? "min-w-0" : "overflow-hidden"}>
    {!embedded && <CardHeader className="flex-row items-center justify-between gap-3 space-y-0 p-5 pb-4">
      <div className="flex min-w-0 items-start gap-3">
        <div className="rounded-lg border bg-muted/30 p-3"><Terminal className="size-5 text-muted-foreground" aria-hidden="true" /></div>
        <div className="min-w-0 space-y-1">
          <CardTitle><h3>{vm.kind === "primary" ? "Primary VM" : "Ephemeral VM"}</h3></CardTitle>
          <p className="text-sm text-muted-foreground">{vm.profile} · {vm.region} · {vm.sku}</p>
        </div>
      </div>
      <Badge variant="secondary" className={`shrink-0 ${awaitingGit ? "border-warning/25 bg-warning-surface text-warning" : ""}`}>
        {awaitingGit ? "Git save blocked" : vm.phase === "ready" ? "Ready" : vm.phase === "stopped" ? "Stopped" : vm.phase.replaceAll("_", " ")}
      </Badge>
    </CardHeader>}
    {!embedded && <dl className="grid gap-4 px-5 pb-4 text-sm sm:grid-cols-3">
      <div className="space-y-1"><dt className="text-muted-foreground">Retained volume</dt><dd className="font-medium">{retainedStorage ? `${retainedStorage.sizeGb} GB` : vm.kind === "primary" ? "Not reported" : "None · Git-only root"}</dd></div>
      <div className="space-y-1"><dt className="text-muted-foreground">Estimated storage</dt><dd className="font-medium">{retainedStorage ? `${retainedStorage.estimatedMonthlyStorage.toFixed(2)} ${currency} / month` : "Not reported"}</dd></div>
      <div className="space-y-1"><dt className="text-muted-foreground">Compute estimate</dt><dd className="font-medium">{vm.computeHourlyRate.toFixed(4)} {currency} / hour</dd>
        <dd className="text-xs text-muted-foreground">{vm.computeReserved ? "Reserved · charges may continue" : "No reservation reported"}</dd></div>
    </dl>}
    {awaitingGit && <section aria-label={`Git save recovery for VM ${vm.id}`} className="border-y border-warning/20 bg-warning-surface p-4 text-sm">
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 size-5 shrink-0 text-warning" aria-hidden="true" />
        <div className="min-w-0 space-y-2">
          <h4 className="font-medium">Git save needs attention</h4>
          <p className="break-words text-muted-foreground">Reported reason: {vm.errorMessage ?? vm.errorCode ?? "No reason reported. Ask your operator to inspect this generation's Git-save operation."}</p>
          <p className="text-muted-foreground">Restore repository access, then retry stop. {vm.computeReserved ? "Compute remains reserved and may incur charges." : "No reservation is reported; provider deletion is not confirmed here."}</p>
          <div className="flex flex-wrap gap-2 pt-1">{stopControl}{discardControl}</div>
        </div>
      </div>
    </section>}
    {vm.startAfterStop && <p role="status" className="px-5 py-2 text-sm">Authorized start queued after this generation safely stops. Stop compute cancels the queued start.</p>}
    {pending && <p role="status" className="px-5 py-2 text-sm">{pending}</p>}
    {!canManage && <p className="px-5 py-2 text-xs text-muted-foreground">Read-only access. A workspace manager is required for compute changes and session authorization.</p>}
    {vm.errorCode && !awaitingGit && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{vm.errorCode}: {vm.errorMessage ?? "No additional details reported."}</p>}
    {error && <p role="alert" className="break-words px-5 py-2 text-sm text-destructive">{error}</p>}
    {(!awaitingGit || !embedded) && <div className={`flex flex-wrap items-center justify-between gap-3 px-5 py-3 ${embedded ? "" : "border-t bg-muted/20"}`}>
      {ready ? <section aria-label={`Connections for VM ${vm.id}`} className="flex flex-wrap items-center gap-2">
        <Button disabled={blocked} onClick={() => authorize("t3")}>Connect to T3</Button>
        <Button variant="outline" disabled={blocked} aria-expanded={previewOpen} onClick={() => setPreviewOpen(previous => !previous)}>Open preview</Button>
      </section> : !stopping ? startControl : <span className="text-xs text-muted-foreground">Generation {vm.generation} · {vm.phase.replaceAll("_", " ")}</span>}
      <p className="flex items-center gap-2 text-xs text-muted-foreground"><Clock3 className="size-3.5" aria-hidden="true" />Last activity: {vm.lastActivityAt ?? "unavailable"}</p>
    </div>}
    {ready && previewOpen && <form aria-label={`Open preview for VM ${vm.id}`} className="flex flex-wrap items-end gap-2 border-t px-5 py-3" onSubmit={event => { event.preventDefault(); authorize(previewSlug.trim()); }}>
      <label className="min-w-0 space-y-1 text-sm">Registered preview slug<Input className="w-full sm:w-48" value={previewSlug} disabled={blocked} placeholder="my-preview" required onChange={event => { ++request.current; setLaunch(null); setPreviewSlug(event.target.value); }} /></label>
      <Button variant="outline" disabled={blocked || !previewSlug.trim()}>Authorize preview</Button>
    </form>}
    {canManage && launch && launch.context === context && Date.parse(launch.expiresAt) > Date.now() && <p className="break-words px-5 py-2 text-sm">
      <a className="text-primary underline underline-offset-4" href={launch.url} target="_blank" rel="noopener noreferrer" onClick={event => {
        if (Date.parse(launch.expiresAt) <= Date.now()) { event.preventDefault(); setLaunch(null); setError("Gateway authorization has expired. Request a new session."); }
      }}>Open authorized {launch.service === "t3" ? "T3 session" : `${launch.service} preview`} on VM {vm.id} generation {vm.generation}</a>
      <span className="ml-2 text-muted-foreground">Expires {launch.expiresAt}</span>
    </p>}
    {vm.kind === "primary" && canManage && canManageConnect && <div className="border-t px-5 py-4">
      <WorkspaceT3Connect workspaceId={workspaceId} vmId={vm.id} organizationId={organizationId}
        generation={vm.generation} revision={vm.revision} ready={ready && !busy} />
    </div>}
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
          <div><dt>Compute estimates</dt><dd>{vm.computeHourlyRate.toFixed(4)} {currency}/hour · {vm.estimatedMonthlyCompute.toFixed(2)} {currency}/month</dd></div>
          <div><dt>Reservation</dt><dd>{vm.computeReserved ? "Reserved — charges may continue until deletion is confirmed." : "No compute reservation reported; this is not confirmation of provider deletion or a final bill."}</dd></div>
          <div><dt>Activity</dt><dd>Last activity: {vm.lastActivityAt ?? "unavailable"} · Idle stop deadline: {vm.shutdownDeadline ?? "none reported"}</dd></div>
          <div><dt>Image / plan / server</dt><dd>{vm.imageId} · {vm.planId} · {vm.providerServerId ?? "none reported"} · Last ready: {vm.lastReadyAt ?? "unavailable"}</dd></div>
          {retainedStorage && <div><dt>Retained volume status</dt><dd>{retainedStorage.status ?? "status unavailable"}</dd></div>}
        </dl>
        <div className="space-y-2 break-words text-xs text-muted-foreground">
          {vm.kind === "primary" ? <p>Only data on the retained workspace volume survives compute replacement; root-disk files are disposable. Retained storage continues to cost money while stopped and is not a backup.</p> : <>
            <p>{lossDisclosure}</p>
            <p>Repository: {vm.repository ?? "unreported"} · {vm.baseRef ?? "unreported"}@{vm.baseCommit ?? "unreported"}</p>
            <p>Work branch: {vm.workBranch ?? "pending"} · Git save: {vm.gitStatus}</p>
            {vm.finalHead && <p>Final local HEAD: {vm.finalHead}</p>}
            {vm.verifiedRemoteCommit && <p>Verified remote: {vm.verifiedRemoteRef ?? "unreported"}@{vm.verifiedRemoteCommit}</p>}
          </>}
          <p>{ready ? "Compute ready. Gateway authorization is separate and never starts compute." : "Sessions and previews are unavailable until this generation passes all readiness checks. Viewing this VM never starts compute."}</p>
        </div>
        {awaitingGit && <div className="space-y-2 text-xs text-muted-foreground">
          <p>Awaiting Git save · generation {vm.generation} · {vm.errorCode ?? vm.gitStatus}</p>
          <ol className="list-inside list-decimal space-y-1">
            <li>Restore repository access and authentication; check remote availability and resolve any reported Git conflicts.</li>
            <li>Verify the isolated work branch can be pushed, then choose Retry stop for this VM generation.</li>
            <li>Keep compute while saving, or explicitly discard this generation's local data. Sibling VMs are not affected.</li>
          </ol>
        </div>}
        <form aria-label={`Resize VM ${vm.id}`} className="grid gap-3" onSubmit={event => {
          event.preventDefault();
          if (!selectionReady || !selection.imageId || !selection.planId) return;
          void run("Saving next-start size…", () => mutateWorkspaceVm(workspaceId, vm.id, organizationId, "resize", { generation: vm.generation, ...selection }));
        }}>
          <WorkspaceSelectionFields workspaceId={workspaceId} organizationId={organizationId} value={selection} onChange={setSelection} onReadyChange={setSelectionReady} disabled={blocked} />
          <p className="text-xs text-muted-foreground">Changes apply on next start only, not live CPU/RAM resizing. Next profile: {vm.nextProfile ?? vm.profile} · image {vm.nextImageId ?? vm.imageId} · plan {vm.nextPlanId ?? vm.planId}.</p>
          <Button variant="outline" disabled={blocked || !selectionReady || !selection.imageId || !selection.planId}>Set size on next start</Button>
        </form>
        <form aria-label={`Task lease for VM ${vm.id}`} className="space-y-3" onSubmit={event => { event.preventDefault(); task(false); }}>
          <label>Task ID<Input required maxLength={128} value={taskId} disabled={blocked} onChange={event => setTaskId(event.target.value)} /></label>
          <p className="text-xs text-muted-foreground">Keep-awake leases are bound to VM {vm.id}, generation {vm.generation}, and expire unless renewed. Completing or cancelling a task releases its lease, not compute.</p>
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
