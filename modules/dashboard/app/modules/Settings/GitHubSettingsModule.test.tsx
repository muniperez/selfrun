import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { apiRequest } from "@/api/apiClient";
import { GitHubSettingsModule } from "./GitHubSettingsModule";

vi.mock("@/api/apiClient", () => ({ apiRequest: vi.fn() }));
const auth = vi.hoisted(() => ({
  selectedOrganization: { id: "org", role: "owner" },
  selectOrganization: vi.fn(),
}));
vi.mock("@/core/auth/AuthProvider", () => ({ useAuth: () => auth }));
beforeEach(() => {
  vi.clearAllMocks(); window.history.replaceState(null, "", "/settings/github");
  auth.selectedOrganization = { id: "org", role: "owner" };
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "org", status: "not_configured" });
});
afterEach(cleanup);

it("offers private personal and organization registration only to administrators", async () => {
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByRole("button", { name: "Set up GitHub" })).toBeInTheDocument();
  expect(screen.getByLabelText("App owner type")).toHaveValue("User");
  fireEvent.change(screen.getByLabelText("App owner type"), { target: { value: "Organization" } });
  expect(screen.getByLabelText("App owner type")).toHaveValue("Organization");
  expect(screen.getByText(/Creating an App does not install it/)).toBeInTheDocument();
  expect(screen.getByText(/Workspace Git credentials/)).toBeInTheDocument();
});

it("does not offer registration to a developer", async () => {
  auth.selectedOrganization.role = "developer";
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  await screen.findByRole("status");
  expect(screen.queryByRole("button", { name: "Set up GitHub" })).not.toBeInTheDocument();
  expect(screen.getByText(/Only organization owners/)).toBeInTheDocument();
});

it("keeps app-created and installed states distinct", async () => {
  vi.mocked(apiRequest).mockResolvedValue({ organizationId: "org", status: "app_created", connectionId: "connection", appSlug: "fixture" });
  render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  expect(await screen.findByRole("button", { name: "Install App / select repositories" })).toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Verify repository access" })).not.toBeInTheDocument();
});

it("fetches repositories from the tenant connection and clears the previous tenant on switch", async () => {
  vi.mocked(apiRequest).mockImplementation(async path => path.endsWith("/repositories")
    ? [{ id: 1, fullName: "fixture/private", private: true }]
    : { organizationId: auth.selectedOrganization.id, status: "connected", connectionId: "connection" });
  const view = render(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  fireEvent.click(await screen.findByRole("button", { name: "Verify repository access" }));
  expect(await screen.findByText("fixture/private (private)")).toBeInTheDocument();
  expect(apiRequest).toHaveBeenCalledWith("/api/github-apps/connection/repositories");
  auth.selectedOrganization = { id: "other", role: "owner" };
  view.rerender(<MemoryRouter><GitHubSettingsModule /></MemoryRouter>);
  await waitFor(() => expect(screen.queryByText("fixture/private (private)")).not.toBeInTheDocument());
});
