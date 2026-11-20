import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { launchEphemeralVm, workspaceOperation } from "@/api/workspacesApi";
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

it("releases the previous primary management context when the signed-in actor changes, even between organization owners", () => {
  const item = workspace();
  const view = render(detail(item));
  expect(releasedCards).not.toHaveBeenCalled();
  auth.profile = { id: "another-owner" };
  view.rerender(detail(item));
  expect(releasedCards).toHaveBeenCalledWith("primary");
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ vm: expect.objectContaining({ id: "primary" }) }));
});

it("resolves the primary by identity, selects it by default and shows only the selected VM without starting compute", () => {
  const item = workspace();
  const view = render(detail(item));
  const list = screen.getByRole("region", { name: "Virtual machines" });
  expect(within(list).getByRole("button", { pressed: true })).toBe(within(list).getByRole("button", { name: "Primary VM, Running" }));
  expect(within(list).getByRole("button", { name: "work/sibling-one, https://git.example/sibling-one.git, Running", pressed: false })).toBeInTheDocument();
  expect(within(list).getByRole("button", { name: "work/sibling-two, https://git.example/sibling-two.git, Stopped", pressed: false })).toBeInTheDocument();
  const selected = screen.getByRole("region", { name: "Selected VM" });
  expect(within(selected).getByRole("article", { name: "VM primary" })).toHaveTextContent("workspace / org-a / primary / generation 3 / EUR / Manage");
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ vm: item.vms![1] }));
  expect(within(selected).getAllByRole("article")).toHaveLength(1);
  expect(screen.queryByRole("table")).not.toBeInTheDocument();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  expect(workspaceOperation).not.toHaveBeenCalled();
  view.rerender(detail(workspace({ vms: [vm("primary", { kind: "primary", desiredState: "stopped", phase: "stopped" })] })));
  expect(within(selected).getByRole("article")).toHaveTextContent("stopped");
  expect(within(list).getAllByRole("button")).toHaveLength(2);
  expect(within(list).getByRole("button", { name: "Launch temporary VM" })).toBeInTheDocument();
  expect(within(list).getByRole("button", { name: "Primary VM, Stopped", pressed: true })).toBeInTheDocument();
});

it("falls back to explicit primary kind and never substitutes an ephemeral VM when primary metadata is missing", () => {
  const view = render(detail(workspace({ primaryVmId: null })));
  expect(within(screen.getByRole("region", { name: "Selected VM" })).getByRole("article", { name: "VM primary" })).toBeInTheDocument();
  view.rerender(detail(workspace({ vms: [vm("sibling-one")] })));
  const selected = screen.getByRole("region", { name: "Selected VM" });
  expect(within(selected).getByText("No primary VM reported.")).toBeInTheDocument();
  expect(within(selected).queryByRole("article")).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Primary VM, Not reported", pressed: true })).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: /^work\/sibling-one/ }));
  expect(within(selected).getByRole("article", { name: "VM sibling-one" })).toBeInTheDocument();
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
it("locks other controls while deleting and offers only an explicit retry after failure", () => {
  const deleting = stoppedWorkspace({ volumeStatus: "deleting", phase: "deleting" });
  deleting.vms![0] = { ...deleting.vms![0], phase: "deleting" };
  const view = render(detail(deleting));
  expect(screen.getByText(/Deleting this dev environment and its saved files/)).toHaveAttribute("role", "status");
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(screen.queryByText(/Read-only access/)).not.toBeInTheDocument();
  expect(vmCards).toHaveBeenLastCalledWith(expect.objectContaining({ canManage: false }));

  const failed = stoppedWorkspace({ volumeStatus: "deleting", phase: "delete_failed",
    errorCode: "workspace_delete_volume_attached", errorMessage: "Detach it before retrying." });
  failed.vms![0] = { ...failed.vms![0], phase: "delete_failed" };
  view.rerender(detail(failed));
  expect(screen.getByRole("alert")).toHaveTextContent("workspace_delete_volume_attached: Detach it before retrying.");
  expect(screen.getByText(/Deleting this dev environment failed/)).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Stop all VMs" })).not.toBeInTheDocument();
});

it("treats ordinary compute deletion as stopping and shows a permanently deleted environment as final", () => {
  const computeDeletion = workspace({ phase: "deleting", vms: [vm("primary", { kind: "primary", phase: "deleting", desiredState: "stopped" })] });
  const view = render(<MemoryRouter>{detail(computeDeletion)}</MemoryRouter>);
  expect(screen.queryByText(/Deleting this dev environment/)).not.toBeInTheDocument();
  view.rerender(<MemoryRouter>{detail(stoppedWorkspace({ volumeStatus: "deleted", phase: "deleted" }))}</MemoryRouter>);
  expect(screen.getByText(/were permanently deleted/)).toHaveAttribute("role", "status");
  expect(screen.getByRole("link", { name: "Back to dev environments" })).toHaveAttribute("href", "/workspaces");
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
