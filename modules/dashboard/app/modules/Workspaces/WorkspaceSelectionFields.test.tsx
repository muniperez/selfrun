import { useState } from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { getWorkspaceSelections, type WorkspaceSelectionOption, type WorkspaceSelections } from "@/api/workspacesApi";
import { WorkspaceSelectionFields, type WorkspaceSelection } from "./WorkspaceSelectionFields";

vi.mock("@/api/workspacesApi", () => ({ getWorkspaceSelections: vi.fn() }));
const choices: WorkspaceSelectionOption[] = [
  { imageId: "image-one", imageName: "Ubuntu · Coding tools", profile: "Standard", planId: "plan-one", planName: "Dev small", providerAccountName: "Team Hetzner", region: "fsn1", sku: "cx23",
    agents: [{ id: "claude", name: "Claude Code", version: "2.1.328" }] },
  { imageId: "image-one", imageName: "Ubuntu · Coding tools", profile: "Heavy", planId: "plan-two", planName: "Dev large", providerAccountName: "Team Hetzner", region: "fsn1", sku: "cx43" },
  { imageId: "image-two", imageName: "Debian · Tools", profile: "Heavy", planId: "plan-two", planName: "Dev large", providerAccountName: "Team Hetzner", region: "fsn1", sku: "cx43" },
];
const selected = { imageId: "image-one", profile: "Standard", planId: "plan-one" };

function Fields({ organizationId = "org-one", workspaceId, initial = selected, disabled = false, onReadyChange }: {
  organizationId?: string; workspaceId?: string; initial?: WorkspaceSelection; disabled?: boolean; onReadyChange?: (ready: boolean) => void;
}) {
  const [value, onChange] = useState(initial);
  return <WorkspaceSelectionFields {...{ organizationId, workspaceId, value, onChange, disabled, onReadyChange }} />;
}

describe("WorkspaceSelectionFields", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(getWorkspaceSelections).mockResolvedValue({ available: true, options: choices });
  });
  afterEach(cleanup);

  it("uses named qualified choices and clears incompatible dependent selections", async () => {
    render(<Fields />);
    expect(await screen.findByRole("option", { name: "Ubuntu · Coding tools" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: /Dev small · Team Hetzner · fsn1 · cx23/ })).toBeInTheDocument();
    expect(screen.getByText("Agents: Claude Code 2.1.328")).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("Size"), { target: { value: "Heavy" } });
    expect(screen.getByLabelText("Plan")).toHaveValue("");
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "plan-two" } });
    fireEvent.change(screen.getByLabelText("Image"), { target: { value: "image-two" } });
    expect(screen.getByLabelText("Size")).toHaveValue("Heavy");
    expect(screen.getByLabelText("Plan")).toHaveValue("plan-two");
    fireEvent.change(screen.getByLabelText("Image"), { target: { value: "image-one" } });
    fireEvent.change(screen.getByLabelText("Size"), { target: { value: "Standard" } });
    fireEvent.change(screen.getByLabelText("Plan"), { target: { value: "plan-one" } });
    fireEvent.change(screen.getByLabelText("Image"), { target: { value: "image-two" } });
    expect(screen.getByLabelText("Size")).toHaveValue("");
    expect(screen.getByLabelText("Plan")).toHaveValue("");
  });

  it("reports loading, retries failures, and clears stale eligibility after a refresh", async () => {
    const onReadyChange = vi.fn();
    render(<Fields onReadyChange={onReadyChange} />);
    expect(screen.getByRole("status")).toHaveTextContent("Loading");
    await waitFor(() => expect(onReadyChange).toHaveBeenLastCalledWith(true));
    vi.mocked(getWorkspaceSelections).mockRejectedValueOnce(new Error("Service unavailable"));
    fireEvent.click(screen.getByRole("button", { name: "Reload options" }));
    expect(onReadyChange).toHaveBeenLastCalledWith(false);
    expect(await screen.findByRole("alert")).toHaveTextContent("Service unavailable");
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce({ available: true, options: [choices[1]] });
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    await screen.findByRole("option", { name: "Ubuntu · Coding tools" });
    await waitFor(() => expect(screen.getByLabelText("Size")).toHaveValue(""));
    expect(screen.getByLabelText("Plan")).toHaveValue("");
    expect(onReadyChange).toHaveBeenLastCalledWith(false);
  });

  it("distinguishes disabled provisioning from no compatible choices", async () => {
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce({ available: false, unavailableReason: "Ask an operator to enable workspaces.", options: [] });
    render(<Fields />);
    expect(await screen.findByText("Ask an operator to enable workspaces.")).toBeInTheDocument();
    expect(screen.getByLabelText("Image")).toBeDisabled();
    vi.mocked(getWorkspaceSelections).mockResolvedValueOnce({ available: true, options: [] });
    fireEvent.click(screen.getByRole("button", { name: "Reload options" }));
    expect(await screen.findByText(/No matching options/)).toBeInTheDocument();
  });

  it("ignores an old organization's response and scopes workspace eligibility", async () => {
    let resolveOld!: (result: WorkspaceSelections) => void;
    vi.mocked(getWorkspaceSelections).mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; }));
    const view = render(<Fields organizationId="old-org" workspaceId="workspace-one" />);
    view.rerender(<Fields organizationId="new-org" workspaceId="workspace-two" />);
    await screen.findByRole("option", { name: "Ubuntu · Coding tools" });
    expect(getWorkspaceSelections).toHaveBeenLastCalledWith("new-org", "workspace-two", expect.any(AbortSignal));
    resolveOld({ available: false, unavailableReason: "Old disabled state", options: [] });
    await waitFor(() => expect(screen.queryByText("Old disabled state")).not.toBeInTheDocument());
    expect(screen.getByLabelText("Image")).toHaveValue("image-one");
  });

  it("locks all controls while an operation is pending", async () => {
    render(<Fields disabled />);
    await screen.findByRole("option", { name: "Ubuntu · Coding tools" });
    for (const control of screen.getAllByRole("combobox")) expect(control).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reload options" })).toBeDisabled();
  });
});
