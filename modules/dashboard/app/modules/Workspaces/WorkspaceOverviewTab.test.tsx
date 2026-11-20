import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { getWorkspaceLaunchUrl, launchEphemeralVm, mutateWorkspaceVm, workspaceTask } from "@/api/workspacesApi";
import type { DevelopmentWorkspaceVmResources } from "@/api/workspaceFilesystemApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceOverviewTab } from "./WorkspaceOverviewTab";

vi.mock("@/api/workspacesApi", () => ({
  getWorkspaceLaunchUrl: vi.fn(), launchEphemeralVm: vi.fn(), mutateWorkspaceVm: vi.fn(), workspaceTask: vi.fn(),
}));
vi.mock("./WorkspaceSelectionFields", () => ({ WorkspaceSelectionFields: () => null }));

function resources(cores: number): DevelopmentWorkspaceVmResources {
  return {
    state: "current", observedAt: "2026-11-20T10:00:00Z", cpu: { logicalCount: cores }, memory: null, filesystems: [],
    unavailable: {}, diskUsageSupported: false, directoryDeletionSupported: false,
  };
}
function vm(id: string, overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id, kind: "ephemeral", generation: 3, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "account", region: "nbg1", sku: "cpx32",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.125,
    estimatedMonthlyCompute: 90, computeReserved: true, revision: 1, repository: `https://git.example/${id}.git`,
    ...overrides,
  };
}
const primary = vm("primary", { kind: "primary", resources: resources(8) });
const search = vm("search", { workBranch: "feat/search", resources: resources(2) });
const build = vm("build", { workBranch: "fix/build", phase: "awaiting_git_save", desiredState: "stopped", gitStatus: "remote_unavailable" });
const failing = vm("failing", { workBranch: "fix/boot", phase: "failed", errorCode: "boot_failed" });
const pending = vm("pending", { desiredState: "stopped", phase: "stopped", computeReserved: false });
function workspace(vms: DevelopmentWorkspaceVm[] = [search, primary, build, failing, pending]): DevelopmentWorkspace {
  return {
    id: "workspace", organizationId: "org", name: "minicloud-dev", imageId: "image", profile: "Standard", planId: "plan",
    providerAccountId: "account", region: "nbg1", sku: "cpx32", volumeSizeGb: 20, desiredState: "running", phase: "ready",
    generation: 3, estimatedMonthlyCompute: 90, estimatedMonthlyStorage: 1.25, currency: "EUR", revision: 1,
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary", vms,
  };
}
const refresh = vi.fn<() => Promise<void>>();
function overview(item = workspace(), canManage = true) {
  return <WorkspaceOverviewTab item={item} organizationId="org" refresh={refresh} refreshAfterMutation={refresh}
    canManage={canManage} canManageConnect={false} actorId="actor" />;
}
const list = () => screen.getByRole("region", { name: "Virtual machines" });
const detail = () => screen.getByRole("region", { name: "Selected VM" });
function select(name: RegExp) {
  fireEvent.click(within(list()).getByRole("button", { name }));
}
function expectNoMutations() {
  expect(mutateWorkspaceVm).not.toHaveBeenCalled();
  expect(launchEphemeralVm).not.toHaveBeenCalled();
  expect(workspaceTask).not.toHaveBeenCalled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
}

beforeEach(() => {
  refresh.mockResolvedValue(undefined);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url: "https://gateway.example/launch", expiresAt: "2099-01-01T00:00:00Z" });
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it("lists the primary VM first and selected, then temporary VMs by work branch with textual state", () => {
  render(overview());
  const entries = within(list()).getAllByRole("button", { pressed: undefined }).filter(button => button.hasAttribute("aria-pressed"));
  expect(entries.map(entry => entry.getAttribute("aria-label"))).toEqual([
    "Primary VM, Running",
    "feat/search, https://git.example/search.git, Running",
    "fix/build, https://git.example/build.git, Git save blocked",
    "fix/boot, https://git.example/failing.git, VM startup failed",
    "Work branch pending, https://git.example/pending.git, Stopped",
  ]);
  expect(entries[0]).toHaveAttribute("aria-pressed", "true");
  expect(entries.slice(1).every(entry => entry.getAttribute("aria-pressed") === "false")).toBe(true);
  expect(within(detail()).getByRole("heading", { name: "Primary VM" })).toBeInTheDocument();
  expect(within(detail()).getByRole("region", { name: "Resources for dev environment primary" })).toHaveTextContent("8 cores");
  expect(within(list()).getByRole("button", { name: "Launch temporary VM" })).toBeInTheDocument();
  expectNoMutations();
});

it("switches the detail, resource reading and action targets together without lifecycle side effects", async () => {
  render(overview());
  select(/^feat\/search/);
  expect(within(list()).getByRole("button", { name: /^feat\/search/ })).toHaveAttribute("aria-pressed", "true");
  expect(within(list()).getByRole("button", { name: /^Primary VM/ })).toHaveAttribute("aria-pressed", "false");
  const card = within(detail()).getByRole("article", { name: "VM search" });
  expect(within(card).getByRole("heading", { name: "feat/search" })).toBeInTheDocument();
  expect(card).toHaveTextContent("Repositoryhttps://git.example/search.git");
  expect(within(card).getByRole("region", { name: "Resources for dev environment search" })).toHaveTextContent("2 cores");
  expect(within(detail()).queryByRole("article", { name: "VM primary" })).not.toBeInTheDocument();
  expect(within(card).getByRole("button", { name: "More actions for feat/search" })).toBeInTheDocument();
  expectNoMutations();
  fireEvent.click(within(card).getByRole("button", { name: "Connect to T3" }));
  await waitFor(() => expect(getWorkspaceLaunchUrl).toHaveBeenCalledExactlyOnceWith("workspace", "search", "org", 3, "t3"));
  expect(await within(card).findByRole("link", { name: "Open T3" })).toBeInTheDocument();
  select(/^Primary VM/);
  select(/^feat\/search/);
  expect(within(detail()).queryByRole("link", { name: "Open T3" })).not.toBeInTheDocument();
  select(/^fix\/build/);
  const recovery = within(detail()).getByRole("region", { name: "Git save recovery for VM build" });
  fireEvent.click(within(recovery).getByRole("button", { name: "Retry stop" }));
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledExactlyOnceWith("workspace", "build", "org", "stop", { generation: 3 }));
});

it("keeps the selection across refreshes and falls back to the primary VM when the selected VM disappears", () => {
  const view = render(overview());
  select(/^feat\/search/);
  view.rerender(overview(workspace([primary, { ...search, revision: 2, phase: "draining", desiredState: "stopped" }, build])));
  expect(within(list()).getByRole("button", { name: /^feat\/search/ })).toHaveAttribute("aria-pressed", "true");
  expect(within(detail()).getByRole("article", { name: "VM search" })).toBeInTheDocument();
  view.rerender(overview(workspace([primary, build])));
  expect(within(list()).getByRole("button", { name: /^Primary VM/ })).toHaveAttribute("aria-pressed", "true");
  expect(within(detail()).getByRole("article", { name: "VM primary" })).toBeInTheDocument();
  view.rerender(overview(workspace([primary, build, search])));
  expect(within(detail()).getByRole("article", { name: "VM primary" })).toBeInTheDocument();
  expectNoMutations();
});

it("shows only the primary entry and launch control without temporary VMs, and hides launch from viewers", () => {
  const view = render(overview(workspace([primary])));
  const buttons = within(list()).getAllByRole("button");
  expect(buttons.map(button => button.getAttribute("aria-label"))).toEqual(["Launch temporary VM", "Primary VM, Running"]);
  view.rerender(overview(workspace([primary, search]), false));
  expect(within(list()).queryByRole("button", { name: "Launch temporary VM" })).not.toBeInTheDocument();
  select(/^feat\/search/);
  const card = within(detail()).getByRole("article", { name: "VM search" });
  expect(within(card).getByRole("button", { name: "Connect to T3" })).toBeDisabled();
  fireEvent.pointerDown(within(card).getByRole("button", { name: "More actions for feat/search" }), { button: 0, ctrlKey: false });
  expect(screen.getByRole("menuitem", { name: "Discard local data…" })).toHaveAttribute("aria-disabled", "true");
  expectNoMutations();
});

it("discards only the selected temporary VM after its typed confirmation from the overflow menu", async () => {
  render(overview());
  select(/^feat\/search/);
  const trigger = within(detail()).getByRole("button", { name: "More actions for feat/search" });
  fireEvent.pointerDown(trigger, { button: 0, ctrlKey: false });
  fireEvent.click(screen.getByRole("menuitem", { name: "Discard local data…" }));
  const dialog = screen.getByRole("dialog", { name: "Discard VM search generation 3" });
  const confirm = within(dialog).getByRole("button", { name: "Confirm irreversible discard" });
  expect(confirm).toBeDisabled();
  fireEvent.change(within(dialog).getByLabelText("Type discard search 3"), { target: { value: "discard search 3" } });
  expect(confirm).toBeDisabled();
  fireEvent.click(within(dialog).getByLabelText(/I acknowledge irreversible/));
  fireEvent.click(confirm);
  await waitFor(() => expect(mutateWorkspaceVm).toHaveBeenCalledExactlyOnceWith("workspace", "search", "org", "discard", {
    generation: 3, discardLocalData: true, acknowledgeIgnoredDataLoss: true,
  }));
  await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  expect(trigger).toHaveFocus();
});

it("keeps the missing-primary message as the primary entry's detail", () => {
  render(overview(workspace([search])));
  expect(within(list()).getByRole("button", { name: "Primary VM, Not reported" })).toHaveAttribute("aria-pressed", "true");
  expect(within(detail()).getByText("No primary VM reported.")).toBeInTheDocument();
  expect(within(detail()).queryByRole("article")).not.toBeInTheDocument();
});
