import { useCallback, useEffect, useRef, useState } from "react";
import { useParams } from "react-router-dom";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PageScaffold } from "@/core/components/PageScaffold";
import { useAuth } from "@/core/auth/AuthProvider";
import { createDevelopmentWorkspace, listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { deleteWorkspace, getWorkspace, getWorkspaceLaunchUrl, launchEphemeralVm, mutateWorkspaceVm, workspaceOperation, workspaceTask } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";

type Selection = { imageId: string; profile: string; planId: string };
const initialSelection: Selection = { imageId: "", profile: "Standard", planId: "" };
const lossDisclosure = "Ephemeral VMs have independent Git-only root disks and no access to the primary retained volume. Ignored, untracked and unpushed local data can be permanently lost; there is no filesystem backup.";

function SelectionFields({ value, onChange }: { value: Selection; onChange: (value: Selection) => void }) {
  return <>
    <label>Registered image ID<Input required value={value.imageId} onChange={event => onChange({ ...value, imageId: event.target.value })} /></label>
    <label>Profile<select value={value.profile} onChange={event => onChange({ ...value, profile: event.target.value })}><option>Standard</option><option>Heavy</option></select></label>
    <label>Approved plan ID<Input required value={value.planId} onChange={event => onChange({ ...value, planId: event.target.value })} /></label>
  </>;
}

function message(reason: unknown) {
  return reason instanceof Error ? reason.message : "Workspace operation failed.";
}

function useRequestKey() {
  const previous = useRef<{ payload: string; key: string } | null>(null);
  return {
    forRequest: (request: unknown) => {
      const payload = JSON.stringify(request);
      if (previous.current?.payload !== payload) previous.current = { payload, key: crypto.randomUUID() };
      return previous.current.key;
    },
    complete: () => { previous.current = null; },
  };
}

export function WorkspacesModule() {
  const { selectedOrganization } = useAuth();
  const { workspaceId } = useParams();
  return <PageScaffold title="Development workspaces">
    <p>Starts are explicit. Viewing this page, readiness checks and previews never allocate compute. Primary retained volumes continue to incur storage charges while stopped.</p>
    <p>{lossDisclosure}</p>
    {workspaceId && <a href="/workspaces">All workspaces</a>}
    {selectedOrganization ? <OrganizationWorkspaces key={`${selectedOrganization.id}:${workspaceId ?? ""}`} organizationId={selectedOrganization.id} workspaceId={workspaceId} /> : <p>Select an organization.</p>}
  </PageScaffold>;
}

function OrganizationWorkspaces({ organizationId, workspaceId }: { organizationId: string; workspaceId?: string }) {
  const [items, setItems] = useState<DevelopmentWorkspace[]>([]);
  const [selection, setSelection] = useState(initialSelection);
  const [name, setName] = useState("");
  const [volumeSizeGb, setVolumeSizeGb] = useState(20);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  const createKey = useRequestKey();
  const active = useRef(true);
  const revision = useRef(0);
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const request = ++revision.current;
    try {
      const result = workspaceId ? [await getWorkspace(workspaceId, organizationId, signal)] : await listDevelopmentWorkspaces(organizationId, signal);
      if (active.current && request === revision.current) { setItems(result); setError(null); }
    } catch (reason) {
      if (active.current && !signal?.aborted && request === revision.current) setError(message(reason));
    } finally {
      if (active.current && request === revision.current) setLoading(false);
    }
  }, [organizationId, workspaceId]);
  useEffect(() => {
    active.current = true;
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => { active.current = false; controller.abort(); ++revision.current; };
  }, [refresh]);

  const applyDeletion = (updated: DevelopmentWorkspace) => {
    if (!active.current) return;
    // A read already in flight may still contain the pre-deletion workspace.
    ++revision.current;
    setItems(current => current.flatMap(item => item.id !== updated.id ? [item]
      : updated.phase === "deleted" && !workspaceId ? [] : [updated]));
  };

  const create = async (event: React.FormEvent) => {
    event.preventDefault();
    if (creating) return;
    setCreating(true);
    setError(null);
    try {
      const request = { organizationId, name, ...selection, volumeSizeGb };
      await createDevelopmentWorkspace({ ...request, idempotencyKey: createKey.forRequest(request) });
      if (!active.current) return;
      createKey.complete();
      setName("");
      await refresh();
    } catch (reason) { if (active.current) setError(message(reason)); }
    finally { if (active.current) setCreating(false); }
  };
  return <>
    {error && <p role="alert">{error}</p>}
    {!workspaceId && <form aria-label="Create workspace" className="grid gap-3 border p-4" onSubmit={event => void create(event)}>
      <h2>Create stopped workspace</h2>
      <label>Name<Input required maxLength={128} value={name} onChange={event => setName(event.target.value)} /></label>
      <SelectionFields value={selection} onChange={setSelection} />
      <label>Retained volume (GB)<Input type="number" required min={10} max={10000} value={volumeSizeGb} onChange={event => setVolumeSizeGb(Number(event.target.value))} /></label>
      <Button disabled={creating}>Create stopped workspace</Button>
    </form>}
    <Button variant="outline" onClick={() => void refresh()}>Refresh workspace status</Button>
    {loading ? <p role="status">Loading workspaces…</p> : items.length === 0 ? <p>No development workspaces.</p> : null}
    <section aria-label="Workspace list" className="space-y-4">
      {items.map(item => <WorkspaceCard key={item.id} item={item} organizationId={organizationId} refresh={refresh} applyDeletion={applyDeletion} />)}
    </section>
  </>;
}

type Operation = (operation: () => Promise<unknown>) => Promise<void>;

function WorkspaceCard({ item, organizationId, refresh, applyDeletion }: {
  item: DevelopmentWorkspace; organizationId: string; refresh: () => Promise<void>; applyDeletion: (item: DevelopmentWorkspace) => void;
}) {
  const [selection, setSelection] = useState(initialSelection);
  const [repository, setRepository] = useState("");
  const [baseRef, setBaseRef] = useState("");
  const [baseCommit, setBaseCommit] = useState("");
  const [repositoryCredentialRef, setRepositoryCredentialRef] = useState("");
  const [pushAuthorized, setPushAuthorized] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmStopAll, setConfirmStopAll] = useState(false);
  const [confirmDeletePhase, setConfirmDeletePhase] = useState<string | null>(null);
  const [deletePending, setDeletePending] = useState(false);
  const launchKey = useRequestKey();
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const run: Operation = async operation => {
    if (busy) return;
    setBusy(true); setError(null);
    try { await operation(); if (mounted.current) await refresh(); }
    catch (reason) { if (mounted.current) setError(message(reason)); }
    finally { if (mounted.current) setBusy(false); }
  };
  const deleting = item.phase === "deleting" && item.volumeStatus === "deleting";
  const deleteFailed = item.phase === "delete_failed";
  const deleted = item.phase === "deleted";
  const canDelete = !deleting && !deleted;
  const confirmDelete = canDelete && confirmDeletePhase === item.phase;
  const remove = async () => {
    if (busy || deletePending || !confirmDelete) return;
    setConfirmDeletePhase(null);
    setConfirmStopAll(false);
    setDeletePending(true);
    setError(null);
    try { applyDeletion(await deleteWorkspace(item.id, organizationId)); }
    catch (reason) { if (mounted.current) setError(message(reason)); }
    finally { if (mounted.current) setDeletePending(false); }
  };
  return <article aria-label={`Workspace ${item.name}`} className="space-y-3 rounded border p-4">
    <h2>{item.name} <small>{item.id}</small></h2>
    <a href={`/workspaces/${encodeURIComponent(item.id)}`}>Workspace details: {item.name}</a>
    {deleting && <p role="status">Permanent deletion is in progress. Refresh workspace status to check again.</p>}
    {deleteFailed && <p>Permanent deletion failed. Resolve the reported error before retrying.</p>}
    {deleted && <p role="status">Workspace permanently deleted.</p>}
    {deletePending && <p role="status">Requesting permanent deletion…</p>}
    {(item.errorCode || item.errorMessage) && <p role="alert">{[item.errorCode, item.errorMessage].filter(Boolean).join(": ")}</p>}
    {error && <p role="alert">{error}</p>}
    {canDelete && !confirmDelete && <Button variant="destructive" disabled={busy || deletePending}
      onClick={() => setConfirmDeletePhase(item.phase)}>{deleteFailed ? "Retry permanent deletion…" : "Permanently delete workspace…"}</Button>}
    {confirmDelete && <section aria-label={`Confirm permanent deletion of ${item.name}`}>
      <p>Irreversible: permanently delete {item.name} ({item.id}). Its primary retained volume and any remaining local VM data will be permanently lost.</p>
      {!deleteFailed && <p>Stop all VMs and wait for compute cleanup to finish before deleting.</p>}
      <Button variant="destructive" disabled={busy || deletePending} onClick={() => void remove()}>Confirm permanent deletion</Button>
      <Button variant="outline" onClick={() => setConfirmDeletePhase(null)}>Cancel deletion</Button>
    </section>}
    {!deleting && !deleteFailed && !deleted && !deletePending && <>
    <p>Primary retained volume: {item.volumeSizeGb} GB · {item.volumeStatus ?? "status unavailable"} · estimated storage {item.estimatedMonthlyStorage.toFixed(2)} {item.currency}/month.</p>
    <p>Operator rates: {item.pricingUpdatedAt ?? "timestamp unavailable"}. Compute estimates are per VM, not a billing guarantee.</p>
    <p>Hetzner compute billing ends on deletion, not power-off, and rounds up to whole hours. Retained storage, backups, IPs, traffic and the control plane may cost extra.</p>
    <Button variant="outline" disabled={busy || !item.vms?.length} onClick={() => setConfirmStopAll(true)}>Stop all VMs</Button>
    {confirmStopAll && <section aria-label="Confirm stop all">
      <p>Stop these VMs independently. Primary data is retained. Each ephemeral VM must pass its own Git-save guard; blocked VMs remain allocated and may incur costs.</p>
      <ul>{item.vms.map(vm => <li key={vm.id}>{vm.id} · {vm.kind} · generation {vm.generation} · {vm.phase} · Git {vm.gitStatus}</li>)}</ul>
      <Button disabled={busy} onClick={() => void run(async () => { await workspaceOperation(item.id, organizationId, "stop-all"); if (mounted.current) setConfirmStopAll(false); })}>Confirm stop all</Button>
      <Button variant="outline" onClick={() => setConfirmStopAll(false)}>Cancel stop all</Button>
    </section>}
    <section aria-label="Workspace VMs" className="space-y-3">
      {(item.vms ?? []).map(vm => <VmCard key={`${vm.id}:${vm.generation}`} workspaceId={item.id} organizationId={organizationId} vm={vm} currency={item.currency} refresh={refresh} />)}
    </section>
    <form aria-label={`Launch ephemeral VM in ${item.name}`} className="grid gap-3 border p-3" onSubmit={event => {
      event.preventDefault();
      if (!pushAuthorized || !acknowledged) return;
      void run(async () => {
        const request = { ...selection, repository, baseRef, baseCommit, pushAuthorized, acknowledgeIgnoredDataLoss: acknowledged,
          ...(repositoryCredentialRef.trim() ? { repositoryCredentialRef: repositoryCredentialRef.trim() } : {}) };
        await launchEphemeralVm(item.id, organizationId, { ...request, idempotencyKey: launchKey.forRequest(request) });
        launchKey.complete();
      });
    }}>
      <h3>Launch independent ephemeral VM</h3>
      <SelectionFields value={selection} onChange={setSelection} />
      <label>Repository URL<Input required value={repository} onChange={event => setRepository(event.target.value)} /></label>
      <label>Base ref<Input required value={baseRef} onChange={event => setBaseRef(event.target.value)} /></label>
      <label>Resolved base commit SHA<Input required pattern="[a-fA-F0-9]{40}|[a-fA-F0-9]{64}" value={baseCommit} onChange={event => setBaseCommit(event.target.value)} /></label>
      <label>Repository credential reference (optional; not a token)<Input value={repositoryCredentialRef} onChange={event => setRepositoryCredentialRef(event.target.value)} /></label>
      <label><input type="checkbox" required checked={pushAuthorized} onChange={event => setPushAuthorized(event.target.checked)} />I authorize task-scoped pushes to this repository's isolated work branch.</label>
      <label><input type="checkbox" required checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />I acknowledge ignored/untracked local data loss on ephemeral root deletion.</label>
      <p>{lossDisclosure} Launch does not itself push, force-push, create a PR or merge.</p>
      <Button disabled={busy || !pushAuthorized || !acknowledged}>Launch ephemeral VM</Button>
    </form>
    </>}
  </article>;
}

function VmCard({ workspaceId, organizationId, vm, currency, refresh }: {
  workspaceId: string; organizationId: string; vm: DevelopmentWorkspaceVm; currency: string; refresh: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<Selection>({ imageId: vm.nextImageId ?? vm.imageId, profile: vm.nextProfile ?? vm.profile, planId: vm.nextPlanId ?? vm.planId });
  const [discardOpen, setDiscardOpen] = useState(false);
  const [discardText, setDiscardText] = useState("");
  const [acknowledged, setAcknowledged] = useState(false);
  const [taskId, setTaskId] = useState("");
  const [leaseResult, setLeaseResult] = useState<string | null>(null);
  const [service, setService] = useState("t3");
  const [launch, setLaunch] = useState<{ url: string; expiresAt: string } | null>(null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const run: Operation = async operation => {
    if (busy) return;
    setBusy(true); setError(null);
    try { await operation(); if (mounted.current) await refresh(); }
    catch (reason) { if (mounted.current) setError(message(reason)); }
    finally { if (mounted.current) setBusy(false); }
  };
  const action = (operation: "start" | "stop" | "cancel-idle-stop") =>
    run(() => mutateWorkspaceVm(workspaceId, vm.id, organizationId, operation, { generation: vm.generation }));
  const confirmText = `discard ${vm.id} ${vm.generation}`;
  const ready = vm.phase === "ready" && vm.desiredState === "running" && !vm.errorCode;
  useEffect(() => { setLaunch(null); }, [ready, service, vm.revision]);
  const task = (complete: boolean, cancelled = false) => run(async () => {
    const lease = await workspaceTask(workspaceId, vm.id, organizationId, taskId, vm.generation, complete, cancelled);
    if (mounted.current) setLeaseResult(`${lease.taskId}: ${lease.status}; generation ${lease.generation}; expires ${lease.expiresAt}`);
  });
  return <article aria-label={`VM ${vm.id}`} className="space-y-2 border p-3">
    <h3>{vm.kind === "primary" ? "Primary · retained volume" : "Ephemeral · Git-only root"} · {vm.id}</h3>
    <p>Generation {vm.generation} · {vm.desiredState} / {vm.phase} · {vm.profile} / {vm.sku} / {vm.region}</p>
    {vm.startAfterStop && <p>Authorized start queued after the current generation is safely stopped.</p>}
    <p>Readiness stage: {vm.phase}. Provisioning → volume attachment (primary only) → enrollment → readiness.</p>
    <p>Estimated compute: {vm.computeHourlyRate.toFixed(4)} {currency}/hour · {vm.estimatedMonthlyCompute.toFixed(2)} {currency}/month · {vm.computeReserved ? "reserved / charges may continue" : "not reserved"}</p>
    <p>Last activity: {vm.lastActivityAt ?? "unavailable"} · Idle stop deadline: {vm.shutdownDeadline ?? "none"}</p>
    {vm.kind === "primary" && <p>Only data on the retained workspace volume survives compute replacement; root-disk files are disposable. A retained volume is not a backup.</p>}
    {vm.kind === "ephemeral" && <><p>{lossDisclosure}</p><p>Repository: {vm.repository} · {vm.baseRef}@{vm.baseCommit} · work branch: {vm.workBranch ?? "pending"} · Git save: {vm.gitStatus}</p>
      {vm.verifiedRemoteCommit && <p>Verified remote: {vm.verifiedRemoteRef}@{vm.verifiedRemoteCommit}</p>}</>}
    {vm.phase === "awaiting_git_save" && <p role="alert">Awaiting Git save. Resolve the remote, authentication or conflict reason below, then retry Stop compute. Keep compute until Git save succeeds, or explicitly discard this generation's local data.</p>}
    {vm.errorCode && <p role="alert">{vm.errorCode}: {vm.errorMessage}</p>}
    {error && <p role="alert">{error}</p>}
    <div className="flex flex-wrap gap-2">
      <Button disabled={busy || vm.desiredState !== "stopped"} onClick={() => void action("start")}>Start compute</Button>
      <Button variant="outline" disabled={busy || (vm.desiredState === "stopped" && vm.phase !== "awaiting_git_save")} onClick={() => void action("stop")}>Stop compute</Button>
      <Button variant="outline" disabled={busy || !vm.shutdownDeadline} onClick={() => void action("cancel-idle-stop")}>Cancel idle stop</Button>
    </div>
    <p>{ready ? "Compute ready. Session and preview links require separately authorized gateway readiness." : "Session and preview links unavailable until this VM generation passes all readiness checks."}</p>
    {ready && <form aria-label={`Open VM ${vm.id}`} className="space-y-2" onSubmit={event => {
      event.preventDefault();
      void run(async () => {
        setLaunch(null);
        const result = await getWorkspaceLaunchUrl(workspaceId, vm.id, organizationId, vm.generation, service);
        if (new URL(result.url).protocol !== "https:") throw new Error("Gateway returned an unsafe launch URL.");
        if (mounted.current) setLaunch(result);
      });
    }}>
      <label>Gateway service (t3 or registered preview slug)<Input required value={service} onChange={event => setService(event.target.value)} /></label>
      <Button disabled={busy}>Authorize session / preview</Button>
      {launch && Date.parse(launch.expiresAt) > Date.now() && <a href={launch.url} target="_blank" rel="noopener noreferrer" onClick={() => setLaunch(null)}>Open {service} on VM {vm.id} generation {vm.generation}</a>}
    </form>}
    <form aria-label={`Resize VM ${vm.id}`} className="grid gap-2" onSubmit={event => { event.preventDefault(); void run(() => mutateWorkspaceVm(workspaceId, vm.id, organizationId, "resize", { generation: vm.generation, ...selection })); }}>
      <SelectionFields value={selection} onChange={setSelection} />
      <p>Change size on next start only; no live CPU/RAM hot resizing. Next profile: {vm.nextProfile ?? "unchanged"}.</p>
      <Button variant="outline" disabled={busy}>Set size on next start</Button>
    </form>
    <form aria-label={`Task lease for VM ${vm.id}`} className="space-y-2" onSubmit={event => { event.preventDefault(); void task(false); }}>
      <label>Task ID<Input required value={taskId} onChange={event => setTaskId(event.target.value)} /></label>
      <p>Keep-awake leases are bound to VM {vm.id}, generation {vm.generation}, and expire unless renewed. Completed/cancelled tasks release their lease.</p>
      {(vm.taskLeases ?? []).map(lease => <p key={`${lease.taskId}:${lease.generation}`}>{lease.taskId}: {lease.status} · generation {lease.generation} · expires {lease.expiresAt}</p>)}
      {!vm.taskLeases?.length && <p>No task leases reported.</p>}
      {leaseResult && <p role="status">{leaseResult}</p>}
      <Button disabled={busy || !taskId || !ready}>Renew keep-awake lease</Button>
      <Button type="button" variant="outline" disabled={busy || !taskId} onClick={() => void task(true)}>Complete task</Button>
      <Button type="button" variant="outline" disabled={busy || !taskId} onClick={() => void task(true, true)}>Cancel task</Button>
    </form>
    {vm.kind === "ephemeral" && <Button variant="destructive" disabled={busy} onClick={() => setDiscardOpen(true)}>Discard local data…</Button>}
    {discardOpen && <form aria-label={`Discard VM ${vm.id} generation ${vm.generation}`} className="space-y-2 border p-3" onSubmit={event => {
      event.preventDefault();
      if (discardText !== confirmText || !acknowledged) return;
      void run(async () => {
        await mutateWorkspaceVm(workspaceId, vm.id, organizationId, "discard", { generation: vm.generation, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
        if (mounted.current) { setDiscardOpen(false); setDiscardText(""); setAcknowledged(false); }
      });
    }}>
      <p>Irreversible: discard VM {vm.id}, generation {vm.generation}. Unpushed, ignored and untracked local data will be permanently lost. Other VMs and the primary volume are not discarded.</p>
      <label>Type {confirmText}<Input required value={discardText} onChange={event => setDiscardText(event.target.value)} /></label>
      <label><input type="checkbox" required checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />I acknowledge irreversible ignored/untracked and unpushed data loss for this generation.</label>
      <Button variant="destructive" disabled={busy || discardText !== confirmText || !acknowledged}>Confirm irreversible discard</Button>
      <Button type="button" variant="outline" onClick={() => { setDiscardOpen(false); setDiscardText(""); setAcknowledged(false); }}>Cancel discard</Button>
    </form>}
  </article>;
}
