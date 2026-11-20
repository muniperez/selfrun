import { cleanup, render, screen, within } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import type { DevelopmentWorkspaceVmResources } from "@/api/workspaceFilesystemApi";
import type { DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { formatBytes, WorkspaceVmResources } from "./WorkspaceVmResources";

const GiB = 1024 ** 3;

function vm(resources: Partial<DevelopmentWorkspaceVmResources> | null): DevelopmentWorkspaceVm {
  return {
    id: "vm-1", kind: "primary", generation: 2, desiredState: "running", phase: "ready",
    imageId: "image", profile: "Standard", planId: "plan", providerAccountId: "provider", region: "region", sku: "sku",
    pushAuthorized: true, ignoredDataAcknowledged: true, gitStatus: "pending", computeHourlyRate: 0.1,
    estimatedMonthlyCompute: 72, computeReserved: true, revision: 1,
    resources: resources && {
      state: "current", observedAt: "2026-01-01T10:00:00Z", receivedAt: "2026-01-01T10:00:01Z",
      cpu: { logicalCount: 4, utilizationPercent: 37.4, windowSeconds: 15 },
      memory: { totalBytes: 8 * GiB, availableBytes: 3 * GiB },
      filesystems: [{ paths: ["/", "/workspace"], totalBytes: 100 * GiB, freeBytes: 45 * GiB, availableBytes: 40 * GiB,
        inodesTotal: 1000, inodesAvailable: 600, nestedMountsExcluded: 1 }],
      unavailable: {}, diskUsageSupported: true, directoryDeletionSupported: true, ...resources,
    },
  };
}

afterEach(cleanup);

it("shows current CPU, memory, disk and file slot availability with the sample time", () => {
  render(<WorkspaceVmResources vm={vm({})} />);
  const section = screen.getByRole("region", { name: "Resources for dev environment vm-1" });
  expect(within(section).getByText("Current")).toBeTruthy();
  expect(within(section).getByText("37% of 4 cores over 15s")).toBeTruthy();
  expect(within(section).getByText("3.0 GiB available of 8.0 GiB")).toBeTruthy();
  expect(section.textContent).toContain("40.0 GiB available of 100 GiB");
  expect(section.textContent).toContain("600 of 1,000 file slots free");
  expect(section.textContent).toContain("Excludes 1 separately mounted folder");
  expect(section.textContent).toContain(new Date("2026-01-01T10:00:00Z").toLocaleString());
});

it("labels the retained volume and scratch storage when the image reports storage tiers", () => {
  render(<WorkspaceVmResources vm={vm({ filesystems: [
    { paths: ["/"], roles: ["system", "scratch"], totalBytes: 30 * GiB, freeBytes: 20 * GiB, availableBytes: 18 * GiB },
    { paths: ["/workspace"], roles: ["workspace"], totalBytes: 100 * GiB, freeBytes: 45 * GiB, availableBytes: 40 * GiB,
      nestedMountsExcluded: 0, scratchBindMounts: 2 }] })} />);
  const section = screen.getByRole("region", { name: "Resources for dev environment vm-1" });
  expect(within(section).getByText("System disk /")).toBeTruthy();
  expect(within(section).getByText("Retained volume /workspace")).toBeTruthy();
  expect(section.textContent).toContain("Also holds scratch storage: caches and build outputs");
  expect(section.textContent).toContain("2 build output folders are kept on scratch storage, not counted here");
  expect(section.textContent).not.toContain("separately mounted");
});

it("marks stale and stopped samples instead of presenting them as current", () => {
  const { rerender } = render(<WorkspaceVmResources vm={vm({ state: "stale" })} />);
  expect(screen.getByText("Stale")).toBeTruthy();
  expect(screen.getByText(/No fresh sample for over 2 minutes/)).toBeTruthy();
  rerender(<WorkspaceVmResources vm={vm({ state: "historical", cpu: { logicalCount: 4 } })} />);
  expect(screen.getByText("Last known")).toBeTruthy();
  expect(screen.getByText(/is not running/)).toBeTruthy();
  expect(screen.getByText("4 cores · utilization not yet measured")).toBeTruthy();
});

it("explains old images that do not report resources and renders nothing without a sample", () => {
  const { rerender, container } = render(<WorkspaceVmResources vm={vm({ state: "unsupported", filesystems: [], cpu: null, memory: null })} />);
  expect(screen.getByText("Not supported")).toBeTruthy();
  expect(screen.queryByText(/available of/)).toBeNull();
  rerender(<WorkspaceVmResources vm={vm(null)} />);
  expect(container.textContent).toBe("");
});

it("formats byte sizes without reporting unknown values as zero", () => {
  expect(formatBytes(null)).toBe("Unknown");
  expect(formatBytes(512)).toBe("512 B");
  expect(formatBytes(1536)).toBe("1.5 KiB");
});
