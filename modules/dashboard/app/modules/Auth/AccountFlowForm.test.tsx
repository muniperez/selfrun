import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../core/auth/AuthProvider";
import {
  accountCapabilities, accountSession, authFixture, browserFlow, googleFlow, googleUrl, jsonResponse,
  passkeyLoginFlow, passkeySettingsFlow
} from "../../test/accountFixtures";
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

describe("passkey sign-in and settings", () => {
  function stubWebAuthn(
    create = vi.fn(),
    get = vi.fn()
  ) {
    vi.stubGlobal("PublicKeyCredential", class {});
    Object.defineProperty(window, "isSecureContext", { value: true, configurable: true });
    Object.defineProperty(navigator, "credentials", {
      value: { create, get },
      configurable: true
    });
    return { create, get };
  }

  function mockAssertionCredential() {
    const rawId = new Uint8Array([1, 2, 3, 4]).buffer;
    return {
      id: "AQIDBA",
      rawId,
      type: "public-key",
      response: {
        authenticatorData: new Uint8Array([10, 11]).buffer,
        clientDataJSON: new Uint8Array([12, 13]).buffer,
        signature: new Uint8Array([14, 15]).buffer,
        userHandle: new Uint8Array([16, 17]).buffer
      }
    };
  }

  function mockAttestationCredential() {
    const rawId = new Uint8Array([5, 6, 7, 8]).buffer;
    return {
      id: "BQYHCA",
      rawId,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        attestationObject: new Uint8Array([20, 21]).buffer,
        clientDataJSON: new Uint8Array([22, 23]).buffer,
        getTransports: () => ["internal"]
      }
    };
  }

  function mountPasskey(
    kind: "login" | "settings" = "login",
    overrides: Partial<Parameters<typeof authFixture>[0]> = {},
    passkeyCapability = true
  ) {
    const auth = authFixture({
      capabilities: { ...accountCapabilities, passkey: passkeyCapability },
      ...overrides
    });
    render(<AuthContext.Provider value={auth}><MemoryRouter><AccountFlowForm kind={kind} /></MemoryRouter></AuthContext.Provider>);
    return auth;
  }

  afterEach(() => {
    window.localStorage.clear();
    Object.defineProperty(navigator, "credentials", { value: undefined, configurable: true });
  });

  it("hides passkey login unless runtime capability, flow options, and WebAuthn support are all present", async () => {
    stubWebAuthn();
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: passkeyLoginFlow(), session: null })));
    mountPasskey("login", {}, false);
    await screen.findByLabelText("Email");
    expect(screen.queryByRole("button", { name: "Sign in with a passkey" })).not.toBeInTheDocument();
    cleanup();

    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: browserFlow("login"), session: null })));
    mountPasskey("login", {}, true);
    await screen.findByLabelText("Email");
    expect(screen.queryByRole("button", { name: "Sign in with a passkey" })).not.toBeInTheDocument();
    cleanup();

    vi.stubGlobal("PublicKeyCredential", undefined);
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: passkeyLoginFlow(), session: null })));
    mountPasskey("login", {}, true);
    await screen.findByLabelText("Email");
    expect(screen.queryByRole("button", { name: "Sign in with a passkey" })).not.toBeInTheDocument();
  });

  it("completes passwordless passkey sign-in, respects keepSignedIn, and refreshes the session", async () => {
    const { get } = stubWebAuthn(vi.fn(), vi.fn().mockResolvedValueOnce(mockAssertionCredential()));
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow: passkeyLoginFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: { ...accountSession, keepSignedIn: true } }));
    vi.stubGlobal("fetch", fetcher);

    const auth = mountPasskey("login", {
      capabilities: { ...accountCapabilities, passkey: true, keepSignedIn: true }
    });
    const checkbox = await screen.findByRole("checkbox", { name: "Keep me signed in on this device" });
    fireEvent.click(checkbox);
    fireEvent.click(screen.getByRole("button", { name: "Sign in with a passkey" }));

    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(get).toHaveBeenCalledOnce();
    const [url, init] = fetcher.mock.calls[1];
    expect(url).toMatch(/\/api\/account\/flows\/login\?flow=[0-9a-f-]+&keepSignedIn=true$/);
    expect(init.headers).not.toHaveProperty("X-Minicloud-CSRF");
    const body = JSON.parse(init.body);
    expect(body.csrf_token).toBe("upstream-csrf");
    expect(body.method).toBe("passkey");
    expect(JSON.parse(body.passkey_login)).toMatchObject({ id: "AQIDBA", type: "public-key" });
    expect(window.localStorage.getItem("minicloud.keepSignedIn")).toBe("1");
  });

  it("handles cancelled passkey sign-in without posting and keeps password fallback usable", async () => {
    stubWebAuthn(vi.fn(), vi.fn().mockRejectedValueOnce(new DOMException("User cancelled", "NotAllowedError")));
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow: passkeyLoginFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);

    const auth = mountPasskey("login");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with a passkey" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Passkey sign-in was cancelled");
    expect(fetcher.mock.calls.filter(call => call[1]?.method === "POST")).toHaveLength(0);

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toMatchObject({ method: "password" });
  });

  it("reloads a fresh login flow when a removed passkey is rejected and preserves password sign-in", async () => {
    stubWebAuthn(vi.fn(), vi.fn().mockResolvedValueOnce(mockAssertionCredential()));
    const fresh = passkeyLoginFlow("22222222-3333-4444-8555-666666666666");
    fresh.ui.nodes[0].value = "fresh-csrf";
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow: passkeyLoginFlow(), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "auth_flow_invalid", message: "invalid" } }, 400))
      .mockResolvedValueOnce(jsonResponse({ flow: fresh, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }));
    vi.stubGlobal("fetch", fetcher);

    const auth = mountPasskey("login");
    fireEvent.click(await screen.findByRole("button", { name: "Sign in with a passkey" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("That passkey could not sign you in");
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(3));

    fireEvent.change(screen.getByLabelText("Email"), { target: { value: "operator@example.test" } });
    fireEvent.change(screen.getByLabelText("Password"), { target: { value: "synthetic-password" } });
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await waitFor(() => expect(auth.refreshSession).toHaveBeenCalledOnce());
    expect(fetcher.mock.calls[3][0]).toContain(`flow=${fresh.id}`);
    expect(JSON.parse(fetcher.mock.calls[3][1].body)).toMatchObject({ csrf_token: "fresh-csrf", method: "password" });
  });

  it("enrolls multiple passkeys, tracks enrollment status, and removes credentials with confirmation", async () => {
    const { create } = stubWebAuthn(
      vi.fn()
        .mockResolvedValueOnce(mockAttestationCredential())
        .mockResolvedValueOnce(mockAttestationCredential())
    );
    const cred1 = { id: "6372656431", displayName: "Touch ID", addedAt: "2026-11-20T12:00:00Z", canRemove: true };
    const cred2 = { id: "6372656432", displayName: "YubiKey", addedAt: "2026-11-21T12:00:00Z", canRemove: true };
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow: passkeySettingsFlow([]), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: passkeySettingsFlow([cred1], "22222222-3333-4444-8555-666666666666"), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: passkeySettingsFlow([cred1, cred2], "33333333-4444-4555-8666-777777777777"), session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: accountSession }))
      .mockResolvedValueOnce(jsonResponse({ flow: passkeySettingsFlow([cred2], "44444444-5555-4666-8777-888888888888"), session: null }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");

    const auth = mountPasskey("settings");
    expect(await screen.findByText("No passkeys enrolled.")).toBeInTheDocument();

    // Enroll first passkey
    fireEvent.click(screen.getByRole("button", { name: "Add passkey" }));
    expect(await screen.findByText("Passkey added.")).toBeInTheDocument();
    expect(screen.getByText("1 passkey enrolled.")).toBeInTheDocument();
    expect(screen.getByText(/Add a second passkey/)).toBeInTheDocument();
    expect(screen.getByText("Touch ID")).toBeInTheDocument();
    expect(create).toHaveBeenCalledTimes(1);
    expect(fetcher.mock.calls[1][1].headers).toHaveProperty("X-Minicloud-CSRF", "synthetic-csrf");
    expect(JSON.parse(fetcher.mock.calls[1][1].body)).toMatchObject({ csrf_token: "upstream-csrf", method: "passkey" });

    // Enroll second passkey
    fireEvent.click(screen.getByRole("button", { name: "Add another passkey" }));
    await waitFor(() => expect(screen.getByText("2 passkeys enrolled.")).toBeInTheDocument());
    expect(screen.getByText("YubiKey")).toBeInTheDocument();
    expect(auth.refreshSession).toHaveBeenCalledTimes(2);

    // Cancel removal does not post
    fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(fetcher).toHaveBeenCalledTimes(5);

    // Confirm removal submits passkey_remove and updates the list
    fireEvent.click(screen.getAllByRole("button", { name: "Remove" })[0]);
    fireEvent.click(screen.getByRole("button", { name: "Confirm remove" }));
    expect(await screen.findByText("Passkey removed.")).toBeInTheDocument();
    expect(screen.getByText("1 passkey enrolled.")).toBeInTheDocument();
    expect(screen.queryByText("Touch ID")).not.toBeInTheDocument();
    expect(screen.getByText("YubiKey")).toBeInTheDocument();
    expect(JSON.parse(fetcher.mock.calls[5][1].body)).toEqual({
      csrf_token: "upstream-csrf",
      method: "passkey",
      passkey_remove: "6372656431"
    });
  });

  it("disables passkey removal when canRemove is false and shows HTTPS notice when WebAuthn is unsupported", async () => {
    vi.stubGlobal("PublicKeyCredential", undefined);
    const lockedCred = { id: "6372656431", displayName: "Only Passkey", addedAt: null, canRemove: false };
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse({ flow: passkeySettingsFlow([lockedCred]), session: null })));

    mountPasskey("settings");
    expect(await screen.findByText("Only Passkey")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove" })).toBeDisabled();
    expect(screen.getByText(/Keep at least one sign-in method/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Add another passkey" })).toBeDisabled();
    expect(screen.getByText(/WebAuthn-capable browser over HTTPS/)).toBeInTheDocument();
  });

  it("offers explicit in-place re-authentication on 403 session_refresh_required and resumes passkey enrollment", async () => {
    const { create, get } = stubWebAuthn(
      vi.fn()
        .mockResolvedValueOnce(mockAttestationCredential())
        .mockResolvedValueOnce(mockAttestationCredential()),
      vi.fn().mockResolvedValueOnce(mockAssertionCredential())
    );
    const enrolled = { id: "6372656431", displayName: "Passkey", addedAt: null, canRemove: true };
    const refreshedSettings = passkeySettingsFlow([], "22222222-3333-4444-8555-666666666666");
    const enrolledSettings = passkeySettingsFlow([enrolled], "33333333-4444-4555-8666-777777777777");
    const reauthentication = passkeyLoginFlow("44444444-5555-4666-8777-888888888888");
    reauthentication.kind = "reauthentication";
    reauthentication.ui.action = `/api/account/flows/reauthentication?flow=${reauthentication.id}`;
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ flow: passkeySettingsFlow([]), session: null }))
      .mockResolvedValueOnce(jsonResponse({ error: { code: "session_refresh_required", message: "Refresh required" } }, 403))
      .mockResolvedValueOnce(jsonResponse({ flow: reauthentication, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: { ...accountSession, keepSignedIn: true } }))
      .mockResolvedValueOnce(jsonResponse({ flow: refreshedSettings, session: null }))
      .mockResolvedValueOnce(jsonResponse({ flow: null, session: { ...accountSession, keepSignedIn: true } }))
      .mockResolvedValueOnce(jsonResponse({ flow: enrolledSettings, session: null }));
    vi.stubGlobal("fetch", fetcher);
    setCsrfToken("synthetic-csrf");

    const auth = mountPasskey("settings", {
      session: { ...accountSession, keepSignedIn: true }
    });
    fireEvent.click(await screen.findByRole("button", { name: "Add passkey" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Log out and sign in again before updating account settings.");
    expect(fetcher).toHaveBeenCalledTimes(2);
    const reauthButton = screen.getByRole("button", { name: "Re-authenticate now" });
    fireEvent.click(reauthButton);

    const confirmPasskey = await screen.findByRole("button", { name: "Confirm with passkey" });
    fireEvent.click(confirmPasskey);
    expect(await screen.findByText("Identity confirmed. You can now update account settings.")).toBeInTheDocument();
    expect(get).toHaveBeenCalledOnce();
    expect(fetcher.mock.calls[3][0]).toContain("/flows/reauthentication?");
    expect(fetcher.mock.calls[3][1].headers).toHaveProperty("X-Minicloud-CSRF", "synthetic-csrf");
    expect(auth.refreshSession).toHaveBeenCalledTimes(1);

    await waitFor(() => {
      expect(fetcher).toHaveBeenCalledTimes(5);
      expect(fetcher.mock.calls[4][0]).toContain("/flows/settings/browser");
      expect(screen.getByRole("button", { name: "Add passkey" })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Add passkey" }));
    expect(await screen.findByText("Passkey added.")).toBeInTheDocument();
    expect(fetcher.mock.calls[5][0]).toContain(`flow=${refreshedSettings.id}`);
    expect(screen.getByText("1 passkey enrolled.")).toBeInTheDocument();
    expect(create).toHaveBeenCalledTimes(2);
  });
});

