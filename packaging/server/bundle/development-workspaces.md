# Optional development workspaces

Disabled by default. The base `compose.yml`, existing service credentials,
volumes and release image defaults are unchanged. This source integration is
not live qualification. Do not enable until provider/network isolation, runtime
image, pinned SSH host keys, T3 issuance and stop/drain/recovery are qualified.
No toolchain is installed on each VM start: prepare and qualify an immutable
workspace image first.

## Protected configuration

1. Set `MINICLOUD_WORKSPACE_SECRETS_DIR` to a separate root-owned 0700 directory
   **outside** the private server installation root, for example
   `/etc/minicloud-workspaces`. Keep the existing `MINICLOUD_SECRETS_DIR`
   unchanged. Installation/maintenance validation requires every file inside
   the installation root to remain 0400/0600; do not weaken that invariant.
   Copy `development-workspaces.example.json` to
   `$MINICLOUD_WORKSPACE_SECRETS_DIR/development-workspaces.json`, owned by
   root:1654, mode 0440, with its parent directory root-only.
   The example intentionally has no image IDs, organization IDs, provider
   credentials or available capacity. Populate qualified image manifests,
   allowed plans/SKUs/regions, current pricing and explicit spend ceilings,
   per-organization repository/ref policies and `DevelopmentWorkspaceWorker`
   `ManagementAddresses` (every control-plane/private management address to
   block). Only then set `DevelopmentWorkspaces.Enabled=true`.
2. Preprovision a **separate** random service credential (at least 32 characters)
   at `$MINICLOUD_WORKSPACE_SECRETS_DIR/workspace-gateway-credential`. Never overwrite an
   existing credential as part of startup. For the image's UID/GID 1654, use
   root:1654 ownership and mode 0440 on this file; keep its parent root-only.
   Docker Compose file secrets are bind mounts: do not rely on secret `mode`
   rewriting the source permissions. Only API and gateway receive this secret.
   Rotate deliberately by replacing the file and recreating both containers.
3. API and worker load exactly the absolute file named by
   `DevelopmentWorkspaces__ConfigurationFile`, without reload, then reapply
   environment overrides. An unset/empty variable loads nothing and preserves
   existing behavior; a missing/invalid explicit file fails startup. Restart
   both after protected configuration changes.

Set `DevelopmentWorkspaces:Images:<image-key>:T3ConnectForNewWorkspaces` to
`true` only for the qualified Connect-capable revision intended for new
environments. Verify the provider snapshot and installed image-manifest digest
against the qualification record before setting it. Set `DisplayName` to
**Muni T3 with Connect**; the label does not control eligibility. If no approved
revision matches an eligible plan, new environment creation is unavailable.
Leave the flag unset or `false` on older revisions; keep the qualified catalog
entries and matching snapshots for existing VM restarts and rollback. Use a new
image key for each new snapshot and manifest rather than replacing an old key.

Do not put provider tokens, SSH keys, OpenBao tokens/mounts, database credentials
or the full workspace configuration in the gateway. It receives only its
dedicated service credential and transport configuration.

For workspaces without a saved idle timeout, `DevelopmentWorkspaces.IdleGraceMinutes`
is the operator default in the protected JSON. Keep a deliberately configured
value; when omitted it defaults to 360 minutes (6 hours). A saved workspace
setting takes precedence for both VM kinds. A dev environment set to **Keep
running** never stops for inactivity; spend limits and manual stop still apply.
Apply the database migration and update the API and worker together before
reopening workspace operations.

## Opt-in startup

Build the standalone gateway from the repository root with
`docker build -f modules/workspace-gateway/Dockerfile ... .`, or preload a
qualified immutable image. Set `MINICLOUD_WORKSPACE_GATEWAY_IMAGE` to the exact
local image ID or immutable registry digest, never `latest`. The override uses
`pull_policy: never`: pre-pull an authorized registry digest explicitly.
Set `MINICLOUD_WORKSPACE_GATEWAY_DOMAIN` to an operator-owned dedicated suffix
(for example `workspaces.example.test`, documentation only). Configure wildcard
DNS `*.<suffix>` to Caddy and allow inbound TCP 80/443. Use a publicly resolvable
real domain and appropriate ACME issuer for deployment.

From the bundle directory, retain the installation's existing environment
and verified `images.env`, set the external workspace secrets directory, then
explicitly opt in:

```sh
export MINICLOUD_WORKSPACE_SECRETS_DIR=/etc/minicloud-workspaces
docker compose --env-file images.env -f compose.yml -f compose.workspaces.yml \
  --profile workspaces config --quiet
docker compose --env-file images.env -f compose.yml -f compose.workspaces.yml \
  --profile workspaces up -d api worker workspace-gateway caddy
```

Keep required installation inputs in the operator environment as with ordinary
startup; do not print resolved configuration or secrets into support logs.
Including the override already modifies API/worker/Caddy even without its
profile: use **both files and the profile together**, or omit the override
entirely. Normal maintenance commands only know base Compose; reconcile the
optional gateway explicitly after upgrade/restore and stop/drain workspaces
before operations that restart the control plane. Before reverting to base,
drain/revoke workspaces and remove the optional gateway with the same override.

Caddy retains the exact API and console hosts and existing private network.
Its separate HTTPS catch-all proxies only `*.<GatewayDomain>`. On-demand
**individual leaf** certificates use HTTP-01 (not wildcard certificates or
DNS-01); the private gateway ask endpoint authorizes only current approved
workspace hosts/generations. No DNS-provider secret is needed. No public
unauthenticated route resolver is exposed. Keep access/query/body logging and
URL tracing off at Caddy and every upstream collector: launch links and
WebSocket query tickets are confidential. The optional Caddy configuration also
excludes HTTP error-request logging, which otherwise captures full request URIs
even without an access log.

## Isolation and limits

Workspace records are not subject to a count limit. Creating a workspace leaves
its primary VM stopped and checks the retained-storage estimate against the
configured spend caps. Starting a VM checks global, per-organization and
per-workspace VM concurrency limits and the combined compute and storage
estimate. Provider quotas still apply. Obsolete workspace-count settings in
existing policy files are ignored and can be removed.

The gateway is separately supervised: 0.5 CPU, 256 MiB memory, 128 PIDs,
read-only root, bounded 16 MiB `/tmp` tmpfs, no capabilities, no privilege
escalation and 3 x 10 MB rotated logs. It publishes no host port. Verify service
readiness and a complete authorized launch/revocation flow before admission.

Two **new** bridges leave the existing private subnet unchanged:

* `workspace-gateway-proxy`: `172.29.110.0/24`, Caddy `.2`, gateway `.3`.
  Only Caddy's exact private IP is trusted for HTTPS forwarding and certificate
  asks. API calls use `http://api:8080` over the existing private network.
* `workspace-relay`: internal `172.29.111.0/24`, worker `.2`, gateway `.3`
  only. SSH.NET forwards bind the worker's literal `172.29.111.2`, never
  `0.0.0.0`; gateway `RelayHost` uses that same literal. Ports 30000–39999
  are container-private, with no host publication.

Worker `ApiBaseUrl` uses the existing public HTTPS `API_URL` for VM enrollment
and heartbeat; the container-private gateway control-plane URL is not usable
from a VM.

If subnets conflict, set `MINICLOUD_WORKSPACE_PROXY_SUBNET`,
`MINICLOUD_WORKSPACE_CADDY_ADDRESS`, `MINICLOUD_WORKSPACE_GATEWAY_ADDRESS`,
`MINICLOUD_WORKSPACE_RELAY_SUBNET`, `MINICLOUD_WORKSPACE_RELAY_WORKER_ADDRESS`
and `MINICLOUD_WORKSPACE_RELAY_GATEWAY_ADDRESS` consistently. Enforce host and
provider egress isolation separately; bridge membership is not a VM firewall.

The release packager recursively includes and hashes all regular bundle files,
including this override, Caddy config and example. A main/seed source package
must carry these optional assets; no GitHub release is needed for an approved
local-source deployment. Existing release manifest versions and pinned upstream
images are not changed. The gateway image is an explicit separately built
operator input, not a new default product image in the release manifest.
