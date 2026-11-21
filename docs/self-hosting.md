# Self-hosting Minicloud

The packaged self-hosting stack is under active development. T40's server
release foundation is merged, but no server images or release bundle have been
published or qualified yet. Do not create a `server-v*` release or expose a
host based on these source assets. Once a versioned release is available, use
its signed installer and release documentation; never build a production host
from a checkout or substitute floating image tags.

The [guided CLI seed](stateless-seed.md#guided-installation) can use explicitly
selected, matching committed source for a **test** installation on a dedicated
Hetzner, DigitalOcean or Vultr VPS. Download a review candidate or publish from a
clean clone. The separate signed option requires an exact compatible published
server release and fails closed if unavailable. Neither path establishes live
provider or host qualification. Domain/public HTTPS access is required; IP-only
access is deferred.

The public control-plane installer, setup flow and coordinated maintenance
now have source implementations. Installation and maintenance have not been
qualified on a real host. The [maintenance guide](server-maintenance.md)
documents `backup`, `upgrade`, `restore`, `restore-confirm` and `transfer`.
These operations fence and drain through the maintenance authority and never
stop accepted work.

`transfer`, and the transfer inside `restore`, revoke an old host only when it
next reaches this database. An old host running on its own restored database
copy, or an unreachable host that still holds live provider credentials, is
**not** stopped. You must shut it down or rotate those credentials yourself.
The independently encrypted recovery-dependency archives (`*-recovery`) are not
full backups. The
[versioned installer/manual guide](server-installer.md) documents exact-release
verification, protected inputs, shared staged Compose operations and resumable
readiness. It is not a claim that a server release exists.
`compose.self-host.yml` is the release-stack
entry point and includes the versioned Compose definition in
`packaging/server/bundle/compose.yml`. It requires an exact release's digest
`images.env`, runtime `server-release.json`, validated host configuration and
root-protected secret files under `MINICLOUD_SECRETS_DIR`. The installer will
create and validate this state before starting services. See the [packaged
installation contract](specs/v1.0-self-hosted-installation.md) and [server
release trust/interface](../packaging/server/README.md) for implementation
status and non-negotiable boundaries.

For local development only, start the synthetic PostgreSQL and OpenBao
development services:

```sh
docker compose -f compose.dev.yml up -d postgres openbao openbao-init
```

`compose.dev.yml` uses a disposable OpenBao development root token and must
never be used for a hosted installation. Run the API/dashboard from source using
the commands in the repository [README](../README.md); the production stack's
OpenBao, Kratos and Caddy configuration is separate.

### Local SQLite initialization and upgrades

SQLite is supported for disposable local API development and tests, not the
hosted control plane. Set `Database:Provider` to `Sqlite` and
`ConnectionStrings:Minicloud` to a disposable SQLite connection string. API
startup initializes it when `Database:RunMigrationsOnStartup` is enabled
(enabled by default in Development); the standalone
`dotnet modules/api/bin/Release/net10.0/Minicloud.Api.dll migrate` command
initializes it regardless of that startup switch. Without a configured
connection string, both SQLite paths use `Data Source=minicloud-dev.db`.

Both paths create the current schema and seed rows atomically in an empty
database. Repeating initialization, or opening a database created by
`EnsureCreated` with the same current model, preserves existing rows and does
not rerun seeds.

The supported populated upgrade prerequisite is the **complete SQLite
`EnsureCreated` schema from source revision
`feb807325b371d813ffb2613135a16da2426f220`**, before T30 identity/session changes.
That revision's `modules/tests/Minicloud.Tests/SqliteTestHarness.cs` uses the
actual SQLite runtime model with EF Core 10.0.5. The regression fixture
`modules/tests/Minicloud.Tests/Fixtures/Sqlite-feb8073.sql` freezes its full
provider-generated schema. This is a verified source prerequisite, not a
published SQLite server release or a hand-created subset of tables.

The upgrade recognizes that exact table/index schema and runs explicit
conversions in a single writer transaction. It preserves internal user and
organization IDs, memberships, API/runtime credential hashes, applications,
environments, secrets and deployment children. It adds current tables and
seeds only newly introduced tables, initializes SSH selection as `none`, and
backfills deployment numbers by organization and UTC day with timestamp/ID
ordering. As required by T30, it retires `firebase_uid`, leaves new Kratos
bindings null, and does not recreate owners or import passwords. Existing
operators require the explicit verified [`bind-operator`](bootstrap.md)
procedure; schema migration alone does not admit browser login.

Optional `__EFMigrationsHistory` is retained unchanged when its table schema
matches EF's SQLite history table and its entries form a known contiguous
migration prefix. History is not used as proof of the source schema and
PostgreSQL migration SQL is never replayed on SQLite. Unknown schemas, extra
objects, unknown/incomplete history, invalid relationships or failed
conversions are rejected without committing schema or row changes. Both
entry points support an idempotent retry after successful upgrade.
Other historical schemas need separately verified conversions; do not delete
populated databases or falsify their migration history to bypass rejection.

PostgreSQL remains the hosted database and continues to use its existing
migrations, drift checks and standalone migration state safeguards.

Server containers expose only the dashboard/API behind Caddy on TCP 80/443.
PostgreSQL, OpenBao and Kratos administration stay on the private Compose
network. Database credentials, encryption keys, OpenBao AppRole material and
deployment SSH keys are mounted from protected files, not image build
arguments or public dashboard configuration. OpenBao recovery material and
encrypted backups must remain independently recoverable off-host; container
restart is not a substitute for an unseal or recovery procedure.

## Stack reference

This section describes the source bundle consumed by the installer and its
manual staged primitives. It is not yet a qualified installation procedure.

### Operator inputs

The bundle reads these values from the release environment. The image
references (`MINICLOUD_*_IMAGE`) come from the generated, digest-pinned
`images.env`.

| Variable | Purpose |
| --- | --- |
| `API_URL` | Public HTTPS API origin, such as `https://api.cloud.example.com`. |
| `CONSOLE_URL` | Public HTTPS dashboard origin. It is also used for CORS, the exact-origin CSRF check and Kratos return URLs. |
| `API_HOSTNAME`, `CONSOLE_HOSTNAME` | The Caddy site hostnames for the API and dashboard. These are also reserved from app routing. |
| `APP_DOMAIN` | The managed application domain suffix. |
| `MANAGE_DNS_RECORDS` | Optional. Defaults to `false`. |
| `MINICLOUD_SOURCE_URL` | Optional. The public source link shown in the dashboard. |
| `MINICLOUD_GOOGLE_OIDC_ENABLED` | Defaults to `false`; the installer derives it only from explicit `googleOidc.enabled`. Shared API/Kratos switch, not a qualification assertion. |
| `MINICLOUD_PORTAL_KEEP_SIGNED_IN_ENABLED` | Defaults to `true`. Offers the per-user **Keep me signed in** option; when `false`, existing remembered sessions follow the standard limits. |
| `MINICLOUD_PORTAL_STANDARD_IDLE_MINUTES`, `MINICLOUD_PORTAL_STANDARD_MAX_MINUTES` | Dashboard inactivity timeout and absolute lifetime for standard sessions. Default `120` (2 hours) and `720` (12 hours). |
| `MINICLOUD_PORTAL_REMEMBERED_IDLE_MINUTES`, `MINICLOUD_PORTAL_REMEMBERED_MAX_MINUTES` | Same limits for **Keep me signed in** sessions. Default `10080` (7 days) and `43200` (30 days). |
| `MINICLOUD_KRATOS_SESSION_LIFESPAN` | Upstream Kratos session lifespan, default `10080m`. Keep it at least the longest enabled idle timeout; the installer derives it. |
| `MINICLOUD_SECRETS_DIR` | Root-owned directory (`0700`) holding the files below. |
| `MINICLOUD_INSTALLATION_ID` | Persistent non-secret installation identity from host metadata, shared by API and bootstrap inputs. Never regenerate it on restart. |
| `MINICLOUD_HOST_ID` | Optional non-secret host-local maintenance identity from `<root>/host-id`, written by the packaged maintenance tooling and never backed up. Leave empty for manual Compose; `transfer` binds the maintenance authority to it. |
| `OPENBAO_ROOT_TOKEN_FILE` | Used only by the `openbao-configure` maintenance service. Unset during normal operation. |

The dashboard container receives only `MINICLOUD_API_URL`,
`MINICLOUD_APP_DOMAIN` and `MINICLOUD_SOURCE_URL`. At startup it validates them
and writes `runtime-config.json`, so one image serves any installation.

### Protected files

Create each file under `MINICLOUD_SECRETS_DIR` with mode `0600`, using
independent random values:

- `postgres-admin-password`, `app-db-password`, `kratos-db-password`
- `secret-encryption-key`
- `kratos.env`: credentials only. See `secrets.env.example`. The `DSN`
  password must match `kratos-db-password`, URL-encoded. Explicit Google opt-in
  also imports the single fixed Google provider JSON here; never dump this file
  or a rendered Compose environment into logs.
- `openbao-server.crt`, `openbao-server.key` and `openbao-ca.crt`: a private CA
  and a server certificate whose SAN includes `openbao`. The API and worker
  trust this CA only for OpenBao, and public HTTPS keeps the system trust store.
- `openbao-approle/role-id`, `openbao-approle/secret-id`: written by
  `openbao-configure`.
- `deploy-ssh-public-key`, plus `ssh/deploy-key` and `ssh/host-pins`. Each
  `host-pins` line is `<host> SHA256:<fingerprint>`, and the worker refuses
  unpinned hosts.

### Coding workspace idle timeout

Upgrade the database and roll out the API and workspace worker together before
reopening workspace operations. Existing and new workspaces without a saved
override inherit the operator's `DevelopmentWorkspaces:IdleGraceMinutes`
(`DevelopmentWorkspaces__IdleGraceMinutes` in the API and worker environment).
Configure the same value in both services. Keep a deliberate override (including
30 minutes) to preserve that behavior; otherwise omit it to use 360 minutes
(6 hours). The migration leaves existing rows unset rather than writing a new
value over them. Existing positive operator values are preserved as defaults,
even outside the supported user range of 1–10,080 whole minutes.

A saved workspace override takes precedence over that operator default for both
primary and ephemeral VMs. Resetting the setting restores inheritance. See the
[API contract](api.md#development-workspace-idle-timeout) for countdown and cost
behavior.

Users can also choose **Keep running** per dev environment, which turns
automatic idle stop off; manual stop and VM concurrency limits and provider
quotas still apply. Projected costs are informational: the former
`MaxEstimatedMonthlySpend` and `MaxEstimatedMonthlySpendPerOrganization`
settings are ignored if still present and can be removed. The additive `keep_running` column defaults existing rows to
idle stop. Roll it out with the API and worker together like the idle timeout.

### Dashboard sign-in sessions

Dashboard sessions end after an inactivity timeout or an absolute lifetime,
whichever comes first. While a user is active, the dashboard renews the session
during the last half of its idle window; background polling alone never keeps a
session alive, and neither does a network reconnect. Renewal never extends past
the absolute lifetime. Logout, revocation and upstream Kratos session loss end
the session immediately, including while a renewal is in flight; see
[session renewal](api.md#session-renewal). Open tabs renew one at a time and
re-check the session when another tab signs out. A Kratos outage during renewal
is retried and is not treated as a logout.

Users opt in per device with **Keep me signed in** at sign-in, or change it for
the current device under account settings. Remembered sessions use a persistent
cookie; standard sessions use a browser-session cookie that the browser may drop
when it closes. API keys and CLI credentials are unaffected.

Configure the policy with the optional installer object (all fields optional;
values are whole minutes, idle 1–43200, maximum from idle to 129600, and
remembered durations no shorter than standard):

```json
"portalSessions": {
  "keepSignedInEnabled": true,
  "standardIdleMinutes": 120,
  "standardMaxMinutes": 720,
  "rememberedIdleMinutes": 10080,
  "rememberedMaxMinutes": 43200
}
```

The API rejects invalid values at startup. Shortening a limit applies to
existing sessions immediately, which may sign users out; lengthening it applies
to the idle window at the next renewal but never extends a session's original
absolute lifetime. Disabling `keepSignedInEnabled` makes existing remembered
sessions follow the standard limits.

### Optional packaged Google OIDC

Password-only installation remains the default. Omitting `googleOidc`, or
setting it to `{"enabled":false}`, needs no Google input. Do not put Google
credentials in public configuration, dashboard runtime config, release assets,
shell arguments or issue/PR bodies.

For an explicitly opted-in **new installation**, before `preflight`/`prepare`,
add this non-secret object to the protected configuration:

```json
"googleOidc": {
  "enabled": true,
  "credentialsFile": "/etc/minicloud-operator/google-oidc.json"
}
```

The credential input must be a canonical absolute, nonsymlink root-owned
`0600`/`0400` JSON file in a root-owned `0700` directory, with protected
ancestors. Keep it outside installation, recovery and release-asset directories.
Use `google-oidc.credentials.example.json` only as a **synthetic shape example**;
replace its values privately with an operator-provided Google **web** client:

```json
{
  "clientId": "synthetic-client.apps.googleusercontent.com",
  "clientSecret": "synthetic-not-a-google-secret"
}
```

Only these two unique fields are accepted. Client IDs must end in
`.apps.googleusercontent.com`; secrets must contain only ASCII letters, digits,
`-` and `_` (1–256 characters), preventing dotenv interpolation/injection.
The installer imports the credentials into protected `secrets/kratos.env`,
alongside the original DSN/cookie/cipher secrets. It does not put credential
values in host metadata or process arguments. Both Kratos services consume this
protected file; neither API nor dashboard receives the provider secret.

The shared opt-in switch enables API Google navigation and Kratos OIDC
together. Compose derives the callback base from the validated API origin.
The only Google redirect URI is:

```text
<apiUrl>/api/account/oidc/self-service/methods/oidc/callback/google
```

The fixed, read-only `google-oidc.mapper.jsonnet` maps a verified Google email
to `traits.email`; arbitrary providers/mappers/redirect overrides are not
accepted. Keep password login enabled, public registration disabled and
Kratos private. Sign in with a password first and link Google in account
settings; Google login only admits identities already bound to Minicloud.
The reverse proxy must preserve the public API Host for callback validation.

Keep the original configuration **and** credential input available unchanged
for staged commands, resume, upgrade and restore. Completed credentials are
validated, never silently rewritten or regenerated; duplicate/unknown
`kratos.env` keys and provider mismatches fail closed. Back up the input
independently in protected operator custody; encrypted installation backups
also contain the imported provider secret. Restore that external input at its
original path before invoking the helper. Existing password-only configurations
remain valid unchanged. The installer still requires exact original
configuration matching: this option does **not** add an in-place enable/disable
or credential-rotation workflow for an established installation. Never edit
installation metadata to bypass that boundary.

Enabling this operator option does **not** claim live qualification. The current
API contract has `GoogleEnabled`, not a separate `GoogleQualified` setting.
Live Google sign-in/link/422 behavior, separate-origin HTTPS/CORS/CSRF, proxy
Host handling, SMTP recovery and browser/Grafana recovery revocation remain
deferred in [#44](https://github.com/muniventures/minicloud/issues/44).
The prior disposable Kratos v1.3.1 checks are not a live Google qualification.

### Optional operator diagnostics

The API, worker, telemetry service and workspace gateway default to
`OperatorDiagnostics__Enabled=false`. To opt in on each process, set it to
`true` and configure a private OTLP collector with the standard
`OTEL_EXPORTER_OTLP_ENDPOINT` and, if needed, `OTEL_EXPORTER_OTLP_PROTOCOL`.
Use an authenticated, encrypted private collector; these are operator-only
signals, not the application-log ingestion endpoint. An unavailable collector
does not block startup or requests. The trace sampler respects an incoming
sampled parent and otherwise samples 10% of new traces. `/health`, OTLP
ingestion paths and exporter requests are excluded from application traces.
Metrics still include health requests. A bounded, source-generated summary
logs working-set bytes and thread-pool queue length once per minute.

The exported metric contract is:

| Instrument | Unit | Allowed attributes |
| --- | --- | --- |
| `http.server.request.duration` | `s` | `http.request.method`, `http.response.status_code`, `http.route`, `network.protocol.version` |
| `http.client.request.duration` | `s` | `http.request.method`, `http.response.status_code`, `network.protocol.version` |
| `dotnet.process.cpu.time` | `s` | `cpu.mode` |
| `dotnet.process.memory.working_set` | `By` | none |
| `dotnet.gc.heap.total_allocated` | `By` | none |
| `dotnet.gc.pause.time` | `s` | none |
| `dotnet.thread_pool.queue.length` | `{work_item}` | none |
| `minicloud.worker.claims` | `{claim}` | `outcome` (`claimed`, `empty`, `refused`) |
| `minicloud.worker.queue.wait` | `s` | none |
| `minicloud.worker.step.duration` | `s` | `operation` (`resolve-provision`, `dns-setup`, `ssh-deploy`, `health-finalize`, `rollback`), `outcome` |
| `minicloud.db.commands` | `{command}` | `operation`, `outcome` |
| `minicloud.db.command.duration` | `s` | `operation`, `outcome` |
| `minicloud.gateway.connections.active` | `{connection}` | `operation` (`http`, `websocket`) |
| `minicloud.gateway.admission.wait` | `s` | `outcome` (`admitted`, `rejected`, `timeout`, `cancelled`) |
| `minicloud.gateway.relay.bytes` | `By` | `operation` (`http`, `websocket`), `direction` (`upstream`, `downstream`) |
| `minicloud.gateway.websocket.closes` | `{connection}` | `outcome` (`client_closed`, `upstream_closed`, `message_limit`, `activity_rejected`, `revoked`, `lifetime`, `aborted`) |

CPU, allocation and pause counters are cumulative; derive rates from their
changes over time. HTTP metrics are the built-in .NET instruments, collected
once. Database operations are fixed names (`ef.command`,
`deployment.number.allocate`, `worker.lease.acquire`, and
`worker.lease.heartbeat`); outcomes are `success`, `error` or `cancelled`.
The shared `Minicloud.Operator` meter and activity source are reserved for
bounded operator signals. New operations must use fixed names, never user data.
The telemetry service emits OTLP ingestion stage spans named `otlp.auth`,
`otlp.read`, `otlp.gzip`, `otlp.decode`, `otlp.normalize`, `otlp.quota`,
`otlp.encode` and `otlp.forward`. See
[OTLP ingestion cost](performance/otlp-ingestion/README.md).
The worker starts a `worker.deployment` span at the durable claim time and a
`worker.step` child span per pipeline step (`operation` is the step name, or
`rollback`); concurrent DNS and SSH steps keep separate intervals. Queue wait is
claim time minus the durable queued timestamp. See
[Worker claim polling](performance/worker-queue/README.md).

Exported spans allow only `http.request.method`, `http.response.status_code`,
`http.route`, `network.protocol.version`, `operation` and `outcome`. SQL,
parameters, URLs, query strings, headers, trace state and exception details are
not exported.
HTTP span names are normalized to `HTTP`; route templates remain available as
an attribute. Configure resource attributes and collector-side processing
without secrets or tenant identifiers. Existing application and proxy logging
policies still apply; in particular, gateway query/body logging must remain
disabled as described in the gateway README.

### Startup order

1. `database-initialize` creates or verifies the `minicloud` and `kratos` roles
   and databases under a session advisory lock. It refuses unexpected database
   owners and elevated/inherited service-role privileges before creating state.
2. `kratos-migrate` and `api-migrate` run once, then exit. `api-migrate` holds a
   PostgreSQL advisory lock, so concurrent runs serialize. It refuses a
   populated database that has no Minicloud migration history, and a database
   migrated by a newer release.
3. `kratos`, `api`, `worker`, `dashboard` and `caddy` start after their
   dependencies are healthy or complete. They start in that order.

Each `docker compose up` re-runs initialization and migrations idempotently,
so upgrades do not depend on first-volume scripts.

First-operator seeding uses the API executable's shared
[`bootstrap` host entry point](bootstrap.md), after migrations. The packaged
installer invokes this producer and the browser setup transport consumes the
same durable state. These implementations do not make the source bundle a
qualified installation.

### Protected host bootstrap interface

The opt-in `api-bootstrap` maintenance service uses the same API image and
application database as the running API. It publishes no port and does not
start the API, worker or OpenBao administration. The installer/manual caller
must preserve one `installationId` and original stable `operationId` in host
metadata. Set `MINICLOUD_INSTALLATION_ID` to that same installation identity;
all mutating request files use both IDs. The API reads the operation identity
from the shared durable bootstrap record, not another environment value.

Before invoking it, prepare root-owned `0700` directories
`$MINICLOUD_SECRETS_DIR/bootstrap/input` and `bootstrap/output`. Input
`request.json` must be a regular, non-symlink root-owned `0600`/`0400` file;
the input directory is mounted read-only. Output is mounted separately
read-write; the producer creates each new token file exclusively as `0600`.
Never put passwords/tokens in environment variables, arguments or query strings.
Use the fields and replay rules in the [bootstrap contract](bootstrap.md).

Source bundle interface examples (not a qualified installation procedure):

```sh
# Default command: bootstrap status --input-file <protected container path>
docker compose --profile maintenance run --rm -T api-bootstrap

# Override only the API arguments; preserve its dotnet entry point.
docker compose --profile maintenance run --rm -T api-bootstrap \
  bootstrap prepare --input-file /run/minicloud-bootstrap/input/request.json \
  --output-file /run/minicloud-bootstrap/output/setup-token-1

# Equivalent protected redirected stdin; never echo a token to construct it.
docker compose --profile maintenance run --rm -T api-bootstrap \
  bootstrap status --stdin < "$MINICLOUD_SECRETS_DIR/bootstrap/input/request.json"
```

`rotate-token` also requires a new `--output-file`. `complete` consumes protected
setup fields; completed host replay accepts the original IDs alone and never
resets credentials. No seed logic is implemented by Compose. The service gates
on successful application migrations but deliberately not healthy Kratos:
ready host replay must remain usable without contacting identity services.
Start/wait for healthy Kratos before operations that require it; otherwise the
shared command returns sanitized nonzero failures. Do not equate service exit
or `/health` alone with onboarding completion: inspect shared bootstrap status
and preserve awaiting-DNS/TLS, awaiting-operator, ready and failed distinctions.

The same entry point exposes `maintenance <status|fence|drain-status|release|transfer>`
for upgrade, restore and host-transfer fencing. See
[server maintenance authority](server-maintenance-authority.md). Operators
normally run these through the installer's `maintenance` operations described
in the [maintenance guide](server-maintenance.md) rather than directly.

### OpenBao lifecycle

OpenBao uses persistent file storage and TLS 1.3 on the private network. After
the first start, initialize it once. Store the unseal keys and root token
off-host, never in OpenBao or in its data volume backup.

```sh
bao_cli="bao -address=https://openbao:8200 -ca-cert=/run/secrets/openbao-ca.crt"
docker compose exec openbao sh -c "$bao_cli operator init"
docker compose exec openbao sh -c "$bao_cli operator unseal"  # repeat until unsealed
```

Then run the configuration service, pointing `OPENBAO_ROOT_TOKEN_FILE` at a
temporary protected copy of the root token:

```sh
docker compose --profile maintenance run --rm openbao-configure
```

This service is idempotent. It creates the KV v2 mount, the scoped `minicloud`
policy and the AppRole (with a 24-hour periodic token), and writes the AppRole
credentials. Delete the temporary root-token copy afterwards.

`openbao-agent` exchanges the AppRole credentials for a periodic token and
renews it. It writes the token to a memory-backed volume, which the API and
worker read on each request.
The Agent healthcheck verifies the current sink credential against OpenBao
over TLS; a retained nonempty token file alone cannot advertise readiness.

OpenBao starts sealed after every host or container restart. Until an operator
unseals it, the agent cannot authenticate and secret operations fail as
unavailable (`SecretStoreUnavailableException`). Other data stays intact.

To rotate the AppRole secret ID:

1. Generate a new one with the root or an operator token.
2. Replace `openbao-approle/secret-id`.
3. Restart `openbao-agent`.

The policy does not cover the optional Grafana operator secret path.
