import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue({ url: "https://connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" });
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
  expect(await screen.findByRole("alert")).toHaveTextContent("owner access");
  expect(popup.close).toHaveBeenCalled();
  expect(popup.location.replace).not.toHaveBeenCalled();
});

it.each([
  { organizationId: "other-org" }, { workspaceId: "other-workspace" }, { vmId: "replacement" },
  { generation: 4 }, { ready: false }, { revision: 2 },
])("never opens a stale authorization after its context changes: %j", async change => {
  const popup = { opener: null, close: vi.fn(), location: { replace: vi.fn() } };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  let resolve!: (result: { url: string; expiresAt: string }) => void;
  vi.mocked(getWorkspaceLaunchUrl).mockImplementationOnce(() => new Promise(done => { resolve = done; }));
  const view = render(<WorkspaceT3Connect {...props} ready revision={1} />);
  fireEvent.click(screen.getByRole("button", { name: "Manage T3 Connect" }));
  view.rerender(<WorkspaceT3Connect {...props} ready revision={1} {...change} />);
  await act(async () => resolve({ url: "https://connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" }));
  expect(popup.close).toHaveBeenCalled();
  expect(popup.location.replace).not.toHaveBeenCalled();
});

it.each([
  { url: "http://connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" },
  { url: "https://user:secret@connect.example/launch", expiresAt: "2099-01-01T00:00:00Z" },
  { url: "https://connect.example/launch", expiresAt: "2000-01-01T00:00:00Z" },
  { url: "https://connect.example/launch", expiresAt: "" },
])("rejects unsafe or expired management launches", async result => {
  const popup = { opener: null, close: vi.fn(), location: { replace: vi.fn() } };
  vi.spyOn(window, "open").mockReturnValue(popup as unknown as Window);
  vi.mocked(getWorkspaceLaunchUrl).mockResolvedValue(result);
  render(<WorkspaceT3Connect {...props} ready />);
  fireEvent.click(screen.getByRole("button", { name: "Manage T3 Connect" }));
  await screen.findByRole("alert");
  expect(popup.close).toHaveBeenCalled();
  expect(popup.location.replace).not.toHaveBeenCalled();
});
