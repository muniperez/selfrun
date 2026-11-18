# Workspace gateway

Opt-in, separately supervised .NET 10 HTTP/WebSocket transport relay. It never
runs terminals, builds or coding tasks locally. T3 runs on each VM at
`127.0.0.1:3773`; a worker-owned pinned SSH tunnel supplies a bounded local
forward. No provider credential, SSH private key or OpenBao credential belongs
in the gateway process.

## Required operator inputs

Do not enable until all of these exist:

* Wildcard DNS for an operator-owned `GatewayDomain`, pointing at the edge,
  and TLS coverage for each admitted VM/service host. Wildcard DNS does **not**
  provide a wildcard certificate. Stock Caddy can issue individual leaf
  certificates on demand using HTTP-01 and the private authorization endpoint
  below; no DNS-01 plugin or wildcard certificate is required. Public port 80
  must reach Caddy for HTTP-01. Alternatively supply an actual certificate
  covering `*.<GatewayDomain>` through an independently configured TLS flow.
* A separate, resource-limited supervisor/container for this executable,
  preferably 0.5 CPU / 256 MiB initially, with capped logs, restart policy and
  readiness monitoring. These are admission limits, not capacity guarantees.
* TLS at the gateway Kestrel listener (configure standard Kestrel certificate
  settings), TLS passthrough, or set `TrustedProxyAddress` to the exact private
  IP of the operator-owned TLS-terminating Caddy. Only that one peer may supply
  `X-Forwarded-Proto`, with a one-hop limit; Caddy must overwrite untrusted
  forwarded headers. Other browser `Forwarded` / `X-Forwarded-*` inputs are
  ignored. Plain HTTP requests without that trusted TLS boundary fail closed.
* A private gateway-to-API route. Exclude `/internal/workspace-gateway/*` from
  public ingress; a dedicated random service credential of at least 32
  characters is additionally required. Use HTTPS, except explicit IP loopback
  HTTP within an explicitly configured private namespace. DNS names such as
  `api` are resolved only to private addresses, with the connected IP validated
  by the gateway transport.
* Worker-owned, generation-bound local forwards with pinned SSH host keys;
  firewall/provider isolation denying primary/sibling lateral access.
* An actually qualified pinned T3 release/image and tested headless session
  issuance. No wildcard domain, certificate or fake ready route is invented.

Keep access/query/body logging disabled at **every** proxy and collector for
gateway traffic. Launch codes and T3 WebSocket tickets appear in query strings;
service credentials and API request/response bodies are confidential. The
executable suppresses ASP.NET request-URL and HttpClient diagnostics. Do not
enable automatic request logging, tracing URL capture or body capture around it.

Both API and gateway bind the `DevelopmentWorkspaceGateway` configuration
section. Default `Enabled=false`. Example non-secret settings:

```json
{
  "DevelopmentWorkspaceGateway": {
    "Enabled": false,
    "GatewayDomain": "workspaces.example.com",
    "ControlPlaneUrl": "https://private-api.example.com",
    "RelayHost": "127.0.0.1",
    "MinimumRelayPort": 42000,
    "MaximumRelayPort": 42999,
    "MaxConnections": 128,
    "MaxConnectionsPerSession": 16
  }
}
```

Supply `DevelopmentWorkspaceGateway__ServiceCredentialFile` as the absolute path
of a protected Docker secret (for example
`/run/secrets/workspace-gateway-credential`) to API and gateway only. The bounded
file loader overrides the inline credential and strips its trailing newline;
missing/invalid files fail closed when enabled. The inline `ServiceCredential`
option remains for synthetic tests/backward compatibility, not the preferred
deployment path. Never put credentials in source, browser or CLI. Also enable the
separate `DevelopmentWorkspaces` feature only after lifecycle qualification.
Rotate this credential by restarting both services; existing browser sessions
still require live API authorization.

## On-demand leaf certificates

Caddy's global `on_demand_tls` configuration uses
`ask http://172.29.110.3:8080/internal/certificate`. The HTTPS catch-all listener
uses `tls { on_demand }`. Configure the dedicated private proxy network with
Caddy at `172.29.110.2`, gateway at `172.29.110.3`, and
`TrustedProxyAddress=172.29.110.2`. Do not publish the gateway listener or ask
endpoint to the host/public ingress. These are isolated private-network example
addresses; preserve unrelated existing networks.

`GET /internal/certificate?domain=<requested leaf host>` is dispatched before
forwarded-header processing and requires the **direct TCP source address** to
equal `TrustedProxyAddress`. Neither browser cookies, Minicloud user login nor
`Forwarded`/`X-Forwarded-For` can satisfy it. It rejects wildcard names, URLs,
ports, noncanonical hosts, foreign domains and malformed generations.
The gateway calls service-credential-protected
`GET /internal/workspace-gateway/certificate-authorization?domain=…` on the API.
Only a current ready/running VM with a fresh heartbeat and current unexpired,
unrevoked, registered service route receives 200; unknown/stale/unready routes
and unauthorized callers receive 404 with no credential/routing response body.
Certificate checks read metadata only: they neither start compute nor fetch
T3 secrets, redeem browser sessions or mark task activity.

Ask calls are capped at eight concurrent checks and ten seconds. The API's
hash-host lookup is bounded to 1024 current matching-generation/service routes;
raise/rework that deliberate fail-closed lookup bound before operating above
that admission scale. Previously issued certificates may remain cached after
a VM stops; HTTP/WebSocket session authorization still rejects stale/stopped
generations independently.

## API and receipts

`POST /api/development-workspaces/{workspaceId}/vms/{vmId}/launch`
accepts `{ "generation": 1, "service": "t3" }` with normal Minicloud
browser-session/CSRF or organization API-key authentication (`apps:write`).
Returns `{ "url": "https://…/__minicloud/launch?code=…", "expiresAt": "…" }`,
or 503 when disabled/unconfigured, 409 when unauthorized/not ready. Launch
requires current live membership/key scope, exact VM generation, running/ready
state, recent heartbeat and a live registered service route. Missing readiness
does not allocate compute. Service defaults to `t3`; previews require a
worker-registered service slug.

The VM origin is
`vm-<first32 lowercase SHA256 hex of VM ID>-g<generation>.<GatewayDomain>`.
Hashing makes existing underscore-containing VM IDs DNS-safe. Registered preview
origins add `-s-<service>` before the domain. Each origin serves the application's
**root**, not a fabricated T3 subpath. Host labels exceeding DNS limits are
rejected. VM kind is never accepted from the browser.

The gateway redeems a 60-second, single-use opaque code through
`POST /internal/workspace-gateway/redeem`, body `{ "host": "…", "receipt": "…" }`.
The protected API atomically consumes it using an optimistic concurrency token
and returns an opaque session with expiry. The gateway sets
`__Host-minicloud-workspace` (`HttpOnly; Secure; SameSite=Lax; Path=/`, no Domain),
then redirects to `/`. Launch links and session receipts are bearer capabilities:
do not share them. No T3 bearer is included.

`POST /internal/workspace-gateway/resolve` has the same body shape and returns
`{ "loopbackPort": 42001, "bearer": "server-only T3 token or null" }`.
Both internal operations require `X-Minicloud-Gateway-Credential`.
Only the API accesses the referenced T3 secret. The gateway receives no general
secret-store capability. All responses are `no-store`.

The database stores only SHA-256 launch/session receipts, actor, organization,
workspace, VM, service, generation and expiry/revocation metadata.
Normal session TTL is 30 minutes, bounded to one hour. Reauthorization reloads
membership or API-key expiry/revocation/scope and VM readiness on every HTTP
request, WebSocket upgrade/reconnect, and at most every 5 seconds during a
WebSocket (configurable 1–10 seconds). Fenced drain, stale generation, stale
heartbeat, route expiry/revocation and lost authorization disconnect sockets.
Passive traffic never renews task leases or useful activity; the narrow T3 input
observer below is the only traffic-based useful-activity signal.
Expired rows need periodic operator housekeeping; TTL checks do not depend on
physical deletion.

## Worker registration contract

`DevelopmentWorkspaceGatewayRoutes` is **worker-owned**, not a browser API.
Register only after the actual runtime and pinned tunnel are healthy:

* `Id`, `OrganizationId`, `WorkspaceId`, `VmId`, current `Generation`;
* `Service = "t3"` or an explicitly approved preview slug (not a supplied port);
* `LoopbackPort` in the operator's reserved port range; the upstream is always
  constructed as `http://<operator RelayHost>:<port>`. `RelayHost` is one exact
  operator allowlisted hostname or loopback/RFC1918/ULA IP, never a VM field,
  URL or browser input. For DNS, **every** returned address must be private;
  each new socket connects to the validated IP, preventing DNS rebinding to a
  public/metadata target. Its default is `127.0.0.1`. Never register metadata endpoints,
  API/database/OpenBao/Docker listeners, primary/sibling ports or shared
  management sockets;
* `T3CredentialReference`: JSON-serialized `StoredSecretReference` for the
  runtime-issued **unbound** bearer session, never its plaintext;
* `RegisteredAt`, short bounded `ExpiresAt`, nullable `RevokedAt`.

The worker owns port reservation, exact tunnel/process lifetime, expiration
renewal and route revocation before stopping/draining. The gateway and forwards
must share a deliberately isolated loopback namespace or use an explicitly
firewalled worker private IP; `127.0.0.1` in two independent containers is not
equivalent. Bind tunnels only to that interface and limit ingress to the gateway.
Do not expose relay ports publicly.
For Compose, `RelayHost=worker`, `MinimumRelayPort=30000`,
`MaximumRelayPort=39999` support worker-container-only forwards. Binding these
to `0.0.0.0` is permissible only **inside** that private container namespace,
with no published ports and an isolated network restricted to the gateway and
worker. Never use that wildcard bind on the control-plane host.
Only one route exists per VM/generation/service. A new generation gets a new
registration and origin; old credentials/routes must be revoked.

## Transport and limitations

Baseline: pinned T3 v0.1.9, commit
`24e77473ecf41bc1f7142529c7911c1c1aae1d76`, started with
`t3 serve --host 127.0.0.1 --port 3773 --base-dir PATH --no-browser`.
The gateway forwards root assets, environment descriptor, authenticated
`GET /api/auth/session`, `POST /api/auth/websocket-ticket`, and
`/ws?wsTicket=…&orchestrationProtocol=2`. It injects the server bearer and
the intended public Host/Origin/HTTPS forwarding headers, discarding browser
authorization/forwarding overrides and gateway cookies. T3 auth-management
HTTP endpoints, OAuth token exchanges and MCP routes are blocked. The session
check returns T3's non-secret authenticated-session descriptor, not its bearer;
the source-derived browser bootstrap accepts that authenticated state without
requiring a T3 browser cookie. HTTP orchestration requests get the pinned
`x-t3-orchestration-protocol: 2` header. Preview routes get no T3 token and retain only
their own host-only application cookies.

Transport payloads are forwarded unchanged. A permitted T3 session can run code
on its own VM; it must not receive control-plane secrets. The input observer
below recognizes user intent, not task execution, completion or idleness.
Do not treat transport access, an open socket or low CPU as task activity or
quiescence. Runtime task enumeration/freeze/Git safeguards remain mandatory;
T3 does not provide a drain API.

## Useful input, idle grace and recovery

The client-to-server WebSocket observer recognizes the pinned Effect RPC JSON
envelope, including arrays and fragmented WebSocket messages, for:

* `terminal.write` with nonempty bounded `data`, `threadId` and `terminalId`;
* `orchestration.dispatchCommand` carrying explicit user-created web/mobile
  `message.dispatch` input with its IDs, text/attachments and known dispatch mode;
* the explicit `prepared-run.release` command with its required IDs.

It does not rewrite, execute or log those payloads. Parsing is bounded to 1 MiB
and JSON depth 32. Passive terminal observe/attach/resize, pings, acknowledgements,
server output, preview traffic and unknown/invalid message formats never renew
activity. Unknown formats are **not** evidence that the VM is idle.

Before forwarding the final fragment of recognized useful input, the gateway
posts `{ host, receipt }` to dedicated-credential-protected
`POST /internal/workspace-gateway/activity`. The API independently reauthorizes
the current opaque T3 session, actor, organization, VM, generation and route,
then invokes `DevelopmentWorkspaceService.ActivityAsync`. This updates useful
activity and cancels an unclaimed idle grace. Updates are coalesced to at most
one per second per connection. Denied/failed activity authorization closes the
socket instead of forwarding that completed input message. This does not create
or renew a task lease or assert that T3 accepted/executed the request.

Source verification: [T3 v0.1.9 wire setup](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/ws.ts#L3609),
`apps/server/src/ws.ts:3609` selects `RpcSerialization.layerJson`;
`packages/contracts/src/rpc.ts:480,1622-1625,1772-1776` binds the methods;
`packages/contracts/src/terminal.ts:25-34,66-69` defines terminal input;
`packages/contracts/src/orchestrationV2.ts:89-106,3187-3240,3618` defines explicit
message dispatch and prepared-run release. Its locked Effect 4.1.2 source,
vendored under `.repos/effect/` in that same commit,
`packages/effect/src/rpc/RpcMessage.ts:74-85`,
`RpcClient.ts:747-752` and `RpcSerialization.ts:141-155`, confirms
`{ "_tag": "Request", "id": "7", "tag": "terminal.write",
"payload": { "threadId": "thread-1", "terminalId": "term-1", "data": "x" },
"headers": [] }`, serialized directly or in an array—not JSON-RPC 2.0.

Normal admissions require ready/running, `GatewayReady`, current heartbeat and
live route. A narrow recovery exception permits terminal access while a
same-generation stop operation is pending/running: draining with step `pending`
or `wait-for-tasks`, or `awaiting_git_save` with step `wait-for-save` and exact
`workspace_git_save_required` error. `GatewayReady` must remain true and no
current-generation quiescence receipt may exist. Certificates use the same
readiness rule. Other blocked states and `gateway-drain`, `quiesce` and deleting
steps remain fenced. Recovery input records activity without cancelling a
claimed stop or reopening Minicloud task admission.

Allowing a T3 terminal during recovery also allows the authorized user to run
new shell commands there; it is **not** a T3 command-admission fence. The worker
must observe active/unobservable T3 work and VM processes, then revoke routes
and disconnect clients before freezing writes. Unused-terminal deletion,
approval completion and dirty-Git repair still require live runtime acceptance;
the observer alone does not establish those properties.

Defaults bound request bodies to 4 MiB, streamed responses to 32 MiB,
WebSocket messages to 1 MiB, HTTP duration to 60 seconds, and all connections
to 30 minutes. Buffers are 16 KiB, redirects are not followed server-side,
arbitrary proxy URLs/CONNECT/upgrades are denied, Global/session admissions
are bounded, and cross-origin requests are denied. Large artifacts and long
HTTP operations require a separately designed bounded channel; do not silently
remove relay limits.

Synthetic tests use real loopback Kestrel HTTP/WebSockets and an isolated SQLite
receipt store; they require no cloud or T3 credentials. They do **not** establish
live T3 headless issuance, TLS/DNS, provider firewall, tunnel or image readiness.
Those are deployment gates and must be qualified on an approved isolated host.

## Wiring

API: `builder.Services.AddDevelopmentWorkspaceGateway(builder.Configuration);`.
The common partial DbContext supplies both gateway DbSets and its mapping
hook; include both tables in the domain migration.
Add `Minicloud.WorkspaceGateway.csproj` and
`Minicloud.WorkspaceGateway.Tests.csproj` to the solution.
Publish the executable independently and supervise it separately from API and
worker; do not run it inside the control-plane request process.
