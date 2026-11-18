import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspacesModule } from "./WorkspacesModule";
import { createDevelopmentWorkspace, listDevelopmentWorkspaces } from "@/api/environmentsApi";
import { deleteWorkspace, getWorkspace, getWorkspaceLaunchUrl, launchEphemeralVm, mutateWorkspaceVm, workspaceOperation, workspaceTask } from "@/api/workspacesApi";

const auth = vi.hoisted(() => ({ selectedOrganization: { id: "org-a" } as { id: string } | null }));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
vi.mock("@/api/environmentsApi", () => ({ createDevelopmentWorkspace: vi.fn(), listDevelopmentWorkspaces: vi.fn() }));
vi.mock("@/api/workspacesApi", () => ({
  deleteWorkspace: vi.fn(), getWorkspace: vi.fn(), getWorkspaceLaunchUrl: vi.fn(), launchEphemeralVm: vi.fn(), mutateWorkspaceVm: vi.fn(), workspaceOperation: vi.fn(), workspaceTask: vi.fn(),
}));

function vm(id = "primary", overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: id === "primary" ? "primary" : "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1, ...overrides,
  };
}
function workspace(overrides: Partial<DevelopmentWorkspace> = {}): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org-a", name: "Coding", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20, desiredState: "stopped", phase: "stopped",
    generation: 3, estimatedMonthlyCompute: 72, estimatedMonthlyStorage: 1, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary",
    vms: [vm(), vm("ephemeral-one"), vm("ephemeral-two", { desiredState: "stopped", phase: "stopped" })], ...overrides,
  };
}
function select(form: HTMLElement) {
  fireEvent.change(within(form).getByLabelText("Registered image ID"), { target: { value: "image" } });
  fireEvent.change(within(form).getByLabelText("Approved plan ID"), { target: { value: "plan" } });
}
async function load() {
  render(<WorkspacesModule />);
  await screen.findByRole("article", { name: "Workspace Coding" });
}

beforeEach(() => {
  auth.selectedOrganization = { id: "org-a" };
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([workspace()]);
  vi.mocked(createDevelopmentWorkspace).mockResolvedValue(workspace());
  vi.mocked(mutateWorkspaceVm).mockResolvedValue(workspace());
  vi.mocked(launchEphemeralVm).mockResolvedValue(workspace());
  vi.mocked(workspaceOperation).mockResolvedValue(workspace());
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it("requires explicit confirmation and removes a deleted workspace from the list", async () => {
  await load();
  const open = () => fireEvent.click(screen.getByRole("button", { name: "Permanently delete workspace…" }));
  open();
  const confirmation = screen.getByRole("region", { name: "Confirm permanent deletion of Coding" });
  expect(confirmation).toHaveTextContent(/primary retained volume and any remaining local VM data will be permanently lost/);
  expect(deleteWorkspace).not.toHaveBeenCalled();
  fireEvent.click(within(confirmation).getByRole("button", { name: "Cancel deletion" }));
  expect(deleteWorkspace).not.toHaveBeenCalled();
  open();
  vi.mocked(deleteWorkspace).mockResolvedValueOnce(workspace({ phase: "deleted", volumeStatus: "deleted" }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm permanent deletion" }));
  expect(await screen.findByText("No development workspaces.")).toBeInTheDocument();
  expect(deleteWorkspace).toHaveBeenCalledExactlyOnceWith("workspace", "org-a");
});

it("shows deletion progress, top-level failure and explicit retry, then retains the detail tombstone", async () => {
  vi.mocked(getWorkspace).mockResolvedValueOnce(workspace());
  render(<MemoryRouter initialEntries={["/workspaces/workspace"]}><Routes>
    <Route path="/workspaces/:workspaceId" element={<WorkspacesModule />} />
  </Routes></MemoryRouter>);
  vi.mocked(deleteWorkspace).mockResolvedValueOnce(workspace({ phase: "deleting", volumeStatus: "deleting" }));
  fireEvent.click(await screen.findByRole("button", { name: "Permanently delete workspace…" }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm permanent deletion" }));
  expect(await screen.findByText(/Permanent deletion is in progress/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Retry permanent deletion…" })).not.toBeInTheDocument();

  const failed = workspace({ phase: "delete_failed", volumeStatus: "delete_failed", errorCode: "volume_delete_failed", errorMessage: "Restore provider access" });
  vi.mocked(getWorkspace).mockResolvedValueOnce(failed);
  fireEvent.click(screen.getByRole("button", { name: "Refresh workspace status" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("volume_delete_failed: Restore provider access");
  fireEvent.click(screen.getByRole("button", { name: "Retry permanent deletion…" }));
  expect(deleteWorkspace).toHaveBeenCalledTimes(1);
  vi.mocked(deleteWorkspace).mockRejectedValueOnce(new Error("Connection interrupted"));
  fireEvent.click(screen.getByRole("button", { name: "Confirm permanent deletion" }));
  expect(await screen.findByText("Connection interrupted")).toBeInTheDocument();

  fireEvent.click(screen.getByRole("button", { name: "Retry permanent deletion…" }));
  vi.mocked(deleteWorkspace).mockResolvedValueOnce(workspace({ phase: "deleting", volumeStatus: "deleting" }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm permanent deletion" }));
  expect(await screen.findByText(/Permanent deletion is in progress/)).toBeInTheDocument();
  vi.mocked(getWorkspace).mockResolvedValueOnce(workspace({ phase: "deleted", volumeStatus: "deleted" }));
  fireEvent.click(screen.getByRole("button", { name: "Refresh workspace status" }));
  expect(await screen.findByText("Workspace permanently deleted.")).toHaveAttribute("role", "status");
  expect(screen.queryByRole("button", { name: /permanently delete|retry permanent deletion/i })).not.toBeInTheDocument();
  expect(screen.getByRole("article", { name: "Workspace Coding" })).toBeInTheDocument();
  expect(deleteWorkspace).toHaveBeenCalledTimes(3);
});

it("keeps retained storage and VM controls visible during ordinary compute deletion", async () => {
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValueOnce([workspace({
    phase: "deleting", volumeStatus: "retained", vms: [vm("primary", { phase: "deleting", desiredState: "stopped" })],
  })]);
  await load();
  expect(screen.getByText(/Primary retained volume: 20 GB · retained/)).toBeInTheDocument();
  expect(screen.getByRole("article", { name: "VM primary" })).toHaveTextContent("stopped / deleting");
  expect(screen.queryByText(/Permanent deletion is in progress/)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Retry permanent deletion…" })).not.toBeInTheDocument();
  expect(deleteWorkspace).not.toHaveBeenCalled();
});

it("loads without starting paid compute; creates stopped workspace and launches only after explicit data/push acknowledgement", async () => {
  await load();
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  const create = screen.getByRole("form", { name: "Create workspace" });
  fireEvent.change(within(create).getByLabelText("Name"), { target: { value: "New workspace" } });
  select(create);
  fireEvent.submit(create);
  await waitFor(() => expect(createDevelopmentWorkspace).toHaveBeenCalledWith(expect.objectContaining({
    organizationId: "org-a", name: "New workspace", imageId: "image", planId: "plan", volumeSizeGb: 20,
  })));
  const launch = screen.getByRole("form", { name: "Launch ephemeral VM in Coding" });
  select(launch);
  fireEvent.change(within(launch).getByLabelText("Repository URL"), { target: { value: "https://git.example/repo.git" } });
  fireEvent.change(within(launch).getByLabelText("Base ref"), { target: { value: "refs/heads/main" } });
  fireEvent.change(within(launch).getByLabelText("Resolved base commit SHA"), { target: { value: "a".repeat(40) } });
  fireEvent.submit(launch);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  expect(within(launch).getByRole("button", { name: "Launch ephemeral VM" })).toBeDisabled();
  fireEvent.click(within(launch).getByLabelText(/I authorize task-scoped pushes/));
  fireEvent.click(within(launch).getByLabelText(/I acknowledge ignored\/untracked/));
  fireEvent.submit(launch);
  await waitFor(() => expect(launchEphemeralVm).toHaveBeenCalledWith("workspace", "org-a", expect.objectContaining({
    repository: "https://git.example/repo.git", baseRef: "refs/heads/main", baseCommit: "a".repeat(40),
    pushAuthorized: true, acknowledgeIgnoredDataLoss: true, idempotencyKey: expect.any(String),
  })));
});

it("isolates VM controls, generation payloads, task leases and next-start resize", async () => {
  await load();
  const first = screen.getByRole("article", { name: "VM ephemeral-one" });
  const second = screen.getByRole("article", { name: "VM ephemeral-two" });
  let release!: (value: DevelopmentWorkspace) => void;
  vi.mocked(mutateWorkspaceVm).mockImplementationOnce(() => new Promise(resolve => { release = resolve; }));
  fireEvent.click(within(first).getByRole("button", { name: "Stop compute" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", "stop", { generation: 3 }));
  expect(within(second).getByRole("button", { name: "Start compute" })).toBeEnabled();
  fireEvent.click(within(second).getByRole("button", { name: "Start compute" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "ephemeral-two", "org-a", "start", { generation: 3 }));
  await act(async () => release(workspace()));
  const resize = within(first).getByRole("form", { name: "Resize VM ephemeral-one" });
  fireEvent.change(within(resize).getByLabelText("Profile"), { target: { value: "Heavy" } });
  fireEvent.submit(resize);
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", "resize",
    { generation: 3, imageId: "image", profile: "Heavy", planId: "plan" }));
  vi.mocked(workspaceTask).mockResolvedValue({ vmId: "ephemeral-one", taskId: "build", generation: 3, status: "active", expiresAt: "2099-01-01T00:00:00Z" });
  fireEvent.change(within(first).getByLabelText("Task ID"), { target: { value: "build" } });
  fireEvent.submit(within(first).getByRole("form", { name: "Task lease for VM ephemeral-one" }));
  await waitFor(() => expect(workspaceTask).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", "build", 3, false, false));
  await waitFor(() => expect(within(first).getByRole("button", { name: "Complete task" })).toBeEnabled());
  fireEvent.click(within(first).getByRole("button", { name: "Complete task" }));
  await waitFor(() => expect(workspaceTask).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", "build", 3, true, false));
});

it("displays failed Git-save guards; requires generation-bound irreversible discard confirmation", async () => {
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([workspace({ vms: [vm("ephemeral-one", {
    phase: "awaiting_git_save", errorCode: "remote_unavailable", errorMessage: "Restore repository access before retrying stop",
  })] })]);
  await load();
  expect(screen.getByText(/Awaiting Git save\./)).toBeInTheDocument();
  expect(screen.getByText(/remote_unavailable: Restore repository access/)).toBeInTheDocument();
  expect(screen.queryByRole("link", { name: /^Open / })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Discard local data…" }));
  const form = screen.getByRole("form", { name: "Discard VM ephemeral-one generation 3" });
  fireEvent.submit(form);
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  fireEvent.change(within(form).getByLabelText("Type discard ephemeral-one 3"), { target: { value: "discard ephemeral-one 2" } });
  fireEvent.click(within(form).getByLabelText(/I acknowledge irreversible/));
  expect(within(form).getByRole("button", { name: "Confirm irreversible discard" })).toBeDisabled();
  fireEvent.change(within(form).getByLabelText("Type discard ephemeral-one 3"), { target: { value: "discard ephemeral-one 3" } });
  fireEvent.submit(form);
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", "discard",
    { generation: 3, discardLocalData: true, acknowledgeIgnoredDataLoss: true }));
});

it("enumerates stop-all and only renders authorized generation-specific ready gateway links", async () => {
  await load();
  expect(screen.queryByRole("form", { name: "Open VM ephemeral-two" })).not.toBeInTheDocument();
  expect(screen.queryByRole("link", { name: /^Open / })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Stop all VMs" }));
  const confirmation = screen.getByRole("region", { name: "Confirm stop all" });
  expect(within(confirmation).getAllByRole("listitem")).toHaveLength(3);
  expect(workspaceOperation).not.toHaveBeenCalled();
  fireEvent.click(within(confirmation).getByRole("button", { name: "Confirm stop all" }));
  await waitFor(() => expect(workspaceOperation).toHaveBeenCalledWith("workspace", "org-a", "stop-all"));
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url: "https://vm-ephemeral-one-g3.gateway.example/__minicloud/launch?code=synthetic", expiresAt: "2099-01-01T00:00:00Z" });
  fireEvent.submit(screen.getByRole("form", { name: "Open VM ephemeral-one" }));
  await waitFor(() => expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "ephemeral-one", "org-a", 3, "t3"));
  expect(await screen.findByRole("link", { name: "Open t3 on VM ephemeral-one generation 3" })).toHaveAttribute("href", expect.stringContaining("vm-ephemeral-one-g3"));
});

it("ignores stale organization results and hides the previous tenant while the new tenant loads", async () => {
  let resolveFirst!: (items: DevelopmentWorkspace[]) => void;
  vi.mocked(listDevelopmentWorkspaces).mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }));
  const view = render(<WorkspacesModule />);
  auth.selectedOrganization = { id: "org-b" };
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValueOnce([workspace({ id: "other", name: "Other organization", organizationId: "org-b", vms: [] })]);
  view.rerender(<WorkspacesModule />);
  await screen.findByRole("article", { name: "Workspace Other organization" });
  await act(async () => resolveFirst([workspace()]));
  expect(screen.queryByRole("article", { name: "Workspace Coding" })).not.toBeInTheDocument();
  expect(listDevelopmentWorkspaces).toHaveBeenLastCalledWith("org-b", expect.any(AbortSignal));
  auth.selectedOrganization = null;
  view.rerender(<WorkspacesModule />);
  expect(screen.queryByRole("article", { name: "Workspace Other organization" })).not.toBeInTheDocument();
  expect(screen.getByText("Select an organization.")).toBeInTheDocument();
});

it("loads a stable sleeping workspace detail with only a read; gateway rejection never produces a link", async () => {
  vi.mocked(getWorkspace).mockResolvedValue(workspace({ vms: [vm("primary", { phase: "stopped", desiredState: "stopped" })] }));
  render(<MemoryRouter initialEntries={["/workspaces/workspace"]}><Routes>
    <Route path="/workspaces/:workspaceId" element={<WorkspacesModule />} />
  </Routes></MemoryRouter>);
  await screen.findByRole("article", { name: "Workspace Coding" });
  expect(getWorkspace).toHaveBeenCalledWith("workspace", "org-a", expect.any(AbortSignal));
  expect(listDevelopmentWorkspaces).not.toHaveBeenCalled();
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(screen.queryByRole("form", { name: "Create workspace" })).not.toBeInTheDocument();
  expect(screen.queryByRole("form", { name: "Open VM primary" })).not.toBeInTheDocument();
  vi.mocked(getWorkspace).mockResolvedValue(workspace({ vms: [vm()] }));
  fireEvent.click(screen.getByRole("button", { name: "Refresh workspace status" }));
  await screen.findByRole("form", { name: "Open VM primary" });
  vi.mocked(getWorkspaceLaunchUrl).mockRejectedValue(new Error("Gateway disabled: configure a supported T3 adapter"));
  fireEvent.submit(screen.getByRole("form", { name: "Open VM primary" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("Gateway disabled");
  expect(screen.queryByRole("link", { name: /^Open / })).not.toBeInTheDocument();
});

it("keeps the same create idempotency key after an ambiguous failure", async () => {
  await load();
  vi.mocked(createDevelopmentWorkspace).mockRejectedValueOnce(new Error("Connection interrupted"));
  const form = screen.getByRole("form", { name: "Create workspace" });
  fireEvent.change(within(form).getByLabelText("Name"), { target: { value: "Coding" } });
  select(form);
  fireEvent.submit(form);
  await screen.findByText("Connection interrupted");
  fireEvent.submit(form);
  await waitFor(() => expect(createDevelopmentWorkspace).toHaveBeenCalledTimes(2));
  expect(vi.mocked(createDevelopmentWorkspace).mock.calls[0][0].idempotencyKey)
    .toBe(vi.mocked(createDevelopmentWorkspace).mock.calls[1][0].idempotencyKey);
});

it("invalidates discard confirmation and gateway links when a VM generation changes", async () => {
  await load();
  const first = screen.getByRole("article", { name: "VM ephemeral-one" });
  fireEvent.click(within(first).getByRole("button", { name: "Discard local data…" }));
  const discard = within(first).getByRole("form", { name: "Discard VM ephemeral-one generation 3" });
  fireEvent.change(within(discard).getByLabelText("Type discard ephemeral-one 3"), { target: { value: "discard ephemeral-one 3" } });
  fireEvent.click(within(discard).getByLabelText(/I acknowledge irreversible/));
  vi.mocked(listDevelopmentWorkspaces).mockResolvedValue([workspace({ vms: [vm("ephemeral-one", { generation: 4, phase: "bootstrapping" })] })]);
  fireEvent.click(screen.getByRole("button", { name: "Refresh workspace status" }));
  await screen.findByText(/Generation 4 ·/);
  expect(screen.queryByRole("form", { name: /Discard VM/ })).not.toBeInTheDocument();
  expect(screen.queryByRole("form", { name: "Open VM ephemeral-one" })).not.toBeInTheDocument();
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
});
