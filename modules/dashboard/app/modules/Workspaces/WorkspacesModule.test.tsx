import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspacesModule } from "./WorkspacesModule";
import { listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { getWorkspace } from "@/api/workspacesApi";
import { ephemeralSummary, lastActivity, primaryVm, workspacePhase } from "./workspaceCollection";

const auth = vi.hoisted(() => ({ selectedOrganization: { id: "org-a", role: "owner" } as { id: string; role: string } | null }));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
vi.mock("@/api/environmentsApi", () => ({ listDevelopmentWorkspaces: vi.fn() }));
vi.mock("@/api/workspacesApi", () => ({ getWorkspace: vi.fn() }));
vi.mock("./CreateWorkspaceDialog", () => ({
  CreateWorkspaceDialog: ({ canManage }: { canManage: boolean }) => canManage ? <button>Create workspace</button> : null,
}));
vi.mock("./WorkspaceDetail", () => ({
  WorkspaceDetail: ({ item }: { item: DevelopmentWorkspace }) => <article aria-label={`Workspace ${item.name}`}><Link to="/workspaces">All workspaces</Link></article>,
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
afterEach(() => { cleanup(); vi.resetAllMocks(); });

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
  fireEvent.change(screen.getByRole("textbox", { name: "Search workspaces" }), { target: { value: "Coding" } });
  expect(screen.queryByRole("link", { name: "Other" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("link", { name: "Coding" }));
  await screen.findByRole("article", { name: "Workspace Coding" });
  expect(getWorkspace).toHaveBeenCalledWith("workspace", "org-a", expect.any(AbortSignal));
  fireEvent.click(screen.getByRole("link", { name: "All workspaces" }));
  await screen.findByRole("link", { name: "Coding" });
  expect(screen.getByRole("textbox", { name: "Search workspaces" })).toHaveValue("Coding");
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
  const rows = within(screen.getByRole("table", { name: "Workspaces" })).getAllByRole("row");
  expect(rows[1]).toHaveTextContent("Recent");
  expect(rows[2]).toHaveTextContent("Coding");
});

it("distinguishes empty organizations, no matches and refresh failure while retaining rows", async () => {
  load();
  await screen.findByRole("link", { name: "Coding" });
  fireEvent.change(screen.getByRole("textbox", { name: "Search workspaces" }), { target: { value: "missing" } });
  expect(screen.getByText("No matching workspaces.")).toBeInTheDocument();
  fireEvent.change(screen.getByRole("textbox", { name: "Search workspaces" }), { target: { value: "" } });
  vi.mocked(listDevelopmentWorkspaces).mockRejectedValueOnce(new Error("Refresh unavailable"));
  fireEvent.click(screen.getByRole("button", { name: "Refresh workspaces" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Refresh unavailable");
  expect(screen.getByRole("link", { name: "Coding" })).toBeInTheDocument();
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValueOnce([]);
  fireEvent.click(screen.getByRole("button", { name: "Retry" }));
  await screen.findByText("No workspaces yet.");
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
  expect(screen.queryByText("Workspace not found.")).not.toBeInTheDocument();
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
