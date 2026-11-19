import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { deleteWorkspace, launchEphemeralVm, workspaceOperation } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceDetail } from "./WorkspaceDetail";

const vmCards = vi.hoisted(() => vi.fn());
const releasedCards = vi.hoisted(() => vi.fn());
const auth = vi.hoisted(() => ({
  profile: { id: "creator" },
  selectedOrganization: { id: "org-a", role: "owner" },
}));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
vi.mock("@/api/workspacesApi", () => ({ deleteWorkspace: vi.fn(), launchEphemeralVm: vi.fn(), workspaceOperation: vi.fn() }));
vi.mock("./WorkspaceVmCard", () => ({
  WorkspaceVmCard: (props: {
    workspaceId: string; organizationId: string; vm: DevelopmentWorkspaceVm; currency: string; canManage: boolean;
    variant?: "primary" | "embedded"; retainedStorage?: { sizeGb: number; status?: string | null; estimatedMonthlyStorage: number };
  }) => {
    useEffect(() => () => releasedCards(props.vm.id), [props.vm.id]);
    vmCards(props);
    const { workspaceId, organizationId, vm, currency, canManage } = props;
    return <article aria-label={`VM ${vm.id}`}>
      {workspaceId} / {organizationId} / {vm.id} / generation {vm.generation} / {currency} / {canManage ? "Manage" : "Read only"} · {vm.phase}
    </article>;
  },
}));
vi.mock("./WorkspaceSelectionFields", () => ({
  WorkspaceSelectionFields: ({ organizationId, workspaceId, onChange, onReadyChange, disabled }: {
    organizationId: string; workspaceId?: string; disabled?: boolean;
    onChange: (selection: { imageId: string; profile: string; planId: string }) => void;
    onReadyChange: (ready: boolean) => void;
  }) => <div aria-label={`Selections for ${organizationId}/${workspaceId}`}>
    <button type="button" disabled={disabled} onClick={() => {
      onChange({ imageId: "image", profile: "Standard", planId: "plan" }); onReadyChange(true);
    }}>Use eligible selection</button>
    <button type="button" disabled={disabled} onClick={() => onReadyChange(false)}>Catalog unavailable</button>
  </div>,
}));

function vm(id: string, overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.125,
    estimatedMonthlyCompute: 90, computeReserved: true, revision: 1,
    repository: `https://git.example/${id}.git`, workBranch: `work/${id}`, baseRef: "refs/heads/main", baseCommit: "a".repeat(40),
    lastActivityAt: "2026-11-16T10:00:00Z", shutdownDeadline: "2026-11-16T10:30:00Z",
    ...overrides,
  };
}
function workspace(overrides: Partial<DevelopmentWorkspace> = {}): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org-a", name: "Coding", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20, desiredState: "stopped", phase: "stopped",
    generation: 3, estimatedMonthlyCompute: 90, estimatedMonthlyStorage: 1.25, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary",
    pricingUpdatedAt: "2026-11-15T00:00:00Z",
    vms: [vm("sibling-one"), vm("primary", { kind: "primary" }), vm("sibling-two", { desiredState: "stopped", phase: "stopped", computeReserved: false })],
    ...overrides,
  };
}
const refresh = vi.fn<() => Promise<void>>();
function detail(item = workspace(), canManage = true, organizationId = "org-a") {
  return <WorkspaceDetail item={item} organizationId={organizationId} refresh={refresh} canManage={canManage} />;
}
function openLaunch() {
  fireEvent.click(screen.getByRole("button", { name: "Launch temporary VM" }));
  return screen.getByRole("form", { name: "Launch temporary VM in Coding" });
}
function fillLaunch(form: HTMLElement) {
  fireEvent.click(within(form).getByRole("button", { name: "Use eligible selection" }));
  fireEvent.change(within(form).getByLabelText("Repository URL"), { target: { value: "https://git.example/repo.git" } });
  fireEvent.change(within(form).getByLabelText("Base ref"), { target: { value: "refs/heads/main" } });
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "b".repeat(40) } });
}
function acknowledge(form: HTMLElement) {
  fireEvent.click(within(form).getByLabelText(/I authorize task-scoped pushes/));
  fireEvent.click(within(form).getByLabelText(/I understand that untracked files/));
}
function openStopAll() {
  fireEvent.click(screen.getByRole("button", { name: "Stop all VMs" }));
  return screen.getByRole("dialog", { name: "Stop all VMs?" });
}
function showCosts() {
  const tab = screen.getByRole("tab", { name: "Storage & costs" });
  fireEvent.mouseDown(tab, { button: 0, ctrlKey: false });
  fireEvent.click(tab);
}

beforeEach(() => {
  auth.profile = { id: "creator" };
  auth.selectedOrganization = { id: "org-a", role: "owner" };
  vi.mocked(launchEphemeralVm).mockResolvedValue(workspace());
  vi.mocked(workspaceOperation).mockResolvedValue(workspace());
  refresh.mockResolvedValue(undefined);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it.each([
  ["owner", "someone-else", true], ["developer", "creator", true],
  ["admin", "someone-else", false], ["developer", "someone-else", false],
])("offers primary-only account management to the creator or organization owner: %s", (role, createdBy, allowed) => {
  auth.selectedOrganization.role = role as string;
  render(detail(workspace({ createdBy: createdBy as string })));
  expect(vmCards).toHaveBeenCalledWith(expect.objectContaining({
    vm: expect.objectContaining({ kind: "primary" }), canManageConnect: allowed,
  }));
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  expect(workspaceOperation).not.toHaveBeenCalled();
});

it("fences Connect ownership against organization context and read-only management", () => {
  auth.selectedOrganization.id = "different-org";
  const view = render(detail(workspace({ createdBy: "creator" })));
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ canManageConnect: false }));
  auth.selectedOrganization.id = "org-a";
  view.rerender(detail(workspace({ createdBy: "creator" }), false));
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ canManageConnect: false }));
});

it("releases the previous primary management context when the signed-in actor changes, even between organization owners", () => {
  const item = workspace();
  const view = render(detail(item));
  expect(releasedCards).not.toHaveBeenCalled();
  auth.profile = { id: "another-owner" };
  view.rerender(detail(item));
  expect(releasedCards).toHaveBeenCalledWith("primary");
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ canManageConnect: true }));
});

it("resolves the primary by identity and passes observed state and retained storage into one unified card without starting compute", () => {
  const item = workspace();
  const view = render(detail(item));
  const primary = screen.getByRole("region", { name: "Primary VM" });
  expect(within(primary).getByRole("article", { name: "VM primary" })).toHaveTextContent("workspace / org-a / primary / generation 3 / EUR / Manage");
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({
    vm: item.vms[1], variant: "primary",
    retainedStorage: { sizeGb: 20, status: "retained", estimatedMonthlyStorage: 1.25 },
  }));
  expect(within(primary).getAllByRole("article")).toHaveLength(1);
  expect(within(primary).queryByRole("article", { name: "VM sibling-one" })).not.toBeInTheDocument();
  expect(screen.getAllByRole("table")).toHaveLength(1);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  expect(workspaceOperation).not.toHaveBeenCalled();
  view.rerender(detail(workspace({ vms: [vm("primary", { kind: "primary", desiredState: "stopped", phase: "stopped" })] })));
  expect(within(primary).getByRole("article")).toHaveTextContent("stopped");
  expect(screen.getByText("No temporary VMs")).toBeInTheDocument();
});

it("shows the primary VM's hourly rate, storage and projected monthly total with the 720-hour assumption", () => {
  render(detail(workspace({ currency: "USD" })));
  const estimate = within(screen.getByRole("region", { name: "Projected cost" }))
    .getByRole("group", { name: "Projected monthly cost" });
  expect(estimate).toHaveTextContent("VM rate$0.125/hour while running");
  expect(estimate).toHaveTextContent("Storage$1.25/month, even while the VM is stopped");
  expect(estimate).toHaveTextContent("Projected monthly total$91.25 if the VM runs 720 hours this month");
  expect(estimate).toHaveTextContent("never blocks creating or starting a VM");
});

it("lists installed coding agents with versions only when the image reports them", () => {
  const view = render(detail(workspace({ agents: [{ id: "claude", name: "Claude Code", version: "2.1.328" }, { id: "muse", name: "Muse Code", version: "1.4.24-R9131.1" }] })));
  const agents = screen.getByRole("region", { name: "Coding agents" });
  expect(within(agents).getAllByRole("listitem").map(item => item.textContent)).toEqual(["Claude Code 2.1.328", "Muse Code 1.4.24-R9131.1"]);
  view.rerender(detail(workspace()));
  expect(screen.queryByRole("region", { name: "Coding agents" })).not.toBeInTheDocument();
});

it("falls back to explicit primary kind and never substitutes an ephemeral VM when primary metadata is missing", () => {
  const view = render(detail(workspace({ primaryVmId: null })));
  expect(within(screen.getByRole("region", { name: "Primary VM" })).getByRole("article", { name: "VM primary" })).toBeInTheDocument();
  view.rerender(detail(workspace({ vms: [vm("sibling-one")] })));
  const primary = screen.getByRole("region", { name: "Primary VM" });
  expect(within(primary).getByText("No primary VM reported.")).toBeInTheDocument();
  expect(within(primary).queryByRole("article")).not.toBeInTheDocument();
  expect(screen.getByText("work/sibling-one")).toBeInTheDocument();
});

it("expands siblings independently and delegates complete Git, generation and activity metadata to embedded VM controls", () => {
  render(detail(workspace({ vms: [
    vm("primary", { kind: "primary" }),
    vm("sibling-one", { verifiedRemoteRef: "refs/heads/work/one", verifiedRemoteCommit: "c".repeat(40), finalHead: "c".repeat(40) }),
    vm("sibling-two", { phase: "awaiting_git_save", gitStatus: "remote_unavailable" }),
  ] })));
  const table = screen.getByRole("table", { name: "Temporary VMs" });
  expect(within(table).getByText("work/sibling-one")).toBeInTheDocument();
  expect(within(table).getByText("https://git.example/sibling-two.git")).toBeInTheDocument();
  expect(within(table).getByText("Remote unavailable")).toBeInTheDocument();
  expect(within(table).getByText("Git save blocked")).toBeInTheDocument();
  const one = screen.getByRole("button", { name: "Expand VM sibling-one" });
  const two = screen.getByRole("button", { name: "Expand VM sibling-two" });
  fireEvent.click(one);
  fireEvent.click(two);
  const firstDetails = screen.getByRole("region", { name: "Details for VM sibling-one" });
  const secondDetails = screen.getByRole("region", { name: "Details for VM sibling-two" });
  expect(one).toHaveAttribute("aria-expanded", "true");
  expect(two).toHaveAttribute("aria-expanded", "true");
  expect(one).toHaveAttribute("aria-controls", firstDetails.id);
  expect(vmCards).toHaveBeenCalledWith(expect.objectContaining({
    variant: "embedded",
    vm: expect.objectContaining({
      id: "sibling-one", generation: 3, baseRef: "refs/heads/main", baseCommit: "a".repeat(40),
      verifiedRemoteRef: "refs/heads/work/one", verifiedRemoteCommit: "c".repeat(40), lastActivityAt: "2026-11-16T10:00:00Z",
    }),
  }));
  expect(within(firstDetails).getByRole("article", { name: "VM sibling-one" })).toBeInTheDocument();
  expect(within(secondDetails).getByRole("article", { name: "VM sibling-two" })).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Collapse VM sibling-one" }));
  expect(firstDetails).not.toBeVisible();
  expect(secondDetails).toBeVisible();
});

it("shows retained storage and observed per-VM estimates in the costs tab, primary first, without billing promises", () => {
  render(detail());
  showCosts();
  expect(screen.getByRole("tab", { name: "Storage & costs" })).toHaveAttribute("aria-selected", "true");
  expect(screen.getByText("20 GB")).toBeInTheDocument();
  expect(screen.getByText("retained")).toBeInTheDocument();
  expect(screen.getByText("1.25 EUR")).toBeInTheDocument();
  expect(screen.getByText("2026-11-15T00:00:00Z")).toBeInTheDocument();
  const costs = screen.getByRole("region", { name: "VM costs" });
  const rows = within(costs).getAllByRole("row");
  expect(rows[1]).toHaveTextContent("primary");
  expect(rows[2]).toHaveTextContent("sibling-one");
  expect(rows[3]).toHaveTextContent("Not allocated");
  expect(within(costs).getAllByText("0.1250 EUR")).toHaveLength(3);
  expect(screen.getByText(/Storage is charged even when all VMs are stopped/)).toBeInTheDocument();
  expect(screen.getByText(/may round up to whole hours/)).toBeInTheDocument();
  expect(screen.getByText(/not invoices or a billing guarantee/)).toBeInTheDocument();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
});

it("guards launch on eligible selections, resolved SHA and both acknowledgments, including programmatic submits", () => {
  render(detail());
  const form = openLaunch();
  expect(screen.getByRole("dialog")).toHaveTextContent(/can't see the primary VM's storage/);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fillLaunch(form);
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.click(within(form).getByLabelText(/I authorize task-scoped pushes/));
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.click(within(form).getByLabelText(/I understand that untracked files/));
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "not-a-commit" } });
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "b".repeat(40) } });
  fireEvent.click(within(form).getByRole("button", { name: "Catalog unavailable" }));
  expect(within(form).getByRole("button", { name: "Launch temporary VM" })).toBeDisabled();
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
});

it("retains idempotency on unchanged failed retries, changes it with payload, then resets and refreshes after success", async () => {
  vi.mocked(launchEphemeralVm).mockRejectedValueOnce(new Error("Timed out")).mockRejectedValueOnce(new Error("Try again"));
  render(detail());
  const form = openLaunch();
  fillLaunch(form);
  acknowledge(form);
  fireEvent.change(within(form).getByLabelText(/Repository credential reference/), { target: { value: " vault/repository " } });
  fireEvent.submit(form);
  await screen.findByText("Timed out");
  fireEvent.submit(form);
  await screen.findByText("Try again");
  const calls = vi.mocked(launchEphemeralVm).mock.calls;
  expect(calls[0]).toEqual(["workspace", "org-a", expect.objectContaining({
    imageId: "image", profile: "Standard", planId: "plan", repositoryCredentialRef: "vault/repository",
    pushAuthorized: true, acknowledgeIgnoredDataLoss: true, baseCommit: "b".repeat(40),
  })]);
  expect(calls[1][2].idempotencyKey).toBe(calls[0][2].idempotencyKey);
  fireEvent.change(within(form).getByLabelText("Base ref"), { target: { value: "refs/heads/feature" } });
  fireEvent.submit(form);
  await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1));
  expect(calls[2][2].idempotencyKey).not.toBe(calls[0][2].idempotencyKey);
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  const reopened = openLaunch();
  expect(within(reopened).getByLabelText("Repository URL")).toHaveValue("");
  expect(within(reopened).getByLabelText(/I authorize task-scoped pushes/)).not.toBeChecked();
  expect(within(reopened).getByLabelText(/I understand that untracked files/)).not.toBeChecked();
});

it("enumerates all stop-all impacts, cancels without mutation and confirms only explicitly", async () => {
  render(detail());
  let dialog = openStopAll();
  const affected = within(dialog).getAllByRole("listitem");
  expect(affected).toHaveLength(3);
  expect(affected[0]).toHaveTextContent(/sibling-one · Running · Git save: pending.*sibling-one · generation 3/);
  expect(affected[1]).toHaveTextContent(/Primary VM · Running .*primary · generation 3/);
  expect(affected[2]).toHaveTextContent(/Stopped .*sibling-two · generation 3/);
  expect(dialog).toHaveTextContent(/a VM that can't stop keeps its server and may keep charging/);
  expect(workspaceOperation).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel stop all" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(workspaceOperation).not.toHaveBeenCalled();
  dialog = openStopAll();
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm stop all" }));
  await waitFor(() => expect(workspaceOperation).toHaveBeenCalledExactlyOnceWith("workspace", "org-a", "stop-all"));
  await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("closes a stop-all confirmation when a VM generation changes and when management access is revoked", () => {
  const view = render(detail());
  openStopAll();
  const changed = workspace({ vms: [vm("primary", { kind: "primary", generation: 4 })] });
  view.rerender(detail(changed));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(workspaceOperation).not.toHaveBeenCalled();
  const dialog = openStopAll();
  expect(dialog).toHaveTextContent("generation 4");
  view.rerender(detail(changed, false));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Launch temporary VM" })).not.toBeInTheDocument();
  expect(screen.getByRole("article", { name: "VM primary" })).toHaveTextContent("Read only");
  expect(workspaceOperation).not.toHaveBeenCalled();
});

it("keeps read-only metadata and costs available and discards launch state on organization changes", () => {
  const view = render(detail(workspace(), false));
  expect(screen.queryByRole("button", { name: "Launch temporary VM" })).not.toBeInTheDocument();
  showCosts();
  expect(screen.getByText("1.25 EUR")).toBeInTheDocument();
  view.rerender(detail(workspace(), true, "org-b"));
  const form = openLaunch();
  fillLaunch(form);
  acknowledge(form);
  view.rerender(detail(workspace(), true, "org-c"));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  const nextForm = openLaunch();
  expect(within(nextForm).getByLabelText("Repository URL")).toHaveValue("");
  expect(within(nextForm).getByLabelText("Selections for org-c/workspace")).toBeInTheDocument();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
});
function stoppedWorkspace(overrides: Partial<DevelopmentWorkspace> = {}) {
  const stopped = { desiredState: "stopped", phase: "stopped", computeReserved: false };
  return workspace({ vms: [vm("primary", { kind: "primary", ...stopped }), vm("sibling-one", stopped)], ...overrides });
}
function deleteSection() {
  return screen.getByRole("region", { name: "Delete environment" });
}

it("offers Delete environment only after every VM is stopped and never to read-only users", () => {
  const view = render(detail());
  const button = within(deleteSection()).getByRole("button", { name: "Delete environment" });
  expect(button).toBeDisabled();
  expect(deleteSection()).toHaveTextContent("Stop all VMs and wait until they finish stopping before deleting.");
  view.rerender(detail(stoppedWorkspace()));
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeEnabled();
  view.rerender(detail(stoppedWorkspace(), false));
  expect(screen.queryByRole("region", { name: "Delete environment" })).not.toBeInTheDocument();
  expect(deleteWorkspace).not.toHaveBeenCalled();
});

it("requires typing the environment name, then deletes and refreshes", async () => {
  vi.mocked(deleteWorkspace).mockRejectedValueOnce(new Error("Stop all workspace VMs first."))
    .mockResolvedValueOnce(stoppedWorkspace({ volumeStatus: "deleting" }));
  render(detail(stoppedWorkspace()));
  fireEvent.click(within(deleteSection()).getByRole("button", { name: "Delete environment" }));
  const dialog = screen.getByRole("dialog", { name: "Delete Coding permanently?" });
  expect(dialog).toHaveTextContent(/Files that are not pushed to Git are lost for good. This cannot be undone./);
  const confirm = within(dialog).getByRole("button", { name: "Delete permanently" });
  expect(confirm).toBeDisabled();
  fireEvent.change(within(dialog).getByLabelText(/Type Coding to confirm/), { target: { value: "Cod" } });
  expect(confirm).toBeDisabled();
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(deleteWorkspace).not.toHaveBeenCalled();

  fireEvent.click(within(deleteSection()).getByRole("button", { name: "Delete environment" }));
  const reopened = screen.getByRole("dialog", { name: "Delete Coding permanently?" });
  fireEvent.change(within(reopened).getByLabelText(/Type Coding to confirm/), { target: { value: "Coding" } });
  fireEvent.click(within(reopened).getByRole("button", { name: "Delete permanently" }));
  expect(await within(reopened).findByRole("alert")).toHaveTextContent("Stop all workspace VMs first.");
  expect(refresh).not.toHaveBeenCalled();
  fireEvent.click(within(reopened).getByRole("button", { name: "Delete permanently" }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(deleteWorkspace).toHaveBeenCalledTimes(2);
  expect(deleteWorkspace).toHaveBeenLastCalledWith("workspace", "org-a");
  expect(refresh).toHaveBeenCalledTimes(1);
});

it("locks other controls while deleting and offers only an explicit retry after failure", () => {
  const deleting = stoppedWorkspace({ volumeStatus: "deleting", phase: "deleting" });
  deleting.vms![0] = { ...deleting.vms![0], phase: "deleting" };
  const view = render(detail(deleting));
  expect(screen.getByText(/Deleting this dev environment and its saved files/)).toHaveAttribute("role", "status");
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(screen.queryByText(/Read-only access/)).not.toBeInTheDocument();
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ canManage: false }));
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeDisabled();

  const failed = stoppedWorkspace({ volumeStatus: "deleting", phase: "delete_failed",
    errorCode: "workspace_delete_volume_attached", errorMessage: "Detach it before retrying." });
  failed.vms![0] = { ...failed.vms![0], phase: "delete_failed" };
  view.rerender(detail(failed));
  expect(screen.getByRole("alert")).toHaveTextContent("workspace_delete_volume_attached: Detach it before retrying.");
  expect(screen.getByText(/Deleting this dev environment failed/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(within(deleteSection()).getByRole("button", { name: "Retry delete environment" })).toBeEnabled();
});

it("treats ordinary compute deletion as stopping and shows a permanently deleted environment as final", () => {
  const computeDeletion = workspace({ phase: "deleting", vms: [vm("primary", { kind: "primary", phase: "deleting", desiredState: "stopped" })] });
  const view = render(<MemoryRouter>{detail(computeDeletion)}</MemoryRouter>);
  expect(screen.queryByText(/Deleting this dev environment/)).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Stop all VMs" })).toBeInTheDocument();
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeDisabled();
  view.rerender(<MemoryRouter>{detail(stoppedWorkspace({ volumeStatus: "deleted", phase: "deleted" }))}</MemoryRouter>);
  expect(screen.getByText(/were permanently deleted/)).toHaveAttribute("role", "status");
  expect(screen.getByRole("link", { name: "Back to dev environments" })).toHaveAttribute("href", "/workspaces");
  expect(screen.queryByRole("region", { name: "Delete environment" })).not.toBeInTheDocument();
});

import { useEffect } from "react";

it("leaves a primary VM's start failure to its card instead of repeating it above the tabs", () => {
  const failure = { errorCode: "workspace_admission_limit", errorMessage: "Organization VM concurrency limit reached." };
  const view = render(<MemoryRouter>{detail(workspace({ ...failure, vms: [vm("primary", {
    kind: "primary", desiredState: "stopped", phase: "stopped", computeReserved: false, ...failure })] }))}</MemoryRouter>);
  expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  view.rerender(<MemoryRouter>{detail(workspace({ errorCode: "workspace_other", errorMessage: "Other." }))}</MemoryRouter>);
  expect(screen.getByRole("alert")).toHaveTextContent("workspace_other: Other.");
  expect(workspaceOperation).not.toHaveBeenCalled();
});
