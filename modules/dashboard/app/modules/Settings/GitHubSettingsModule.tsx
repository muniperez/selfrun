import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { apiRequest } from "@/api/apiClient";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useAuth } from "@/core/auth/AuthProvider";
import { PageScaffold } from "@/core/components/PageScaffold";

type Connection = {
  organizationId: string; status: string; connectionId?: string; ownerLogin?: string;
  appSlug?: string; installationId?: number; failureCode?: string; lastVerifiedAt?: string;
};
type Repository = { id: number; fullName: string; private: boolean };
type Manifest = { connectionId: string; postUrl: string; manifest: unknown };

function message(error: unknown) {
  return error instanceof Error ? error.message : "GitHub connection could not be updated.";
}

function githubDestination(value: string) {
  const url = new URL(value);
  if (url.origin !== "https://github.com" || url.username || url.password)
    throw new Error("Untrusted GitHub destination.");
  return url.href;
}

export function GitHubSettingsModule() {
  const { selectedOrganization, selectOrganization } = useAuth();
  const [connection, setConnection] = useState<Connection | null>(null);
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [owner, setOwner] = useState("");
  const [ownerType, setOwnerType] = useState("User");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const organizationId = selectedOrganization?.id;
  const activeOrganization = useRef(organizationId);
  activeOrganization.current = organizationId;
  const requestedOrg = new URLSearchParams(window.location.search).get("organizationId");
  const admin = ["owner", "admin"].includes(selectedOrganization?.role.toLowerCase() ?? "");

  async function refresh() {
    if (!organizationId) return;
    const result = await apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organizationId)}`);
    if (activeOrganization.current === organizationId) setConnection(result);
  }
  useEffect(() => {
    if (requestedOrg && requestedOrg !== organizationId) {
      if (!selectOrganization(requestedOrg)) setError("The requested organization is not available to your account.");
      return;
    }
    let active = true;
    setConnection(null); setRepositories([]); setError("");
    if (organizationId) {
      void apiRequest<Connection>(`/api/github-apps?organizationId=${encodeURIComponent(organizationId)}`)
        .then(value => { if (active) setConnection(value); })
        .catch(reason => { if (active) setError(message(reason)); });
    }
    return () => { active = false; };
  }, [organizationId, requestedOrg, selectOrganization]);

  async function run(action: () => Promise<void>) {
    setBusy(true); setError("");
    try { await action(); } catch (reason) { setError(message(reason)); }
    finally { setBusy(false); }
  }
  async function begin() {
    const result = await apiRequest<Manifest>("/api/github-apps/setup", {
      method: "POST", body: { organizationId, ownerLogin: owner, ownerType },
    });
    const form = document.createElement("form");
    form.method = "POST"; form.action = githubDestination(result.postUrl);
    const manifest = document.createElement("input");
    manifest.type = "hidden"; manifest.name = "manifest"; manifest.value = JSON.stringify(result.manifest);
    form.append(manifest); document.body.append(form); form.submit();
  }
  async function mutate(action: string) {
    await apiRequest(`/api/github-apps/${connection!.connectionId}/${action}`, { method: "POST" });
    setRepositories([]); await refresh();
  }

  return <PageScaffold breadcrumbs={[{ label: "Settings" }, { label: "GitHub" }]} title="GitHub">
    <p>Register your own private GitHub App, then install it on the same personal account or organization.
      Creating an App does not install it or grant repository access.</p>
    <p>Requests Contents and Pull requests read/write and mandatory Metadata read.
      Organization registration requires an organization owner; enterprise policies may require approval.</p>
    {error && <p role="alert">{error}</p>}
    {connection && <p role="status">Status: {connection.status}{connection.ownerLogin ? ` - ${connection.ownerLogin}` : ""}</p>}
    {connection?.failureCode && <p>Last failure: {connection.failureCode}. Check GitHub ownership, approval and App settings, then reconcile.</p>}
    {connection?.appSlug && <a href={`https://github.com/apps/${encodeURIComponent(connection.appSlug)}`} target="_blank" rel="noreferrer">View your GitHub App</a>}
    {admin && connection?.status === "not_configured" && <form className="space-y-3" onSubmit={event => { event.preventDefault(); void run(begin); }}>
      <label htmlFor="github-owner">GitHub owner login</label>
      <Input id="github-owner" value={owner} onChange={event => setOwner(event.target.value)} required maxLength={39} />
      <label htmlFor="github-owner-type">App owner type</label>
      <select id="github-owner-type" value={ownerType} onChange={event => setOwnerType(event.target.value)}>
        <option value="User">Personal account</option><option value="Organization">Organization</option>
      </select>
      <Button disabled={busy || Boolean(requestedOrg && requestedOrg !== organizationId)} type="submit">Set up GitHub</Button>
    </form>}
    {admin && connection?.connectionId && <div className="flex flex-wrap gap-3">
      {["app_created", "revoked", "suspended", "repositories_unverified"].includes(connection.status) &&
        <Button disabled={busy} onClick={() => void run(async () => {
          const result = await apiRequest<{ url: string }>(`/api/github-apps/${connection.connectionId}/installation-url`, { method: "POST" });
          window.location.assign(githubDestination(result.url));
        })}>Install App / select repositories</Button>}
      {connection.installationId && connection.status !== "disconnected" &&
        <Button disabled={busy} onClick={() => void run(() => mutate("reconcile"))}>Reconcile with GitHub</Button>}
      {connection.status === "disconnected" ?
        <Button disabled={busy} onClick={() => void run(() => mutate("reconnect"))}>Reconnect existing App</Button> :
        <Button disabled={busy} variant="outline" onClick={() => void run(() => mutate("disconnect"))}>Disconnect</Button>}
      {["disconnected", "removing"].includes(connection.status) && <Button disabled={busy} variant="outline" onClick={() => {
        if (window.confirm("Remove stored App credentials? The user-owned GitHub App will NOT be deleted. Delete an unused App separately on GitHub before registering a replacement."))
          void run(async () => { await apiRequest(`/api/github-apps/${connection.connectionId}`, { method: "DELETE" }); await refresh(); });
      }}>Remove stored credentials / replace App</Button>}
    </div>}
    {!admin && <p>Only organization owners and administrators can configure this integration.</p>}
    {connection?.status === "connected" && <Button disabled={busy} onClick={() => void run(async () => {
      setRepositories([]);
      const result = await apiRequest<Repository[]>(`/api/github-apps/${connection.connectionId}/repositories`);
      if (activeOrganization.current === organizationId) setRepositories(result);
      await refresh();
    })}>Verify repository access</Button>}
    {repositories.length > 0 && <ul aria-label="Accessible repositories">{repositories.map(repo =>
      <li key={repo.id}>{repo.fullName}{repo.private ? " (private)" : ""}</li>)}</ul>}
    <p>This connects the control plane only. It does not enable automatic clone, push or pull-request authentication for any app.</p>
  </PageScaffold>;
}

const callback = typeof window !== "undefined" && /^\/settings\/github\/(callback|installed)$/.test(window.location.pathname)
  ? { path: window.location.pathname, parameters: new URLSearchParams(window.location.search) } : null;
if (callback) window.history.replaceState(null, "", callback.path);

export function GitHubCallbackModule() {
  const { status } = useAuth();
  const started = useRef(false);
  const [result, setResult] = useState("Verifying GitHub callback...");
  useEffect(() => {
    if (status !== "authenticated" || started.current) return;
    started.current = true;
    const parameters = callback?.parameters;
    const connectionId = parameters?.get("connectionId");
    const state = parameters?.get("state");
    if (!parameters || !connectionId || !state) { setResult("Missing callback context. Return to GitHub settings and start again."); return; }
    const installation = callback?.path.endsWith("/installed");
    void apiRequest<Connection>(installation ? "/api/github-apps/installation" : "/api/github-apps/complete", {
      method: "POST", body: installation
        ? { connectionId, state, installationId: Number(parameters.get("installation_id")) }
        : { connectionId, state, code: parameters.get("code") },
    }).then(value => setResult(`GitHub status: ${value.status}. ${installation ? "Repository access verified." : "App created. Install it next to grant repository access."}`))
      .catch(reason => setResult(message(reason)));
  }, [status]);
  return <main className="mx-auto max-w-xl space-y-4 p-6">
    <h1>GitHub connection</h1>
    <p role="status">{status === "authenticated" ? result : status === "initializing" ? "Checking your Minicloud session..." : "Sign in to Minicloud, then restart GitHub setup. No callback credentials have been saved in browser storage."}</p>
    <Link to="/settings/github">Return to GitHub settings</Link>
  </main>;
}
