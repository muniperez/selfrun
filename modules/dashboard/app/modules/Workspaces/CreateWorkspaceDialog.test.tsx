import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createDevelopmentWorkspace, mutateDevelopmentWorkspace } from "@/api/environmentsApi";
import { getWorkspaceSelections, workspaceOperation } from "@/api/workspacesApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { CreateWorkspaceDialog } from "./CreateWorkspaceDialog";

vi.mock("@/api/environmentsApi", () => ({ createDevelopmentWorkspace: vi.fn(), mutateDevelopmentWorkspace: vi.fn() }));
vi.mock("@/api/workspacesApi", () => ({ getWorkspaceSelections: vi.fn(), workspaceOperation: vi.fn() }));
const workspace = { id: "created-workspace", name: "API sandbox", desiredState: "stopped" } as DevelopmentWorkspace;
const props = { organizationId: "org-one", onCreated: vi.fn(), canManage: true };

async function openAndFill() {
  fireEvent.click(screen.getByRole("button", { name: "Create workspace" }));
  await screen.findByRole("option", { name: "Ubuntu · Coding tools" });
  fireEvent.change(screen.getByLabelText("Workspace name"), { target: { value: "API sandbox" } });
  fireEvent.change(screen.getByLabelText("Workspace image"), { target: { value: "image-one" } });
  fireEvent.change(screen.getByLabelText("Profile"), { target: { value: "Standard" } });
  fireEvent.change(screen.getByLabelText("Approved plan"), { target: { value: "plan-one" } });
  await waitFor(() => expect(screen.getByRole("button", { name: "Create stopped workspace" })).toBeEnabled());
}

describe("CreateWorkspaceDialog", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(getWorkspaceSelections).mockResolvedValue({
      available: true,
      options: [{ imageId: "image-one", imageName: "Ubuntu · Coding tools", profile: "Standard", planId: "plan-one", planName: "Dev small", providerAccountName: "Team Hetzner", region: "fsn1", sku: "cx23" }],
    });
    vi.mocked(createDevelopmentWorkspace).mockResolvedValue(workspace);
  });
  afterEach(cleanup);

  it("does not offer creation or request eligibility to read-only users", () => {
    render(<CreateWorkspaceDialog {...props} canManage={false} />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(getWorkspaceSelections).not.toHaveBeenCalled();
  });

  it("cancel and Escape dismiss without writes, reset entries, and restore trigger focus", async () => {
    render(<CreateWorkspaceDialog {...props} />);
    const trigger = screen.getByRole("button", { name: "Create workspace" });
    trigger.focus();
    await openAndFill();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(trigger).toHaveFocus();
    fireEvent.click(trigger);
    expect(screen.getByLabelText("Workspace name")).toHaveValue("");
    expect(screen.getByLabelText("Retained storage (GB)")).toHaveValue(20);
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape", code: "Escape" });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
  });

  it("validates backend name constraints and retained storage bounds", async () => {
    render(<CreateWorkspaceDialog {...props} />);
    await openAndFill();
    expect(screen.getByText(/Storage charges continue/)).toBeInTheDocument();
    for (const name of ["   ", "a".repeat(129), "bad\u0007name"]) {
      fireEvent.change(screen.getByLabelText("Workspace name"), { target: { value: name } });
      expect(screen.getByRole("button", { name: "Create stopped workspace" })).toBeDisabled();
      fireEvent.submit(screen.getByRole("form", { name: "Create stopped workspace" }));
      expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
    }
    fireEvent.change(screen.getByLabelText("Workspace name"), { target: { value: "Valid name" } });
    for (const size of ["", "9", "10001", "10.5"]) {
      fireEvent.change(screen.getByLabelText("Retained storage (GB)"), { target: { value: size } });
      expect(screen.getByRole("button", { name: "Create stopped workspace" })).toBeDisabled();
    }
    for (const size of ["10", "10000"]) {
      fireEvent.change(screen.getByLabelText("Retained storage (GB)"), { target: { value: size } });
      expect(screen.getByRole("button", { name: "Create stopped workspace" })).toBeEnabled();
    }
  });

  it("keeps entries and the idempotency key after ambiguous failure, prevents duplicate submission, and creates without starting", async () => {
    let reject!: (reason: Error) => void;
    vi.mocked(createDevelopmentWorkspace).mockImplementationOnce(() => new Promise((_, fail) => { reject = fail; }));
    render(<CreateWorkspaceDialog {...props} />);
    await openAndFill();
    const form = screen.getByRole("form", { name: "Create stopped workspace" });
    fireEvent.submit(form);
    fireEvent.submit(form);
    expect(createDevelopmentWorkspace).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Creating…" })).toBeDisabled();
    reject(new Error("Connection interrupted; result unknown."));
    expect(await screen.findByRole("alert")).toHaveTextContent("result unknown");
    expect(screen.getByLabelText("Workspace name")).toHaveValue("API sandbox");
    expect(screen.getByLabelText("Approved plan")).toHaveValue("plan-one");
    fireEvent.submit(form);
    await waitFor(() => expect(props.onCreated).toHaveBeenCalledWith(workspace));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[0][0]).toEqual(calls[1][0]);
    expect(calls[0][0]).toEqual({ organizationId: "org-one", name: "API sandbox", imageId: "image-one", profile: "Standard", planId: "plan-one", volumeSizeGb: 20, idempotencyKey: expect.any(String) });
    expect(mutateDevelopmentWorkspace).not.toHaveBeenCalled();
    expect(workspaceOperation).not.toHaveBeenCalled();
  });

  it("uses a new idempotency key when valid input changes after failure", async () => {
    vi.mocked(createDevelopmentWorkspace).mockRejectedValueOnce(new Error("Temporary failure"));
    render(<CreateWorkspaceDialog {...props} />);
    await openAndFill();
    fireEvent.click(screen.getByRole("button", { name: "Create stopped workspace" }));
    await screen.findByRole("alert");
    fireEvent.change(screen.getByLabelText("Retained storage (GB)"), { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Create stopped workspace" }));
    await waitFor(() => expect(props.onCreated).toHaveBeenCalled());
    const calls = vi.mocked(createDevelopmentWorkspace).mock.calls;
    expect(calls[0][0].idempotencyKey).not.toBe(calls[1][0].idempotencyKey);
  });

  it("blocks creation if refreshed eligibility removes the selected plan", async () => {
    render(<CreateWorkspaceDialog {...props} />);
    await openAndFill();
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce({ available: true, options: [] });
    fireEvent.click(screen.getByRole("button", { name: "Refresh choices" }));
    expect(screen.getByRole("button", { name: "Create stopped workspace" })).toBeDisabled();
    await screen.findByText(/No compatible choices/);
    fireEvent.submit(screen.getByRole("form", { name: "Create stopped workspace" }));
    expect(createDevelopmentWorkspace).not.toHaveBeenCalled();
  });
});
