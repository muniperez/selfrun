# Minicloud API

Minicloud uses one unversioned first-party namespace: `/api`.
The prelaunch application/environment cutover deliberately removes old product
API routes and identity aliases. Clients and server must be updated together.

- `/api/apps` lists/creates applications with environment summaries.
- `/api/apps/{applicationId}` reads/updates/deletes an application.
- `/api/apps/{applicationId}/environments` lists/creates its environments.
- `/api/apps/{applicationId}/environments/{environmentId}` reads/updates/deletes
  one environment, with ownership and lifecycle checks.
- `/api/environments/{environmentId}` returns the runtime descriptor used by
  operational clients. Its telemetry, logs, storage and image actions use the
  same explicit environment identity. Plan and preview actions also target this
  environment resource; they do not resolve application aliases.
- Other product resources use `/api`, including providers, plans, deployments,
  secrets, runtime ingestion, registry credentials and CLI authentication.

Anonymous `GET /api/instance` returns `product`, `protocolVersion` and `appDomain`.
Packaged servers additionally report `serverVersion`, `sourceRevision` and
`cliCompatibility` from verified release metadata. Compatibility includes the
protocol, optional half-open version interval and explicit source revisions;
it does not imply that CLI and server versions match. Source builds without
release metadata leave these additional fields null. See the
[server release contract](../packaging/server/README.md).

Application creation accepts nested `initialEnvironment` configuration. Unknown
flat creation fields are rejected. Configuration `appId` is a canonical
application ID. Deployments require environment selection when multiple active
persistent environments exist. No client retries against an older API, resolves
runtime IDs as application aliases, or implicitly chooses the dashboard default
for CLI deployment.

Every request retains organization authorization and resource ownership checks.
Browser users authenticate through instance-local Kratos and opaque API sessions;
existing users require explicit host-side identity binding, never email adoption.
Summary reads
select only the latest non-asset deployment and active domains; no full history
graph or response cache is needed.

Docker Registry, OTLP, OpenBao, Loki and provider version identifiers belong to
those external protocols and remain unchanged. They are not parallel Minicloud
product APIs. Stored migration metadata preserves existing resources without
publishing identity-resolution aliases.

## Development workspace status and selections

`GET /api/development-workspaces/{id}?organizationId={id}` can report
`vms[].startupStage` for the VM's current startup operation: `preparing_vm`,
`preparing_storage` (primary only), `setting_up`, or `checking_readiness`.
An absent stage means the precise startup step is unknown. A blocked start
can report its last recorded stage; only a ready VM phase confirms readiness.

Workspace list/detail responses include optional `createdBy` (the workspace
creator's actor ID). The dashboard uses it only to present primary T3 Connect
management to the creator or an organization Owner. The Connect launch and
gateway endpoints independently reauthorize the current dashboard identity;
this field does not confer authority and ordinary workspace managers do not
automatically receive account-linking permission.

`GET /api/development-workspaces/selections?organizationId={id}` requires
authentication and organization app-read permission (`apps:read` for API keys).
Without `workspaceId`, it returns only qualified T3 Connect images explicitly
approved for new dev environments.
Add `&workspaceId={id}` for next-start resize or ephemeral launch choices: these
must remain in that workspace's fixed provider account and region. A missing or
foreign workspace returns `404 workspace_not_found`; organization access denial
returns 403, and anonymous requests return 401.

The uncached response is:

```json
{
  "available": true,
  "unavailableReason": null,
  "options": [
    {
      "imageId": "example-connect-revision",
      "imageName": "Muni T3 with Connect",
      "profile": "standard",
      "planId": "plan_example",
      "planName": "Development standard",
      "providerAccountName": "Primary Hetzner",
      "region": "nbg1",
      "sku": "cx-example"
    }
  ]
}
```

Submit `imageId`, `profile` and `planId` unchanged to the existing create, resize
or ephemeral-launch write contracts. IDs are stable configuration/plan identities,
not labels or option indexes; labels may change. Configure the optional
`DevelopmentWorkspaces:Images:{imageId}:DisplayName` for a human-readable image
label. Omitted or blank labels display the image configuration key (`imageId`);
supplied labels are trimmed.

Discovery shares selection eligibility with writes: explicitly enabled,
runtime-qualified amd64 images with a valid manifest and qualification timestamp;
approved 8/16 GiB profiles with positive CPU/pricing and explicit plan, SKU and
region allowlists; and enabled organization-owned Hetzner accounts/plans with
matching image account, architecture and regions. Cross-organization entries and
credential references are never returned. Discovery only reads control-plane
metadata: it performs no credential resolution, provider requests, reservation,
allocation or mutation.

For new environments, the image entry must also have
`DevelopmentWorkspaces:Images:{imageId}:T3ConnectForNewWorkspaces` set to `true`.
Create rejects stale or unapproved image selections with `workspace_image_invalid`;
there is no legacy fallback. This approval does not affect workspace-scoped
selections or restart on an existing VM's recorded image, and does not link a
T3 account. See [image registration](t3-connect.md#registering-an-image-for-new-dev-environments).

- `available: true` with an empty `options` array means configuration is available
  but no compatible selections exist for this organization/placement (for
  unscoped discovery, no approved Connect image has a compatible plan).
- `available: false`, a human-readable `unavailableReason` and empty `options`
  means workspaces are disabled or required quota/pricing/lease configuration is
  unavailable or invalid (including stale pricing).
- Catalog database/time-out failures return sanitized HTTP 503
  `workspace_selections_unavailable`, not an empty catalog. Other HTTP failures
  remain failures, not configuration states.

Discovery is advisory, not a capacity, spend or credential-availability guarantee.
Writes revalidate current selection and placement policy. Creation leaves the
workspace stopped and checks retained-storage spend. Starting or launching
compute checks VM concurrency and compute-plus-storage spend; provider quotas
still apply. Neither operation limits the number of workspace records or the
number of distinct active workspaces. Resize records a next-start selection
without reserving compute; start revalidates it again. Existing idempotency,
generation and repository-approval requirements are unchanged.

## Development workspace idle timeout

Workspace list and detail responses include `idleTimeoutMinutes` (the saved
whole-minute override, or `null` to inherit) and `effectiveIdleTimeoutMinutes`
(the whole-minute timeout in use). They use the existing organization app-read
authorization. `POST /api/development-workspaces` accepts optional
`idleTimeoutMinutes`; omission or `null` inherits and remains compatible with
existing create callers and idempotent retries.

`PUT /api/development-workspaces/{id}/idle-timeout?organizationId={id}` takes
`{"idleTimeoutMinutes":90}` and returns the updated workspace. Send explicit
`null` to restore inheritance. The field is required on update, and overrides
must be JSON integers from 1 through 10,080 minutes (7 days); zero does not
disable shutdown. Updates use the existing workspace-management authorization
(organization app-write, `apps:write` for API keys) and browser CSRF protection.

Each VM uses its workspace's effective timeout independently: the saved override
wins, then `DevelopmentWorkspaces:IdleGraceMinutes`, then 360 minutes (6 hours).
The timeout measures inactivity, not maximum VM lifetime. Changing it applies
to running and future VMs, can cancel a premature unclaimed idle countdown,
and does not undo a claimed shutdown. A shorter timeout lets eligible VMs enter
the normal countdown on the next monitor pass. The countdown and gateway drain
are separate. Shutdown deletes compute while retaining primary storage; compute
charges continue until deletion is confirmed, and a longer timeout may increase
cost. Dashboard users manage the value in **Workspaces → workspace → Settings**.

## Browser accounts

The dashboard uses credentialed requests only to its configured API origin.
Session cookies are Secure, HttpOnly and host-only. Session and CSRF secrets are
not stored in browser storage. `/api/me` retains its existing organization
membership response for both browser users and authorized machine credentials.

| Route | Behavior |
| --- | --- |
| `GET /api/account/capabilities` | Password/optional-method availability and authoritative `required`, `ready` or `blocked` bootstrap state. |
| `GET /api/account/session` | Browser user ID, absolute/idle expiry and session-bound CSRF token. |
| `POST /api/account/logout` | Revoke the browser session and Grafana access; clear authentication cookies without revoking API keys. |
| `GET /api/account/flows/{kind}/browser` | Start `login`, `settings` or enabled `recovery`; `returnTo` must be a same-dashboard relative path. |
| `GET /api/account/flows/{kind}?id={uuid}` | Read an existing matching form. |
| `POST /api/account/flows/{kind}?flow={uuid}` | Submit the form once; return a sanitized form or rotated browser-session metadata, never a session-cookie secret. |
| `POST /api/account/flows/{kind}/oidc?flow={uuid}&returnTo={path}` | Optional Google only (`login` or `settings` link). Body `{csrf_token, provider:"google"}`; returns `{provider, redirectUrl}` for explicit top-level navigation. |
| `GET /api/account/oidc/self-service/methods/oidc/callback/google` | Fixed provider callback. Completes the pending flow and answers `303` to the dashboard's `/sign-in/continue`. |

Settings and other cookie-authenticated mutations require exact dashboard Origin
and `X-Minicloud-CSRF`. Initial login/recovery submissions instead require exact
Origin and the Kratos form's CSRF field/cookie. Browser credentials never replace
machine authentication: explicit bearer credentials take precedence, including
invalid bearers. CLI approval still requires explicit human consent and
organization API-key-write permission.

Account responses are not cacheable. Confirmed expiry is 401; authorization denial
remains 403/resource-hiding 404. Identity dependency failures are sanitized 503
`auth_unavailable`, not a logout or a setup transition. Forms have explicit restart
actions and mutations are never automatically replayed.

Public registration is disabled. Email recovery is optional and requires
separate SMTP/integration qualification; password-only use does not depend on
either optional method.

When the dashboard detects an expired sign-in form, it requests a fresh browser
flow. It does not resubmit a password or start a Google redirect automatically;
the user continues sign-in from the new form.

### Optional Google sign-in

Google is off by default (`Kratos:GoogleEnabled=false`). The packaged installer
supports deliberate opt-in through `googleOidc.enabled` and a protected
credential-input file; see [packaged setup](self-hosting.md#optional-packaged-google-oidc).
Configuration enables the option, not a live qualification claim. The current
API has no separate `GoogleQualified` setting. Live Google and separate-origin
HTTPS qualification remain
tracked in [the authentication follow-up](https://github.com/muniventures/minicloud/issues/44).

- **Discovery.** `ui.providers` is present only when Google is enabled and the
  Kratos flow offers it. Each entry is `{id:"google", action, method:"POST"}`;
  `action` is exactly `/api/account/flows/{kind}/oidc?flow={id}`. Upstream OIDC
  nodes are never forwarded, and the ordinary submit route rejects `provider`.
- **Navigation.** The dashboard shows "Continue with Google" or "Link Google
  account" only when both the capability and the provider entry are present.
  A click posts the flow CSRF token to the provider action with the exact
  dashboard Origin (`settings` also needs `X-Minicloud-CSRF`). The API submits
  the Kratos `oidc` method and accepts only Kratos's 422
  `browser_location_change_required` response. Its target must be
  `https://accounts.google.com/o/oauth2/...` on the default port, with no user
  info or fragment. It must have one `client_id`, one `state`,
  `response_type=code`, and a `redirect_uri` equal to this API's fixed callback.
  Anything else returns 503 `auth_unavailable`. The browser, not the API, then
  opens the URL. The API never follows upstream redirects or contacts the
  provider, and it forwards no Minicloud cookie or bearer.
- **Callback.** The provider action sets a 10-minute `__Host-minicloud-oidc`
  cookie. It records the kind, flow ID, a SHA-256 hash of the provider state
  and the validated same-dashboard return path. The callback requires that
  cookie and a matching `state`, then deletes the cookie. It forwards only
  `state` with either `code` or `error` to the Kratos callback, with allowlisted
  flow cookies, including `ory_kratos_continuity`. It then admits only the bound
  identity, as for password login. Settings must match the current browser
  user. The callback redirects to
  `/sign-in/continue?result=success|failed|forbidden|unavailable&kind=...&returnTo=...`.
  On failure, the redirect can include `flow` for the same pending flow. The
  dashboard then re-reads it, or after success returns to the safe path, such
  as explicit CLI consent.

The installer derives `MINICLOUD_GOOGLE_OIDC_ENABLED` from the explicit
configuration and Compose passes it to both `Kratos__GoogleEnabled` on the API
and `SELFSERVICE_METHODS_OIDC_ENABLED` on Kratos (both default false).
`SELFSERVICE_METHODS_OIDC_CONFIG_BASE_REDIRECT_URI` is always derived as
`<API_URL>/api/account/oidc`; the resulting Google redirect URI is
`<API_URL>/api/account/oidc/self-service/methods/oidc/callback/google`.
The installer imports only the fixed `google` provider into the protected
`kratos.env` as `SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS`, using the bundled
read-only verified-email Jsonnet mapper. Unknown environment/configuration
fields still fail validation; manually adding enable/callback overrides to
`kratos.env` is not supported. Minicloud ships no default client or secret.
Google login admits only identities already bound to a Minicloud user. An
operator first links Google from account settings; public registration stays
disabled. Keep the public API Host intact through the reverse proxy.
