import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ChevronRight, Terminal } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { PageScaffold } from "@/core/components/PageScaffold";
import { CollectionToolbar } from "@/core/components/CollectionToolbar";
import { compareRecords, useCollectionQuery } from "@/core/components/collectionQuery";
import { useAuth } from "@/core/auth/AuthProvider";
import { listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { getWorkspace } from "@/api/workspacesApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { CreateWorkspaceDialog } from "./CreateWorkspaceDialog";
import { WorkspaceDetail } from "./WorkspaceDetail";
import { ephemeralSummary, lastActivity, primaryVm, workspacePhase } from "./workspaceCollection";

export function WorkspacesModule() {
  const { selectedOrganization } = useAuth();
  const { workspaceId } = useParams();
  if (!selectedOrganization) return <PageScaffold title="Workspaces"><p>Select an organization.</p></PageScaffold>;
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
  const [created, setCreated] = useState<DevelopmentWorkspace | null>(null);
  const [query, setQuery] = useCollectionQuery(`${organizationId}:workspaces`, "name", "asc");
  const active = useRef(true);
  const revision = useRef(0);
  const refresh = useCallback(async (signal?: AbortSignal) => {
    const request = ++revision.current;
    setLoading(true);
    try {
      const result = workspaceId ? [await getWorkspace(workspaceId, organizationId, signal)]
        : await listDevelopmentWorkspaces(organizationId, signal);
      if (active.current && request === revision.current) { setItems(result); setError(null); }
    } catch (reason) {
      if (active.current && !signal?.aborted && request === revision.current)
        setError(reason instanceof Error ? reason.message : "Unable to load workspaces.");
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

  const item = items[0];
  const filtered = items.filter(workspace => workspace.name.toLocaleLowerCase().includes(query.search.trim().toLocaleLowerCase())
    && (!query.filters.primary || workspacePhase(primaryVm(workspace)?.phase).value === query.filters.primary)
    && (!query.filters.ephemeral || ephemeralSummary(workspace).state === query.filters.ephemeral))
    .sort((a, b) => compareRecords(query.sort === "activity" ? lastActivity(a) : a.name,
      query.sort === "activity" ? lastActivity(b) : b.name, query.direction, a.id, b.id));
  return <PageScaffold title={workspaceId ? item?.name ?? "Workspace" : "Workspaces"}
    breadcrumbs={workspaceId ? [{ label: "Workspaces", to: "/workspaces" }, { label: item?.name ?? "Workspace" }] : undefined}
    actions={workspaceId ? <Button variant="outline" disabled={loading} onClick={() => void refresh()}>Refresh workspace status</Button> : undefined}>
    {!workspaceId && <>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0 flex-1"><CollectionToolbar label="workspaces" query={query}
          onChange={next => setQuery({ ...next, view: "table" })} onRefresh={() => refresh()} loading={loading}
          count={filtered.length} filters={[
            { id: "primary", label: "Primary VM state", options: [
              { value: "ready", label: "Ready" }, { value: "stopped", label: "Stopped" },
              { value: "starting", label: "Starting" }, { value: "stopping", label: "Stopping" },
              { value: "attention", label: "Needs attention" }, { value: "unavailable", label: "Unavailable" },
            ] },
            { id: "ephemeral", label: "Ephemeral VMs", options: [
              { value: "attention", label: "Needs attention" }, { value: "running", label: "Running" },
              { value: "transitioning", label: "Starting or stopping" }, { value: "stopped", label: "Stopped" },
              { value: "none", label: "None" }, { value: "unavailable", label: "Unavailable" },
            ] },
          ]} sorts={[{ value: "name", label: "Name" }, { value: "activity", label: "Last activity" }]} /></div>
        <CreateWorkspaceDialog organizationId={organizationId} canManage={canManage} onCreated={workspace => {
          setCreated(workspace); void refresh();
        }} />
      </div>
      <p className="text-sm text-muted-foreground">{filtered.length} of {items.length} workspaces</p>
      {created && <p role="status" className="text-sm">
        Created stopped workspace <Link className="font-medium text-primary underline" to={`/workspaces/${encodeURIComponent(created.id)}`}>{created.name}</Link>. Compute has not been started.
      </p>}
    </>}
    {error && <div role="alert" className="space-y-2 text-sm text-destructive">
      <p>{error}</p><Button variant="outline" onClick={() => void refresh()} disabled={loading}>Retry</Button>
    </div>}
    {loading && !items.length && <p role="status">Loading {workspaceId ? "workspace" : "workspaces"}…</p>}
    {!loading && !error && !items.length && <p>{workspaceId ? "Workspace not found." : "No workspaces yet."}
      {!workspaceId && !canManage ? " Ask an organization administrator to create a workspace." : ""}</p>}
    {workspaceId && item && <WorkspaceDetail item={item} organizationId={organizationId} refresh={refresh} canManage={canManage} />}
    {!workspaceId && items.length > 0 && <>
      {!filtered.length && <p className="text-sm text-muted-foreground">Adjust search or Filter &amp; sort to find a workspace.</p>}
      {filtered.length > 0 && <div className="rounded-lg border">
        <Table aria-label="Workspaces">
          <TableHeader><TableRow><TableHead>Workspace</TableHead><TableHead>Primary VM</TableHead>
            <TableHead>Ephemeral VMs</TableHead><TableHead>Storage</TableHead><TableHead>Last activity</TableHead></TableRow></TableHeader>
          <TableBody>{filtered.map(workspace => {
            const primary = primaryVm(workspace);
            const phase = workspacePhase(primary?.phase);
            const ephemeral = ephemeralSummary(workspace);
            const activity = lastActivity(workspace);
            const phaseStyle = phase.value === "ready" ? "bg-success-surface text-success"
              : phase.value === "attention" ? "bg-warning-surface text-warning"
                : ["starting", "stopping"].includes(phase.value) ? "bg-info-surface text-info"
                  : "bg-muted text-muted-foreground";
            return <TableRow key={workspace.id}>
              <TableCell data-label="Workspace"><div className="flex items-center gap-3">
                <Terminal aria-hidden="true" className="hidden h-9 w-9 rounded-md border p-2 text-muted-foreground sm:block" />
                <div className="min-w-0"><Link className="break-words font-medium text-primary hover:underline"
                  to={`/workspaces/${encodeURIComponent(workspace.id)}`}>{workspace.name}<ChevronRight aria-hidden="true" className="ml-1 inline h-4 w-4" /></Link>
                  <p className="text-xs text-muted-foreground">{primary?.profile ?? "Profile unavailable"}</p></div>
              </div></TableCell>
              <TableCell data-label="Primary VM"><span className={`inline-flex rounded-md border px-2 py-1 text-xs font-medium ${phaseStyle}`}>{phase.label}</span></TableCell>
              <TableCell data-label="Ephemeral VMs"><span className={ephemeral.state === "attention" ? "text-warning" : ""}>{ephemeral.label}</span></TableCell>
              <TableCell data-label="Storage">{Number.isFinite(workspace.volumeSizeGb) ? `${workspace.volumeSizeGb} GB` : "Unavailable"}</TableCell>
              <TableCell data-label="Last activity">{activity === null ? "Unavailable" : <time dateTime={new Date(activity).toISOString()}>{new Date(activity).toLocaleString()}</time>}</TableCell>
            </TableRow>;
          })}</TableBody>
        </Table>
      </div>}
    </>}
  </PageScaffold>;
}
