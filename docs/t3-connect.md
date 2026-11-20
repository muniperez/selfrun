# T3 Connect for managed workspaces

T3 Connect is an **optional account link on an existing, running primary VM**,
not another VM type or an always-on compute plan. Viewing status, signing in on
another device and opening a link never start compute. Ordinary **Open T3**
continues to use only terminal/orchestration read and operate permissions.

On the workspace detail page, primary and ephemeral VMs show the startup
stages reported by the control plane. The dashboard checks status automatically
for a limited time and keeps the last reported status and costs visible if a
check fails. **Refresh workspace status** checks again without starting compute.

After you request an **Open T3** or **Open preview** link, its countdown shows
how long you have to open that single-use link, not how long the session lasts.
If it expires, select **Request new link**. The dashboard does not request or
open a replacement automatically.

## Link, check and unlink

1. As the workspace creator or an organization Owner, open a **ready primary**
   workspace and select **Manage T3 Connect**. An organization member without
   workspace ownership, a read-only user, an outsider and an API key cannot open
   this management route. Existing unqualified images do not expose this service:
   an operator must qualify the updated image/runtime before offering it. This
   feature never silently replaces or upgrades a running VM.
   The control is on the workspace's **Settings** tab, for the primary VM only,
   and is shown only to that workspace's creator or an organization Owner;
   temporary VMs never offer it. Organization,
   VM generation, readiness/revision and ownership changes discard pending launch
   responses instead of opening a stale account-management page. The API and
   gateway independently enforce ownership; hiding the control is not authorization.
2. Read the independent-access disclosure and choose **Link my T3 account**.
   The private page shows T3's device-approval link and code. Open it on your own
   device, verify the code matches and approve with your own T3 account. The page
   checks approval automatically and offers a new request if the code expires.
   Do not share, log, screenshot or paste approval material into a T3 terminal.
3. After approval, save work and finish agent turns, then explicitly choose
   **Activate and restart T3**. Confirm interruption of T3 turns and terminals. This
   restarts **only `minicloud-t3.service`**, on the same VM and the same
   `/workspace/t3` data directory. It does not install T3's background service,
   replace the VM, reinstall T3, or erase projects, threads or provider logins.
4. The page checks again during the restart and shows **Ready to connect** only
   when the running tunnel answers T3's reachability check. Otherwise a confirmed
   registration is shown as **Linked but not reachable**.
5. On another supported T3 client (for example `app.t3.codes`), sign in to that
   same T3 account, select the environment and open an **existing** project and
   thread. Resume it using the provider already configured on the VM. No other
   VM should appear in Minicloud's provider inventory because of linking.
6. Under **Manage connection**, **Unlink T3 Connect** disables the local link,
   clears the T3 CLI account sign-in and attempts relay-side machine removal.
   **Cancel approval** uses the same operation during a pending approval. This
   does **not** sign out Codex or another coding provider. If remote removal is
   not confirmed, remove the stale machine from your T3 account. Use this
   account-side removal if the VM is offline or was lost.

Approval codes and raw CLI output exist only in the management process's memory
and no-store HTTP responses. The service disables access/error journaling and
does not launch this flow in a terminal: pinned T3 persists terminal output under
`userdata/logs/terminals`. A management-service restart clears an unfinished
approval; start again explicitly. Pending approval is bounded to 15 minutes
(T3 may expire it sooner). Finish/cancel approval **before** requesting Stop;
otherwise shutdown fails closed until that operation finishes or expires.

## What account linking authorizes

Pinned T3 mints `cloud-connect` pairing credentials with
`AuthStandardClientScopes`, not Minicloud's reduced gateway scopes. These include
terminal, filesystem, source-control, provider-management, access-management and
relay-management authority. Linked clients can use configured provider sessions
and act as the coding user. Treat linking as granting that account shell-level
access to the entire workspace.

**Relay traffic does not pass through the Minicloud gateway.** Minicloud login,
organization membership removal, gateway receipt expiry, generation revocation
and gateway mutation fencing do not independently revoke T3 relay access.
Minicloud's idle detection still fails closed on unknown processes/active work;
an idle socket is not a keep-awake lease. A relay user may need an explicit
Minicloud keep-awake task lease for long interactive work.

Unlink stops the managed tunnel and removes local minting configuration; remote
account removal revokes the relay registration. Neither is documented upstream
as revoking every already-issued environment session or directly paired route.
For incident revocation, also revoke affected clients/sessions in T3
**Sharing & access**, remove separately paired routes/credentials and stop compute
through Minicloud as appropriate. Revoke Minicloud membership separately.
An unlinked T3 account must not discover or mint credentials for this machine.

Owner checks and a separate, root-readable **management bearer** protect the
Minicloud enrollment page. It is not a T3 bearer and grants no T3 API scopes.
The gateway injects it server-side only after reauthorizing the owner, current
generation, ready primary, heartbeat and route; API keys cannot obtain it.
Ordinary T3 bearer credentials cannot call the management service or T3's
`relay:read`/`relay:write` APIs. Users already entrusted with a coding shell are
not sandboxed against installing their own software/tunnels or obtaining their
own CLI authorization; owner-only enrollment is not a shell isolation boundary.

## Lifecycle and operator notes

- The reserved `connect` gateway service is root-reserved loopback port **774**, primary only.
  Keep both 3773 and 774 inaccessible publicly and from sibling VMs; preserve the
  existing private SSH forwarding/firewall contract. Do not register this as a
  user preview or change its port/unit. The service refuses startup if the
  kernel permits unprivileged binding to 774: otherwise a coding process could
  impersonate a stopped listener and capture its gateway-injected management
  bearer. Do not lower `net.ipv4.ip_unprivileged_port_start` past this port.
  Before **every** Connect route publication/refresh (including cached OpenBao
  references), the worker requires a generation-bound admission proof over its
  host-key-pinned management SSH connection. The root helper rechecks current
  kernel reservation, immutable root-owned helper/unit files, active root
  MainPID/cgroup/command, and the loopback listening socket's UID and inode
  ownership. Credential export independently applies the same checks. HTTP 200
  alone is never admission; missing or unsafe evidence revokes only Connect's
  route, leaving ordinary T3 access unchanged.
  Account linking requires T3's hosted
  identity/relay endpoints and cloudflared connectivity (including supported
  outbound QUIC/HTTP2); opening inbound service ports is not a workaround.
- The image recipe installs upstream's pinned, checksum-verified cloudflared
  `2026.10.4` root-owned. The idle guard recognizes only that exact binary,
  T3-parented process, service cgroup and pinned tunnel arguments; other tunnels,
  helpers, child processes or injected loader environments still block shutdown.
  This is infrastructure recognition, not evidence that application work is idle.
- `t3 connect link --headless --base-dir /workspace/t3` is the supported
  non-service-installing enrollment command. At the pinned revision it stores
  desired account linking for **next server startup**. Activation therefore uses
  the existing Minicloud unit under the runtime's generation/config/Ready lock.
  Never run bare `t3 connect`, `t3 up`, `t3 service install`, or start another
  `t3 serve` to implement this flow. The web/desktop linking API is intentionally
  not impersonated: upstream's `canLinkHere` disallows remote/proxied sessions.
- The root-only management bearer is bound to the VM/generation in disposable
  `/var/lib/minicloud-workspace`, stored only by reference in OpenBao on the
  control plane, never returned to the browser. Account credentials stay in T3's
  secret store on the retained `/workspace/t3` volume. Protect/encrypt backups
  accordingly. Generalization removes workspace, runtime state and account
  credentials; **never capture an enrolled workspace as a reusable machine image**.
- A controlled service restart retains environment identity, projects, threads
  and provider sign-ins. After a crash or OOM kill, systemd restarts T3 on the same VM
  within a bounded retry budget. Connect management returns with T3. Restart does
  not resume an interrupted agent turn. Root-owned admission locking and a pending-operation
  fence prevent CLI credential writes racing volume quiescence. Root service
  `PartOf=minicloud-t3.service` stops enrollment workers with T3; a weak `Wants`
  dependency brings primary management back on T3 start/resume without making
  optional account-management failure a prerequisite for ordinary Open T3.
- Stop/delete makes the environment offline; it does not promise immediate
  removal from the T3 account. Upstream releases/re-registers managed tunnels
  around shutdown/startup. An explicit Start with the **same retained volume**
  should preserve the environment identity and recover the tunnel. New volumes,
  old backup restores, account revocation or lost secrets may require re-linking
  and account-side stale-registration cleanup. Never run two copies of the same
  retained T3 identity concurrently. Stale gateway generations cannot use the
  management credential for a replacement VM.

## Registering an image for new dev environments

The Connect-capable image recipe on Minicloud main includes the Connect
management unit and admission helper from #150 (merged at
`e8cbb6232bad3d1881b519053b7336d4bdd2aa18`). Its image lock pins upstream
T3 `v0.1.9` at `24e77473ecf41bc1f7142529c7911c1c1aae1d76` and cloudflared
`2026.10.4`. These are source pins, not proof of the active hosted snapshot;
the hosted snapshot ID, installed manifest digest and qualification are not
verified here.

Before enabling creation, verify the intended provider snapshot against its
qualification evidence: the installed `/opt/minicloud-workspace/image-manifest.json`
must record the expected Minicloud `provenance.sourceRevision`, contain the
expected component pins, and hash to the configured `ManifestSha256`. Use the
[prepared image qualification procedure](../templates/development-workspaces/README.md)
to verify Connect runtime admission on a disposable primary VM; a label or a T3
version alone does not qualify a snapshot.

In the protected API and worker `DevelopmentWorkspaces` catalog, register that
revision under a distinct, stable `Images` key. Set `DisplayName` to
**Muni T3 with Connect**, record its `ProviderImageId`, `ProviderAccountId`,
`AllowedRegions`, `Architecture` (`linux/amd64`), `ManifestSha256`, `Enabled`,
`RuntimeQualified` and `QualifiedAt`, and set `T3ConnectForNewWorkspaces: true`
only on the qualified revision intended for new environments in that account.
Approve matching profiles and organization-owned plans. Restart both services
after updating the protected file and verify that unscoped discovery returns
only the intended revision, or no choices when it has no eligible plan.
Omitting the flag defaults to false. Creating a stopped environment or starting
compute still does not link a T3 account.

Retain the catalog entries and provider snapshots referenced by existing VMs or
needed for rollback. To withdraw a revision from creation, clear its
`T3ConnectForNewWorkspaces` flag rather than disabling or deleting it. A restart
without an explicit image change still requires the recorded snapshot and
manifest to match qualified configuration; do not repoint an existing image key.
Use a distinct key for each revision and explicitly select it for next start
when upgrading or rolling back an existing VM.

## Qualification status

Source is pinned to T3 `v0.1.9`,
[`24e77473ecf41bc1f7142529c7911c1c1aae1d76`](https://github.com/pingdotgg/t3code/tree/24e77473ecf41bc1f7142529c7911c1c1aae1d76):
`docs/user/remote-access.md`, `apps/server/src/cli/connect.ts`,
`cloud/CloudLink.ts`, `cloud/linkChecks.ts`, `cloud/http.ts`,
`packages/contracts/src/auth.ts` and `packages/shared/src/relayClient.ts`.
The issue's shorter `24e77473ecf41bc1f7142529c7911c1aae1d76` URL is a typo;
the checked-in image lock and source above use the full commit.

Deterministic tests cover owner/non-owner and generation checks, separate local
management authorization, exact supported CLI invocation, pending-flow shutdown
fencing, same-unit activation, redacted status, memory-only approval cleanup,
remote-deletion uncertainty, pinned relay process recognition and no implicit
compute/session launches. Regression fixtures also reject an unprivileged-port
policy with spoofed HTTP 200, cached credential-reference bypasses, and missing
or incorrectly owned management listeners. The disposable native harness now requires a probe
that ordinary gateway credentials cannot access either enrollment management
or T3's relay APIs. **That native probe has not been run for this change.**

The following remain **unqualified**, not simulated successes: build/register
the updated image; actual systemd restart/PartOf/cloudflared-stop behavior; live
T3 device approval and reachability; a second physical device finding the same
environment/thread and resuming its real provider; a different T3 account being
denied; Minicloud user denial against a deployed gateway; account-side revocation,
already-issued session behavior, offline listing, stop/start and replacement
with the retained volume; backup/new-volume stale-registration removal. Use a
disposable authorized primary VM and two real T3 accounts/devices to qualify
these before marking issue #146's hosted acceptance complete. Do not retain
approval codes, credentials, private project contents or provider sign-ins in
evidence.
