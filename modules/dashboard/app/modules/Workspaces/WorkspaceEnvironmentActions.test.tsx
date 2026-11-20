import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { workspaceOperation } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceEnvironmentActions } from "./WorkspaceEnvironmentActions";

vi.mock("@/api/workspacesApi", () => ({ workspaceOperation: vi.fn() }));

function vm(id: string, overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.125,
    estimatedMonthlyCompute: 90, computeReserved: true, revision: 1, workBranch: `work/${id}`, ...overrides,
  };
}
function workspace(overrides: Partial<DevelopmentWorkspace> = {}): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org-a", name: "Coding", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20, desiredState: "stopped", phase: "stopped",
    generation: 3, estimatedMonthlyCompute: 90, estimatedMonthlyStorage: 1.25, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary",
    vms: [vm("sibling-one"), vm("primary", { kind: "primary" }), vm("sibling-two", { desiredState: "stopped", phase: "stopped", computeReserved: false })],
    ...overrides,
  };
}
const refreshAfterMutation = vi.fn<(observation?: DevelopmentWorkspace) => Promise<void>>();
const onRefreshStatus = vi.fn();
function actions(item = workspace(), canManage = true, refreshing = false) {
  return <WorkspaceEnvironmentActions item={item} organizationId="org-a" refreshAfterMutation={refreshAfterMutation}
    canManage={canManage} onRefreshStatus={onRefreshStatus} refreshing={refreshing} />;
}
function openMenu() {
  fireEvent.pointerDown(screen.getByRole("button", { name: "Environment actions" }), { button: 0, ctrlKey: false });
  return screen.getByRole("menu");
}
function openStopAll() {
  fireEvent.click(within(openMenu()).getByRole("menuitem", { name: "Stop all VMs" }));
  return screen.findByRole("dialog", { name: "Stop all VMs?" });
}

beforeEach(() => {
  vi.mocked(workspaceOperation).mockResolvedValue(workspace());
  refreshAfterMutation.mockResolvedValue(undefined);
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it("offers Refresh status and Stop all VMs to managers and checks status on request", () => {
  render(actions());
  const menu = openMenu();
  expect(within(menu).getAllByRole("menuitem").map(item => item.textContent)).toEqual(["Refresh status", "Stop all VMs"]);
  fireEvent.click(within(menu).getByRole("menuitem", { name: "Refresh status" }));
  expect(onRefreshStatus).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("menu")).not.toBeInTheDocument();
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(workspaceOperation).not.toHaveBeenCalled();
});

it("offers viewers only Refresh status and disables it while a check is running", () => {
  const view = render(actions(workspace(), false));
  let menu = openMenu();
  expect(within(menu).getAllByRole("menuitem").map(item => item.textContent)).toEqual(["Refresh status"]);
  fireEvent.keyDown(menu, { key: "Escape" });
  view.rerender(actions(workspace(), false, true));
  menu = openMenu();
  expect(within(menu).getByRole("menuitem", { name: "Refresh status" })).toHaveAttribute("aria-disabled", "true");
});

it("disables Stop all VMs when there are no VMs", () => {
  render(actions(workspace({ vms: [] })));
  expect(within(openMenu()).getByRole("menuitem", { name: "Stop all VMs" })).toHaveAttribute("aria-disabled", "true");
});

it("enumerates all stop-all impacts, cancels without mutation and confirms only explicitly", async () => {
  render(actions());
  let dialog = await openStopAll();
  const affected = within(dialog).getAllByRole("listitem");
  expect(affected).toHaveLength(3);
  expect(affected[0]).toHaveTextContent(/sibling-one · Running · Git save: pending.*sibling-one · generation 3/);
  expect(affected[1]).toHaveTextContent(/Primary VM · Running .*primary · generation 3/);
  expect(affected[2]).toHaveTextContent(/Stopped .*sibling-two · generation 3/);
  expect(dialog).toHaveTextContent(/a VM that can't stop keeps its server and may keep charging/);
  expect(workspaceOperation).not.toHaveBeenCalled();
  fireEvent.click(within(dialog).getByRole("button", { name: "Cancel stop all" }));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  await waitFor(() => expect(screen.getByRole("button", { name: "Environment actions" })).toHaveFocus());
  expect(workspaceOperation).not.toHaveBeenCalled();
  dialog = await openStopAll();
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm stop all" }));
  await waitFor(() => expect(workspaceOperation).toHaveBeenCalledExactlyOnceWith("workspace", "org-a", "stop-all"));
  await waitFor(() => expect(refreshAfterMutation).toHaveBeenCalledExactlyOnceWith(workspace()));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
});

it("keeps the confirmation open with the error when stop all is rejected", async () => {
  vi.mocked(workspaceOperation).mockRejectedValueOnce(new Error("Provider unavailable"));
  render(actions());
  const dialog = await openStopAll();
  fireEvent.click(within(dialog).getByRole("button", { name: "Confirm stop all" }));
  expect(await within(dialog).findByRole("alert")).toHaveTextContent("Provider unavailable");
  expect(refreshAfterMutation).not.toHaveBeenCalled();
});

it("closes a stop-all confirmation when a VM generation changes and when management access is revoked", async () => {
  const view = render(actions());
  await openStopAll();
  const changed = workspace({ vms: [vm("primary", { kind: "primary", generation: 4 })] });
  view.rerender(actions(changed));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(workspaceOperation).not.toHaveBeenCalled();
  const dialog = await openStopAll();
  expect(dialog).toHaveTextContent("generation 4");
  view.rerender(actions(changed, false));
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  expect(within(openMenu()).queryByRole("menuitem", { name: "Stop all VMs" })).not.toBeInTheDocument();
  expect(workspaceOperation).not.toHaveBeenCalled();
});
