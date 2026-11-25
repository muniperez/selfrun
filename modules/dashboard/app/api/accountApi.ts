import { apiRequest, apiUrl, setCsrfToken } from "./apiClient";
import type { PasskeyLoginOptionsJson, PasskeyRegistrationOptionsJson } from "./webauthn";

export type AccountCapabilities = {
  password: boolean;
  google: boolean;
  emailRecovery: boolean;
  /** Whether the operator offers "Keep me signed in". */
  keepSignedIn?: boolean;
  /** Whether WebAuthn passkeys are enabled for dashboard sign-in and account settings. */
  passkey?: boolean;
  bootstrapState: "required" | "ready" | "blocked";
};

export type AccountSession = {
  userId: string;
  expiresAt: string;
  idleExpiresAt: string;
  csrfToken: string;
  /** Whether this session is a remembered ("Keep me signed in") session. */
  keepSignedIn?: boolean;
  /** Earliest time an explicit renewal extends the idle expiry; null once renewal can no longer extend it. */
  renewAfter?: string | null;
  /** The user's stored sign-in default. */
  keepSignedInPreference?: boolean;
};

export type FlowKind = "login" | "settings" | "recovery" | "reauthentication";
export type FlowMessage = { id: number; text: string; type: string };
export type FlowNode = {
  group: string;
  type: string;
  name: string;
  value: string | null;
  required: boolean;
  messages: FlowMessage[];
};
export type FlowProvider = { id: string; action: string; method: string };
export type FlowPasskeyCredential = {
  id: string;
  displayName: string;
  addedAt: string | null;
  canRemove: boolean;
};
export type FlowPasskey = {
  loginOptions?: PasskeyLoginOptionsJson | null;
  registrationOptions?: PasskeyRegistrationOptionsJson | null;
  credentials: FlowPasskeyCredential[];
};
export type BrowserFlow = {
  id: string;
  kind: FlowKind;
  expiresAt: string;
  ui: {
    action: string;
    method: string;
    nodes: FlowNode[];
    messages: FlowMessage[];
    providers?: FlowProvider[] | null;
    passkey?: FlowPasskey | null;
  };
};
export type ProviderContinuation = {
  result: "success" | "failed" | "forbidden" | "unavailable";
  kind: "login" | "settings";
  flowId: string | null;
  returnTo: string;
};
export type FlowResponse = { flow: BrowserFlow | null; session: AccountSession | null };

export const getAccountCapabilities = () =>
  apiRequest<AccountCapabilities>("/api/account/capabilities", { notifyAuth: false });

export async function getAccountSession() {
  return apiRequest<AccountSession>("/api/account/session", { notifyAuth: false });
}

/** Explicit activity renewal; reading the session never extends it. */
export const renewAccountSession = () =>
  apiRequest<AccountSession>("/api/account/session/renew", { method: "POST", notifyAuth: false });

export async function setKeepSignedIn(keepSignedIn: boolean) {
  const session = await apiRequest<AccountSession>("/api/account/session/preference", {
    method: "PUT", body: { keepSignedIn }, notifyAuth: false
  });
  rememberKeepSignedInHint(session.keepSignedInPreference === true);
  return session;
}

const keepSignedInHintKey = "selfrun.keepSignedIn";

/** Non-secret, per-browser default for the sign-in checkbox. */
export function keepSignedInHint() {
  try {
    return window.localStorage.getItem(keepSignedInHintKey) === "1";
  } catch {
    return false;
  }
}

export function rememberKeepSignedInHint(value: boolean) {
  try {
    window.localStorage.setItem(keepSignedInHintKey, value ? "1" : "0");
  } catch {
    // Storage may be unavailable; the checkbox then defaults to off.
  }
}

const keepSignedInQuery = (keepSignedIn: boolean | undefined) =>
  keepSignedIn === undefined ? "" : `&keepSignedIn=${keepSignedIn}`;

export const logoutAccount = () =>
  apiRequest<void>("/api/account/logout", { method: "POST", notifyAuth: false });

export function safeReturnPath(value: unknown): string {
  if (typeof value !== "string" || !value.startsWith("/") || value.startsWith("//") ||
      /[\\\u0000-\u0020\u007f]/.test(value)) return "/apps";
  try {
    const decoded = decodeURIComponent(value);
    if (decoded.startsWith("//") || /[\\\u0000-\u0020\u007f]/.test(decoded)) return "/apps";
    const url = new URL(value, window.location.origin);
    if (url.origin !== window.location.origin ||
        ["/sign-in", "/sign-in/continue", "/sign-up", "/recovery"].includes(url.pathname)) return "/apps";
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return "/apps";
  }
}

function flowPath(kind: FlowKind) {
  if (!["login", "settings", "recovery", "reauthentication"].includes(kind)) throw new Error("Unsupported account flow.");
  return `/api/account/flows/${kind}`;
}

const flowIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function flowId(id: string) {
  if (!flowIdPattern.test(id)) {
    throw new Error("Invalid account form.");
  }
  return id;
}

function validatePasskey(passkey: FlowPasskey, kind: FlowKind) {
  if (kind === "recovery" || typeof passkey !== "object" || passkey === null || !Array.isArray(passkey.credentials)) {
    throw new Error("Invalid passkey flow options.");
  }
  for (const credential of passkey.credentials) {
    if (!credential || typeof credential.id !== "string" || !credential.id ||
        typeof credential.displayName !== "string" || typeof credential.canRemove !== "boolean" ||
        (credential.addedAt !== null && typeof credential.addedAt !== "string")) {
      throw new Error("Invalid passkey credential metadata.");
    }
  }
  if (passkey.loginOptions != null) {
    if (kind !== "login" && kind !== "reauthentication" || typeof passkey.loginOptions?.publicKey?.challenge !== "string" ||
        !passkey.loginOptions.publicKey.challenge) {
      throw new Error("Invalid passkey login options.");
    }
  }
  if (passkey.registrationOptions != null) {
    const pk = passkey.registrationOptions?.publicKey;
    if (kind !== "settings" || typeof pk?.challenge !== "string" || !pk.challenge ||
        typeof pk.rp?.name !== "string" || !pk.rp.name ||
        typeof pk.user?.id !== "string" || !pk.user.id ||
        !Array.isArray(pk.pubKeyCredParams) || pk.pubKeyCredParams.length === 0) {
      throw new Error("Invalid passkey registration options.");
    }
  }
}

export function validateFlow(flow: BrowserFlow, kind: FlowKind) {
  const expected = apiUrl(`${flowPath(kind)}?flow=${flowId(flow.id)}`);
  const action = new URL(flow.ui.action, apiUrl(flowPath(kind)));
  if (flow.kind !== kind || flow.ui.method.toUpperCase() !== "POST" || action.href !== expected) {
    throw new Error("Invalid account form destination.");
  }
  for (const provider of flow.ui.providers ?? []) {
    const providerAction = new URL(provider.action, apiUrl(flowPath(kind)));
    if (kind === "recovery" || provider.id !== "google" || provider.method.toUpperCase() !== "POST" ||
        providerAction.href !== apiUrl(`${flowPath(kind)}/oidc?flow=${flow.id}`)) {
      throw new Error("Invalid account provider destination.");
    }
  }
  if (flow.ui.passkey != null) {
    validatePasskey(flow.ui.passkey, kind);
  }
  return flow;
}

/** Only HTTPS Google authorization URLs are navigable; the API validates the same allowlist first. */
export function validateProviderUrl(value: unknown) {
  if (typeof value !== "string") throw new Error("Invalid account provider destination.");
  const url = new URL(value);
  if (url.protocol !== "https:" || url.hostname !== "accounts.google.com" || url.port || url.username ||
      url.password || url.hash || !url.pathname.startsWith("/o/oauth2/")) {
    throw new Error("Invalid account provider destination.");
  }
  return url.href;
}

export const browserNavigation = { assign: (url: string) => window.location.assign(url) };

function flowCsrfToken(flow: BrowserFlow): string {
  return flow.ui.nodes.find(node => node.name === "csrf_token" && node.type === "hidden")?.value ?? "";
}

export async function startProviderRedirect(flow: BrowserFlow, provider: "google", returnTo: string, keepSignedIn?: boolean) {
  validateFlow(flow, flow.kind);
  if (!flow.ui.providers?.some(item => item.id === provider)) throw new Error("Invalid account provider destination.");
  const result = await apiRequest<{ provider: string; redirectUrl: string }>(
    `${flowPath(flow.kind)}/oidc?flow=${flowId(flow.id)}&returnTo=${encodeURIComponent(safeReturnPath(returnTo))}` +
      keepSignedInQuery(flow.kind === "login" ? keepSignedIn : undefined), {
      method: "POST",
      body: { csrf_token: flowCsrfToken(flow), provider },
      browserFlow: flow.kind === "login" || flow.kind === "recovery",
      notifyAuth: false
    });
  if (result.provider !== provider) throw new Error("Invalid account provider destination.");
  return validateProviderUrl(result.redirectUrl);
}

export function parseProviderContinuation(search: URLSearchParams): ProviderContinuation {
  const result = search.get("result");
  const kind = search.get("kind") === "settings" ? "settings" : "login";
  const flow = search.get("flow");
  return {
    result: result === "success" || result === "forbidden" || result === "unavailable" ? result : "failed",
    kind,
    flowId: flow && flowIdPattern.test(flow) ? flow : null,
    returnTo: safeReturnPath(search.get("returnTo") ?? (kind === "settings" ? "/settings/account" : "/apps"))
  };
}

async function readEnvelope(response: Promise<FlowResponse>, kind: FlowKind) {
  const result = await response;
  if (Boolean(result.flow) === Boolean(result.session)) throw new Error("Invalid account response.");
  if (result.flow) validateFlow(result.flow, kind);
  if (result.session) setCsrfToken(result.session.csrfToken);
  return result;
}

export function startAccountFlow(kind: FlowKind, returnTo: string) {
  return readEnvelope(apiRequest<FlowResponse>(
    `${flowPath(kind)}/browser?returnTo=${encodeURIComponent(safeReturnPath(returnTo))}`,
    { notifyAuth: false }
  ), kind);
}

export function getAccountFlow(kind: FlowKind, id: string) {
  return readEnvelope(apiRequest<FlowResponse>(
    `${flowPath(kind)}?id=${flowId(id)}`, { notifyAuth: false }
  ), kind);
}

export function submitAccountFlow(flow: BrowserFlow, fields: Record<string, string>, keepSignedIn?: boolean) {
  validateFlow(flow, flow.kind);
  const query = keepSignedInQuery(flow.kind === "login" ? keepSignedIn : undefined);
  return readEnvelope(apiRequest<FlowResponse>(`${flowPath(flow.kind)}?flow=${flowId(flow.id)}${query}`, {
    method: "POST",
    body: fields,
    browserFlow: flow.kind === "login" || flow.kind === "recovery",
    notifyAuth: false
  }), flow.kind);
}

export function submitPasskeyLogin(flow: BrowserFlow, passkeyLoginJson: string, keepSignedIn?: boolean) {
  if (flow.kind !== "login" && flow.kind !== "reauthentication") throw new Error("Invalid account form destination.");
  return submitAccountFlow(flow, {
    csrf_token: flowCsrfToken(flow),
    method: "passkey",
    passkey_login: passkeyLoginJson
  }, keepSignedIn);
}

export function submitPasskeyRegistration(flow: BrowserFlow, passkeyRegisterJson: string) {
  if (flow.kind !== "settings") throw new Error("Invalid account form destination.");
  return submitAccountFlow(flow, {
    csrf_token: flowCsrfToken(flow),
    method: "passkey",
    passkey_settings_register: passkeyRegisterJson
  });
}

export function submitPasskeyRemoval(flow: BrowserFlow, credentialId: string) {
  if (flow.kind !== "settings") throw new Error("Invalid account form destination.");
  return submitAccountFlow(flow, {
    csrf_token: flowCsrfToken(flow),
    method: "passkey",
    passkey_remove: credentialId
  });
}
