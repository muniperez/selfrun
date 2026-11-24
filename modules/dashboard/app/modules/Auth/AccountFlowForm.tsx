import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Fragment, useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import {
  browserNavigation, getAccountFlow, keepSignedInHint, rememberKeepSignedInHint, renamePasskey, startAccountFlow,
  startProviderRedirect, submitAccountFlow,
  type BrowserFlow, type FlowKind, type FlowNode
} from "../../api/accountApi";
import { accountErrorMessage } from "../../api/apiClient";
import { useAuth } from "../../core/auth/AuthProvider";
import { authenticateWithPasskey, createPasskeyCredential, isPasskeySupported } from "../../core/auth/webauthn";
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

function findFlowCsrfToken(flow: BrowserFlow) {
  return flow.ui.nodes.find(node => node.name === "csrf_token" && node.type === "hidden")?.value ?? "";
}

export function AccountFlowForm({ kind, returnTo = "/apps", flowId, notice }: {
  kind: FlowKind;
  returnTo?: string;
  /** Re-read this existing flow once (provider callback continuation) instead of starting a new one. */
  flowId?: string;
  notice?: string;
}) {
  const { capabilities, refreshSession } = useAuth();
  const [flow, setFlow] = useState<BrowserFlow | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [statusMessage, setStatusMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [complete, setComplete] = useState(false);
  const [restartRequired, setRestartRequired] = useState(false);
  const [reauthNeeded, setReauthNeeded] = useState(false);
  const [reauthFlow, setReauthFlow] = useState<BrowserFlow | null>(null);
  const [reauthBusy, setReauthBusy] = useState(false);
  const [reauthError, setReauthError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  const generation = useRef(0);
  const enabled = kind === "recovery" ? capabilities?.emailRecovery === true : capabilities?.password === true;
  const offerKeepSignedIn = kind === "login" && capabilities?.keepSignedIn === true;
  const passkeyEnabled = capabilities?.passkey === true;
  const passkeySupported = isPasskeySupported();
  const [keepSignedIn, setKeepSignedIn] = useState(keepSignedInHint);
  const keepSignedInChoice = offerKeepSignedIn ? keepSignedIn : undefined;

  useEffect(() => {
    const current = ++generation.current;
    setFlow(null);
    setComplete(false);
    setError(null);
    setStatusMessage(null);
    setRestartRequired(false);
    setReauthNeeded(false);
    setReauthFlow(null);
    setReauthError(null);
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

  function restartFlow() {
    setBusy(true);
    setRevision(value => value + 1);
  }

  async function reloadSettingsAfterMutation(current: number, message: string) {
    await refreshSession();
    if (current !== generation.current) return;
    try {
      const next = await startAccountFlow("settings", returnTo);
      if (current !== generation.current) return;
      setFlow(next.flow);
      setStatusMessage(message);
    } catch (loadError) {
      if (current !== generation.current) return;
      setStatusMessage(message);
      setError(accountErrorMessage(loadError));
      setRestartRequired(true);
    }
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
          setReauthNeeded(true);
        }
      }
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  async function signInWithPasskey() {
    if (kind !== "login" || !flow?.ui.passkey?.challengeOptions || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    let refreshing = false;
    try {
      const assertion = await authenticateWithPasskey(flow.ui.passkey.challengeOptions);
      if (current !== generation.current) return;
      const result = await submitAccountFlow(flow, {
        csrf_token: findFlowCsrfToken(flow),
        method: "passkey",
        passkey_login: assertion
      }, keepSignedInChoice);
      if (current !== generation.current) return;
      setFlow(result.flow);
      if (result.session) {
        if (keepSignedInChoice !== undefined) rememberKeepSignedInHint(keepSignedInChoice);
        setComplete(true);
        await refreshSession();
      }
    } catch (passkeyError) {
      if (current !== generation.current) return;
      if (passkeyError instanceof Error && passkeyError.name === "PasskeyError") {
        setError(accountErrorMessage(passkeyError));
      } else if (expiredLoginFlow(kind, passkeyError)) {
        refreshing = true;
        restartFlow();
      } else if (passkeyError instanceof ApiError &&
        (passkeyError.code === "auth_passkey_rejected" || passkeyError.code === "auth_flow_invalid")) {
        setError(accountErrorMessage(passkeyError));
        try {
          const result = await getAccountFlow(kind, flow.id);
          if (current === generation.current) setFlow(result.flow);
        } catch (readError) {
          if (current === generation.current) {
            if (expiredLoginFlow(kind, readError)) {
              refreshing = true;
              restartFlow();
            } else {
              setError(accountErrorMessage(readError));
              setRestartRequired(true);
            }
          }
        }
      } else {
        setError(accountErrorMessage(passkeyError));
        setRestartRequired(true);
      }
    } finally {
      if (current === generation.current && !refreshing) setBusy(false);
    }
  }

  async function registerPasskey(displayName: string) {
    if (kind !== "settings" || !flow?.ui.passkey?.registrationOptions || busy || restartRequired) return false;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return false;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    try {
      const attestation = await createPasskeyCredential(flow.ui.passkey.registrationOptions);
      if (current !== generation.current) return false;
      const fields: Record<string, string> = {
        csrf_token: findFlowCsrfToken(flow),
        method: "passkey",
        passkey_settings_register: attestation
      };
      if (displayName) {
        fields.passkey_display_name = displayName;
      }
      const result = await submitAccountFlow(flow, fields);
      if (current !== generation.current) return false;
      if (result.session) {
        await reloadSettingsAfterMutation(current, "Passkey added.");
        return true;
      }
      setFlow(result.flow);
      return false;
    } catch (passkeyError) {
      if (current !== generation.current) return false;
      if (passkeyError instanceof Error && passkeyError.name === "PasskeyError") {
        setError(accountErrorMessage(passkeyError));
      } else if (passkeyError instanceof ApiError &&
        (passkeyError.code === "auth_passkey_rejected" || passkeyError.code === "auth_flow_invalid")) {
        setError(accountErrorMessage(passkeyError));
        try {
          const result = await getAccountFlow("settings", flow.id);
          if (current === generation.current) setFlow(result.flow);
        } catch (readError) {
          if (current === generation.current) {
            setError(accountErrorMessage(readError));
            setRestartRequired(true);
          }
        }
      } else {
        setError(accountErrorMessage(passkeyError));
        setRestartRequired(true);
        if (passkeyError instanceof ApiError && passkeyError.code === "session_refresh_required") {
          setReauthNeeded(true);
        }
      }
      return false;
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  async function removePasskey(credentialId: string) {
    if (kind !== "settings" || !flow || busy || restartRequired) return;
    if (Date.parse(flow.expiresAt) <= Date.now()) {
      handleExpiredFlow();
      return;
    }
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    try {
      const result = await submitAccountFlow(flow, {
        csrf_token: findFlowCsrfToken(flow),
        method: "passkey",
        passkey_remove: credentialId
      });
      if (current !== generation.current) return;
      if (result.session) {
        await reloadSettingsAfterMutation(current, "Passkey removed.");
        return;
      }
      setFlow(result.flow);
    } catch (removeError) {
      if (current !== generation.current) return;
      if (removeError instanceof ApiError &&
        (removeError.code === "auth_passkey_remove_rejected" || removeError.code === "auth_flow_invalid")) {
        setError(accountErrorMessage(removeError));
        try {
          const result = await getAccountFlow("settings", flow.id);
          if (current === generation.current) setFlow(result.flow);
        } catch (readError) {
          if (current === generation.current) {
            setError(accountErrorMessage(readError));
            setRestartRequired(true);
          }
        }
      } else {
        setError(accountErrorMessage(removeError));
        setRestartRequired(true);
        if (removeError instanceof ApiError && removeError.code === "session_refresh_required") {
          setReauthNeeded(true);
        }
      }
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  async function handleRenamePasskey(credentialId: string, displayName: string) {
    if (kind !== "settings" || !flow || busy || restartRequired) return false;
    const current = generation.current;
    setBusy(true);
    setError(null);
    setStatusMessage(null);
    try {
      const updated = await renamePasskey(credentialId, displayName);
      if (current !== generation.current) return false;
      setFlow(existing => existing?.ui.passkey?.credentials
        ? {
            ...existing,
            ui: {
              ...existing.ui,
              passkey: {
                ...existing.ui.passkey,
                credentials: existing.ui.passkey.credentials.map(cred =>
                  cred.id === updated.id ? { ...cred, displayName: updated.displayName } : cred)
              }
            }
          }
        : existing);
      setStatusMessage("Passkey renamed.");
      return true;
    } catch (renameError) {
      if (current !== generation.current) return false;
      setError(accountErrorMessage(renameError));
      if (renameError instanceof ApiError && renameError.code === "session_refresh_required") {
        setRestartRequired(true);
        setReauthNeeded(true);
      }
      return false;
    } finally {
      if (current === generation.current) setBusy(false);
    }
  }

  async function startReauthentication() {
    if (reauthBusy) return;
    const current = generation.current;
    setReauthBusy(true);
    setReauthError(null);
    try {
      const result = await startAccountFlow("login", returnTo);
      if (current !== generation.current) return;
      setReauthFlow(result.flow);
    } catch (loadError) {
      if (current !== generation.current) return;
      setReauthError(accountErrorMessage(loadError));
    } finally {
      if (current === generation.current) setReauthBusy(false);
    }
  }

  async function completeReauthentication(current: number) {
    setReauthFlow(null);
    setReauthNeeded(false);
    setReauthError(null);
    setError(null);
    setRestartRequired(false);
    await reloadSettingsAfterMutation(current, "Identity verified. You can now update account settings.");
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
      const result = await submitAccountFlow(reauthFlow, fields);
      if (current !== generation.current) return;
      form.reset();
      if (result.session) {
        await completeReauthentication(current);
        return;
      }
      setReauthFlow(result.flow);
    } catch (submitError) {
      if (current !== generation.current) return;
      form.reset();
      setReauthError(accountErrorMessage(submitError));
      if (submitError instanceof ApiError && submitError.code === "auth_flow_expired") {
        setReauthFlow(null);
      }
    } finally {
      if (current === generation.current) setReauthBusy(false);
    }
  }

  async function submitReauthPasskey() {
    if (!reauthFlow?.ui.passkey?.challengeOptions || reauthBusy) return;
    const current = generation.current;
    setReauthBusy(true);
    setReauthError(null);
    try {
      const assertion = await authenticateWithPasskey(reauthFlow.ui.passkey.challengeOptions);
      if (current !== generation.current) return;
      const result = await submitAccountFlow(reauthFlow, {
        csrf_token: findFlowCsrfToken(reauthFlow),
        method: "passkey",
        passkey_login: assertion
      });
      if (current !== generation.current) return;
      if (result.session) {
        await completeReauthentication(current);
        return;
      }
      setReauthFlow(result.flow);
    } catch (passkeyError) {
      if (current !== generation.current) return;
      setReauthError(accountErrorMessage(passkeyError));
      if (passkeyError instanceof ApiError && passkeyError.code === "auth_flow_expired") {
        setReauthFlow(null);
      }
    } finally {
      if (current === generation.current) setReauthBusy(false);
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
  const reauthNodes = reauthFlow?.ui.nodes.filter(supportedNode) ?? [];
  const google = capabilities?.google === true && kind !== "recovery" &&
    flow?.ui.providers?.some(provider => provider.id === "google") === true;
  const offerPasskeyLogin = kind === "login" && passkeyEnabled && passkeySupported &&
    Boolean(flow?.ui.passkey?.challengeOptions);
  const offerPasskeySettings = kind === "settings" && passkeyEnabled && Boolean(flow?.ui.passkey);
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
  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>{kind === "login" ? "Sign in" : kind === "settings" ? "Account settings" : "Recover account"}</CardTitle>
        <CardDescription>
          {kind === "settings"
            ? passkeyEnabled ? "Manage your password and passkeys." : "Change your password."
            : "Access your Minicloud account."}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!enabled ? <p>This authentication method is not enabled. Contact the host administrator.</p> : null}
        {busy && !flow ? <p role="status">Loading account form</p> : null}
        {complete ? <p role="status">{kind === "settings" ? "Password updated." : "Account verified."}</p> : null}
        {statusMessage && !complete ? <p role="status">{statusMessage}</p> : null}
        {notice ? <p role="alert" className="text-sm text-destructive">{notice}</p> : null}
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
        {kind === "settings" && reauthNeeded && !reauthFlow ? (
          <Button type="button" variant="outline" className="w-full" disabled={reauthBusy} onClick={() => { void startReauthentication(); }}>
            Verify identity to continue
          </Button>
        ) : null}
        {kind === "settings" && reauthFlow ? (
          <section aria-label="Re-authenticate" className="rounded-md border p-4 space-y-3">
            <div className="space-y-1">
              <h3 className="text-sm font-semibold">Verify your identity</h3>
              <p className="text-xs text-muted-foreground">Confirm your password or an existing passkey to update account settings.</p>
            </div>
            {reauthError ? <p role="alert" className="text-sm text-destructive">{reauthError}</p> : null}
            {passkeyEnabled && passkeySupported && reauthFlow.ui.passkey?.challengeOptions ? (
              <Button type="button" variant="outline" className="w-full" disabled={reauthBusy} onClick={() => { void submitReauthPasskey(); }}>
                Verify with passkey
              </Button>
            ) : null}
            <form key={reauthFlow.id} className="space-y-3" onSubmit={submitReauthPassword}>
              {reauthNodes.map((node, index) => {
                const id = `reauth-${index}`;
                const key = `reauth:${node.group}:${node.name}:${node.type}`;
                if (node.type === "hidden") return <input key={key} type="hidden" name={node.name} value={node.value ?? ""} />;
                if (node.type === "submit") {
                  return (
                    <div key={key} className="flex gap-2">
                      <Button type="submit" name={node.name} value={node.value ?? ""} disabled={reauthBusy}>
                        Confirm password
                      </Button>
                      <Button type="button" variant="outline" disabled={reauthBusy} onClick={() => { setReauthFlow(null); setReauthError(null); }}>
                        Cancel
                      </Button>
                    </div>
                  );
                }
                return (
                  <div key={key} className="space-y-2">
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
        {flow && !complete && offerPasskeyLogin ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={() => { void signInWithPasskey(); }}>
            Sign in with a passkey
          </Button>
        ) : null}
        {flow && !complete && google ? (
          <Button type="button" variant="outline" className="w-full" disabled={busy || restartRequired} onClick={continueWithGoogle}>
            {kind === "settings" ? "Link Google account" : "Continue with Google"}
          </Button>
        ) : null}
        {flow && !complete && offerPasskeySettings && flow.ui.passkey ? (
          <PasskeySettingsSection
            passkey={flow.ui.passkey}
            disabled={busy || restartRequired}
            supported={passkeySupported}
            onRegister={registerPasskey}
            onRemove={removePasskey}
            onRename={handleRenamePasskey}
          />
        ) : null}
        {enabled && (kind !== "login" || restartRequired) ? <Button variant="outline" disabled={busy} onClick={restartFlow}>Restart form</Button> : null}
        {kind === "login" && capabilities?.emailRecovery ? <Link className="block underline" to="/recovery" state={{ from: returnTo }}>Forgot password?</Link> : null}
        {kind === "recovery" ? <Link className="block underline" to="/sign-in" state={{ from: returnTo }}>Return to sign in</Link> : null}
      </CardContent>
    </Card>
  );
}
