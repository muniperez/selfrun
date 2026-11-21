import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  cancelFilesystemJob, getFilesystemJob, startDirectoryDeletion, startDiskUsageScan,
  type DiskUsageEntry, type FilesystemJob,
} from "@/api/workspaceFilesystemApi";
import { ApiError, type DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceDiskUsage } from "./WorkspaceDiskUsage";

vi.mock("@/api/workspaceFilesystemApi", () => ({
  cancelFilesystemJob: vi.fn(), getFilesystemJob: vi.fn(), startDirectoryDeletion: vi.fn(), startDiskUsageScan: vi.fn(),
}));

const refresh = vi.fn<() => Promise<void>>();
const future = () => new Date(Date.now() + 5 * 60_000).toISOString();

function vm(overrides: Partial<DevelopmentWorkspaceVm> = {}): DevelopmentWorkspaceVm {
  return {
    id: "vm-1", kind: "primary", generation: 2, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "provider", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1,
    resources: {
      state: "current", observedAt: "2026-01-01T10:00:00Z", filesystems: [{ paths: ["/", "/workspace"], totalBytes: 100 * 1024 ** 3,
        freeBytes: 45 * 1024 ** 3, availableBytes: 40 * 1024 ** 3 }], unavailable: {},
      diskUsageSupported: true, directoryDeletionSupported: true,
    },
    ...overrides,
  };
}

function entry(path: string, overrides: Partial<DiskUsageEntry> = {}): DiskUsageEntry {
  return {
    name: path.split("/").pop()!, path, bytes: 2048, state: "complete", errors: [], canOpen: true, canDelete: false,
    targetToken: `token:${path}`, protectedReason: "protected_path", ...overrides,
  };
}

function scanJob(path: string, entries: DiskUsageEntry[], overrides: Partial<FilesystemJob> = {}): FilesystemJob {
  return {
    id: `scan:${path}`, vmId: "vm-1", generation: 2, kind: "disk_usage", state: "completed", path,
    createdAt: "2026-01-01T10:00:00Z", completedAt: "2026-01-01T10:00:05Z", expiresAt: future(), cancelRequested: false,
    diskUsage: {
      totalBytes: 10_000, filesHereBytes: 1024, complete: true, omittedEntries: 0, omittedBytes: 0, hardLinksApproximate: false,
      errors: {}, targetsExpireAt: future(), entries,
    },
    ...overrides,
  };
}

const project = entry("/workspace/repos/project", { canDelete: true, protectedReason: null, bytes: 4096 });
const rootScan = () => scanJob("/workspace", [
  entry("/workspace/repos"),
  entry("/workspace/cache", { bytes: null, state: "unreadable", canOpen: false, targetToken: null, protectedReason: "unreadable" }),
]);
const reposScan = () => scanJob("/workspace/repos", [project,
  entry("/workspace/repos/vendor", { bytes: 512, state: "partial", protectedReason: "not_owned_by_coding_user" }),
  entry("/workspace/repos/target", { bytes: null, state: "excluded", canOpen: false, targetToken: null,
    protectedReason: "scratch", storage: "scratch" })]);

function panel(machine = vm(), ready = true) {
  return <WorkspaceDiskUsage workspaceId="ws-1" workspaceName="Payments" organizationId="org" vm={machine} ready={ready} refresh={refresh} />;
}

beforeEach(() => {
  vi.mocked(startDiskUsageScan).mockImplementation(async (_w, _v, _o, request) =>
    request.targetToken === "token:/workspace/repos" ? reposScan() : rootScan());
  refresh.mockResolvedValue();
});
afterEach(() => { cleanup(); vi.clearAllMocks(); vi.useRealTimers(); });

async function openRepos() {
  fireEvent.click(screen.getByRole("button", { name: "Fetch disk usage" }));
  fireEvent.click(await screen.findByRole("button", { name: "Open /workspace/repos" }));
  return screen.findByRole("list", { name: "Folders in /workspace/repos" });
}

it("only scans when asked, then drills into a folder and back", async () => {
  render(panel());
  expect(startDiskUsageScan).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Fetch disk usage" }));
  const root = await screen.findByRole("list", { name: "Folders in /workspace" });
  expect(startDiskUsageScan).toHaveBeenCalledWith("ws-1", "vm-1", "org", expect.objectContaining({ generation: 2, targetToken: null }));
  expect(within(root).getByText("Unknown size")).toBeTruthy();
  expect(within(root).getByText("Unreadable")).toBeTruthy();
  expect(within(root).getByText("Files directly in this folder")).toBeTruthy();
  expect(screen.getByText(/40.0 GiB available of 100 GiB/)).toBeTruthy();
  expect(screen.getByText(/won't match disk usage exactly/)).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Open /workspace/repos" }));
  const repos = await screen.findByRole("list", { name: "Folders in /workspace/repos" });
  expect(within(repos).getByText("Partial: at least this size")).toBeTruthy();
  expect(within(repos).getByText("Protected: not owned by the dev environment user.")).toBeTruthy();
  expect(within(repos).getByText(/Build output on scratch storage/)).toBeTruthy();
  expect(within(repos).queryByRole("button", { name: "Delete /workspace/repos/target…" })).toBeNull();
  expect(within(repos).queryByRole("button", { name: "Delete /workspace/repos/vendor…" })).toBeNull();
  expect(within(repos).getByRole("button", { name: "Delete /workspace/repos/project…" })).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Back" }));
  expect(await screen.findByRole("list", { name: "Folders in /workspace" })).toBeTruthy();
  expect(screen.getByText(/top-level and tool folders|only folders inside/)).toBeTruthy();
});

it("is unavailable until the dev environment is ready and on images without folder sizes", () => {
  const { rerender } = render(panel(vm(), false));
  expect(screen.getByRole("button", { name: "Fetch disk usage" }).hasAttribute("disabled")).toBe(true);
  expect(screen.getByText("Start the dev environment to fetch folder sizes.")).toBeTruthy();
  rerender(panel(vm({ resources: { ...vm().resources!, diskUsageSupported: false } })));
  expect(screen.queryByRole("button", { name: "Fetch disk usage" })).toBeNull();
  expect(screen.getByText(/not supported by this dev environment image/)).toBeTruthy();
});

it("requires the exact full path and acknowledgement, shows the warnings, and reports the outcome", async () => {
  vi.mocked(startDirectoryDeletion).mockResolvedValue({ ...scanJob("/workspace/repos/project", []), id: "delete-1",
    kind: "directory_deletion", state: "running", diskUsage: null });
  vi.mocked(getFilesystemJob).mockResolvedValue({ ...scanJob("/workspace/repos/project", []), id: "delete-1",
    kind: "directory_deletion", state: "partially_deleted", diskUsage: null,
    deletion: { outcome: "partially_deleted", removedEntries: 12, removedBytes: 4096, errors: { permission_denied: 1 } } });
  render(panel());
  await openRepos();
  fireEvent.click(screen.getByRole("button", { name: "Delete /workspace/repos/project…" }));
  const dialog = screen.getByRole("dialog", { name: "Delete folder" });
  expect(within(dialog).getByText("Payments")).toBeTruthy();
  expect(within(dialog).getByText("primary vm-1, generation 2")).toBeTruthy();
  expect(within(dialog).getByText(/About 4.0 KiB at .* \(estimate\)/)).toBeTruthy();
  expect(within(dialog).getByText(/agent settings and credentials/)).toBeTruthy();
  expect(within(dialog).getByText(/not stopped; active use blocks checkout deletion/)).toBeTruthy();
  expect(within(dialog).getByText(/Backups are not an undo/)).toBeTruthy();

  const confirm = within(dialog).getByRole("button", { name: "Delete folder permanently" });
  const input = within(dialog).getByRole("textbox");
  fireEvent.change(input, { target: { value: "project" } });
  fireEvent.click(within(dialog).getByRole("checkbox"));
  expect(confirm.hasAttribute("disabled")).toBe(true);
  fireEvent.change(input, { target: { value: "/workspace/repos/project/" } });
  expect(confirm.hasAttribute("disabled")).toBe(true);
  fireEvent.change(input, { target: { value: "/workspace/repos/project" } });
  expect(confirm.hasAttribute("disabled")).toBe(false);

  vi.useFakeTimers({ shouldAdvanceTime: true });
  fireEvent.click(confirm);
  await waitFor(() => expect(startDirectoryDeletion).toHaveBeenCalledWith("ws-1", "vm-1", "org", expect.objectContaining({
    generation: 2, targetToken: "token:/workspace/repos/project", confirmationPath: "/workspace/repos/project",
    acknowledgePermanentLoss: true,
  })));
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
  expect(await screen.findByText(/Folder partly deleted.*Removed 4.0 KiB in 12 items/)).toBeTruthy();
  expect(refresh).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("list", { name: "Folders in /workspace/repos" })).toBeNull();
});

it("shows a checkout deletion refusal without claiming success", async () => {
  vi.mocked(startDirectoryDeletion).mockResolvedValue(scanJob("/workspace/repos/project", [], {
    id: "delete-refused", kind: "directory_deletion", state: "failed", diskUsage: null,
    errorCode: "checkout_in_use", errorMessage: "Close processes using this checkout before deleting it.",
  }));
  render(panel());
  await openRepos();
  fireEvent.click(screen.getByRole("button", { name: "Delete /workspace/repos/project…" }));
  const dialog = screen.getByRole("dialog", { name: "Delete folder" });
  fireEvent.change(within(dialog).getByRole("textbox"), { target: { value: project.path } });
  fireEvent.click(within(dialog).getByRole("checkbox"));
  fireEvent.click(within(dialog).getByRole("button", { name: "Delete folder permanently" }));
  expect(await screen.findByText("Folder was not deleted.")).toBeTruthy();
  expect(screen.getByText("Close processes using this checkout before deleting it.")).toBeTruthy();
  expect(screen.queryByText("Folder deleted.")).toBeNull();
  expect(getFilesystemJob).not.toHaveBeenCalled();
});

it("cancelling the dialog sends nothing", async () => {
  render(panel());
  await openRepos();
  fireEvent.click(screen.getByRole("button", { name: "Delete /workspace/repos/project…" }));
  fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Cancel" }));
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  expect(startDirectoryDeletion).not.toHaveBeenCalled();
});

it("polls a running scan with backoff and can cancel it", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  const running = { ...rootScan(), state: "running" as const, diskUsage: null, completedAt: null };
  vi.mocked(startDiskUsageScan).mockResolvedValue(running);
  vi.mocked(getFilesystemJob).mockResolvedValueOnce(running).mockResolvedValueOnce({ ...running, cancelRequested: true })
    .mockResolvedValue({ ...running, state: "cancelled", completedAt: "2026-01-01T10:00:09Z" });
  vi.mocked(cancelFilesystemJob).mockResolvedValue({ ...running, cancelRequested: true });
  render(panel());
  fireEvent.click(screen.getByRole("button", { name: "Fetch disk usage" }));
  fireEvent.click(await screen.findByRole("button", { name: "Cancel scan" }));
  await waitFor(() => expect(cancelFilesystemJob).toHaveBeenCalledWith("ws-1", "vm-1", "org", "scan:/workspace"));
  expect(await screen.findByText("Cancelling…")).toBeTruthy();
  await act(async () => { await vi.advanceTimersByTimeAsync(550); });
  expect(getFilesystemJob).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(800); });
  expect(getFilesystemJob).toHaveBeenCalledTimes(1);
  await act(async () => { await vi.advanceTimersByTimeAsync(300); });
  expect(getFilesystemJob).toHaveBeenCalledTimes(2);
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
  expect(await screen.findByText("Scan cancelled.")).toBeTruthy();
});

it("hides folder actions once results expire", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  render(panel());
  await openRepos();
  await act(async () => { await vi.advanceTimersByTimeAsync(5 * 60_000 + 100); });
  expect(screen.getByText(/These results have expired/)).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Delete /workspace/repos/project…" })).toBeNull();
  expect(screen.queryByRole("button", { name: /^Open / })).toBeNull();
});

it("closes the panel when write access is lost", async () => {
  render(panel());
  await openRepos();
  vi.mocked(startDiskUsageScan).mockRejectedValue(new ApiError(403, { code: "forbidden", message: "Forbidden", details: [] }));
  fireEvent.click(screen.getByRole("button", { name: "Scan again" }));
  expect(await screen.findByText("You no longer have permission to manage this dev environment.")).toBeTruthy();
  expect(screen.queryByRole("list")).toBeNull();
});

it("starts over when the dev environment moves to a new generation", async () => {
  const { rerender } = render(panel());
  await openRepos();
  rerender(panel(vm({ generation: 3 })));
  expect(screen.queryByRole("list")).toBeNull();
  expect(screen.getByRole("button", { name: "Fetch disk usage" })).toBeTruthy();
});
