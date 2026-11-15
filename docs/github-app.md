# Connect your own GitHub App

An organization owner or administrator can open **GitHub** in workspace
administration and select **Set up GitHub**. Alternatively:

```sh
minicloud github setup --organization <organization-id> --no-browser
minicloud github status --organization <organization-id> --json
minicloud github repositories --organization <organization-id>
```

The CLI prints a dashboard URL, not credentials. Sign in to Minicloud in the
browser and approve the same dashboard flow. Existing CLI tokens need the
`github:read` scope; logging in again obtains the current default read scopes.
CLI disconnect/reconnect/reconcile requires a separately granted `github:write`
scope. Browser configuration requires an owner/admin membership independently
of API-key scopes.

## Registration is not installation

1. Choose the exact personal account or organization that will own the App.
   You must be able to register an App there. Organization registration may
   require an organization owner and enterprise approval.
2. Approve GitHub's generated manifest. GitHub generates the App ID, private key,
   OAuth client credentials and webhook secret. The server exchanges the
   short-lived callback code and writes credentials directly to OpenBao.
3. Return to GitHub settings in Minicloud and choose **Install App / select
   repositories**. Install on the same GitHub account/organization and select
   only the repositories you intend this Minicloud organization to access.
4. Return to Minicloud and **Verify repository access**. Installation and
   repository ownership are verified with GitHub, not inferred from the
   browser's installation ID.

Each Minicloud organization has its own private, owner-controlled GitHub App,
with one installation on that App's owner account. A personal registration
cannot silently connect another organization. To use organization repositories,
register the App under that organization. Apps are private intentionally;
cross-account public distribution is not enabled by this flow.

The manifest requests only Contents read/write, Pull requests read/write and
mandatory Metadata read. It does not request Issues or Workflows permissions.
Installation lifecycle webhooks are delivered automatically by GitHub.
No Marketplace publication, central credential service or hand-copied private
key, client secret, webhook secret or environment variable is needed.

Development workspaces can use this connection only when an operator explicitly
selects its connection ID as `GitHubConnectionId` in the exact organization's
`DevelopmentWorkspaces:Repositories` push policy. The repository must be a
canonical `https://github.com/owner/repository.git` URL and remain accessible in
the connected installation. Git access receives a freshly verified, single
repository Contents-write token expiring within one hour; the App key and
installation-wide credentials never enter the workspace. Do not configure
`Credential` alongside `GitHubConnectionId`; the latter is renewable authority,
not a stored token, so it does not require `CredentialExpiresAt`.
Disconnecting or removing repository access stops renewal. Existing tokens may
remain valid until GitHub revokes them or their returned expiry. This does not
authenticate `gh`, Codex or Claude accounts.

## Status, recovery and credential rotation

Status distinguishes `setup_pending`, `converting`, `app_created` (installation
pending), `connected`, `repositories_unverified`, `suspended`, `revoked`,
`failed`, `disconnected` and `removing`. Repository listing always verifies
current GitHub access. Signed installation/repository-change webhooks clear
cached access immediately; **Reconcile with GitHub** refreshes authoritative
state after suspension, permission changes or missed webhooks.

Setup and installation callbacks expire after 30 minutes and belong to the
initiating Minicloud browser identity. Expired, mismatched and substituted
callbacks are rejected. Repeating the identical completed callback does not
repeat conversion or replace an installation.

If conversion is uncertain, do not register another App automatically. Check
the App on GitHub and restore OpenBao availability. Retrying the identical
callback can recover credentials already written to the recorded secret path,
but never repeats the one-use conversion. If GitHub returned credentials but
they never reached OpenBao, they cannot be reconstructed from the database.
An administrator must explicitly disconnect, remove the stored setup and
register a replacement.

**Disconnect** immediately disables use and invalidates pending callbacks, but
retains credentials for explicit **Reconnect existing App**. It does not
uninstall or delete the GitHub App. Reconnect verifies the existing installation
again. **Remove stored credentials / replace App** is separate and only allowed
after disconnect; it deletes all stored secret versions and the local setup
record. A failed removal retains a retryable `removing` record. Delete an unused
App or installation separately in GitHub settings.

For credential rotation without manual credential imports, disconnect, remove
stored credentials and register/install a replacement App. This changes App
identity and involves downtime. In-place private-key/webhook-secret rotation
is not implemented. Revoke/delete the old user-owned App on GitHub explicitly.
For recovery preserving App identity, restore coordinated PostgreSQL and
OpenBao backups (including OpenBao recovery material), then reconnect and
reconcile. Database backups contain only metadata and secret references, not
the App private key or client/webhook secrets.

## Operator requirements

Configure canonical public HTTPS `Console:BaseUrl` and `GitHub:ApiUrl`. The server
never derives manifest destinations from request Host or forwarding headers.
The packaged deployment maps these from `CONSOLE_URL` and `API_URL`.
Apply the current packaged OpenBao policy when upgrading: the API needs the
integration credential namespace in addition to existing app/provider paths.
Do not expose OpenBao tokens or recovery material to the dashboard or CLI.

Keep TLS verification enabled. The API's fixed GitHub client refuses redirects,
does not use cookies, bounds response sizes/timeouts and disables HTTP-client
URL logging because a conversion URL contains a temporary code. Dashboard
callbacks remove code/state from browser history immediately and use no-referrer,
no-store responses. Do not enable proxy/access/body logging that records callback
query strings or conversion request URLs. The packaged Caddy does not enable
access logging; custom proxies must redact `code`, `state` and secret bodies.
Webhook signatures use the exact request bytes and durable delivery receipts.
Missed or oversized webhooks require explicit reconciliation.
