import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { getWorkspaceLaunchUrl } from "@/api/workspacesApi";
import { WorkspaceT3Connect } from "./WorkspaceT3Connect";

vi.mock("@/api/workspacesApi", () => ({ getWorkspaceLaunchUrl: vi.fn() }));
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.resetAllMocks(); });
const props = { workspaceId: "workspace", vmId: "primary", organizationId: "org", generation: 3 };

it("does not link or start compute on view and cannot launch management while offline", () => {
  render(<WorkspaceT3Connect {...props} ready={false} />);
  expect(screen.getByRole("button", { name: "Manage T3 Connect" })).toBeDisabled();
  expect(getWorkspaceLaunchUrl).not.toHaveBeenCalled();
});

it("requests a separate owner-authorized Connect route for this exact generation", async () => {
  const popup = { opener: {}, location: { replace: vi.fn() }, close: vi.fn() };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url: "https://connect.example/launch", expiresAt: "" });
  render(<WorkspaceT3Connect {...props} ready />);
  fireEvent.click(screen.getByRole("button", { name: "Manage T3 Connect" }));
  await waitFor(() => expect(popup.location.replace).toHaveBeenCalledWith("https://connect.example/launch"));
  expect(getWorkspaceLaunchUrl).toHaveBeenCalledWith("workspace", "primary", "org", 3, "connect");
  expect(popup.opener).toBeNull();
});

it("does not navigate on unauthorized or unavailable management", async () => {
  const popup = { opener: null, close: vi.fn(), location: { replace: vi.fn() } };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  vi.mocked(getWorkspaceLaunchUrl).mockRejectedValue(new Error("not authorized"));
  render(<WorkspaceT3Connect {...props} ready />);
  fireEvent.click(screen.getByRole("button", { name: "Manage T3 Connect" }));
  expect(await screen.findByRole("alert")).toHaveTextContent("workspace-owner access");
  expect(popup.close).toHaveBeenCalled();
  expect(popup.location.replace).not.toHaveBeenCalled();
});
