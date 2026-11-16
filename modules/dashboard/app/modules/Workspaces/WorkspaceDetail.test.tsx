import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { launchEphemeralVm, workspaceOperation } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceDetail } from "./WorkspaceDetail";

const vmCards = vi.hoisted(() => vi.fn());
vi.mock("@/api/workspacesApi", () => ({ launchEphemeralVm: vi.fn(), workspaceOperation: vi.fn() }));
vi.mock("./WorkspaceVmCard", () => ({
  WorkspaceVmCard: (props: {
    workspaceId: string; organizationId: string; vm: DevelopmentWorkspaceVm; currency: string; canManage: boolean;
    variant?: "primary" | "embedded"; retainedStorage?: { sizeGb: number; status?: string | null; estimatedMonthlyStorage: number };
  }) => {
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
  fireEvent.click(screen.getByRole("button", { name: "Launch ephemeral VM" }));
  return screen.getByRole("form", { name: "Launch ephemeral VM in Coding" });
}
function fillLaunch(form: HTMLElement) {
  fireEvent.click(within(form).getByRole("button", { name: "Use eligible selection" }));
  fireEvent.change(within(form).getByLabelText("Repository URL"), { target: { value: "https://git.example/repo.git" } });
  fireEvent.change(within(form).getByLabelText("Base ref"), { target: { value: "refs/heads/main" } });
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "b".repeat(40) } });
}
function acknowledge(form: HTMLElement) {
  fireEvent.click(within(form).getByLabelText(/I authorize task-scoped pushes/));
  fireEvent.click(within(form).getByLabelText(/I acknowledge ignored\/untracked/));
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
  vi.mocked(launchEphemeralVm).mockResolvedValue(workspace());
  vi.mocked(workspaceOperation).mockResolvedValue(workspace());
  refresh.mockResolvedValue(undefined);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

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
  expect(screen.getByText("No ephemeral VMs")).toBeInTheDocument();
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
  const table = screen.getByRole("table", { name: "Ephemeral VMs" });
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
  const costs = screen.getByRole("region", { name: "Per-VM compute estimates" });
  const rows = within(costs).getAllByRole("row");
  expect(rows[1]).toHaveTextContent("primary");
  expect(rows[2]).toHaveTextContent("sibling-one");
  expect(rows[3]).toHaveTextContent("Not reserved");
  expect(within(costs).getAllByText("0.1250 EUR")).toHaveLength(3);
  expect(screen.getByText(/retained storage continues to incur charges/i)).toBeInTheDocument();
  expect(screen.getByText(/rounds up to whole hours/)).toBeInTheDocument();
  expect(screen.getByText(/not provider invoices or a billing guarantee/)).toBeInTheDocument();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
});

it("guards launch on eligible selections, resolved SHA and both acknowledgments, including programmatic submits", () => {
  render(detail());
  const form = openLaunch();
  expect(screen.getByRole("dialog")).toHaveTextContent(/no access to the primary retained volume/);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fillLaunch(form);
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.click(within(form).getByLabelText(/I authorize task-scoped pushes/));
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.click(within(form).getByLabelText(/I acknowledge ignored\/untracked/));
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "not-a-commit" } });
  fireEvent.submit(form);
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  fireEvent.change(within(form).getByLabelText("Resolved base commit SHA"), { target: { value: "b".repeat(40) } });
  fireEvent.click(within(form).getByRole("button", { name: "Catalog unavailable" }));
  expect(within(form).getByRole("button", { name: "Launch ephemeral VM" })).toBeDisabled();
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
  expect(within(reopened).getByLabelText(/I acknowledge ignored\/untracked/)).not.toBeChecked();
});

it("enumerates all stop-all impacts, cancels without mutation and confirms only explicitly", async () => {
  render(detail());
  let dialog = openStopAll();
  const affected = within(dialog).getAllByRole("listitem");
  expect(affected).toHaveLength(3);
  expect(affected[0]).toHaveTextContent("sibling-one · ephemeral · generation 3 · ready · Git save: pending");
  expect(affected[1]).toHaveTextContent("primary · primary · generation 3");
  expect(affected[2]).toHaveTextContent("sibling-two · ephemeral · generation 3 · stopped");
  expect(dialog).toHaveTextContent(/blocked VMs remain allocated and may incur costs/);
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
  expect(screen.queryByRole("button", { name: "Launch ephemeral VM" })).not.toBeInTheDocument();
  expect(screen.getByRole("article", { name: "VM primary" })).toHaveTextContent("Read only");
  expect(workspaceOperation).not.toHaveBeenCalled();
});

it("keeps read-only metadata and costs available and discards launch state on organization changes", () => {
  const view = render(detail(workspace(), false));
  expect(screen.queryByRole("button", { name: "Launch ephemeral VM" })).not.toBeInTheDocument();
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
