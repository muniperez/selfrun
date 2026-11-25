# Versioned server installer and manual Compose

The implementation exists, but **no server release is published or qualified**.
The commands below describe a future exact signed release, not permission to
publish, install a public host, or replace missing release assets with source
builds. Native amd64/arm64, full-image, real Kratos, Caddy/ACME and hosted
acceptance remain release gates. [Maintenance](server-maintenance.md) provides
coordinated backup, upgrade, restore and transfer through the maintenance
authority. Those flows are not yet qualified on a real host.

The optional [standalone CLI seed](seed.md) supplies a verified dedicated host,
official Ubuntu Docker/Compose/Buildx/Python/OpenSSH/OpenSSL packages and pinned
Cosign, then independently verifies and invokes this exact installer. It does
not replace these checks or duplicate Compose/bootstrap. Seed also requires an
explicit protected off-VPS recovery export; host-local output alone cannot
complete its handoff. No new server release has been published by that feature.

## Trust and prerequisites

Use a dedicated Ubuntu 24.04 host, `amd64` or `arm64`. Install Python 3,
Cosign **2.6.5**, Docker Engine, Compose and Buildx independently using their
official distributions. OpenSSL and OpenSSH's `ssh-keygen` are required.
T40's verifier already requires Docker/Compose/Buildx; this installer does
not bootstrap, upgrade or replace an engine. Engine 24+ and Compose 2.24+
are mechanical command-interface guards, not a fully qualified version matrix.
The installer uses only the local `/var/run/docker.sock`, not an inherited
remote context. No source checkout, GitHub login, .NET SDK, Node or compilation
is needed on the server.

Allow outbound HTTPS for the exact GitHub release, GHCR/dependency registries,
Sigstore production trust/transparency infrastructure and certificate issuance.
Subsequent provider/API traffic is operator-configured. Allow inbound TCP
80/443 and, for Caddy HTTP/3, UDP 443. SSH and host firewall policy remain
entirely operator-managed; the installer never modifies them.

Select an actual published `server-vMAJOR.MINOR.PATCH[-prerelease]`, not
repository-wide `latest`. Follow the independent
[T40 trust procedure](../packaging/server/README.md#trust-and-exact-release-verification):
download its six named assets into an empty root-owned `0700` directory,
with regular, nonsymlink root-owned `0600`/`0400` files. Its path ancestors
must be root-owned and not group/world writable. Independently run
`cosign verify-blob` with the exact selected tag's workflow identity and
production issuer, then `sha256sum --check --strict checksums.txt`.
Only after that may the downloaded verifier or installer execute.
Never use an unchecked `curl | sh`, adjacent checksums as publisher identity,
wildcard certificate identities, or disabled transparency/inclusion checks.

The installer repeats independent signed-inventory/hash checks before
executing the downloaded verifier and the archive's versioned helper. The
verifier checks the detached manifest, complete safe archive inventory,
runtime manifest core, pinned images and source/workflow provenance.
Image verification is read-only before host preflight. Anonymous image pulls
(both release platforms, then the native variant) happen only after preflight
and before creating installation state, with empty Docker authentication.
No missing/unsigned release is treated as an installable release.

The optional `--download` flag downloads only absent assets from that **exact**
release URL into an already protected assets directory. You must still have
independently verified the installer before using it. Existing files are never
replaced. Failed downloads retain a `.partial` file; inspect and remove only
that exact partial file before retrying. No installer/verifier executes after
a failed download, signature or checksum check.
Promotion uses an atomic no-replace hard link on the assets filesystem: if
another invocation supplies the destination during download, it is preserved,
the completed `.partial` remains for inspection, and this invocation fails.

## Protected inputs and preflight

Prepare non-secret JSON using the release's `configuration.example.json` and
`configuration.schema.json`. Store it outside installation/recovery outputs
as a root-owned regular `0600`/`0400` file, in a root-owned `0700` input
directory. Supply lowercase hostnames and exact HTTPS origins without a port,
path or trailing slash. The API hostname is `api.<dashboard hostname>`.
The app suffix is separate; wildcard app DNS is **not** control-plane DNS.
Unknown/duplicate fields, ambiguous paths and symlinks are rejected.
The optional `portalSessions` object sets dashboard sign-in timeouts and the
**Keep me signed in** option; see
[dashboard sign-in sessions](self-hosting.md#dashboard-sign-in-sessions).

Choose three distinct canonical absolute paths:

- `$assets`: verified download directory (`0700`).
- `$root`: installation directory, normally `/opt/selfrun`. Its parent
  must already exist and be root-owned, without group/world write access.
  The target must be absent for a fresh install; even an unknown empty
  directory is not silently adopted.
- `$recovery`: existing root-owned `0700` operator recovery directory,
  preferably independently mounted/off-host recovery media, **outside** and
  nonoverlapping with both installation and asset paths. If configured,
  `recovery.openBaoUnsealMaterialPath` must equal this path. A path alone
  does not prove an independent/off-host copy; verify recoverability yourself.
  `backupDestination` is reserved operator information, not configured backup
  automation or a claim that full coordinated backup exists.

Set `$config` to the protected input and `$address` to the host's public IP
address. Repeat `--public-address` for each public IPv4/IPv6 address. Both
hostnames must resolve only to these supplied addresses. Future-release
command template, after independent verification:

```sh
sh "$assets/install-server.sh" --tag "$tag" --assets-dir "$assets" preflight \
  --root "$root" --config "$config" --recovery-dir "$recovery" \
  --public-address "$address"
```

Preflight checks OS/architecture, local daemon/tool interfaces, protected
configuration/paths, conflicting services, TCP 80/443 and UDP 443, DNS,
existing installation identities and installation-owned volumes/networks.
A host with unsupported IPv6, or a kernel-confirmed global IPv6 disablement,
may pass using IPv4 alone. IPv6 TCP/UDP listener conflicts, permission errors
and unconfirmed address errors still fail; the installer never enables IPv6.
A fresh dedicated host must have no containers (including stopped ones) or
Selfrun-prefixed volumes/networks. Recognized resumes may reuse only their
installation-labeled persistent resources and original Compose working path.
Never rename/delete existing populated volumes or edit metadata to bypass
these checks.

Mechanical capacity guards are **2 logical CPUs, 2 GiB physical RAM**, and
free disk on **both installation and Docker storage filesystems** of at least
`max(10 GiB, 3 × compressed release archive bytes)`. The disk rule reserves
archive/extraction headroom plus a fixed image/runtime floor. These are
conservative refusal guards, **not measured supported hardware requirements**;
they do not predict image expansion, app storage or upgrade/backup needs.
T90/T91 must measure the whole stack and publish qualified requirements.
No automatic pruning/cleanup occurs. Missing/mismatched DNS returns
`awaiting_dns_tls` before creating a fresh installation. Public TLS cannot
exist before issuance; it is checked with system certificate validation,
without redirects, during activation/status. There is no insecure admin mode.

## Automatic sequence

Repeat the template with `install` in place of `preflight`. Under the persistent
installation-wide lock, it verifies/pulls, durably records original
`installationId`/`operationId` and exact release metadata, extracts only verified
files, generates independent protected credentials and deployment SSH keys,
then calls the existing Compose database initializer and API/Kratos migrations.
It starts Kratos before shared bootstrap operations.

OpenBao is initialized only once. Its original recovery response (three
unseal shares, threshold two, and root token), separate root-token file and
private CA key are written under `$recovery/<installationId>/` with `0600`.
Unseal keys use protected redirected input, not arguments. Configuration uses
the existing `openbao-configure` helper's protected root-token bind; ordinary
API/worker consumers use only the renewable Agent memory-backed sink.
The root-token file is not mounted into normal services. See the
[OpenBao handoff](../packaging/server/bundle/openbao-handoff.md).
Protect and independently copy recovery material before relying on the host.
An initialized server without original recovery output, or uncertain/missing
AppRole output, fails closed for host reconciliation; it is never reinitialized.

`bootstrap prepare` uses the existing API image and producer, never another
seed algorithm. Its protected request carries the two original stable IDs;
separate read-only input/read-write output directories retain T41's interface.
The one-time token goes only to
`$root/secrets/bootstrap/output/setup-token-initial`, never routine stdout,
environment, URLs or diagnostics. Locally read that protected file and enter
the token at the reported HTTPS `/setup` URL. Follow
[operator setup](operator-setup.md); provider/plan configuration and VPS
allocation are subsequent explicit actions.

## Manual Compose parity

Use the **same independently verified installer asset as a staged host helper**,
changing only its command in the template:

1. `prepare`: all preflight, verification/pulls, durable metadata, extraction
   and protected configuration/credentials; no services started.
2. `initialize`: shared Compose `postgres`, `database-initialize`,
   `api-migrate`, `kratos-migrate`, healthy `kratos`, persistent OpenBao/Agent
   configuration, then shared `api-bootstrap status`/`prepare`.
3. `activate`: shared Compose `api`, `dashboard`, `worker`, then `caddy`,
   followed by the same HTTPS/bootstrap readiness.
4. `status`: same read-only readiness; updates only host readiness metadata.

`install` calls these exact same primitives, not another installation mode.
Each step retains the same config, tag, paths, public addresses and operation
identities and acquires the same persistent lock. The bundle under
`$root/bundle/` is the release's digest-rendered Compose, not source Compose.
The shared helper supplies only validated public values, image references,
`SELFRUN_INSTALLATION_ID` and protected secret **paths**. Direct Compose
host maintenance must preserve that exact environment and the same lock;
do not bypass it with ad-hoc `docker compose up`, copied templates or parallel
host commands. Existing migration/bootstrap/database locks remain separate.
Every resume verifies the persisted bundle path is exactly the canonical
`$root/bundle` and its installed file inventory/bytes match the selected signed
manifest. This is repeated immediately before every shared Compose command,
including manual initialization, activation, status and token rotation.
Extra, modified or missing installed files cannot execute or be silently
overwritten. Only `install`/`prepare` while the durable phase is `initializing`
may fill missing files from an interrupted initial extraction; existing
partial files must still match the signed release exactly.

## Resume, token rotation and readiness

Rerun the same `install` to resume incomplete initialization; manual users
repeat the interrupted step. Established credentials/keys are never regenerated.
Unknown outcomes remain unknown until reconciled; never delete bootstrap rows,
metadata, volumes or key material to retry.

If output was lost or the token expired, use the same template with
`rotate-token --token-output-name setup-token-2`. Choose a **new basename**:
rotation calls only the shared producer with the same IDs and cannot overwrite
an output or reset a completed operator. Keep protected token files out of
screenshots and logs. A ready installation is not reinstalled; use a future
explicit qualified upgrade procedure instead.

Exit `0` means the requested preflight/staging step succeeded, or final
`ready`; exit `2` means resumable `awaiting_dns_tls`/`awaiting_operator`;
exit `1` means failure, not fresh state or success. JSON progress/status never
includes credentials. Readiness requires the host bootstrap state, trusted
exact-origin HTTPS API health/release identity, no-store HTTPS setup transport,
matching dashboard runtime config and its HTTPS setup route. Only a shared
`ready` bootstrap state plus those checks reports `ready`. Container health
alone is insufficient; every required service must also be running, and each
configured service healthcheck must pass. Compose startup never pulls missing
images implicitly after verification. DNS/ACME failures retain state: correct DNS, inbound
ports and certificate issuance, then run `status`. A ready result does not
qualify a release, authorize deployment or prove backup/restore.

The root sibling `<root>.installation.lock` is persistent and never removed.
`installation.json` schema 1 stores exact release/schema/digest identities,
original IDs, paths/configuration, phase and last successful operation;
it contains no credentials. Preserve both; maintenance depends on them.
Keep each installed release's verified assets directory: `upgrade` requires it
as `--previous-assets-dir`.

## Optional email/code account recovery

**First-install scope:** a new, independently verified signed release containing
`compose.email-recovery.yml` and `identity.recovery.schema.json` supports this
opt-in through the same `preflight`, `prepare`, `initialize`, `activate` and
`install` commands above. `server-v0.0.1-rc.3` does **not** contain this feature;
setting only the API flag on that release cannot deliver a code. This source
change does not create or publish a replacement release.

Omit `emailRecovery`, or use `{"enabled": false}`, to keep the original
password-only setup: no SMTP input, courier activation, API recovery capability
or identity schema change. Google remains independently disabled by default.
The `recovery` object used for OpenBao/off-host material is unrelated to account
email recovery.

For an opted-in **new** installation, add this non-secret object to `$config`:

```json
"emailRecovery": {
  "enabled": true,
  "smtpFile": "/root/selfrun-inputs/email-recovery.smtp.json"
}
```

Use the signed `email-recovery.smtp.example.json` as the shape for the separate
protected SMTP input. All three fields are required:

```json
{
  "connectionUri": "smtps://synthetic-user:percent-encoded-password@smtp.example.invalid:465/",
  "fromAddress": "recovery@example.invalid",
  "fromName": "Selfrun"
}
```

The example is not a working external relay. On an authorized self-hosted
installation, use your operator-managed relay and approved sender. `smtps`
means implicit TLS; `smtp` means mandatory StartTLS by default. Both validate
certificates; skipping TLS certificate verification is not supported. Include
an explicit port and no path beyond `/`. Percent-encode SMTP username/password
components, including `$`, quotes, `#` and backslashes. Provide both username
and password, or neither if the relay explicitly permits unauthenticated TLS.
The sender must be a bare email address; the sender name supports 1-100 ASCII
letters, digits, spaces, dots, underscores and hyphens, without edge spaces.
Unknown/duplicate fields, unsafe dotenv values and arbitrary SMTP query
options are rejected without logging their values.

Protect this input just like Google credentials: a canonical absolute path
outside installation, assets and recovery directories, in a root-owned `0700`
input directory, with a root-owned nonsymlink `0600`/`0400` regular file.
Do not put the URI in CLI arguments, ordinary environment variables, logs or
Git. The helper imports it into the existing protected `secrets/kratos.env`;
only Kratos/migration consumers read that file. Public configuration,
installation metadata and Compose command arguments contain the input path,
not SMTP credential values. Docker administrators can inspect container
environment values, so retain the existing root/Docker trust boundary.

The inventory-verified overlay enables the API's `Kratos:EmailRecoveryEnabled`
and Kratos v1.3.1 code recovery together, keeps passwordless login/MFA off,
selects the email-recoverable schema under the original `user` schema ID, and
runs the ordinary SMTP courier with `serve --watch-courier` in the existing
Kratos container. No new service/image, published Kratos port, admin proxy,
alternate bootstrap or authentication bypass is introduced. Opted-in HTTPS
readiness also requires the deployed public capabilities endpoint to advertise
password and email recovery; readiness is not a delivery qualification.

**Existing-release scope:** resumes and maintenance retain the exact original
configuration and SMTP input values, credentials, schema ID and identity trait
shape. Existing password-only installations stay password-only on upgrade.
An installation originally created with this opt-in can retain it only with a
target signed release that supports the same contract. Activation on existing
installs, credential rotation, legacy recoverable-address backfill and
identity/config migrations are **not** implemented here. Do not edit installed
bundle bytes, configuration, identities or `kratos.env` to force activation.
Missing established secrets or changed SMTP input fail closed rather than
regenerating or overwriting them. Keep the original protected input available
for resume/maintenance; backups/restore remain subject to their original
protected recovery procedures and exact-configuration checks.

### Disposable capture-sink qualification recipe

This is a **future signed-candidate QA recipe**, not a command to run against a
customer instance. Use two explicitly authorized disposable fixtures: a fresh
dedicated signed-install host and a separate private capture-sink host. Do not
run foreign sink containers on the dedicated Selfrun host: the installer
deliberately rejects them. Preserve its ordinary DNS/TLS/provisioning checks.
Use only `recovery-synthetic@example.invalid`, generated fixture passwords and
synthetic provider/key/membership data; never copy real identities, credentials
or databases. No Google or real third-party mailbox is involved.

On the isolated sink fixture, independently obtain a vetted Mailpit binary
supporting these flags, clear inherited `MP_*` configuration, and deny external
egress. Allow SMTP ingress only from the signed fixture host (including its
Docker egress address). Bind SMTP to the fixture's private address and the UI
only to loopback:

```sh
mailpit --smtp "$sink_private_ip:1025" --listen 127.0.0.1:8025 \
  --disable-version-check --smtp-disable-rdns \
  --smtp-allowed-recipients '^recovery-synthetic@example\.invalid$'
```

Do not configure relay, forwarding, release-to-SMTP, SMTP auth acceptance
overrides, link checking or third-party integrations. Access the sink UI only
locally or through your fixture SSH tunnel. This captures normal SMTP delivery;
it does **not** prove delivery through an external authenticated TLS relay.

For this fixture only, use the private sink address in protected SMTP JSON:

```json
{
  "connectionUri": "smtp://192.0.2.10:1025/?disable_starttls=true",
  "fromAddress": "recovery@example.invalid",
  "fromName": "Selfrun fixture"
}
```

Replace the documentation-only `192.0.2.10` with the isolated reachable private
fixture address. Add `"allowInsecureSmtp": true` alongside `enabled` and
`smtpFile` in the first-install `emailRecovery` object. This explicit
unauthenticated plaintext exception is solely for capture tests; SMTP
authentication credentials with `disable_starttls=true` are rejected.

1. Independently verify the new exact signed release and complete the supported
   manual `prepare` / `initialize` / `activate` flow and HTTPS `/setup`, using
   the synthetic address as the operator email. Do not use a Kratos admin
   identity write or bootstrap/CSRF bypass to prepare recovery metadata.
2. Confirm `GET /api/account/capabilities` reports `emailRecovery: true`,
   `password: true`, `google: false`. Sign in normally in two separate browser
   profiles; create a disposable machine key and membership through supported
   product actions if needed, and record their baseline access.
3. In a third profile open dashboard `/recovery`. Submit the synthetic email
   through its bounded `/api/account/flows/recovery/browser` and
   `/api/account/flows/recovery?flow=...` flow with both normal cookie/CSRF
   boundaries. Confirm the sink receives that recipient's code via SMTP;
   unknown recipients must not receive mail.
4. Redeem the captured code through the same public flow, enter password
   settings, and set a new generated password. Confirm an incorrect/expired
   code cannot grant a session and a redeemed code cannot be reused. Keep
   passwords, codes, cookies and tokens out of screenshots/logs.
5. Confirm the prior browser/Grafana sessions are rejected, the old password
   fails and the new password signs in. Confirm the synthetic machine key
   still works and memberships are unchanged. Resume the signed host helper
   and confirm it preserves its installation ID and protected credential bytes.

Offline packaging tests verify input protection, disabled/default and resume
contracts, schema/Compose wiring, capability alignment and signed-manifest
inventory generation. They simulate host commands and signature verification;
they do **not** claim SMTP delivery, code redemption, full password/session
revocation, signed installation or external relay qualification. The sequence
above and authenticated TLS SMTP delivery remain #309/#44 signed-release QA
for the integration coordinator, with no production/service changes authorized
by this patch.
