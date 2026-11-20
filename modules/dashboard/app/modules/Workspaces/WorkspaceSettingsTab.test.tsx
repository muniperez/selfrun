import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { deleteWorkspace, getWorkspaceLaunchUrl } from "@/api/workspacesApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceSettingsTab } from "./WorkspaceSettingsTab";
import { permanentDeletion } from "./workspaceCollection";

vi.mock("@/api/workspacesApi", () => ({ deleteWorkspace: vi.fn(), getWorkspaceLaunchUrl: vi.fn(), updateWorkspaceIdleTimeout: vi.fn() }));

function vm(id: string, overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.125,
    estimatedMonthlyCompute: 90, computeReserved: true, revision: 1, ...overrides,
  };
}
function workspace(overrides: Partial<DevelopmentWorkspace> = {}): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org-a", name: "Coding", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20, desiredState: "running", phase: "ready",
    generation: 3, estimatedMonthlyCompute: 90, estimatedMonthlyStorage: 1.25, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary",
    vms: [vm("sibling-one"), vm("primary", { kind: "primary" })], ...overrides,
  };
}
function stoppedWorkspace(overrides: Partial<DevelopmentWorkspace> = {}) {
  const stopped = { desiredState: "stopped", phase: "stopped", computeReserved: false };
  return workspace({ vms: [vm("primary", { kind: "primary", ...stopped }), vm("sibling-one", stopped)], ...overrides });
}
const refresh = vi.fn<() => Promise<void>>();
function tab(item = workspace(), { canManageRole = true, canManageConnect = true } = {}) {
  const deletion = permanentDeletion(item);
  return <WorkspaceSettingsTab item={item} organizationId="org-a" refresh={refresh} refreshAfterMutation={refresh}
    canManage={canManageRole && !deletion} canManageRole={canManageRole} canManageConnect={canManageConnect} deletion={deletion} actorId="creator" />;
}
const deleteSection = () => screen.getByRole("region", { name: "Delete environment" });

beforeEach(() => { refresh.mockResolvedValue(undefined); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.resetAllMocks(); });

it("keeps idle timeout alongside primary-VM T3 account linking with its independent-access disclosure", async () => {
  const popup = { opener: {}, location: { replace: vi.fn() }, close: vi.fn() };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url: "https://connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" });
  render(tab());
  expect(screen.getByText("Idle timeout")).toBeInTheDocument();
  const connect = screen.getByRole("region", { name: "T3 Connect account linking" });
  expect(connect).toHaveTextContent(/outside Minicloud's session controls/);
  expect(connect).toHaveTextContent(/interrupts running turns and terminals/);
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
  fireEvent.click(within(connect).getByRole("button", { name: "Manage T3 Connect" }));
  await waitFor(() => expect(getWorkspaceLaunchUrl).toHaveBeenCalledExactlyOnceWith("workspace", "primary", "org-a", 3, "connect"));
});

it("gates T3 account linking like the primary VM card: management, ownership, startup and a running primary", () => {
  const view = render(tab(workspace(), { canManageConnect: false }));
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  view.rerender(tab(workspace(), { canManageRole: false }));
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  view.rerender(tab(workspace({ vms: [vm("primary", { kind: "primary", phase: "provisioning" })] })));
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  view.rerender(tab(workspace({ vms: [vm("primary", { kind: "primary", phase: "failed" })] })));
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  view.rerender(tab(workspace({ primaryVmId: null, vms: [vm("sibling-one")] })));
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  view.rerender(tab(stoppedWorkspace()));
  expect(within(screen.getByRole("region", { name: "T3 Connect account linking" }))
    .getByRole("button", { name: "Manage T3 Connect" })).toBeDisabled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
});

it("lists installed coding agents with versions only when the image reports them", () => {
  const view = render(tab(workspace({ agents: [{ id: "claude", name: "Claude Code", version: "2.1.328" }, { id: "muse", name: "Muse Code", version: "1.4.24-R9131.1" }] })));
  const agents = screen.getByRole("region", { name: "Coding agents" });
  expect(within(agents).getAllByRole("listitem").map(item => item.textContent)).toEqual(["Claude Code 2.1.328", "Muse Code 1.4.24-R9131.1"]);
  view.rerender(tab(workspace()));
  expect(screen.queryByRole("region", { name: "Coding agents" })).not.toBeInTheDocument();
});

it("offers Delete environment only after every VM is stopped and never to read-only users", () => {
  const view = render(tab());
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeDisabled();
  expect(deleteSection()).toHaveTextContent("Stop all VMs and wait until they finish stopping before deleting.");
  view.rerender(tab(stoppedWorkspace()));
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeEnabled();
  view.rerender(tab(stoppedWorkspace(), { canManageRole: false }));
  expect(screen.queryByRole("region", { name: "Delete environment" })).not.toBeInTheDocument();
  expect(deleteWorkspace).not.toHaveBeenCalled();
});

it("requires typing the environment name, then deletes and refreshes", async () => {
  vi.mocked(deleteWorkspace).mockRejectedValueOnce(new Error("Stop all workspace VMs first."))
    .mockResolvedValueOnce(stoppedWorkspace({ volumeStatus: "deleting" }));
  render(tab(stoppedWorkspace()));
  expect(deleteSection()).toHaveTextContent(/Stopping VMs keeps your files; deleting the environment does not./);
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

it("locks deletion while it runs, offers an explicit retry after failure and hides it once deleted", () => {
  const deleting = stoppedWorkspace({ volumeStatus: "deleting", phase: "deleting" });
  deleting.vms![0] = { ...deleting.vms![0], phase: "deleting" };
  const view = render(tab(deleting));
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeDisabled();
  expect(screen.queryByRole("region", { name: "T3 Connect account linking" })).not.toBeInTheDocument();
  const failed = stoppedWorkspace({ volumeStatus: "deleting", phase: "delete_failed" });
  failed.vms![0] = { ...failed.vms![0], phase: "delete_failed" };
  view.rerender(tab(failed));
  expect(within(deleteSection()).getByRole("button", { name: "Retry delete environment" })).toBeEnabled();
  view.rerender(tab(stoppedWorkspace({ volumeStatus: "deleted", phase: "deleted" })));
  expect(screen.queryByRole("region", { name: "Delete environment" })).not.toBeInTheDocument();
  view.rerender(tab(workspace({ phase: "deleting", vms: [vm("primary", { kind: "primary", phase: "deleting", desiredState: "stopped" })] })));
  expect(within(deleteSection()).getByRole("button", { name: "Delete environment" })).toBeDisabled();
});
