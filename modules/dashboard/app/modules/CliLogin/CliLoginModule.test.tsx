import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { MemoryRouter, useNavigate } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import { authFixture } from "../../test/accountFixtures";
import { approveCliLoginSession, getCliLoginSession } from "../../api/cliLoginApi";
import { CliLoginModule } from "./CliLoginModule";

vi.mock("../../api/cliLoginApi", () => ({
  approveCliLoginSession: vi.fn(),
  getCliLoginSession: vi.fn()
}));
afterEach(() => { cleanup(); vi.clearAllMocks(); });
const session = { sessionId: "request-1", status: "pending" as const, scopes: ["api_keys:write" as const], expiresAt: "2099-01-01T00:00:00Z" };
const secondOrganization = { id: "org-2", name: "Second organization", slug: "second", role: "admin" };
function multiOrgAuth() {
  const auth = authFixture();
  return authFixture({ profile: { ...auth.profile!, organizations: [...auth.profile!.organizations, secondOrganization] } });
}
function NextRequest() {
  const navigate = useNavigate();
  return <button onClick={() => navigate("/cli/login?session=request-2")}>Next request</button>;
}
function mount(auth = authFixture()) {
  const content = (value: typeof auth) => <AuthContext.Provider value={value}>
    <MemoryRouter initialEntries={["/cli/login?session=request-1"]}><CliLoginModule /><NextRequest /></MemoryRouter>
  </AuthContext.Provider>;
  const result = render(content(auth));
  return { ...result, updateAuth: (value: typeof auth) => result.rerender(content(value)) };
}
it("preserves explicit human consent instead of automatically approving after login", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  vi.mocked(approveCliLoginSession).mockResolvedValue({ ...session, status: "approved" });
  mount();
  expect(await screen.findByText("Status: pending")).toBeInTheDocument();
  expect(approveCliLoginSession).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole("button", { name: "Approve CLI login" }));
  await waitFor(() => expect(approveCliLoginSession).toHaveBeenCalledWith("request-1", "org-1"));
  expect(await screen.findByText("You can return to your terminal.")).toBeInTheDocument();
});
it("does not approve when the membership cannot write API keys", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  const auth = authFixture();
  const organization = { ...auth.selectedOrganization!, role: "developer" };
  mount(authFixture({ profile: { ...auth.profile!, organizations: [organization] }, selectedOrganization: organization }));
  await screen.findByText("Status: pending");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  expect(screen.getByRole("alert")).toHaveTextContent("do not have permission");
  expect(approveCliLoginSession).not.toHaveBeenCalled();
});

it.each(["fresh document", "dashboard already selected B"])("requires visible multi-org consent on %s without changing dashboard selection", async scenario => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  vi.mocked(approveCliLoginSession).mockResolvedValue({ ...session, status: "approved" });
  const auth = multiOrgAuth();
  if (scenario === "dashboard already selected B") auth.selectedOrganization = secondOrganization;
  mount(auth);
  await screen.findByText("Status: pending");
  const selector = screen.getByRole("combobox", { name: "Organization" });
  expect(selector).toHaveValue("");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  fireEvent.change(selector, { target: { value: "org-2" } });
  expect(screen.getByText("Authorize CLI access for Second organization.")).toBeInTheDocument();
  fireEvent.click(screen.getByRole("button", { name: "Approve CLI login" }));
  await waitFor(() => expect(approveCliLoginSession).toHaveBeenCalledWith("request-1", "org-2"));
  expect(auth.selectOrganization).not.toHaveBeenCalled();
});

it("automatically chooses the single eligible organization, excluding developer memberships", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  const auth = multiOrgAuth();
  const developer = { ...auth.profile!.organizations[0], role: "developer" };
  mount(authFixture({ profile: { ...auth.profile!, organizations: [developer, secondOrganization] }, selectedOrganization: developer }));
  await screen.findByText("Status: pending");
  expect(screen.queryByRole("option", { name: /Example/ })).not.toBeInTheDocument();
  expect(screen.getByRole("combobox", { name: "Organization" })).toHaveValue("org-2");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeEnabled();
});

it.each(["removed", "downgraded"])("invalidates a %s choice without silently approving another organization", async change => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  const auth = multiOrgAuth();
  const { updateAuth } = mount(auth);
  await screen.findByText("Status: pending");
  fireEvent.change(screen.getByRole("combobox"), { target: { value: "org-2" } });
  updateAuth(authFixture({ profile: { ...auth.profile!, organizations: change === "removed"
    ? [auth.profile!.organizations[0]]
    : [auth.profile!.organizations[0], { ...secondOrganization, role: "developer" }] } }));
  expect(screen.getByRole("combobox")).toHaveValue("");
  expect(screen.getByRole("alert")).toHaveTextContent("no longer eligible");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  expect(approveCliLoginSession).not.toHaveBeenCalled();
});

it("blocks stale profile approval while keeping the consent choice independent of dashboard selection", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  const auth = multiOrgAuth();
  const { updateAuth } = mount(auth);
  await screen.findByText("Status: pending");
  fireEvent.change(screen.getByRole("combobox"), { target: { value: "org-2" } });
  updateAuth({ ...auth, selectedOrganization: auth.profile!.organizations[0] });
  expect(screen.getByRole("combobox")).toHaveValue("org-2");
  updateAuth({ ...auth, profileError: "Account service is temporarily unavailable." });
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  expect(screen.getByRole("combobox")).toBeDisabled();
  expect(screen.getByRole("alert")).toHaveTextContent("Account service");
});

it("locks the visible choice during approval and permits retry after a server rejection", async () => {
  vi.mocked(getCliLoginSession).mockResolvedValue(session);
  let rejectApproval!: (error: Error) => void;
  vi.mocked(approveCliLoginSession).mockImplementation(() => new Promise((_, reject) => { rejectApproval = reject; }));
  mount(multiOrgAuth());
  await screen.findByText("Status: pending");
  fireEvent.change(screen.getByRole("combobox"), { target: { value: "org-2" } });
  fireEvent.click(screen.getByRole("button", { name: "Approve CLI login" }));
  expect(screen.getByRole("combobox")).toBeDisabled();
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  rejectApproval(new Error("Membership was revoked."));
  expect(await screen.findByRole("alert")).toHaveTextContent("Membership was revoked.");
  expect(screen.getByRole("combobox")).toHaveValue("org-2");
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeEnabled();
});

it("does not leave a loading spinner after a failed request", async () => {
  vi.mocked(getCliLoginSession).mockRejectedValue(new Error("Request not found."));
  mount();
  expect(await screen.findByRole("alert")).toHaveTextContent("Request not found.");
  expect(screen.queryByText("Loading CLI request")).not.toBeInTheDocument();
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
});

it.each(["approved", "exchanged", "expired"] as const)("does not offer consent for a %s request", async status => {
  vi.mocked(getCliLoginSession).mockResolvedValue({ ...session, status });
  mount();
  await screen.findByText(`Status: ${status}`);
  expect(screen.queryByRole("button", { name: "Approve CLI login" })).not.toBeInTheDocument();
  expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
});

it("resets consent on request navigation and ignores a stale loading result", async () => {
  vi.mocked(approveCliLoginSession).mockResolvedValue({ ...session, sessionId: "request-2", status: "approved" });
  let resolveFirst!: (value: typeof session) => void;
  let resolveSecond!: (value: typeof session) => void;
  vi.mocked(getCliLoginSession)
    .mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }))
    .mockImplementationOnce(() => new Promise(resolve => { resolveSecond = resolve; }));
  mount();
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  fireEvent.click(screen.getByRole("button", { name: "Next request" }));
  resolveFirst(session);
  await waitFor(() => expect(getCliLoginSession).toHaveBeenCalledWith("request-2"));
  expect(screen.getByRole("button", { name: "Approve CLI login" })).toBeDisabled();
  resolveSecond({ ...session, sessionId: "request-2" });
  await screen.findByText("Status: pending");
  fireEvent.click(screen.getByRole("button", { name: "Approve CLI login" }));
  await waitFor(() => expect(approveCliLoginSession).toHaveBeenCalledWith("request-2", "org-1"));
});
