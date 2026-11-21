import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ChevronRight, Terminal } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { PageScaffold } from "@/core/components/PageScaffold";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { compareRecords, useCollectionQuery } from "@/core/components/collectionQuery";
import { useAuth } from "@/core/auth/AuthProvider";
import { listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { getWorkspace } from "@/api/workspacesApi";
import { ApiError, type DevelopmentWorkspace } from "@/models/controlPlane";
import { CreateWorkspaceDialog } from "./CreateWorkspaceDialog";
import { WorkspaceDetail } from "./WorkspaceDetail";
import { WorkspaceEnvironmentActions } from "./WorkspaceEnvironmentActions";
import { ephemeralSummary, lastActivity, permanentDeletion, primaryState, primaryVm } from "./workspaceCollection";
import { vmStartup } from "./WorkspaceVmStartup";
import { message } from "./workspaceVmLabels";

const fastPollMs = 3_000;
const slowPollMs = 10_000;
const slowStartupMs = 45_000;
const autoRefreshLimitMs = 180_000;
const statusRequestTimeoutMs = 15_000;

export function WorkspacesModule() {
  const { selectedOrganization } = useAuth();
  const { workspaceId } = useParams();
  if (!selectedOrganization) return <PageScaffold title="Dev Environments"><p>Select an organization.</p></PageScaffold>;
  const canManage = ["owner", "admin", "developer"].includes(selectedOrganization.role?.toLowerCase() ?? "");
  return <OrganizationWorkspaces key={`${selectedOrganization.id}:${workspaceId ?? ""}`}
    organizationId={selectedOrganization.id} workspaceId={workspaceId} canManage={canManage} />;
}

function OrganizationWorkspaces({ organizationId, workspaceId, canManage }: {
  organizationId: string; workspaceId?: string; canManage: boolean;
}) {
  const [items, setItems] = useState<DevelopmentWorkspace[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [delayed, setDelayed] = useState(false);
  const [paused, setPaused] = useState(false);
  const [pollCycle, setPollCycle] = useState(0);
  const [actionError, setActionError] = useState<string | null>(null);
  const [query, setQuery] = useCollectionQuery(`${organizationId}:workspaces`, "name", "asc");
  const navigate = useNavigate();
  const active = useRef(true);
  const request = useRef<{ controller: AbortController; done: Promise<void> } | null>(null);
  const refresh = useCallback((observation?: DevelopmentWorkspace) => {
    if (!active.current) return Promise.resolve();
    if (observation && observation.id === workspaceId && observation.organizationId === organizationId) {
      setItems(items => !items[0] || items[0].revision < observation.revision ? [observation] : items);
      request.current?.controller.abort();
    } else if (request.current) return request.current.done;
    const controller = new AbortController();
    const { signal } = controller;
    const timeout = workspaceId ? window.setTimeout(() => controller.abort("status timeout"), statusRequestTimeoutMs) : undefined;
    setLoading(true);
    const done = Promise.resolve().then(async () => {
      try {
        if (signal.aborted) return;
        const result = workspaceId ? [await getWorkspace(workspaceId, organizationId, signal)]
          : await listDevelopmentWorkspaces(organizationId, signal);
        if (active.current && request.current?.controller === controller && !signal.aborted) {
          setItems(result);
          setError(null);
        }
      } catch (reason) {
        if (active.current && request.current?.controller === controller && !signal.aborted) {
          if (workspaceId && reason instanceof ApiError && [401, 403, 404].includes(reason.status)) setItems([]);
          setError(reason instanceof Error ? reason.message : "Couldn't load dev environments.");
        }
      } finally {
        window.clearTimeout(timeout);
        if (active.current && request.current?.controller === controller) {
          if (signal.reason === "status timeout") setError("The status request timed out.");
          request.current = null;
          setLoading(false);
        }
      }
    });
    request.current = { controller, done };
    return done;
  }, [organizationId, workspaceId]);
  useEffect(() => {
    active.current = true;
    void refresh();
    return () => {
      active.current = false;
      request.current?.controller.abort();
      request.current = null;
    };
  }, [refresh]);

  const item = items[0];
  const startingGenerations = workspaceId ? (item?.vms ?? [])
    .filter(vm => vm.startAfterStop || vmStartup(vm).starting)
    .map(vm => `${vm.id}:${vm.generation}`).sort().join(",") : "";
  useEffect(() => {
    setDelayed(false);
    setPaused(false);
    if (!startingGenerations) return;
    let active = true;
    let timer: number | undefined;
    const started = Date.now();
    const schedule = () => {
      if (!active) return;
      const elapsed = Date.now() - started;
      if (elapsed >= slowStartupMs) setDelayed(true);
      if (elapsed >= autoRefreshLimitMs) { setPaused(true); return; }
      timer = window.setTimeout(async () => {
        if (Date.now() - started < autoRefreshLimitMs) await refresh();
        schedule();
      }, Math.min(elapsed >= slowStartupMs ? slowPollMs : fastPollMs, autoRefreshLimitMs - elapsed));
    };
    schedule();
    return () => { active = false; window.clearTimeout(timer); };
  }, [startingGenerations, pollCycle, refresh]);
  const checkStatus = () => {
    if (paused) setPollCycle(cycle => cycle + 1);
    void refresh();
  };
  const refreshAfterMutation = async (observation?: DevelopmentWorkspace) => {
    setActionError(null);
    try { await refresh(observation); }
    catch (reason) { if (active.current) setActionError(`Operation accepted. Refresh failed: ${message(reason)}`); }
  };
  const filtered = items.filter(workspace => workspace.name.toLocaleLowerCase().includes(query.search.trim().toLocaleLowerCase())
    && (!query.filters.primary || primaryState(workspace).value === query.filters.primary)
    && (!query.filters.ephemeral || ephemeralSummary(workspace).state === query.filters.ephemeral))
    .sort((a, b) => compareRecords(query.sort === "activity" ? lastActivity(a)?.at ?? null : a.name,
      query.sort === "activity" ? lastActivity(b)?.at ?? null : b.name, query.direction, a.id, b.id));
  return <PageScaffold title={workspaceId ? item?.name ?? "Dev environment" : "Dev Environments"}
    breadcrumbs={workspaceId ? [{ label: "Dev Environments", to: "/workspaces" }, { label: item?.name ?? "Dev environment" }] : undefined}
    actions={workspaceId && item ? <WorkspaceEnvironmentActions key={item.id} item={item} organizationId={organizationId}
      refreshAfterMutation={refreshAfterMutation} canManage={canManage && !permanentDeletion(item)}
      onRefreshStatus={checkStatus} refreshing={loading} /> : undefined}>
    {!workspaceId && <>
      <CollectionToolbar label="dev environments" compact query={query}
        onChange={next => setQuery({ ...next, view: "table" })} onRefresh={() => refresh()} loading={loading}
        count={filtered.length} filters={[
          { id: "primary", label: "Primary VM state", options: [
            { value: "ready", label: "Running" }, { value: "stopped", label: "Stopped" },
            { value: "starting", label: "Starting" }, { value: "stopping", label: "Stopping" },
            { value: "attention", label: "Needs attention" }, { value: "unavailable", label: "Unavailable" },
          ] },
          { id: "ephemeral", label: "Temporary VMs", options: [
            { value: "attention", label: "Needs attention" }, { value: "running", label: "Running" },
            { value: "transitioning", label: "Starting or stopping" }, { value: "stopped", label: "Stopped" },
            { value: "none", label: "None" }, { value: "unavailable", label: "Unavailable" },
          ] },
        ]} sorts={[{ value: "name", label: "Name" }, { value: "activity", label: "Last activity" }]}
        trailingAction={<CreateWorkspaceDialog organizationId={organizationId} canManage={canManage}
          onCreated={workspace => navigate(`/workspaces/${encodeURIComponent(workspace.id)}`)} />} />
      <p className="text-sm text-muted-foreground">{filtered.length} of {items.length} dev environments</p>
    </>}
    {error && <div role="alert" className="space-y-2 text-sm text-destructive">
      <p>{workspaceId && item ? `Status refresh failed. Updates are delayed; showing the last reported status. ${error}` : error}</p>
      <Button variant="outline" onClick={checkStatus} disabled={loading}>{workspaceId && item ? "Retry status check" : "Retry"}</Button>
    </div>}
    {actionError && <p role="alert" className="text-sm text-destructive">{actionError}</p>}
    {workspaceId && <p role="status" className="text-sm text-muted-foreground">
      {startingGenerations && (paused ? "Automatic status checks paused. Select Refresh status to check again."
        : delayed && !error ? "Still waiting for the VM to be ready. Startup is taking longer than usual." : "")}
    </p>}
    {loading && !items.length && <p role="status">Loading {workspaceId ? "dev environment" : "dev environments"}…</p>}
    {!loading && !error && !items.length && <p>{workspaceId ? "Dev environment not found." : "No dev environments yet."}
      {!workspaceId && !canManage ? " Ask an organization administrator to create one." : ""}</p>}
    {workspaceId && item && <WorkspaceDetail item={item} organizationId={organizationId} refresh={refresh} canManage={canManage} />}
    {!workspaceId && items.length > 0 && <>
      {!filtered.length && <p className="text-sm text-muted-foreground">Adjust search or Filter &amp; sort to find a dev environment.</p>}
      {filtered.length > 0 && <div className="rounded-lg border">
        <Table aria-label="Dev environments">
          <TableHeader><TableRow><TableHead>Dev environment</TableHead><TableHead>Primary VM</TableHead>
            <TableHead>Temporary VMs</TableHead><TableHead>Storage</TableHead><TableHead>Last activity</TableHead></TableRow></TableHeader>
          <TableBody>{filtered.map(workspace => {
            const primary = primaryVm(workspace);
            const phase = primaryState(workspace);
            const ephemeral = ephemeralSummary(workspace);
            const activity = lastActivity(workspace);
            const phaseStyle = phase.value === "ready" ? "bg-success-surface text-success"
              : phase.value === "attention" ? "bg-warning-surface text-warning"
                : ["starting", "stopping"].includes(phase.value) ? "bg-info-surface text-info"
                  : "bg-muted text-muted-foreground";
            return <TableRow key={workspace.id}>
              <TableCell data-label="Dev environment"><div className="flex items-center gap-3">
                <Terminal aria-hidden="true" className="hidden h-9 w-9 rounded-md border p-2 text-muted-foreground sm:block" />
                <div className="min-w-0"><Link className="break-words font-medium text-primary hover:underline"
                  to={`/workspaces/${encodeURIComponent(workspace.id)}`}>{workspace.name}<ChevronRight aria-hidden="true" className="ml-1 inline h-4 w-4" /></Link>
                  <p className="text-xs text-muted-foreground">{primary?.profile ?? "Size unavailable"}</p></div>
              </div></TableCell>
              <TableCell data-label="Primary VM"><span className={`inline-flex rounded-md border px-2 py-1 text-xs font-medium ${phaseStyle}`}>{phase.label}</span></TableCell>
              <TableCell data-label="Temporary VMs"><span className={ephemeral.state === "attention" ? "text-warning" : ""}>{ephemeral.label}</span></TableCell>
              <TableCell data-label="Storage">{Number.isFinite(workspace.volumeSizeGb) ? `${workspace.volumeSizeGb} GB` : "Unavailable"}</TableCell>
              <TableCell data-label="Last activity">{activity === null ? "Unavailable" : <>
                {activity.source === "created" && "Created "}
                <time dateTime={new Date(activity.at).toISOString()}>{new Date(activity.at).toLocaleString()}</time></>}</TableCell>
            </TableRow>;
          })}</TableBody>
        </Table>
      </div>}
    </>}
  </PageScaffold>;
}
