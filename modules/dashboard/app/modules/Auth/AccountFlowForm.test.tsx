import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import { accountCapabilities, accountSession, authFixture, browserFlow, googleFlow, googleUrl, jsonResponse } from "../../test/accountFixtures";
import { AccountFlowForm } from "./AccountFlowForm";
import { SessionSettingsCard } from "./SessionSettingsCard";
import { routes } from "../../routes";
import { setCsrfToken } from "../../api/apiClient";
import { browserNavigation } from "../../api/accountApi";

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); setCsrfToken(null); });
function mount(kind: "login" | "settings" | "recovery" = "login", recovery = false, google = false, returnTo?: string) {
  const auth = authFixture({ capabilities: { ...accountCapabilities, emailRecovery: recovery, google } });
  render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind={kind} returnTo={returnTo} /></MemoryRouter></AuthContext.Provider>);
  return auth;
}

describe("account browser forms", () => {
  it("submits password and upstream CSRF once without persisting or displaying secrets", async () => {
    const storage = vi.spyOn(Storage.prototype, "setItem");
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount();
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
      csrf_token: "upstream-csrf", identifier: "operator@example.test", password: "synthetic-password", method: "password"
    });
    expect(storage).not.toHaveBeenCalled();
    expect(document.body.textContent).not.toContain("synthetic-password");
    expect(document.body.textContent).not.toContain(accountSession.csrfToken);
  });

  it("handles invalid credentials by reading the form, never replaying the password", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_invalid", message: "synthetic-password upstream diagnostic" } }, 400))
      .mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Check your details");
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3));
    expect(fetcher.mock.calls.filter(call => call[1].method === "POST")).toHaveLength(1);
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(document.body.textContent).not.toContain("synthetic-password");
  });

  it("requests a fresh sign-in form when the initial flow has expired", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_expired", message: "Expired" } }, 410))
      .mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount("login", false, false, "/cli/login?session=request-1");
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeEnabled();
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(fetcher.mock.calls[1][0]).toContain("/browser?returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
    expect(screen.queryByRole("button", { name: "Restart form" })).not.toBeInTheDocument();
  });

  it.each(["on the page", "on submission"])("refreshes a flow expired %s without replaying the password", async expiry => {
    const initialFlow = browserFlow();
    if (expiry === "on the page") initialFlow.expiresAt = "2000-01-01T00:00:00Z";
    const freshFlow = browserFlow();
    freshFlow.id = "22222222-3333-4444-8555-666666666666";
    freshFlow.ui.action = `/api/account/flows/login?flow=${freshFlow.id}`;
    freshFlow.ui.nodes[0].value = "refreshed-csrf";
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: initialFlow, session: null }));
    if (expiry === "on submission") {
      fetcher.mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_expired", message: "Expired" } }, 410));
    }
    fetcher.mockResolvedValueOnce(jsonResponse({ flow: freshFlow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount();
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "original-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Sign in" })).toBeEnabled();
      expect(screen.getByLabelText("Password")).toHaveValue("");
    });
    expect(fetcher.mock.calls.filter(call => call[1].method === "POST")).toHaveLength(expiry === "on submission" ? 1 : 0);
    expect(auth.refreshSession).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "new-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    const [url, request] = fetcher.mock.calls.at(-1)!;
    expect(url).toContain(`flow=${freshFlow.id}`);
    expect(JSON.parse(request.body)).toMatchObject({ csrf_token: "refreshed-csrf", password: "new-password" });
  });

  it("offers a manual retry if fetching a fresh sign-in form fails", async () => {
    const expired = () => jsonResponse({ error: { code: "auth_flow_expired", message: "Expired" } }, 410);
    const fetcher = vi.fn().mockResolvedValueOnce(expired()).mockResolvedValueOnce(expired())
      .mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }));
    vi.stubGlobal("fetch", fetcher);
    mount();
    expect(await screen.findByRole("alert")).toHaveTextContent("Restart");
    expect(fetcher).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: "Restart form" }));
    expect(await screen.findByRole("button", { name: "Sign in" })).toBeEnabled();
  });

  it("does not render signup, Google or recovery unless explicitly enabled", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: browserFlow(), session: null })));
    mount();
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
    expect(screen.queryByText("Forgot password?")).not.toBeInTheDocument();
    expect(routes.flatMap(route => route.children ?? []).some(route => route.path === "/sign-up")).toBe(false);
  });

  it("does not start a disabled recovery flow", () => {
    const fetcher = vi.fn();
    vi.stubGlobal("fetch", fetcher);
    mount("recovery");
    expect(screen.getByText(/not enabled/)).toBeInTheDocument();
    expect(fetcher).not.toHaveBeenCalled();
  });

  it("supports enabled recovery email, resend, invalid code alert, and code redemption steps", async () => {
    const flow = browserFlow("recovery");
    flow.ui.nodes = [flow.ui.nodes[0],
      { group: "code", type: "email", name: "email", value: null, required: true, messages: [] },
      { group: "code", type: "submit", name: "method", value: "code", required: false, messages: [] }];
    const codeFlow = {
      ...flow,
      ui: {
        ...flow.ui,
        messages: [{ id: 1060003, text: "Continue with the account form.", type: "info" }],
        nodes: [
          flow.ui.nodes[0],
          { group: "code", type: "text", name: "code", value: null, required: true, messages: [] },
          { group: "code", type: "hidden", name: "method", value: "code", required: false, messages: [] },
          flow.ui.nodes[2],
          { group: "code", type: "submit", name: "email", value: "operator@example.test", required: false, messages: [] }
        ]
      }
    };
    const invalidCodeFlow = {
      ...codeFlow,
      ui: { ...codeFlow.ui, messages: [{ id: 4060006, text: "Check your account details.", type: "error" }] }
    };
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: codeFlow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: codeFlow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: invalidCodeFlow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    const auth = mount("recovery", true);
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.click(screen.getByRole("button", { name: "Continue recovery" }));
    expect(await screen.findByLabelText("Recovery code")).toHaveValue("");
    const resend = screen.getByRole("button", { name: "Resend code" });
    expect(resend).toHaveAttribute("type", "button");
    fireEvent.click(resend);
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3));
    expect(JSON.parse(fetcher.mock.calls[2][1].body)).toEqual({
      csrf_token: "upstream-csrf", method: "code", email: "operator@example.test"
    });
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "000000" } });
    fireEvent.click(screen.getByRole("button", { name: "Continue recovery" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Check your account details");
    expect(screen.getByLabelText("Recovery code")).toHaveValue("");
    expect(auth.refreshSession).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText("Recovery code"), { target: { value: "123456" } });
    fireEvent.click(screen.getByRole("button", { name: "Continue recovery" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(JSON.parse(fetcher.mock.calls[4][1].body)).toEqual({
      csrf_token: "upstream-csrf", code: "123456", method: "code"
    });
  });

  it("renders and submits both profile name parts from the real Kratos settings shape", async () => {
    const flow = browserFlow("settings");
    flow.ui.nodes = [
      flow.ui.nodes[0],
      { group: "profile", type: "text", name: "traits.name.first", value: "Synthetic", required: false, messages: [] },
      { group: "profile", type: "text", name: "traits.name.last", value: "Operator", required: false, messages: [] },
      { group: "profile", type: "submit", name: "method", value: "profile", required: false, messages: [] }
    ];
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    mount("settings");
    expect(await screen.findByLabelText("First name")).toHaveValue("Synthetic");
    fireEvent.change(screen.getByLabelText("Last name"), { target: { value: "Lovelace" } });
    fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2));
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({
      csrf_token: "upstream-csrf", "traits.name.first": "Synthetic", "traits.name.last": "Lovelace", method: "profile"
    });
  });

  it("renders enabled recovery nodes safely and sends session CSRF for password settings", async () => {
    const flow = browserFlow("settings");
    flow.ui.nodes[2].value = "must-not-render";
    flow.ui.nodes.push({ group: "default", name: "injected", type: "script", value: "<script>alert(1)</script>", required: false, messages: [] });
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    mount("settings");
    fireEvent.change(await screen.findByLabelText("Email"), { target: { value: "operator@example.test" } });
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(document.body.innerHTML).not.toContain("must-not-render");
    expect(document.body.innerHTML).not.toContain("<script>");
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-new-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));
    expect(await screen.findByText("Password updated.")).toBeInTheDocument();
    expect(fetcher.mock.calls[1][1].headers).toHaveProperty("X-Minicloud-CSRF", "synthetic-csrf");
  });

  it("clears a rejected password, reloads the same form, and only submits again on explicit retry", async () => {
    const flow = browserFlow("settings");
    flow.ui.nodes = flow.ui.nodes.filter(node => node.name !== "identifier");
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_password_rejected", message: "private diagnostic" } }, 400))
      .mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    const auth = mount("settings");
    fireEvent.change(await screen.findByLabelText("Password"), { target: { value: "rejected-password" } });
    const save = screen.getByRole("button", { name: "Save password" });
    fireEvent.click(save);

    expect(await screen.findByRole("alert")).toHaveTextContent("Choose a different password");
    await waitFor(() => expect(save).toBeEnabled());
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(fetcher.mock.calls[2][0]).toContain(`/api/account/flows/settings?id=${flow.id}`);
    expect(fetcher.mock.calls.filter(call => call[1].method === "POST")).toHaveLength(1);
    expect(auth.refreshSession).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "replacement-password" } });
    fireEvent.click(save);
    expect(await screen.findByText("Password updated.")).toBeInTheDocument();
  });

  it.each([
    [410, "auth_flow_expired", "This form expired. Restart to try again."],
    [403, "session_refresh_required", "Log out and sign in again before updating account settings."],
    [503, "auth_unavailable", "Account service is unavailable. Retry when it is available."]
  ])("shows an actionable settings error for %s without replaying the password", async (status, code, message) => {
    const flow = browserFlow("settings");
    flow.ui.nodes = flow.ui.nodes.filter(node => node.name !== "identifier");
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow, session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code, message: "private diagnostic" } }, status))
      .mockResolvedValueOnce(jsonResponse({ flow, session: null }));
    const assign = vi.spyOn(browserNavigation, "assign").mockImplementation(() => {});
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");
    const auth = mount("settings");
    fireEvent.change(await screen.findByLabelText("Password"), { target: { value: "synthetic-new-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(message);
    expect(screen.getByLabelText("Password")).toHaveValue("");
    expect(screen.getByRole("button", { name: "Save password" })).toBeDisabled();
    expect(fetcher).toHaveBeenCalledTimes(2);
    expect(auth.refreshSession).not.toHaveBeenCalled();
    expect(assign).not.toHaveBeenCalled();
    if (status === 410) {
      fireEvent.click(screen.getByRole("button", { name: "Restart form" }));
      await waitFor(() => expect(screen.getByRole("button", { name: "Save password" })).toBeEnabled());
      expect(fetcher.mock.calls[2][0]).toContain("/api/account/flows/settings/browser?");
      expect(fetcher.mock.calls.filter(call => call[1].method === "POST")).toHaveLength(1);
    }
  });

  it("hides Google unless both runtime capabilities and the flow expose the provider", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ flow: googleFlow(), session: null })));
    mount("login", false, false);
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
    cleanup();
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: browserFlow(), session: null })));
    mount("login", false, true);
    await screen.findByLabelText("Email");
    expect(screen.queryByText(/Google/)).not.toBeInTheDocument();
  });

  it("navigates to the validated provider only after an explicit click", async () => {
    const assign = vi.spyOn(browserNavigation, "assign").mockImplementation(() => {});
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: googleFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ provider: "google", redirectUrl: googleUrl }));
    vi.stubGlobal("fetch", fetcher);
    mount("login", false, true, "/cli/login?session=request-1");
    const button = await screen.findByRole("button", { name: "Continue with Google" });
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(assign).not.toHaveBeenCalled();
    fireEvent.click(button);
    await waitFor(() => expect(assign).toHaveBeenCalledWith(googleUrl));
    expect(fetcher.mock.calls[1][0]).toContain("/api/account/flows/login/oidc?flow=");
    expect(fetcher.mock.calls[1][0]).toContain("returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toEqual({ csrf_token: "upstream-csrf", provider: "google" });
  });

  it("never navigates to an unexpected provider destination", async () => {
    const assign = vi.spyOn(browserNavigation, "assign").mockImplementation(() => {});
    vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(jsonResponse({ flow: googleFlow("settings"), session: null }))
      .mockResolvedValueOnce(jsonResponse({ provider: "google", redirectUrl: "https://evil.test/o/oauth2/v2/auth" })));
    setCsrfToken("synthetic-csrf");
    mount("settings", false, true);
    fireEvent.click(await screen.findByRole("button", { name: "Link Google account" }));
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(assign).not.toHaveBeenCalled();
  });
});

describe("keep me signed in", () => {
  afterEach(() => window.localStorage.clear());

  it("is offered only when enabled and sends the explicit choice without posting it as an upstream field", async () => {
    const fetcher = vi.fn().mockResolvedValueOnce(jsonResponse({ flow: browserFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: { ...accountSession, keepSignedIn: true } }));
    vi.stubGlobal("fetch", fetcher);
    const auth = authFixture({ capabilities: { ...accountCapabilities, keepSignedIn: true } });
    render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind="login" /></MemoryRouter></AuthContext.Provider>);
    const checkbox = await screen.findByRole("checkbox", { name: "Keep me signed in on this device" });
    expect(checkbox).not.toBeChecked();
    fireEvent.click(checkbox);
    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(fetcher.mock.calls[1][0]).toMatch(/\/api\/account\/flows\/login\?flow=[0-9a-f-]+&keepSignedIn=true$/);
    expect(Object.keys(JSON.parse(fetcher.mock.calls[1][1].body))).toEqual(["csrf_token", "identifier", "password", "method"]);
    expect(window.localStorage.getItem("minicloud.keepSignedIn")).toBe("1");
  });

  it("defaults from this browser's last choice and is absent when the operator disables it", async () => {
    window.localStorage.setItem("minicloud.keepSignedIn", "1");
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: browserFlow(), session: null })));
    const auth = authFixture({ capabilities: { ...accountCapabilities, keepSignedIn: true } });
    const view = render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind="login" /></MemoryRouter></AuthContext.Provider>);
    expect(await screen.findByRole("checkbox", { name: "Keep me signed in on this device" })).toBeChecked();
    view.unmount();
    mount();
    expect(await screen.findByLabelText("Email")).toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
  });

  it("changes the current session preference from account settings and reloads the session", async () => {
    setCsrfToken("synthetic-csrf");
    const fetcher = vi.fn(async (input: string) => input.endsWith("/session/preference")
      ? jsonResponse({ ...accountSession, keepSignedIn: true, keepSignedInPreference: true })
      : jsonResponse({ flow: browserFlow("settings"), session: null }));
    vi.stubGlobal("fetch", fetcher);
    const auth = authFixture({ capabilities: { ...accountCapabilities, keepSignedIn: true }, session: { ...accountSession, keepSignedIn: false } });
    render(<AuthContext.Provider value={auth}><MemoryRouter><SessionSettingsCard /></MemoryRouter></AuthContext.Provider>);
    fireEvent.click(screen.getByRole("checkbox", { name: "Keep me signed in on this device" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toMatch(/\/api\/account\/session\/preference$/);
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ keepSignedIn: true });
    expect((init.headers as Record<string, string>)["X-Minicloud-CSRF"]).toBe("synthetic-csrf");
    expect(window.localStorage.getItem("minicloud.keepSignedIn")).toBe("1");
  });
});
