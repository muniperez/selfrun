import { useEffect } from "react";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { getWorkspaceLaunchUrl, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";
import type { WorkspaceSelection } from "./WorkspaceSelectionFields";
import { WorkspaceVmCard } from "./WorkspaceVmCard";

vi.mock("@/api/workspacesApi", () => ({
  getWorkspaceLaunchUrl: vi.fn(), mutateWorkspaceVm: vi.fn(), workspaceTask: vi.fn(),
}));
vi.mock("./WorkspaceSelectionFields", () => ({
  WorkspaceSelectionFields: ({ value, onChange, disabled, onReadyChange }: {
    value: WorkspaceSelection; onChange: (selection: WorkspaceSelection) => void; disabled?: boolean;
    onReadyChange: (ready: boolean) => void;
  }) => {
    useEffect(() => onReadyChange(true), [onReadyChange]);
    return <label>Profile<select value={value.profile} disabled={disabled}
      onChange={event => onChange({ ...value, profile: event.target.value })}><option>Standard</option><option>Heavy</option></select></label>;
  },
}));

function vm(overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id: "task-vm", kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "provider", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1, ...overrides,
  };
}
const refresh = vi.fn<() => Promise<void>>();
function card(machine = vm(), canManage = true) {
  return <WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={machine} currency="EUR" refresh={refresh} canManage={canManage} />;
}
function openAdvanced() {
  fireEvent.click(screen.getByText("Advanced VM controls"));
}
function openDiscard() {
  fireEvent.click(screen.getByRole("button", { name: "Discard local data…" }));
  return screen.getByRole("dialog", { name: "Discard VM task-vm generation 3" });
}
function confirmDiscard(dialog: HTMLElement, phrase = "discard task-vm 3") {
  fireEvent.change(within(dialog).getByLabelText("Type discard task-vm 3"), { target: { value: phrase } });
  fireEvent.click(within(dialog).getByLabelText(/I acknowledge irreversible/));
}
beforeEach(() => {
  refresh.mockResolvedValue(undefined);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({
    url: "https://gateway.example/__minicloud/launch?code=synthetic", expiresAt: "2099-01-01T00:00:00Z",
  });
  vi.mocked(workspaceTask).mockResolvedValue({
    vmId: "task-vm", taskId: "build", generation: 3, status: "active", expiresAt: "2099-01-01T00:00:00Z",
  });
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.resetAllMocks(); });

it("embeds opt-in Connect only in an explicitly owner-authorized primary card", () => {
  const props = { workspaceId: "workspace", organizationId: "org", currency: "EUR", refresh, canManage: true };
  const view = render(<WorkspaceVmCard {...props} vm={vm({ kind: "primary" })} canManageConnect />);
  expect(screen.getByRole("button", { name: "Manage T3 Connect" })).toBeEnabled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  view.rerender(<WorkspaceVmCard {...props} vm={vm({ kind: "primary" })} canManageConnect={false} />);
  expect(screen.queryByRole("button", { name: "Manage T3 Connect" })).not.toBeInTheDocument();
  view.rerender(<WorkspaceVmCard {...props} vm={vm()} canManageConnect />);
  expect(screen.queryByRole("button", { name: "Manage T3 Connect" })).not.toBeInTheDocument();
});

it("closes pending Connect authorization when primary ownership is lost", async () => {
  const popup = { opener: null, close: vi.fn(), location: { replace: vi.fn() } };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  let resolve!: (result: { url: string; expiresAt: string }) => void;
  vi.mocked(getWorkspaceLaunchUrl).mockImplementationOnce(() => new Promise(done => { resolve = done; }));
  const props = { workspaceId: "workspace", organizationId: "org", currency: "EUR", refresh, canManage: true, vm: vm({ kind: "primary" }) };
  const view = render(<WorkspaceVmCard {...props} canManageConnect />);
  fireEvent.click(screen.getByRole("button", { name: "Manage T3 Connect" }));
  view.rerender(<WorkspaceVmCard {...props} canManageConnect={false} />);
  await act(async () => resolve({ url: "https://connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" }));
  expect(popup.close).toHaveBeenCalled();
  expect(popup.location.replace).not.toHaveBeenCalled();
});

it("viewing a sleeping VM never starts compute or authorizes sessions; start uses current generation", async () => {
  render(card(vm({ phase: "stopped", desiredState: "stopped", computeReserved: false })));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  expect(screen.queryByRole("button", { name: "Connect to T3" })).not.toBeInTheDocument();
  expect(screen.getByText(/not confirmation of provider deletion/)).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Start compute" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "start", { generation: 3 }));
  expect(refresh).toHaveBeenCalledOnce();
});

it("scopes stop retries and shows the Git-save reason, reservation and recovery steps", async () => {
  render(card(vm({ phase: "awaiting_git_save", desiredState: "stopped", errorCode: "remote_unavailable", errorMessage: "Restore repository access." })));
  expect(screen.getByText(/Compute remains reserved/)).toBeInTheDocument();
  expect(screen.getByText(/Reported reason: Restore repository access/)).toBeInTheDocument();
  expect(screen.getByText(/Restore repository access and authentication/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Connect to T3" })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Retry stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});

it("queues an explicit next-generation start and allows stop to cancel that queued start", async () => {
  const machine = vm({ phase: "draining", desiredState: "stopped" });
  const view = render(card(machine));
  fireEvent.click(screen.getByRole("button", { name: "Queue start after stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "start", { generation: 3 }));
  view.rerender(card({ ...machine, startAfterStop: true, revision: 2 }));
  expect(screen.getByRole("button", { name: "Queue start after stop" })).toBeDisabled();
  await waitFor(() => expect(screen.getByRole("button", { name: "Stop compute" })).toBeEnabled());
  fireEvent.click(screen.getByRole("button", { name: "Stop compute" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});

it("isolates pending requests to the selected VM and surfaces mutation errors", async () => {
  let reject!: (reason: Error) => void;
  vi.mocked(mutateWorkspaceVm).mockImplementationOnce(() => new Promise((_, no) => { reject = no; }));
  render(<>{card()}<WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={vm({ id: "sibling" })} currency="EUR" refresh={refresh} canManage /></>);
  const selected = screen.getByRole("article", { name: "VM task-vm" });
  const sibling = screen.getByRole("article", { name: "VM sibling" });
  fireEvent.click(within(selected).getByRole("button", { name: "Stop compute" }));
  expect(within(selected).getByRole("button", { name: "Stop compute" })).toBeDisabled();
  expect(within(sibling).getByRole("button", { name: "Stop compute" })).toBeEnabled();
  await act(async () => reject(new Error("Shutdown was already claimed.")));
  expect(within(selected).getByRole("alert")).toHaveTextContent("Shutdown was already claimed.");
  expect(refresh).not.toHaveBeenCalled();
});

it("supports generation-scoped idle-stop cancellation, next-start resize and task lease transitions", async () => {
  render(card(vm({ shutdownDeadline: "2099-01-01T00:00:00Z" })));
  fireEvent.click(screen.getByRole("button", { name: "Cancel idle stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "cancel-idle-stop", { generation: 3 }));
  openAdvanced();
  await waitFor(() => expect(screen.getByRole("button", { name: "Set size on next start" })).toBeEnabled());
  fireEvent.change(screen.getByLabelText("Profile"), { target: { value: "Heavy" } });
  fireEvent.submit(screen.getByRole("form", { name: "Resize VM task-vm" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "resize", {
    generation: 3, imageId: "image", profile: "Heavy", planId: "plan",
  }));
  await waitFor(() => expect(screen.getByRole("button", { name: "Renew keep-awake lease" })).toBeDisabled());
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  for (const [label, complete, cancelled] of [
    ["Renew keep-awake lease", false, false], ["Complete task", true, false], ["Cancel task", true, true],
  ] as const) {
    await waitFor(() => expect(screen.getByRole("button", { name: label })).toBeEnabled());
    fireEvent.click(screen.getByRole("button", { name: label }));
    await waitFor(() => expect(workspaceTask).toHaveBeenLastCalledWith("workspace", "task-vm", "org", "build", 3, complete, cancelled));
  }
});

it("authorizes T3 only on click and preserves the valid link without an immediate refresh", async () => {
  const view = render(card());
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  expect(await screen.findByRole("link", { name: /Open authorized T3 session/ })).toHaveAttribute("href", expect.stringContaining("https://gateway.example"));
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "task-vm", "org", 3, "t3");
  expect(refresh).not.toHaveBeenCalled();
  view.rerender(card(vm()));
  expect(screen.getByRole("link", { name: /Open authorized/ })).toBeInTheDocument();
  view.rerender(card(vm({ revision: 2 })));
  expect(screen.queryByRole("link", { name: /Open authorized/ })).not.toBeInTheDocument();
});

it("uses the registered preview slug and invalidates authorization when the service changes", async () => {
  render(card());
  expect(screen.queryByLabelText("Registered preview slug")).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Open preview" }));
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  fireEvent.change(screen.getByLabelText("Registered preview slug"), { target: { value: "web" } });
  fireEvent.click(screen.getByRole("button", { name: "Authorize preview" }));
  expect(await screen.findByRole("link", { name: /Open authorized web preview/ })).toBeInTheDocument();
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "task-vm", "org", 3, "web");
  fireEvent.change(screen.getByLabelText("Registered preview slug"), { target: { value: "api" } });
  expect(screen.queryByRole("link", { name: /Open authorized/ })).not.toBeInTheDocument();
});

it.each([
  ["http://gateway.example/launch", "2099-01-01T00:00:00Z", /unsafe launch URL/],
  ["not a url", "2099-01-01T00:00:00Z", /invalid launch URL/],
  ["https://user:password@gateway.example/launch", "2099-01-01T00:00:00Z", /unsafe launch URL/],
  ["https://gateway.example/launch", "2000-01-01T00:00:00Z", /expired/],
  ["https://gateway.example/launch", "invalid date", /invalid expiry/],
])("rejects unsafe or expired gateway authorization: %s %s", async (url, expiresAt, expected) => {
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url, expiresAt });
  render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(expected);
  expect(screen.getByRole("alert")).toHaveTextContent("Compute was not started");
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("provides an actionable fallback for rejected gateway authorization without starting compute", async () => {
  vi.mocked(getWorkspaceLaunchUrl).mockRejectedValue(new Error("Gateway disabled."));
  render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(/Gateway disabled.*Check gateway configuration/);
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it.each([
  { revision: 2 },
  { generation: 4, revision: 2 },
])("rejects delayed authorization after a VM change: %o", async change => {
  let resolve!: (value: { url: string; expiresAt: string }) => void;
  vi.mocked(getWorkspaceLaunchUrl).mockImplementationOnce(() => new Promise(yes => { resolve = yes; }));
  const view = render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  view.rerender(card(vm(change)));
  await act(async () => resolve({ url: "https://gateway.example/old-generation", expiresAt: "2099-01-01T00:00:00Z" }));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("removes an authorized link when its expiry is reached", async () => {
  vi.useFakeTimers();
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({
    url: "https://gateway.example/launch", expiresAt: new Date(Date.now() + 1_000).toISOString(),
  });
  render(card());
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Connect to T3" })));
  expect(screen.getByRole("link", { name: /Open authorized T3 session/ })).toBeInTheDocument();
  await act(async () => vi.advanceTimersByTime(1_001));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("clears session authorization permanently on permission loss", async () => {
  const view = render(card());
  fireEvent.click(screen.getByRole("button", { name: "Connect to T3" }));
  await screen.findByRole("link", { name: /Open authorized T3 session/ });
  view.rerender(card(vm(), false));
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
  view.rerender(card());
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("gates mutations and sessions for read-only access including direct form submission", () => {
  render(card(vm(), false));
  openAdvanced();
  expect(screen.getByRole("button", { name: "Stop compute" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Connect to T3" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Open preview" })).toBeDisabled();
  expect(screen.getByRole("button", { name: "Discard local data…" })).toBeDisabled();
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  fireEvent.submit(screen.getByRole("form", { name: "Resize VM task-vm" }));
  fireEvent.submit(screen.getByRole("form", { name: "Task lease for VM task-vm" }));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(workspaceTask).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
});

it("cannot renew a lease before readiness but can complete the current generation's task", async () => {
  render(card(vm({ phase: "draining", desiredState: "stopped" })));
  openAdvanced();
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "build" } });
  expect(screen.getByRole("button", { name: "Renew keep-awake lease" })).toBeDisabled();
  fireEvent.submit(screen.getByRole("form", { name: "Task lease for VM task-vm" }));
  expect(workspaceTask).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Complete task" }));
  await waitFor(() => expect(workspaceTask).toHaveBeenCalledWith("workspace", "task-vm", "org", "build", 3, true, false));
});

it("requires the exact VM/generation phrase and acknowledgement before irreversible discard", async () => {
  render(card());
  const dialog = openDiscard();
  confirmDiscard(dialog, "discard task-vm 2");
  expect(within(dialog).getByRole("button", { name: "Confirm irreversible discard" })).toBeDisabled();
  fireEvent.submit(within(dialog).getByRole("form"));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  fireEvent.change(within(dialog).getByLabelText("Type discard task-vm 3"), { target: { value: "discard task-vm 3" } });
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm irreversible discard" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "discard", {
    generation: 3, discardLocalData: true, acknowledgeIgnoredDataLoss: true,
  }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
});

it("cancel discard performs no write and reopening clears all confirmation input", () => {
  render(card());
  const dialog = openDiscard();
  confirmDiscard(dialog);
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel discard" }));
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  const reopened = openDiscard();
  expect(within(reopened).getByLabelText("Type discard task-vm 3")).toHaveValue("");
  expect(within(reopened).getByLabelText(/I acknowledge irreversible/)).not.toBeChecked();
});

it("closes discard and resets lease inputs when the generation changes", async () => {
  const view = render(card());
  openAdvanced();
  fireEvent.change(screen.getByLabelText("Task ID"), { target: { value: "old-task" } });
  confirmDiscard(openDiscard());
  view.rerender(card(vm({ generation: 4, revision: 2 })));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  openAdvanced();
  expect(screen.getByLabelText("Task ID")).toHaveValue("");
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(workspaceTask).not.toHaveBeenCalled();
});

it("combines retained-storage observations with primary metrics and collapses secondary controls", () => {
  render(<WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={vm({ kind: "primary" })}
    currency="EUR" refresh={refresh} canManage variant="primary"
    retainedStorage={{ sizeGb: 25, status: "retained", estimatedMonthlyStorage: 1.52 }} />);
  expect(screen.getAllByRole("heading", { name: "Primary VM" })).toHaveLength(1);
  const article = screen.getByRole("article", { name: "VM task-vm" });
  const metrics = article.querySelector(":scope > dl")!;
  expect(metrics).toHaveTextContent("Retained volume25 GB");
  expect(metrics).toHaveTextContent("Estimated storage1.52 EUR / month");
  expect(metrics).toHaveTextContent("Reserved · charges may continue");
  const advanced = screen.getByText("Advanced VM controls").closest("details")!;
  expect(advanced).not.toHaveAttribute("open");
  expect(within(advanced).getByText(/Generation 3/)).toBeInTheDocument();
  expect(within(advanced).getByRole("button", { name: "Stop compute" })).toBeInTheDocument();
});

it("embeds compact Git recovery first without repeating VM headers or metrics", async () => {
  render(<WorkspaceVmCard workspaceId="workspace" organizationId="org" vm={vm({
    phase: "awaiting_git_save", desiredState: "stopped", errorMessage: "Remote unavailable.",
    baseRef: "refs/heads/main", baseCommit: "base-sha", finalHead: "final-sha",
    verifiedRemoteRef: "refs/heads/work", verifiedRemoteCommit: "verified-sha",
  })} currency="EUR" refresh={refresh} canManage variant="embedded" />);
  const article = screen.getByRole("article", { name: "VM task-vm" });
  expect(within(article).queryByRole("heading", { name: "Ephemeral VM" })).not.toBeInTheDocument();
  expect(article.querySelector(":scope > dl")).toBeNull();
  const recovery = screen.getByRole("region", { name: "Git save recovery for VM task-vm" });
  expect(article.firstElementChild).toBe(recovery);
  expect(recovery).toHaveClass("bg-warning-surface");
  expect(recovery).toHaveTextContent("Remote unavailable.");
  expect(recovery).toHaveTextContent("Compute remains reserved");
  expect(within(recovery).getByRole("button", { name: "Discard local data…" })).toBeEnabled();
  const advanced = screen.getByText("Advanced VM controls").closest("details")!;
  expect(advanced).not.toHaveAttribute("open");
  expect(advanced).toHaveTextContent("refs/heads/main@base-sha");
  expect(advanced).toHaveTextContent("final-sha");
  expect(advanced).toHaveTextContent("refs/heads/work@verified-sha");
  fireEvent.click(within(recovery).getByRole("button", { name: "Retry stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledWith("workspace", "task-vm", "org", "stop", { generation: 3 }));
});
