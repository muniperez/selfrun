import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { getFilesystemJob, startDiskUsageScan, type FilesystemJob } from "@/api/workspaceFilesystemApi";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceStorageCostsTab } from "./WorkspaceStorageCostsTab";

type VmResources = NonNullable<DevelopmentWorkspaceVm["resources"]>;

vi.mock("@/api/workspaceFilesystemApi", () => ({
  cancelFilesystemJob: vi.fn(), getFilesystemJob: vi.fn(), startDirectoryDeletion: vi.fn(), startDiskUsageScan: vi.fn(),
}));

const resources = (overrides: Partial<VmResources> = {}): VmResources => ({
  state: "current", observedAt: "2026-11-16T10:00:00Z", unavailable: {},
  filesystems: [{ paths: ["/workspace"], totalBytes: 100 * 1024 ** 3, freeBytes: 45 * 1024 ** 3, availableBytes: 40 * 1024 ** 3 }],
  diskUsageSupported: true, directoryDeletionSupported: true, ...overrides,
});
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
    createdAt: "", updatedAt: "", volumeStatus: "retained", primaryVmId: "primary", pricingUpdatedAt: "2026-11-15T00:00:00Z",
    vms: [vm("sibling-one"), vm("primary", { kind: "primary", workBranch: null }),
      vm("sibling-two", { desiredState: "stopped", phase: "stopped", computeReserved: false })],
    ...overrides,
  };
}
const refresh = vi.fn<() => Promise<void>>();
function tab(item = workspace(), canManage = true) {
  return <WorkspaceStorageCostsTab item={item} organizationId="org-a" refresh={refresh} canManage={canManage} />;
}

beforeEach(() => { refresh.mockResolvedValue(undefined); });
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it("shows retained storage and per-VM rates, primary first and temporary VMs by work branch, without billing promises", () => {
  render(tab());
  expect(screen.getByText("20 GB")).toBeInTheDocument();
  expect(screen.getByText("retained")).toBeInTheDocument();
  expect(screen.getByText("1.25 EUR")).toBeInTheDocument();
  expect(screen.getByText("2026-11-15T00:00:00Z")).toBeInTheDocument();
  const rows = within(screen.getByRole("region", { name: "VM costs" })).getAllByRole("row");
  expect(rows[1]).toHaveTextContent(/^Primary VMprimary/);
  expect(rows[2]).toHaveTextContent(/^work\/sibling-onesibling-one/);
  expect(rows[3]).toHaveTextContent(/^work\/sibling-two.*Not allocated/);
  expect(within(screen.getByRole("region", { name: "VM costs" })).getAllByText("0.1250 EUR")).toHaveLength(3);
  expect(screen.getByText(/Storage is charged even when all VMs are stopped/)).toBeInTheDocument();
  expect(screen.getByText(/may round up to whole hours/)).toBeInTheDocument();
  expect(screen.getByText(/not invoices or a billing guarantee/)).toBeInTheDocument();
});

it("shows the primary VM's hourly rate, storage and projected monthly total with the 720-hour assumption", () => {
  const view = render(tab(workspace({ currency: "USD" })));
  const estimate = within(screen.getByRole("region", { name: "Projected cost" })).getByRole("group", { name: "Projected monthly cost" });
  expect(estimate).toHaveTextContent("VM rate$0.125/hour while running");
  expect(estimate).toHaveTextContent("Storage$1.25/month, even while the VM is stopped");
  expect(estimate).toHaveTextContent("Projected monthly total$91.25 if the VM runs 720 hours this month");
  expect(estimate).toHaveTextContent("never blocks creating or starting a VM");
  view.rerender(tab(workspace({ vms: [vm("sibling-one")] })));
  expect(screen.queryByRole("region", { name: "Projected cost" })).not.toBeInTheDocument();
});

it("offers disk tools per VM with supported resources, scoped to that VM's own disk", async () => {
  const job: FilesystemJob = { id: "scan", vmId: "sibling-one", generation: 3, kind: "disk_usage", state: "queued", path: "/workspace",
    createdAt: "2026-11-16T10:00:00Z", expiresAt: "2099-01-01T00:00:00Z", cancelRequested: false };
  vi.mocked(startDiskUsageScan).mockResolvedValue(job);
  vi.mocked(getFilesystemJob).mockReturnValue(new Promise(() => {}));
  render(tab(workspace({ vms: [
    vm("sibling-one", { resources: resources() }),
    vm("primary", { kind: "primary", workBranch: null, desiredState: "stopped", phase: "stopped", resources: resources() }),
    vm("sibling-two", { resources: resources({ state: "unsupported" }) }),
    vm("sibling-three"),
  ] })));
  const disks = screen.getAllByRole("region", { name: /^Disk tools for / });
  expect(disks.map(disk => disk.getAttribute("aria-label"))).toEqual(["Disk tools for Primary VM", "Disk tools for work/sibling-one"]);
  const primary = disks[0];
  expect(within(primary).getByRole("region", { name: "Disk usage for dev environment primary" })).toBeInTheDocument();
  expect(within(primary).getByRole("button", { name: "Fetch disk usage" })).toBeDisabled();
  expect(within(primary).getByText("Start the dev environment to fetch folder sizes.")).toBeInTheDocument();
  const sibling = disks[1];
  expect(within(sibling).getByRole("region", { name: "Disk usage for dev environment sibling-one" })).toBeInTheDocument();
  fireEvent.click(within(sibling).getByRole("button", { name: "Fetch disk usage" }));
  await waitFor(() => expect(startDiskUsageScan).toHaveBeenCalledExactlyOnceWith("workspace", "sibling-one", "org-a",
    expect.objectContaining({ generation: 3, targetToken: null })));
});

it("never offers disk tools to viewers", () => {
  render(tab(workspace({ vms: [vm("primary", { kind: "primary", resources: resources() })] }), false));
  expect(screen.queryByRole("heading", { name: "Disk usage" })).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Fetch disk usage" })).not.toBeInTheDocument();
  expect(screen.getByRole("region", { name: "VM costs" })).toBeInTheDocument();
  expect(startDiskUsageScan).not.toHaveBeenCalled();
});
