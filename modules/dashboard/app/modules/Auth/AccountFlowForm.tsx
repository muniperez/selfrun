import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Fragment, useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import {
  browserNavigation, getAccountFlow, keepSignedInHint, rememberKeepSignedInHint, startAccountFlow, startProviderRedirect,
  submitAccountFlow, submitPasskeyLogin, submitPasskeyRegistration, submitPasskeyRemoval,
  type BrowserFlow, type FlowKind, type FlowNode
} from "../../api/accountApi";
import { accountErrorMessage } from "../../api/apiClient";
import {
  classifyPasskeyError, createPasskeyCredential, getPasskeyAssertion, isPasskeySupported
} from "../../api/webauthn";
import { useAuth } from "../../core/auth/AuthProvider";
import { ApiError } from "../../models/controlPlane";
import { PasskeySettingsSection } from "./PasskeySettingsSection";

function fieldLabel(name: string) {
  switch (name) {
    case "identifier": case "traits.email": case "email": return "Email";
    case "password": return "Password";
    case "code": return "Recovery code";
    case "traits.name": return "Display name";
    case "traits.name.first": return "First name";
    case "traits.name.last": return "Last name";
    default: return "Account detail";
  }
}

function supportedNode(node: FlowNode) {
  if (node.name === "password" && node.type !== "password" ||
      node.name === "csrf_token" && node.type !== "hidden") return false;
  return ["default", "password", "profile", "code", "recovery"].includes(node.group) &&
    ["hidden", "text", "email", "password", "submit"].includes(node.type) &&
    ["csrf_token", "identifier", "password", "method", "email", "code", "traits.email", "traits.name", "traits.name.first", "traits.name.last"].includes(node.name);
}

function expiredLoginFlow(kind: FlowKind, error: unknown) {
  return kind === "login" && error instanceof ApiError && error.code === "auth_flow_expired";
}

export function AccountFlowForm({ kind, returnTo = "/apps", flowId, notice }: {
  kind: FlowKind;
  returnTo?: string;
  /** Re-read this existing flow once (provider callback continuation) instead of starting a new one. */
  flowId?: string;
  notice?: string;
}) {
  const { capabilities, session, refreshSession } = useAuth();
  const [flow, setFlow] = useState<BrowserFlow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [complete, setComplete] = useState(false);
  const [restartRequired, setRestartRequired] = useState(false);
  const [reauthRequired, setReauthRequired] = useState(false);
  const [reauthFlow, setReauthFlow] = useState<BrowserFlow | null>(null);
  const [reauthBusy, setReauthBusy] = useState(false);
  const [reauthError, setReauthError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  const generation = useRef(0);
  const enabled = kind === "recovery" ? capabilities?.emailRecovery === true : capabilities?.password === true;
  const offerKeepSignedIn = kind === "login" && capabilities?.keepSignedIn === true;
  const passkeyEnabled = capabilities?.passkey === true && kind !== "recovery";
  const browserPasskeySupported = isPasskeySupported();
  const [keepSignedIn, setKeepSignedIn] = useState(keepSignedInHint);
  const keepSignedInChoice = offerKeepSignedIn ? keepSignedIn : undefined;

  useEffect(() => {
    const current = ++generation.current;
    setFlow(null);
    setComplete(false);
    setError(null);
    setRestartRequired(false);
    setReauthRequired(false);
    setReauthFlow(null);
    setReauthError(null);
    if (revision === 0) setStatusMessage(null);
    if (!enabled) return;
    setBusy(true);
    const request = flowId && revision === 0 ? getAccountFlow(kind, flowId) : startAccountFlow(kind, returnTo);
    request.catch(loadError => {
      if (current === generation.current && revision === 0 && expiredLoginFlow(kind, loadError)) {
        return startAccountFlow(kind, returnTo);
      }
      throw loadError;
    }).then(result => {
      if (current === generation.current) setFlow(result.flow);
    }).catch(loadError => {
      if (current === generation.current) {
        setError(accountErrorMessage(loadError));
        setRestartRequired(true);
      }
    }).finally(() => { if (current === generation.current) setBusy(false); });
    return () => { ++generation.current; };
  }, [enabled, kind, returnTo, revision, flowId]);

  function handleExpiredFlow() {
    if (kind === "login") {
      restartFlow();
    } else {
      setError("This form expired. Restart to try again.");
      setRestartRequired(true);
    }
  }

  function restartFlow(nextStatus?: string) {
    setStatusMessage(nextStatus ?? null);
    setBusy(true);
    setRevision(value => value + 1);
  }

  async function continueWithGoogle() {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    try {
      const destination = await startProviderRedirect(flow, "google", returnTo, keepSignedInChoice);
      if (current !== generation.current) return;
      if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
      browserNavigation.assign(destination);
    } catch (providerError) {
      if (current !== generation.current) return;
      if (expiredLoginFlow(kind, providerError)) {
        restartFlow();
        return;
      }
      setError(accountErrorMessage(providerError));
      setRestartRequired(true);
      setBusy(false);
    }
  }

  async function signInWithPasskey() {
    const loginOptions = flow?.ui.passkey?.loginOptions;
    if (!flow || !loginOptions || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    let assertionJson: string;
    try {
      assertionJson = await getPasskeyAssertion(loginOptions);
    } catch (webauthnError) {
      if (current !== generation.current) return;
      const classified = classifyPasskeyError(webauthnError, "login");
      setError(classified.message);
      setBusy(false);
      return;
    }
    if (current !== generation.current) return;
    let refreshing = false;
    try {
      const result = await submitPasskeyLogin(flow, assertionJson, keepSignedInChoice);
      if (current !== generation.current) return;
      setFlow(result.flow);
      if (result.session) {
        if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
        setComplete(true);
        await refreshSession();
      }
    } catch (submitError) {
      if (current !== generation.current) return;
      if (expiredLoginFlow(kind, submitError)) {
        refreshing = true;
        restartFlow();
      } else if (submitError instanceof ApiError && submitError.code === "auth_flow_invalid") {
        setError("That passkey could not sign you in. Try another passkey or sign in with your password.");
        try {
          const fresh = await startAccountFlow("login", returnTo);
          if (current === generation.current) setFlow(fresh.flow);
        } catch (reloadError) {
          if (current === generation.current) {
            setError(accountErrorMessage(reloadError));
            setRestartRequired(true);
          }
        }
      } else {
        setError(accountErrorMessage(submitError));
        setRestartRequired(true);
      }
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  async function addPasskey() {
    const registrationOptions = flow?.ui.passkey?.registrationOptions;
    if (!flow || !registrationOptions || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    let registrationJson: string;
    try {
      registrationJson = await createPasskeyCredential(registrationOptions);
    } catch (webauthnError) {
      if (current !== generation.current) return;
      const classified = classifyPasskeyError(webauthnError, "register");
      setError(classified.message);
      setBusy(false);
      return;
    }
    if (current !== generation.current) return;
    try {
      const result = await submitPasskeyRegistration(flow, registrationJson);
      if (current !== generation.current) return;
      if (result.session) {
        await refreshSession();
        if (current !== generation.current) return;
        const fresh = await startAccountFlow("settings", returnTo);
        if (current !== generation.current) return;
        setFlow(fresh.flow);
        setStatusMessage("Passkey added.");
      } else {
        setFlow(result.flow);
      }
    } catch (submitError) {
      if (current !== generation.current) return;
      await handleSettingsPasskeyError(submitError, "Could not add that passkey. Try again or use another authenticator.", current);
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  async function removePasskey(credentialId: string) {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    try {
      const result = await submitPasskeyRemoval(flow, credentialId);
      if (current !== generation.current) return;
      if (result.session) {
        await refreshSession();
        if (current !== generation.current) return;
        const fresh = await startAccountFlow("settings", returnTo);
        if (current !== generation.current) return;
        setFlow(fresh.flow);
        setStatusMessage("Passkey removed.");
      } else {
        setFlow(result.flow);
      }
    } catch (submitError) {
      if (current !== generation.current) return;
      await handleSettingsPasskeyError(submitError, "Could not remove that passkey. Ensure another sign-in method remains available.", current);
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  async function handleSettingsPasskeyError(submitError: unknown, invalidMessage: string, current: number) {
    if (submitError instanceof ApiError && submitError.code === "session_refresh_required") {
      setError(accountErrorMessage(submitError));
      setRestartRequired(true);
      setReauthRequired(true);
      return;
    }
    if (submitError instanceof ApiError && submitError.code === "auth_flow_invalid") {
      setError(invalidMessage);
      try {
        const fresh = await startAccountFlow("settings", returnTo);
        if (current === generation.current) setFlow(fresh.flow);
      } catch (reloadError) {
        if (current === generation.current) {
          setError(accountErrorMessage(reloadError));
          setRestartRequired(true);
        }
      }
      return;
    }
    setError(accountErrorMessage(submitError));
    setRestartRequired(true);
  }

  async function startReauthentication() {
    if (reauthBusy) return;
    const current = generation.current;
    setReauthBusy(true);
    setReauthError(null);
    try {
      const result = await startAccountFlow("reauthentication", returnTo);
      if (current !== generation.current) return;
      setReauthFlow(result.flow);
    } catch (loadError) {
      if (current !== generation.current) return;
      setReauthError(accountErrorMessage(loadError));
    } finally {
      if (current === generation.current) setReauthBusy(false);
    }
  }

  async function finishReauthentication(result: { flow: BrowserFlow | null; session: unknown }, current: number) {
    if (current !== generation.current) return;
    if (result.session) {
      await refreshSession();
      if (current !== generation.current) return;
      setReauthFlow(null);
      setReauthRequired(false);
      setReauthError(null);
      restartFlow("Identity confirmed. You can now update account settings.");
    } else {
      setReauthFlow(result.flow);
    }
  }

  async function submitReauthPassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!reauthFlow || reauthBusy) return;
    const current = generation.current;
    const form = event.currentTarget;
    const submitter = (event.nativeEvent as SubmitEvent).submitter;
    const data = new FormData(form);
    const fields: Record<string, string> = {};
    for (const [name, value] of data) {
      if (typeof value === "string") fields[name] = value;
    }
    if (submitter instanceof HTMLButtonElement && submitter.name) fields[submitter.name] = submitter.value;
    setReauthBusy(true);
    setReauthError(null);
    try {
      const result = await submitAccountFlow(reauthFlow, fields, session?.keepSignedIn);
      form.reset();
      await finishReauthentication(result, current);
    } catch (submitError) {
      if (current !== generation.current) return;
      form.reset();
      if (submitError instanceof ApiError && (submitError.code === "auth_flow_expired" || submitError.code === "auth_flow_invalid")) {
        setReauthError(accountErrorMessage(submitError));
        try {
          const fresh = await startAccountFlow("reauthentication", returnTo);
          if (current === generation.current) setReauthFlow(fresh.flow);
        } catch (reloadError) {
          if (current === generation.current) setReauthError(accountErrorMessage(reloadError));
        }
      } else {
        setReauthError(accountErrorMessage(submitError));
      }
    } finally {
      if (current === generation.current) setReauthBusy(false);
    }
  }

  async function submitReauthPasskey() {
    const loginOptions = reauthFlow?.ui.passkey?.loginOptions;
    if (!reauthFlow || !loginOptions || reauthBusy) return;
    const current = generation.current;
    setReauthBusy(true);
    setReauthError(null);
    let assertionJson: string;
    try {
      assertionJson = await getPasskeyAssertion(loginOptions);
    } catch (webauthnError) {
      if (current !== generation.current) return;
      setReauthError(classifyPasskeyError(webauthnError, "login").message);
      setReauthBusy(false);
      return;
    }
    if (current !== generation.current) return;
    try {
      const result = await submitPasskeyLogin(reauthFlow, assertionJson, session?.keepSignedIn);
      await finishReauthentication(result, current);
    } catch (submitError) {
      if (current !== generation.current) return;
      setReauthError(submitError instanceof ApiError && submitError.code === "auth_flow_invalid"
        ? "That passkey could not confirm your identity. Try another passkey or your password."
        : accountErrorMessage(submitError));
      try {
        const fresh = await startAccountFlow("reauthentication", returnTo);
        if (current === generation.current) setReauthFlow(fresh.flow);
      } catch (reloadError) {
        if (current === generation.current) setReauthError(accountErrorMessage(reloadError));
      }
    } finally {
      if (current === generation.current) setReauthBusy(false);
    }
  }

  async function submitFields(form: HTMLFormElement | null, fields: Record<string, string>) {
    if (!flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    let refreshing = false;
    function refreshExpiredLogin() {
      refreshing = true;
      restartFlow();
    }
    try {
      const result = await submitAccountFlow(flow, fields, keepSignedInChoice);
      if (current !== generation.current) return;
      form?.reset();
      setFlow(result.flow);
      if (result.session) {
        if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
        setComplete(true);
        await refreshSession();
      }
    } catch (submitError) {
      if (current !== generation.current) return;
      form?.reset();
      if (expiredLoginFlow(kind, submitError)) {
        refreshExpiredLogin();
      } else if (submitError instanceof ApiError &&
        (submitError.code === "auth_flow_invalid" || submitError.code === "auth_password_rejected")) {
        setError(accountErrorMessage(submitError));
        try {
          const result = await getAccountFlow(kind, flow.id);
          if (current === generation.current) setFlow(result.flow);
        } catch (readError) {
          if (current === generation.current) {
            if (expiredLoginFlow(kind, readError)) {
              refreshExpiredLogin();
            } else {
              setError(accountErrorMessage(readError));
              setRestartRequired(true);
            }
          }
        }
      } else {
        setError(accountErrorMessage(submitError));
        setRestartRequired(true);
        if (kind === "settings" && submitError instanceof ApiError && submitError.code === "session_refresh_required") {
          setReauthRequired(true);
        }
      }
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const submitter = (event.nativeEvent as SubmitEvent).submitter;
    const data = new FormData(form);
    const fields: Record<string, string> = {};
    for (const [name, value] of data) {
      if (typeof value === "string") fields[name] = value;
    }
    if (submitter instanceof HTMLButtonElement && submitter.name) fields[submitter.name] = submitter.value;
    await submitFields(form, fields);
  }

  async function resendRecoveryCode(email: string) {
    if (!email) return;
    const fields: Record<string, string> = {};
    for (const node of nodes) {
      if (node.type === "hidden" && node.value !== null) fields[node.name] = node.value;
    }
    fields.method = fields.method || "code";
    fields.email = email;
    await submitFields(formRef.current, fields);
  }

  const formRef = useRef<HTMLFormElement | null>(null);
  const nodes = flow?.ui.nodes.filter(supportedNode) ?? [];
  const google = capabilities?.google === true && kind !== "recovery" &&
    flow?.ui.providers?.some(provider => provider.id === "google") === true;
  const passkeyLogin = kind === "login" && passkeyEnabled && Boolean(flow?.ui.passkey?.loginOptions);
  const passkeySettings = kind === "settings" && passkeyEnabled ? flow?.ui.passkey ?? undefined : undefined;
  const firstSubmit = nodes.findIndex(node => node.type === "submit");
  const hasSubmit = firstSubmit >= 0;
  const keepSignedInControl = offerKeepSignedIn ? (
    <label className="flex items-start gap-2 text-sm">
      {/* Unnamed so it is never sent as an upstream form field. */}
      <input type="checkbox" className="mt-1 accent-primary" checked={keepSignedIn} disabled={busy || restartRequired}
        onChange={event => setKeepSignedIn(event.target.checked)} />
      <span>Keep me signed in on this device</span>
    </label>
  ) : null;
  const reauthNodes = reauthFlow?.ui.nodes.filter(supportedNode) ?? [];
  const reauthPasskey = passkeyEnabled && browserPasskeySupported && Boolean(reauthFlow?.ui.passkey?.loginOptions);

  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>{kind === "login" ? "Sign in" : kind === "settings" ? "Account settings" : "Recover account"}</CardTitle>
        <CardDescription>
          {kind === "settings"
            ? (passkeyEnabled ? "Manage your password and passkeys." : "Change your password.")
            : "Access your Selfrun account."}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!enabled ? <p>This authentication method is not enabled. Contact the host administrator.</p> : null}
        {busy && !flow ? <p role="status">Loading account form</p> : null}
        {complete ? <p role="status">{kind === "settings" ? "Password updated." : "Account verified."}</p> : null}
        {statusMessage && !complete ? <p role="status">{statusMessage}</p> : null}
        {notice ? <p role="alert" className="text-sm text-destructive">{notice}</p> : null}
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {kind === "settings" && reauthRequired && !reauthFlow ? (
          <Button type="button" className="w-full" disabled={reauthBusy} onClick={startReauthentication}>
            {reauthBusy ? "Starting re-authentication..." : "Re-authenticate now"}
          </Button>
        ) : null}
        {kind === "settings" && reauthFlow ? (
          <section aria-label="Re-authenticate" className="space-y-3 rounded-md border p-3">
            <div className="space-y-1">
              <h3 className="text-sm font-medium">Confirm your identity</h3>
              <p className="text-xs text-muted-foreground">
                Re-authenticate with your passkey or password to continue updating account settings.
              </p>
            </div>
            {reauthError ? <p role="alert" className="text-sm text-destructive">{reauthError}</p> : null}
            {reauthPasskey ? (
              <Button type="button" variant="outline" className="w-full" disabled={reauthBusy} onClick={submitReauthPasskey}>
                Confirm with passkey
              </Button>
            ) : null}
            <form key={reauthFlow.id} className="space-y-3" onSubmit={submitReauthPassword}>
              {reauthNodes.map((node, index) => {
                const id = `reauth-${index}`;
                const key = `reauth:${node.group}:${node.name}:${node.type}`;
                if (node.type === "hidden") return <input key={key} type="hidden" name={node.name} value={node.value ?? ""} />;
                if (node.type === "submit") return (
                  <div key={key} className="flex items-center gap-2">
                    <Button type="submit" name={node.name} value={node.value ?? ""} disabled={reauthBusy}>
                      Confirm identity
                    </Button>
                    <Button type="button" variant="outline" disabled={reauthBusy} onClick={() => {
                      setReauthFlow(null);
                      setReauthError(null);
                    }}>
                      Cancel
                    </Button>
                  </div>
                );
                return (
                  <div key={key} className="space-y-1">
                    <Label htmlFor={id}>{fieldLabel(node.name)}</Label>
                    <Input id={id} name={node.name} type={node.name === "password" ? "password" : node.type}
                      autoComplete={node.name === "password" ? "current-password" : "username"}
                      defaultValue={node.name === "password" ? "" : node.value ?? ""}
                      required={node.required} disabled={reauthBusy} />
                  </div>
                );
              })}
            </form>
          </section>
        ) : null}
        {flow && !complete ? (
          <form ref={formRef} key={flow.id} className="space-y-4" onSubmit={submit}>
            {flow.ui.messages.length > 0 ? (
              flow.ui.messages.some(message => message.type === "error") ? (
                <p role="alert" className="text-sm text-destructive">Check your account details and try again.</p>
              ) : (
                <p role="status">Review your account details to continue.</p>
              )
            ) : null}
            {nodes.map((node, index) => {
              const id = `account-${index}`;
              const key = `${node.group}:${node.name}:${node.type}`;
              if (node.type === "hidden") return <input key={key} type="hidden" name={node.name} value={node.value ?? ""} />;
              if (node.type === "submit") {
                const isResend = kind === "recovery" && node.name === "email";
                return (
                  <Fragment key={key}>
                    {index === firstSubmit ? keepSignedInControl : null}
                    {isResend ? (
                      <Button type="button" variant="outline" disabled={busy || restartRequired}
                        onClick={() => { void resendRecoveryCode(node.value ?? ""); }}>
                        Resend code
                      </Button>
                    ) : (
                      <Button type="submit" name={node.name} value={node.value ?? ""} disabled={busy || restartRequired}>
                        {kind === "login" ? "Sign in" : kind === "settings" ? node.value === "profile" ? "Save profile" : "Save password" : "Continue recovery"}
                      </Button>
                    )}
                  </Fragment>
                );
              }
              return (
                <div key={key} className="space-y-2">
                  <Label htmlFor={id}>{fieldLabel(node.name)}</Label>
                  <Input id={id} name={node.name} type={node.name === "password" ? "password" : node.type}
                    autoComplete={node.name === "password" ? kind === "settings" ? "new-password" : "current-password" : node.name === "code" ? "one-time-code" :
                      node.name === "traits.name.first" ? "given-name" : node.name === "traits.name.last" ? "family-name" : "username"}
                    defaultValue={node.name === "password" || node.name === "code" ? "" : node.value ?? ""}
                    required={node.required} disabled={busy || restartRequired} aria-invalid={node.messages.length > 0} />
                  {node.messages.length > 0 ? <p className="text-sm text-destructive">Check this field.</p> : null}
                </div>
              );
            })}
            {!hasSubmit ? <p role="alert">This form cannot continue. Restart or contact the host administrator.</p> : null}
          </form>
        ) : null}
        {flow && !complete && passkeyLogin && browserPasskeySupported ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={signInWithPasskey}>
            Sign in with a passkey
          </Button>
        ) : null}
        {flow && !complete && google ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={continueWithGoogle}>
            {kind === "settings" ? "Link Google account" : "Continue with Google"}
          </Button>
        ) : null}
        {flow && !complete && passkeySettings ? (
          <PasskeySettingsSection
            passkey={passkeySettings}
            browserSupported={browserPasskeySupported}
            busy={busy}
            disabled={restartRequired}
            onAdd={addPasskey}
            onRemove={removePasskey}
          />
        ) : null}
        {enabled && (kind !== "login" || restartRequired) ? <Button variant="outline" disabled={busy} onClick={() => restartFlow()}>Restart form</Button> : null}
        {kind === "login" && capabilities?.emailRecovery ? <Link className="block underline" to="/recovery" state={{ from: returnTo }}>Forgot password?</Link> : null}
        {kind === "recovery" ? <Link className="block underline" to="/sign-in" state={{ from: returnTo }}>Return to sign in</Link> : null}
      </CardContent>
    </Card>
  );
}
