import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { updateWorkspaceIdleTimeout } from "@/api/workspacesApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceIdleTimeoutSettings } from "./WorkspaceIdleTimeoutSettings";

vi.mock("@/api/workspacesApi", () => ({ updateWorkspaceIdleTimeout: vi.fn() }));

const workspace: DevelopmentWorkspace = {
  id: "workspace-a", organizationId: "org-a", name: "Fixture", imageId: "image", profile: "standard",
  planId: "plan", providerAccountId: "account", region: "region", sku: "sku", volumeSizeGb: 20,
  desiredState: "stopped", phase: "stopped", generation: 0, estimatedMonthlyCompute: 0,
  estimatedMonthlyStorage: 2, currency: "USD", revision: 1, createdAt: "", updatedAt: "", vms: [],
  idleTimeoutMinutes: null, effectiveIdleTimeoutMinutes: 360,
};

const saveButton = () => screen.getByRole("button", { name: "Save idle timeout" });
const timeoutInput = () => screen.getByLabelText("Idle timeout (minutes)");
const refresh = vi.fn(async () => {});

beforeEach(() => vi.resetAllMocks());
afterEach(cleanup);

it("shows the effective duration and cost disclosure without editing controls for readers", () => {
  render(<WorkspaceIdleTimeoutSettings item={workspace} organizationId="org-a" canManage={false} refresh={refresh} />);
  expect(screen.getByText("6 hours")).toBeInTheDocument();
  expect(screen.getByText(/not a maximum VM lifetime/)).toBeInTheDocument();
  expect(screen.getByText(/retains primary storage/)).toHaveTextContent("charges continue until deletion is confirmed");
  expect(screen.queryByLabelText("Idle timeout (minutes)")).not.toBeInTheDocument();
});

it("saves an explicit duration, displays the server response, and can restore inheritance", async () => {
  const saved = { ...workspace, idleTimeoutMinutes: 90, effectiveIdleTimeoutMinutes: 90, revision: 2 };
  vi.mocked(updateWorkspaceIdleTimeout).mockResolvedValueOnce(saved)
    .mockResolvedValueOnce({ ...workspace, effectiveIdleTimeoutMinutes: 30, revision: 3 });
  const view = render(<WorkspaceIdleTimeoutSettings item={workspace} organizationId="org-a" canManage refresh={refresh} />);
  fireEvent.change(timeoutInput(), { target: { value: "90" } });
  fireEvent.click(saveButton());
  expect(await screen.findByText("1 hour 30 minutes")).toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("Idle timeout saved.");
  expect(updateWorkspaceIdleTimeout).toHaveBeenLastCalledWith("workspace-a", "org-a", 90);
  expect(refresh).toHaveBeenCalledTimes(1);

  view.rerender(<WorkspaceIdleTimeoutSettings item={saved} organizationId="org-a" canManage refresh={refresh} />);
  fireEvent.click(screen.getByRole("button", { name: "Use server default" }));
  expect(await screen.findByText("30 minutes")).toBeInTheDocument();
  expect(updateWorkspaceIdleTimeout).toHaveBeenLastCalledWith("workspace-a", "org-a", null);
});

it("rejects invalid durations without calling the API and keeps a failed draft available for retry", async () => {
  vi.mocked(updateWorkspaceIdleTimeout).mockRejectedValueOnce(new Error("Workspace changed; reload and retry."));
  render(<WorkspaceIdleTimeoutSettings item={workspace} organizationId="org-a" canManage refresh={refresh} />);
  for (const invalid of ["", "0", "-1", "1.5", "10081", "999999999999999999999"]) {
    fireEvent.change(timeoutInput(), { target: { value: invalid } });
    fireEvent.click(saveButton());
    expect(screen.getByRole("alert")).toHaveTextContent(/Enter a whole number/);
  }
  expect(updateWorkspaceIdleTimeout).not.toHaveBeenCalled();

  fireEvent.change(timeoutInput(), { target: { value: "1" } });
  fireEvent.click(saveButton());
  expect(await screen.findByRole("alert")).toHaveTextContent("Workspace changed; reload and retry.");
  await waitFor(() => expect(timeoutInput()).toBeEnabled());
  expect(timeoutInput()).toHaveValue("1");
  expect(screen.getByText("6 hours")).toBeInTheDocument();
});
