import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspacesModule } from "./WorkspacesModule";
import { listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { getWorkspace } from "@/api/workspacesApi";
import { ephemeralSummary, lastActivity, permanentDeletion, primaryState, primaryVm, workspacePhase } from "./workspaceCollection";

const auth = vi.hoisted(() => ({ selectedOrganization: { id: "org-a", role: "owner" } as { id: string; role: string } | null }));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
vi.mock("@/api/environmentsApi", () => ({ listDevelopmentWorkspaces: vi.fn() }));
vi.mock("@/api/workspacesApi", () => ({ getWorkspace: vi.fn() }));
vi.mock("./CreateWorkspaceDialog", () => ({
  CreateWorkspaceDialog: ({ canManage, onCreated }: { canManage: boolean; onCreated: (created: DevelopmentWorkspace) => void }) =>
    canManage ? <button onClick={() => onCreated({ id: "created/one", name: "Created" } as DevelopmentWorkspace)}>Create workspace</button> : null,
}));
vi.mock("./WorkspaceDetail", () => ({
  WorkspaceDetail: ({ item, refresh }: { item: DevelopmentWorkspace; refresh: (observed?: DevelopmentWorkspace) => Promise<void> }) =>
    <article aria-label={`Workspace ${item.name}`}>
      {item.vms.map(vm => <p key={vm.id}>{vm.id}: {vm.phase}</p>)}
      <p>{item.estimatedMonthlyStorage} {item.currency} storage</p>
      <Link to="/workspaces">All workspaces</Link>
      <button onClick={() => void refresh({ ...item, revision: item.revision + 1,
        vms: item.vms.map(machine => ({ ...machine, desiredState: "running", phase: "provisioning" })) })}>Observe accepted start</button>
    </article>,
}));

function vm(id = "primary", overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: id === "primary" ? "primary" : "ephemeral", generation: 3, desiredState: "stopped", phase: "stopped",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: false, revision: 1, ...overrides,
  };
}
function workspace(overrides: Partial<DevelopmentWorkspace> = {}): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org-a", name: "Coding", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20, desiredState: "stopped", phase: "stopped",
    generation: 3, estimatedMonthlyCompute: 72, estimatedMonthlyStorage: 1, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary",
    vms: [vm("sibling", { phase: "awaiting_git_save", computeReserved: true }), vm()], ...overrides,
  };
}
function load() {
  return render(<MemoryRouter><Routes><Route path="/workspaces" element={<WorkspacesModule />} />
    <Route path="/workspaces/:workspaceId" element={<WorkspacesModule />} /><Route path="/" element={<WorkspacesModule />} /></Routes></MemoryRouter>);
}

beforeEach(() => {
  sessionStorage.clear();
  auth.selectedOrganization = { id: "org-a", role: "owner" };
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([workspace()]);
  vi.mocked(getWorkspace).mockResolvedValue(workspace());
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.resetAllMocks(); });

function loadDetail() {
  return render(<MemoryRouter initialEntries={["/workspaces/workspace"]}><Routes>
    <Route path="/workspaces/:workspaceId" element={<WorkspacesModule />} />
    <Route path="/workspaces" element={<WorkspacesModule />} />
  </Routes></MemoryRouter>);
}

it("opens the created environment's detail page and follows its queued startup without listing again", async () => {
  vi.useFakeTimers();
  const starting = workspace({ id: "created/one", name: "Created", vms: [vm("primary", { desiredState: "running", phase: "provisioning", generation: 1 })] });
  vi.mocked(getWorkspace).mockResolvedValueOnce(starting)
    .mockResolvedValueOnce(workspace({ ...starting, revision: 2, vms: [vm("primary", { desiredState: "running", phase: "ready", generation: 1 })] }));
  load();
  await act(async () => {});
  expect(listDevelopmentWorkspaces).toHaveBeenCalledTimes(1);
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Create workspace" })));
  expect(getWorkspace).toHaveBeenCalledWith("created/one", "org-a", expect.any(AbortSignal));
  expect(screen.getByRole("article", { name: "Workspace Created" })).toHaveTextContent("primary: provisioning");
  expect(screen.queryByRole("button", { name: "Create workspace" })).not.toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(3_000));
  expect(screen.getByText("primary: ready")).toBeInTheDocument();
  expect(listDevelopmentWorkspaces).toHaveBeenCalledTimes(1);
});

it("keeps an accepted start and costs visible after a failed read; checks status until ready", async () => {
  vi.useFakeTimers();
  vi.mocked(getWorkspace).mockResolvedValueOnce(workspace({ vms: [vm()] }))
    .mockRejectedValueOnce(new Error("Network unavailable"))
    .mockResolvedValueOnce(workspace({ revision: 3, vms: [vm("primary", { phase: "ready", desiredState: "running" })] }));
  loadDetail();
  await act(async () => {});
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Observe accepted start" })));
  expect(screen.getByRole("alert")).toHaveTextContent(/Status refresh failed.*showing the last reported status/);
  expect(screen.getByText("primary: provisioning")).toBeInTheDocument();
  expect(screen.getByText("1 EUR storage")).toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(3_000));
  expect(screen.getByText("primary: ready")).toBeInTheDocument();
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(60_000));
  expect(getWorkspace).toHaveBeenCalledTimes(3);
});

it("shows slow startup for a temporary VM, bounds automatic reads, and lets a manual read recover", async () => {
  vi.useFakeTimers();
  vi.mocked(getWorkspace).mockResolvedValue(workspace({
    vms: [vm(), vm("temporary", { phase: "provisioning", desiredState: "running" })],
  }));
  loadDetail();
  await act(async () => {});
  await act(async () => vi.advanceTimersByTimeAsync(46_000));
  expect(screen.getByText(/Startup is taking longer/)).toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(135_000));
  expect(screen.getByText(/Automatic status checks paused/)).toBeInTheDocument();
  const reads = vi.mocked(getWorkspace).mock.calls.length;
  await act(async () => vi.advanceTimersByTimeAsync(180_000));
  expect(getWorkspace).toHaveBeenCalledTimes(reads);
  vi.mocked(getWorkspace).mockResolvedValueOnce(workspace({ vms: [vm(), vm("temporary", { phase: "blocked", desiredState: "running" })] }));
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Refresh status" })));
  expect(screen.getByText("temporary: blocked")).toBeInTheDocument();
  expect(screen.queryByText(/Automatic status checks paused/)).not.toBeInTheDocument();
  await act(async () => vi.advanceTimersByTimeAsync(180_000));
  expect(getWorkspace).toHaveBeenCalledTimes(reads + 1);
});

it("times out one status read at a time and aborts a pending read when leaving detail", async () => {
  vi.useFakeTimers();
  vi.mocked(getWorkspace).mockResolvedValueOnce(workspace({ vms: [vm("primary", { phase: "provisioning", desiredState: "running" })] }))
    .mockImplementation((_id, _org, signal) => new Promise((_resolve, reject) => {
      signal?.addEventListener("abort", () => reject(signal.reason), { once: true });
    }));
  loadDetail();
  await act(async () => {});
  await act(async () => vi.advanceTimersByTimeAsync(17_000));
  expect(getWorkspace).toHaveBeenCalledTimes(2);
  const timedOut = vi.mocked(getWorkspace).mock.calls[1][2];
  await act(async () => vi.advanceTimersByTimeAsync(1_000));
  expect(timedOut?.aborted).toBe(true);
  expect(screen.getByRole("alert")).toHaveTextContent(/status request timed out/i);
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Retry status check" })));
  const retry = vi.mocked(getWorkspace).mock.calls[2][2];
  expect(retry?.aborted).toBe(false);
  await act(async () => fireEvent.click(screen.getByRole("link", { name: "All workspaces" })));
  expect(retry?.aborted).toBe(true);
  await act(async () => vi.advanceTimersByTimeAsync(180_000));
  expect(getWorkspace).toHaveBeenCalledTimes(3);
});

it("shows a stopped primary independently of a blocked sibling and performs reads only", async () => {
  load();
  const row = (await screen.findByRole("link", { name: "Coding" })).closest("tr")!;
  expect(within(row).getByText("Stopped")).toBeInTheDocument();
  expect(within(row).getByText("1 needs attention")).toBeInTheDocument();
  expect(within(row).getByText("Standard")).toBeInTheDocument();
  expect(within(row).getByText("20 GB")).toBeInTheDocument();
  expect(within(row).getByText("Unavailable")).toBeInTheDocument();
  expect(screen.queryByRole("form")).not.toBeInTheDocument();
  expect(listDevelopmentWorkspaces).toHaveBeenCalledTimes(1);
  expect(getWorkspace).not.toHaveBeenCalled();
});

it("preserves search while following stable detail links and returning", async () => {
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([workspace(), workspace({ id: "other", name: "Other" })]);
  load();
  await screen.findByRole("link", { name: "Coding" });
  fireEvent.change(screen.getByRole("textbox", { name: "Search dev environments" }), { target: { value: "Coding" } });
  expect(screen.queryByRole("link", { name: "Other" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("link", { name: "Coding" }));
  await screen.findByRole("article", { name: "Workspace Coding" });
  expect(getWorkspace).toHaveBeenCalledWith("workspace", "org-a", expect.any(AbortSignal));
  fireEvent.click(screen.getByRole("link", { name: "All workspaces" }));
  await screen.findByRole("link", { name: "Coding" });
  expect(screen.getByRole("textbox", { name: "Search dev environments" })).toHaveValue("Coding");
  expect(screen.queryByRole("link", { name: "Other" })).not.toBeInTheDocument();
});

it("filters primary and ephemeral independently and sorts missing activity last", async () => {
  sessionStorage.setItem("org-a:workspaces", JSON.stringify({
    filters: { primary: "stopped", ephemeral: "attention" }, sort: "activity", direction: "desc",
  }));
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([
    workspace(), workspace({ id: "recent", name: "Recent", vms: [vm(), vm("blocked", { phase: "blocked", lastActivityAt: "2026-11-16T12:00:00Z" })] }),
    workspace({ id: "running", name: "Running", vms: [vm("primary", { phase: "ready" })] }),
  ]);
  load();
  await screen.findByRole("link", { name: "Recent" });
  expect(screen.queryByRole("link", { name: "Running" })).not.toBeInTheDocument();
  const rows = within(screen.getByRole("table", { name: "Dev environments" })).getAllByRole("row");
  expect(rows[1]).toHaveTextContent("Recent");
  expect(rows[2]).toHaveTextContent("Coding");
});

it("distinguishes empty organizations, no matches and refresh failure while retaining rows", async () => {
  load();
  await screen.findByRole("link", { name: "Coding" });
  fireEvent.change(screen.getByRole("textbox", { name: "Search dev environments" }), { target: { value: "missing" } });
  expect(screen.getByText("No matching dev environments.")).toBeInTheDocument();
  fireEvent.change(screen.getByRole("textbox", { name: "Search dev environments" }), { target: { value: "" } });
  vi.mocked(listDevelopmentWorkspaces).mockRejectedValueOnce(new Error("Refresh unavailable"));
  fireEvent.click(screen.getByRole("button", { name: "Refresh dev environments" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Refresh unavailable");
  expect(screen.getByRole("link", { name: "Coding" })).toBeInTheDocument();
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValueOnce([]);
  fireEvent.click(screen.getByRole("button", { name: "Retry" }));
  await screen.findByText("No dev environments yet.");
});

it("hides creation for a read-only role", async () => {
  auth.selectedOrganization = { id: "org-a", role: "viewer" };
  load();
  await screen.findByRole("link", { name: "Coding" });
  expect(screen.queryByRole("button", { name: "Create workspace" })).not.toBeInTheDocument();
});

it("clears previous organization data and ignores a late response", async () => {
  let resolveFirst!: (items: DevelopmentWorkspace[]) => void;
  vi.mocked(listDevelopmentWorkspaces).mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }));
  const view = load();
  await waitFor(() => expect(listDevelopmentWorkspaces).toHaveBeenCalledTimes(1));
  auth.selectedOrganization = { id: "org-b", role: "viewer" };
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValueOnce([workspace({ id: "other", name: "Other organization", organizationId: "org-b" })]);
  view.rerender(<MemoryRouter><WorkspacesModule /></MemoryRouter>);
  await screen.findByRole("link", { name: "Other organization" });
  await act(async () => resolveFirst([workspace()]));
  expect(screen.queryByRole("link", { name: "Coding" })).not.toBeInTheDocument();
  expect(listDevelopmentWorkspaces).toHaveBeenLastCalledWith("org-b", expect.any(AbortSignal));
  auth.selectedOrganization = null;
  view.rerender(<MemoryRouter><WorkspacesModule /></MemoryRouter>);
  expect(screen.queryByRole("link", { name: "Other organization" })).not.toBeInTheDocument();
  expect(screen.getByText("Select an organization.")).toBeInTheDocument();
});

it("displays detail read errors without treating them as empty success", async () => {
  vi.mocked(getWorkspace).mockRejectedValue(new Error("Workspace access forbidden"));
  render(<MemoryRouter initialEntries={["/workspaces/workspace"]}><Routes>
    <Route path="/workspaces/:workspaceId" element={<WorkspacesModule />} />
  </Routes></MemoryRouter>);
  await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("Workspace access forbidden"));
  expect(screen.queryByText("Dev environment not found.")).not.toBeInTheDocument();
  expect(listDevelopmentWorkspaces).not.toHaveBeenCalled();
});

it("uses observed identity, phases and activity with defined ephemeral precedence", () => {
  expect(primaryVm(workspace())?.id).toBe("primary");
  expect(primaryVm(workspace({ vms: [vm("sibling")] }))).toBeUndefined();
  expect(workspacePhase("future_state").label).toBe("Unavailable");
  expect(ephemeralSummary(workspace({ vms: [vm("a", { phase: "ready" }), vm("b", { phase: "blocked" })] })).state).toBe("attention");
  expect(ephemeralSummary(workspace({ vms: [vm("a", { phase: "ready" }), vm("b", { phase: "provisioning" })] })).state).toBe("running");
  expect(ephemeralSummary(workspace({ vms: [vm("a", { phase: "provisioning" })] })).state).toBe("transitioning");
  expect(ephemeralSummary(workspace({ vms: [vm("a", { phase: "new_state" })] })).state).toBe("unavailable");
  expect(ephemeralSummary(workspace({ vms: [vm("a", { phase: "ready" }), vm("b", { phase: "new_state" })] })).label).toContain("1 unavailable");
  expect(ephemeralSummary(workspace({ vms: [vm("a")] })).label).toBe("1 stopped");
  expect(ephemeralSummary(workspace({ vms: [] })).label).toBe("None");
  expect(lastActivity(workspace({ lastActivityAt: "invalid" }))).toBeNull();
});

it("labels permanent deletion from volume status, not from ordinary compute deletion", () => {
  const computeDeletion = workspace({ phase: "deleting", vms: [vm("primary", { phase: "deleting" })] });
  expect(permanentDeletion(computeDeletion)).toBeNull();
  expect(primaryState(computeDeletion).label).toBe("Stopping");
  const deleting = workspace({ volumeStatus: "deleting", phase: "deleting", vms: [vm("primary", { phase: "deleting" })] });
  expect(primaryState(deleting)).toEqual({ value: "stopping", label: "Deleting environment" });
  const failed = workspace({ volumeStatus: "deleting", phase: "delete_failed", vms: [vm("primary", { phase: "delete_failed" })] });
  expect(primaryState(failed)).toEqual({ value: "attention", label: "Deletion failed" });
  expect(permanentDeletion(workspace({ volumeStatus: "deleted" }))).toBe("deleted");
});
