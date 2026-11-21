# Portable development VM runtime (protocol 1)

Opt-in issue #110 implementation, **not a published/qualified provider image**. Python 3.12,
Ubuntu 24.04, linux/amd64, systemd/cgroup v2, existing ext4. Default tests need only Python
and Git; they never mount disks, contact providers, install tools, or start T3.

```sh
templates/development-workspaces/test.sh
```

Prepared images seed the supported T3 clone/add-folder default to
`/workspace/repos` without overwriting an explicit user choice. This does not
enable Clone for reduced gateway sessions or change named New Project's
hard-coded parent in pinned T3. See [project defaults and the exact upstream
dependency for #202](../../docs/workspace-projects.md); broad filesystem and
source-control scopes remain deliberately ungranted.

Outputs: ignored `.local/artifacts/issue110/runtime/`. Fixtures use two real independent
clones and a local bare remote; injected process/mount/encryption/transport adapters test
failure handling. This is not a real ext4, systemd-freezer, age, or live restore qualification.

### Safe native qualification diagnostics

If a disposable native qualification stops at `T3_RESTART_LEFT_UNKNOWN_WORK` or
`T3_DID_NOT_REMAIN_STOPPED`, retain only the probe/source identity, short allowlisted
work-inventory reason code and bounded service-state/cgroup evidence. Evidence includes
allowlisted active/sub/result states, job/main/control presence, cgroup population and the
direct service-cgroup process count (not descendant counts). Missing, unreadable, malformed
or over-limit reads are explicit diagnostic states, never evidence of emptiness. Do not retain
runtime logs, process arguments, executable or cgroup paths, SQL, usernames, or secrets.
The native probe polls positive advisory idle for at most 30 seconds, with paced,
deadline-bounded calls, and rechecks VM, T3 service, retained-volume, and Ready identities
on every observation. Repeat a clean native
qualification before capturing an image.

### Explicit native batches and receipts

`image/native.py run --expected-runtime-sha256 <sha256> --authorization <file>`
remains the full legacy invocation. It does not renew authorization. For a run that
cannot fit the **300-second ceiling**, use the three explicitly authorized batches
on the **same audited immutable installed image and inputs**, not a continuation
of a previous fixture:

```sh
python3 -B image/native.py plan --expected-runtime-sha256 <sha256>
python3 -B image/native.py batch --batch tooling \
  --expected-runtime-sha256 <sha256> --authorization <fresh-tooling-authorization>
python3 -B image/native.py batch --batch recovery \
  --expected-runtime-sha256 <sha256> --authorization <fresh-recovery-authorization>
python3 -B image/native.py batch --batch workspace \
  --expected-runtime-sha256 <sha256> --authorization <fresh-workspace-authorization>
python3 -B image/native.py aggregate --expected-runtime-sha256 <sha256> \
  --expected <root-0600-exact-receipt-mapping> \
  --output .local/artifacts/native/aggregate.json
```

These are disposable-builder-only commands, never workstation or hosted-workspace
commands. `tooling` isolates Codex/Git; `recovery` includes bootstrap, HTTP/WS,
remount, Connect management and **all** protected recovery cases together;
`workspace` repeats bootstrap/HTTP/WS and keeps terminal input, both unfrozen/frozen
PTY proofs, quiesce, readback and encrypted synthetic restore together. `plan` lists
the exact ordered probes for each batch. No required probe is dropped. Each batch
creates a fresh run ID, loop-backed synthetic fixture and complete owned cleanup.
There is no auto-orchestration, auto-issued authorization, clock override or refresh.
Runtime checks use the current native VM UTC clock, before **and after** each probe.
A probe exceeding its authorization fails even if its operation returned successfully.
Time margins still require disposable native measurement; offline tests do not prove
batch duration or systemd/kernel qualification.

The existing explicit ACK, exact machine/runtime hash, trusted issuance clock
(`native-vm-utc` or `provider-https-date`), issuance/expiry with TTL at most 300 seconds,
`native-<32 lowercase hex>` run ID, loop-only and synthetic-erasure acknowledgements
remain mandatory. A batch authorization additionally binds `batchId`,
`installedImageManifestSha256`, `installedImmutableInputs` (the manifest's exact
absolute-path/hash map), and `harnessSources` (the `native.py`, `native-t3-probe.mjs` and
`manifest.lock.json` filename/hash map from `plan`). These must match the actual
root-owned sources and installed inputs at guard entry. Private wrappers must
obtain **independent explicit fresh approval for each batch**, retain these bindings,
and use the batch exit status/`batchPassed`, not `nativeCandidatePassed`.
A batch never claims full native candidate or runtime qualification.
Its authorization cannot be reused for another batch or the legacy full `run`.

The aggregate's expected JSON contains those same identity fields plus
`machineIdSha256`, `runtimeSha256`, and **exactly** this `batches` map:

```json
{
  "tooling": {"runId": "native-<32 hex>", "reportPath": "<exact report>", "reportSha256": "<sha256>"},
  "recovery": {"runId": "native-<different 32 hex>", "reportPath": "<exact report>", "reportSha256": "<sha256>"},
  "workspace": {"runId": "native-<different 32 hex>", "reportPath": "<exact report>", "reportSha256": "<sha256>"}
}
```

This example is the **value of `batches`**, not a complete expected file. There is no
directory scan or opportunistic selection. Reports and expected files must be
root-owned, private (`0600`), regular non-symlink files under root-owned non-writable
ancestor directories. Aggregation verifies exact report hashes, source/image/inputs,
machine, unique expected run IDs, authorization binding and each successful probe's
fresh completion time; missing/duplicate/failed/mismatched probes or incomplete cleanup
fail closed. Only the separate successful aggregate establishes complete native
coverage. It does not qualify provider persistence, off-host backup, gateway, account
sign-in or browser behavior.

Each phase receipt records its actual `startedAt`, each probe's `completedAt`, and
`finishedAt` after owned cleanup. Aggregation requires timezone-aware, ordered
timestamps and validates authorization at phase start and every probe completion.
Only after every component passes validation and full nonempty native coverage is
established does the aggregate record its authoritative span: the earliest component
`startedAt` and latest component `finishedAt`, normalized to UTC ISO timestamps.
It does not read the current clock or re-stamp component receipts. Owned synthetic
cleanup may finish after authorization expires; this does not extend authorization
or allow probes after expiry.

Native outputs now live under the owning repository's ignored
`.local/artifacts/native/<runId>/`, or the staged subtree's `.local/artifacts/native/`
when no repository is present, **not `image/runs/`**. Keep root permissions; do not
make receipts public for aggregation. Expired authorization still permits bounded
teardown and inspection of positively owned fixtures (exact processes/units, guarded
loop/aliases, VM config/state and synthetic erasure). This is a safety obligation,
not permission for new mutation experiments or probes. Incomplete cleanup preserves
ambiguous data/loops for inspection and cannot yield a pass.

Focused offline verification:
`python3 -B -m unittest discover -s templates/development-workspaces/tests -p test_image.py`.
The broader affected checks are available through `templates/development-workspaces/test.sh`.

Immediately before quiescence, after other Codex invocations have exited, the workspace
batch runs normal `codex --version` as coding UID 2000 without credentials, with the
image's scratch environment and retained `CODEX_HOME`. In pinned
[Codex 0.169.0](https://github.com/openai/codex/blob/rust-v0.169.0/codex-rs/arg0/src/lib.rs),
the three absolute helper symlinks live under `CODEX_HOME/tmp/arg0-linux/codex-arg0*`,
not `TMPDIR`, and remain after this command exits. The batch requires fresh complete
helper evidence on the actual retained filesystem without intermediate symlink aliases,
then verifies those same links are encrypted and restored as metadata, never target
contents. It never copies helpers or redirects `CODEX_HOME` to scratch.
Encrypted restore verifies every retained regular file's hash, all retained symlink
metadata, and source directories, rather than only selected representative files.
Scratch cache targets and nested scratch mounts remain outside the archive; root
filesystem metadata remains excluded and the independent target's metadata is preserved.
This is synthetic local writer/age/import evidence, not backend off-host backup or
provider-volume recovery qualification.

Optional [T3 Connect enrollment](../../docs/t3-connect.md) uses the existing
primary VM and managed T3 unit, a private memory-only approval page on loopback
774, and a distinct root-only management credential. Normal gateway scopes do
not expand. Pending approval fences shutdown. Its updated native probe and
hosted cross-device/lifecycle acceptance remain unqualified; never run the
upstream service installer or show device approval in T3 terminal history.

## Prepared image

`image/manifest.lock.json` pins the Ubuntu cloud-image **serial and SHA256**, immutable
Ubuntu apt snapshot, Node 24.11.1, .NET SDK 10.0.100, Codex 0.169.0 (npm integrity lock),
the coding agents listed under [Coding agents](#coding-agents), GitHub CLI **2.83.1** and GitLab CLI **1.74.0** (official linux/amd64 release
archives, SHA256-verified before copying only their executables to `/usr/local/bin`),
T3 with its exact release, upstream commit and artifact digests, and Chromium shared libraries. The default supplies the coding environment, not an
application stack: it installs no application database server/client, creates no
application database/roles, and ships no emulator or preview canary application.
A browser executable or additional application SDK is intentionally not
silently downloaded; register an explicit pinned workload extension if needed.

An operator must download and verify the specified base image, then boot it in a
**disposable isolated builder VM without workspace volumes**. No build script allocates
provider resources. Stage only this subtree at `/opt/minicloud-image-source` inside that
VM. These commands are **not for a development workstation/control-plane/shared host**:

```sh
# INSIDE THE DISPOSABLE BUILDER ONLY, as root:
printf '%s\n' DESTROY-DISPOSABLE-IMAGE-BUILDER > /etc/minicloud-disposable-image-builder
cd /opt/minicloud-image-source
python3 -B image/build.py install \
  --acknowledgement DESTROY-DISPOSABLE-IMAGE-BUILDER \
  --source-revision <exact-40-character-minicloud-commit> --builder-identity <operator-build-id>
python3 -B image/build.py generalize \
  --acknowledgement DESTROY-DISPOSABLE-IMAGE-BUILDER
# Power off via the builder's management console, then explicitly capture/register the image.
```

The destructive guard requires all of: exact acknowledgement, root-owned builder marker,
Ubuntu 24.04, actual `/etc/cloud/build.info` serial matching the pinned `20251001` **before
any package mutation**, x86_64 VM, no enrolled runtime, no mounted workspace or provider volume.
Generalization erases root/user homes, agent logins, build source, workspace and T3 data,
enrollment/runtime state, cloud-init seed/state/logs, machine IDs, SSH host keys and logs.
First boot regenerates identity/host keys through systemd/cloud-init. Do not execute more
interactive build commands after generalization. Capture only after power-off.

### Provider images (Hetzner, DigitalOcean, Vultr)

The same recipe produces the image for every supported provider; only capture and
registration differ. Build in the provider account and region that will run the
workspaces, from the pinned Ubuntu 24.04 cloud image or that provider's stock Ubuntu
24.04 x64 image **only when its `/etc/cloud/build.info` serial matches the lock** (the
guard refuses any other serial; import the pinned cloud image otherwise).

| Provider | Builder | Capture | `ProviderImageId` |
| --- | --- | --- | --- |
| Hetzner | Server without volumes | Server snapshot after power-off | numeric image ID |
| DigitalOcean | Droplet without volumes, or import the pinned `.img` as a custom image and boot it | Droplet snapshot after power-off | numeric image ID of the snapshot |
| Vultr | Instance without block storage | Instance snapshot after power-off | snapshot UUID |

Each `DevelopmentWorkspaces:Images:<image-key>` entry names exactly one
`ProviderAccountId` and `ProviderImageId`. Register one image key per provider account
(for example `t3-connect-2026-11-hetzner`, `…-digitalocean`, `…-vultr`) with the same
`ManifestSha256`, and list the regions where the image is available in `AllowedRegions`
(DigitalOcean and Vultr snapshots are regional; transfer them first if needed). Copy the
installed manifest's `agents` array into the entry's `Agents` so the API, CLI and
dashboard show what the image contains:

```json
"Agents": [
  {"Id": "codex", "Name": "Codex", "Version": "0.169.0"},
  {"Id": "claude", "Name": "Claude Code", "Version": "2.1.328"}
]
```

Provider differences that affect isolation and operations:

* **Volumes.** Hetzner and DigitalOcean create ext4 volumes; the worker only probes them.
  Vultr block storage is created raw, so the worker formats it with `bootstrap.py
  volume-format` **only** when the volume was created by the same operation and no
  filesystem is present; an existing volume without a filesystem blocks instead of being
  formatted. Stable devices are `scsi-0HC_Volume_<id>` (Hetzner), `scsi-0DO_Volume_<name>`
  (DigitalOcean) and `virtio-<first 20 characters of the mount ID>` (Vultr).
* **Firewalls.** Hetzner and DigitalOcean firewalls allow inbound SSH only from the
  management addresses and restrict egress to HTTP(S), DNS and NTP. Vultr firewall
  groups are **inbound only**: the same SSH restriction applies, but egress is not
  provider-filtered. Treat Vultr workspaces as having weaker network isolation.
* **Root password e-mail.** DigitalOcean e-mails a root password when no provider SSH
  key is attached. The image's SSH configuration disables password authentication, and
  management access uses only the cloud-init key.

Installed manifest: `/opt/minicloud-workspace/image-manifest.json`, including actual dpkg
versions, source revision, builder identity, artifact integrity and runtime CLI checks.
This is a reproducible **input/toolchain recipe**, not a claim of byte-identical cloud
snapshot disks. Package auto-updates are masked: patch by building/registering a new pinned
image, not by silently changing an existing image. Toolchain installation happens only
in the builder, never on a workspace boot.

The installed binaries and `systemd/` units use UID/GID **2000:2000** (`coding`, no SSH login).
The workspace root is explicitly traversable (0755). User-created application paths
and data belong to the user; no database-engine directories or accounts are provisioned.
No recursive permission changes occur.
The pinned T3 empty Foundation database is recognized only with zero events, projections
and outbox entries. Native x86-64 stdin-only `pselect6` additionally requires reading and
rechecking the actual fd set; a freezer-obscured wait requires its cgroup's frozen proof.
Neither adapter enables terminal-input observation or qualifies the production gateway.
The pinned T3 read-only resource monitor is recognized separately only by its exact
release digest, immutable root-owned code, argument-free invocation, direct T3 parent,
registered service cgroup and stdio IPC, with no TTY or children. Other
children, agents and helpers remain unregistered work. Tool archives install root-owned.
The pinned T3 CLI's graceful interrupted exit (130) is a successful systemd stop;
other nonzero exits still fail the clean-stop fence.
Only root key-based SSH is admitted, with no agent forwarding. Provider firewalls/network
rules **must** deny lateral VM/sibling/control-plane-private access and public service
ports; this filesystem runtime cannot prove network isolation. Never forward a worker SSH
agent/key or provider/control-plane credentials into coding processes.

## GitHub Copilot CLI in T3

The recipe pins the official Linux x64 Copilot CLI **1.0.114** release archive and SHA-256
alongside the existing Codex installation. `/usr/local/bin/copilot` is a root-owned launcher
for `/opt/minicloud-copilot/copilot`, with automatic updates disabled so a user cache cannot
silently replace the qualified version. Terminal commands and the T3 service use this same
executable and coding user's home, `/workspace/home`. No login is included in the image.

Before each T3 start, `copilot_provider.py configure` runs as `coding`. It adds
`minicloud-copilot` to `/workspace/t3/userdata/settings.json`, using the pinned T3
`acpRegistry` driver with `source: local`, `commandPath: /usr/local/bin/copilot`, and
literal `commandArgs: ["--acp", "--stdio"]`. This is the documented **Local ACP command**
path, not an invented Copilot driver. The card is named **GitHub Copilot CLI** under
**Settings → Providers**. Available models come from the agent after sign-in; none are
hard-coded or promised before authentication. Its only environment override is the nonsecret
`COPILOT_AUTO_UPDATE=false`, also applied by T3 to the agent's sign-in command.

Setup preserves unrelated settings, Codex, native Copilot credentials, and T3 threads.
Reapplying it leaves an existing local Copilot ACP instance (including a custom ID, home,
or disabled state) untouched instead of duplicating it. A conflicting managed ID or invalid
settings produces a sanitized setup error in the service journal; the pre-start failure
does not prevent Codex/T3 from starting. Repair the settings or ID before retrying setup.
Do not run `configure` against a live T3 settings writer: use the managed service's next
start, after safely stopping active work. Existing workspaces require an explicit prepared
image rollout preserving their workspace volume; this recipe does not patch running hosts.

### Headless sign-in and diagnostics

Use an authorized, **unrecorded management SSH terminal**, not a CI log or a recorded T3
terminal (T3 retains terminal output). Do not use the provider's in-app terminal sign-in
for this private flow. On the workspace host:

```sh
# As the workspace operator; no tokens in command arguments or environment.
umask 077
runuser -u coding -- env -i HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  copilot login --device-code
# Separately check the actual pinned stdio transport, without opening a thread:
runuser -u coding -- env -i HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  python3 -B /opt/minicloud-workspace/copilot_provider.py probe
```

Open the displayed GitHub URL on your own browser and enter the short-lived code privately.
An existing login in the coding user's home is reused; do **not** log out, delete
`~/.copilot`, replace the home, or sign in as root to enable the provider. On a headless
host without a credential store, Copilot may store its token under `~/.copilot`; treat the
entire home and workspace backups as sensitive. Never capture device codes/tokens in build
logs, screenshots, support bundles or exported provider configuration.

The probe reports `Compatible` only after an actual ACP v1 `initialize` response identifies
the pinned Copilot version. It explicitly reports authentication as **NotChecked**: it does
not send prompts, create sessions, export credentials, or assert model access. Missing,
incompatible, malformed and unresponsive installations report actionable `SetupRequired`;
rebuild/roll out the pinned image instead of installing an unverified latest CLI. When
signed out, the pinned agent advertises `copilot-login` terminal authentication; T3's ACP
provider health/sign-in state remains authoritative. Complete the private management-terminal
login above and retry/reconnect the provider before choosing a model.

Automatic managed setup needs no T3 bearer. Normal gateway session scopes remain unchanged;
do not grant settings-management access to routine coding sessions. Any manual provider
configuration must use a separately authorized T3 settings session.

Qualification references:
[pinned T3 local-command guide](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/docs/user/providers-acp.md#add-a-local-command),
its `packages/provider-acp-registry/src/settings.ts` and `packages/contracts/src/providerInstance.ts`,
and [GitHub ACP stdio reference](https://docs.github.com/en/copilot/reference/copilot-cli-reference/acp-server).
The issue's guide URL omits `c1` from the commit; the manifest's full 40-character SHA is used.

Deterministic tests cover seeding/reapplication, existing settings/login/thread preservation,
transport/version errors and artifact installation. CLI 1.0.114 `--acp --stdio` initialization
was also exercised locally against the installed release. The opt-in disposable-image native
harness checks seeded settings and stdio initialization as UID 2000, but **has not been run
for this recipe**. No new provider image is qualified by these tests.

**Remaining hosted acceptance:** build/generalize/register the image, start a fresh workspace,
use a sanctioned authenticated remote browser to confirm the provider card and sign-in state,
select an agent-reported model, send a project prompt and a follow-up in the same thread.
Also verify Codex chat, then reapply setup/restart safely on an already signed-in volume and
confirm both providers and existing threads still work. No authenticated browser prompt,
follow-up, or hosted Codex result is claimed without that session.

## Coding agents

All agents are pinned in `image/manifest.lock.json` and installed root-owned with
self-update disabled; patch them by building a new image. The manifest's `agents` array
records the IDs, names and versions shown by the API, CLI and dashboard.

| Agent | Version | Install | T3 provider |
| --- | --- | --- | --- |
| Codex | 0.169.0 | npm lockfile integrity (`tools/package-lock.json`) | built-in `codex` |
| GitHub Copilot CLI | 1.0.114 | release archive SHA-256 | `minicloud-copilot` (local ACP, above) |
| Claude Code | 2.1.328 | native `@anthropic-ai/claude-code-linux-x64` package, npm lockfile integrity; `/usr/local/bin/claude` sets `DISABLE_AUTOUPDATER=1` | built-in `claudeAgent` (enabled by default, uses `claude` from `PATH`) |
| Antigravity | 1.4.5 | ACP server zip SHA-256 and size; only `agy_acp_server.par` and `localharness_external` are copied to `/opt/minicloud-antigravity` | `minicloud-antigravity` (`antigravity` driver, `binaryPath` set) |
| Muse Code | 1.4.24-R9131.1 | release binary SHA-256 and size at `/opt/minicloud-muse/muse`; `/usr/local/bin/muse` sets `MUSE_NO_AUTO_UPDATE=1` | `minicloud-muse` (`muse` driver, `binaryPath` set) |

T3 0.1.9 supports all of these natively. Its `antigravity` and `muse` drivers are disabled
by default, so `copilot_provider.py configure` adds an enabled instance for each one whose
pinned executable exists. Any existing instance of the same driver, including a disabled
one, is the user's choice and is left alone. An occupied instance ID is reported in the
setup result without blocking T3. The Claude Code build verifies `claude --version`.
Antigravity and Muse are verified by digest and size only, because their version commands
need a signed-in home.

The image includes no agent logins. Sign in privately as `coding` (HOME=/workspace/home)
from an unrecorded management terminal, as for Copilot: `claude` (then `/login`) for Claude
Code and `muse login` for Muse. Choose the Antigravity sign-in method in **Settings →
Providers**. The three agent binaries add about 1.8 GB to the image.

## GitHub and GitLab source control

The T3 service explicitly uses `PATH=/usr/local/bin:/usr/bin:/bin` and
`HOME=/workspace/home`; its terminals inherit access to `gh` and `glab` as UID 2000.
No provider sign-in or Git credential setup runs during image build/bootstrap.
No provider tokens, account configurations, device codes, or test-repository data
belong in the image, manifest, native reports, CI output, or screenshots. Generalize
after qualification and before capture; never capture a workspace after user login.

On a fresh workspace, open **T3 → Settings → Source control** and refresh discovery.
Both providers should be available and signed out, not missing/unknown. From its terminal,
run `gh --version` and `glab --version`. Use the provider's sign-in action if offered by
the pinned T3 configuration. Otherwise an operator can provide a sanctioned,
**non-recorded interactive management session** running the CLI as the workspace user.
T3 persists terminal output, so do **not** display browser/device codes or enter credentials
in its recorded terminal. From the approved session, the operator launches one of:

```sh
setpriv --reuid=2000 --regid=2000 --clear-groups env -i \
  HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  gh auth login --hostname github.com --git-protocol https --web
setpriv --reuid=2000 --regid=2000 --clear-groups env -i \
  HOME=/workspace/home PATH=/usr/local/bin:/usr/bin:/bin \
  glab auth login --hostname gitlab.com
```

The user completes the flow for their own account; never sign in as root or use an
operator/provider account. Follow the displayed browser/device instructions on your own browser; do not capture
the terminal/session or paste tokens into commands, chat, logs, or shell history.
Use only user-authorized accounts and minimal access to a designated test repository.
For headless GitLab, follow `glab auth login --help` for the supported interactive
browser/token choices; enter any token only into its hidden prompt, never an argument.
Refresh Source control and confirm the account/authentication state. Without printing
credentials, verify read access with `gh repo view OWNER/TEST-REPO` and
`glab repo view GROUP/TEST-REPO`. Do not push/create repositories as a qualification step.

When GitHub asks whether to authenticate **Git**, decline. Do not run
`gh auth setup-git`, change global `credential.helper`, or replace Minicloud's staged
repository-bound Git credentials. CLI sign-in is separate from the root-managed
`git-credential.py` helper, short-lived token expiry, configured repository allowlist,
push authorization, and Git-save gates; none are broadened by installing these tools.
CLI credentials can grant additional access as that user, so sign-in requires explicit
user authorization. The headless image has no assumed OS keyring: CLI credentials may
be stored in private files under `/workspace/home/.config/{gh,glab-cli}`. Treat retained
home/backup data as sensitive; do not copy it to another user/workspace or image.
Ephemeral workspaces do not retain these sign-ins after deletion.

The native qualification now requires missing-binary/version checks as coding UID 2000
using the actual running T3 service PATH, execution of both version commands through a
real T3 terminal, and the authenticated `server.discoverSourceControl` RPC reporting both
providers available and unauthenticated with matching versions. This is the RPC behind
the pinned Settings page: upstream commit above, `packages/contracts/src/rpc.ts`,
`packages/source-control-{github,gitlab}/src/server/*SourceControlProvider.ts`, and
`apps/web/src/components/settings/SourceControlSettings.tsx`. It never performs sign-in,
supplies provider credentials, or reports raw auth output. A missing provider blocks the
native candidate. Source tests use synthetic archives/RPC fixtures, not a hosted login.
**The updated image/native probes and browser/account/repository checks have not been
run in a sanctioned hosted environment.** `sourceControlUserSignInQualified` remains
false; source tests are not evidence of provider authentication or browser qualification.

### Qualify the latest stable T3 release

The [T3 release candidate workflow](../../.github/workflows/t3-release-candidate.yml)
checks the upstream stable release daily and supports manual dispatch. A newer
release yields a candidate lockfile and qualification checklist. The candidate
pins the upstream commit, verifies the Linux x64 archive against GitHub's
published SHA-256, and pins the resource monitor extracted by reading its archive
entry. A draft PR is optional. Configure the repository secret
`T3_RELEASE_PR_TOKEN` with a bot's fine-grained personal access token scoped
to this repository with Contents and Pull requests write permissions. With no
token, use the workflow artifact's lockfile in a manual candidate PR.

To prepare the same candidate locally from the repository root with Python 3.11+
and network access:

```sh
mkdir -p .local/artifacts/t3-release
python3 -B templates/development-workspaces/image/t3-candidate.py \
  --output .local/artifacts/t3-release/manifest.lock.json
```

Review the release notes and use the candidate lockfile in the exact source tree
for the [disposable builder](#prepared-image). On it, run `image/native.py plan`
with the runtime SHA-256, then `image/native.py run` with the same digest and the
root-owned short-lived authorization from the operator. Hosted operators can use
their guarded builder qualification and preflight helpers with the exact source
revision and runtime SHA-256. Retain the installed manifest and native report before generalizing the builder. Use the exact installed
version and protocol in the qualified image registration's `T3Version` and
`T3EnvironmentProtocol`. Protocol 2 is currently supported.

For hosted acceptance, start a disposable workspace on that exact registered
image. Follow the [T3 Connect procedure](../../docs/t3-connect.md) to verify the
authenticated gateway, WebSocket, owner enrollment, relay and desktop discovery.
Use synthetic data and a verified backup to test clean stop, retained-volume
reattachment and rollback before moving existing workspaces. Publish the
qualified image as the default only after the native and hosted checks pass.

### Rolling out to existing workspaces

Build, qualify, generalize, power off, capture/register a **new immutable image revision**;
retain the previous image and its exact policy registration until all running
generations that use it are stopped. Keep it available for rollback. Update the approved image selection for new
workspaces. Changing a catalog/default image alone does not update an existing VM.
Do not install packages ad hoc on retained workspaces or change the manifest in place.

For each retained primary, use the authorized lifecycle to fence admissions, finish
active work, quiesce, obtain/validate the clean-stop receipt, and verify a restorable
encrypted off-host backup before deleting old compute. Record the volume identity,
filesystem UUID, owner claim, generation, and old image. Prove old compute is absent
and exclusive volume ownership before attaching that **same retained volume** to a
replacement VM on the new image. Re-enroll with a new generation and
`initializeOwner:false`; never format/reinitialize the volume or clone over its files.
Preserve all of `/workspace`, including repos (tracked/untracked/ignored files), T3 state,
home/CLI sign-ins, transcripts, config, artifacts, and user application data. Verify
the bootstrap receipt, versions, Source control, repository access and retained data
before reopening admissions. This is an operator procedure, not an automatic migration;
use the previous image and the same exclusive-ownership/generation fences for rollback.
For ephemeral workspaces, first complete the normal verified Git-save or explicitly
authorized discard flow; then create a new workspace on the new image. Root-disk,
ignored files and local credentials are not retained by that flow.

## Bootstrap and volume contract (parent worker)

The image installs:

* `/opt/minicloud-workspace/{runtime.py,bootstrap.py,issue-t3-session.py,git-credential.py,copilot_provider.py,storage_tiers.py,scratch.env}`
* `/etc/systemd/system/minicloud{work.slice,-workspace.service,-workspace-mount.service,-t3.service}`
* `/etc/tmpfiles.d/minicloud-scratch.conf` and `/etc/profile.d/minicloud-scratch.sh` (see
  [Storage tiers](#storage-tiers))
* `/opt/minicloud-workspace/image-manifest.json`

Upload root-owned mode-0600 configuration to `/etc/minicloud-workspace/runtime.json`.
First-boot cloud-init must explicitly enable key-only root access (`disable_root:false`)
with only the selected management public key; the image's SSH allowlist admits root only.
Example **primary** (replace synthetic values; root fields outside this schema fail closed):

```json
{
  "vmId": "vm-example", "generation": 7, "kind": "primary",
  "uid": 2000, "gid": 2000, "workspace": "/workspace",
  "volume": {
    "device": "/dev/disk/by-id/scsi-0HC_Volume_123",
    "providerDeviceId": "scsi-0HC_Volume_123", "providerVolumeId": "123",
    "filesystemUuid": "UUID-READ-FROM-THE-ACTUAL-FORMATTED-VOLUME",
    "initializeOwner": false,
    "owner": {
      "organizationId": "org-example", "workspaceId": "workspace-example",
      "providerAccountId": "account-example", "location": "region-example",
      "providerVolumeId": "123"
    }
  },
  "infrastructureUnits": ["minicloud-t3.service"],
  "stopUnits": ["minicloud-t3.service"]
}
```

The examples use a Hetzner device. On DigitalOcean the device is
`/dev/disk/by-id/scsi-0DO_Volume_<volume-name>`, and on Vultr it is
`/dev/disk/by-id/virtio-<first 20 characters of the block mount ID>`. Vultr volumes are
created raw. For a new Vultr volume only, the worker first runs `bootstrap.py volume-format`
with the same `--device` and `--provider-device-id`, which refuses any other filesystem or
signature and returns the existing ext4 volume on retry; it then probes.

For a **newly provider-formatted empty volume only**, the trusted worker first probes:

```sh
python3 -B /opt/minicloud-workspace/bootstrap.py volume-probe \
  --device /dev/disk/by-id/scsi-0HC_Volume_123 \
  --provider-device-id scsi-0HC_Volume_123
```

This returns `{"ok":true,"result":{"schemaVersion":1,"type":"VolumeProbe","device":"…",
"providerDeviceId":"…","filesystemUuid":"…","filesystem":"ext4","formatted":false}}`.
Persist the UUID in the control plane **before** admitting a writer. Set `initializeOwner:true`
only for that new, empty, separately provisioned volume. It creates a root-owned
`.minicloud-volume.json` exactly equal to `owner`. Existing content without a matching
ownership marker blocks; resume never initializes/rewrites claims or formats anything.
`blkid`, stable provider by-id device, ext4 UUID, mounted source/UUID, and ownership marker
are checked. Unexpected mounts/devices/filesystems/claims, missing devices or nonempty local
fallback directories block. The worker must separately prove exclusive provider ownership
and previous-generation compute absence before attach. There is no detach/force-detach API.

After successful primary bootstrap, managed T3 is enabled on that VM. On reboot
`minicloud-workspace-mount.service` must succeed before T3 or either scratch
unit may start. It checks the persisted generation and reopens `/workspace`
using the recorded provider by-id device and ext4 UUID, then verifies the
existing owner marker. It never formats a device or initializes ownership.
Only an unfenced `Ready` generation can automatically remount; a clean stop
does not authorize reopening it. Ephemeral storage is unchanged.

The mount guard waits up to 45 seconds for the provider device. If the device
is missing or identity checks fail, see
`journalctl -b -u minicloud-workspace-mount.service`. T3 and scratch stay
stopped; `/workspace` is not used as local fallback storage. After the same
registered device is available, starting `minicloud-t3.service` retries the
mount. The runtime reports `StorageUnavailable` until the retained mount can
be verified; on its next successful observation the worker blocks gateway
admission until both the retained mount and T3 readiness are verified again.
Existing VMs get this behavior only when started on a newly qualified image.

An **ephemeral** configuration must omit `volume`, `backup`, `restore`, and export hooks:

```json
{
  "vmId": "vm-ephemeral", "generation": 1, "kind": "ephemeral",
  "uid": 2000, "gid": 2000, "workspace": "/workspace",
  "ignoredDataAcknowledged": true,
  "repositories": [{
    "path": "/workspace/repos/project", "origin": "https://example.invalid/team/project.git",
    "baseSha": "0123456789012345678901234567890123456789",
    "branch": "work/vm-ephemeral-g1", "remoteRef": "refs/heads/work/vm-ephemeral-g1",
    "defaultBranches": ["main"], "pushAuthorized": false
  }]
}
```

The worker supplies a unique work branch and exact resolved base SHA. First boot clones and
checks out that branch; no commit/push is performed. Primary bootstrap never clones over
retained files. Ephemeral storage is root-disk only; volume claims, common mounted exports,
known primary/backup paths and shared container sockets are refused. Provider allocation,
networking, credential grants and gateway authorization must enforce the remaining isolation.
No primary credentials are inherited. Private Git over pinned SSH can use an explicitly
selected per-generation key at `/run/minicloud-git/key` (owner 2000, mode 0400; directory
root-owned 0711), plus root-owned `/etc/minicloud-git-known-hosts` (0644). HTTPS credential
helpers from repositories/user configuration are disabled. SSH keys require independent
revocation policy; GitHub deploy keys are **not intrinsically expiring**.

Preferred private Git uses an explicitly selected, genuinely repository-bound GitHub App
installation token issued for at most one hour. Parent stages `/run/minicloud-git/credential.json`
(regular file, owner 2000, mode 0400, single link, directory root-owned 0711):
`{"origin":"https://github.com/org/repo.git","username":"x-access-token","password":"SECRET",
"expiresAt":1790000000}`. Do not put this material in runtime config, image, logs or receipts.
The installed root-owned `git-credential.py get` answers only the exact HTTPS host/path
with future expiry no more than one hour away; store/erase, redirects, missing path,
other repositories/hosts and expired credentials have no response or diagnostics.
Git requests may end at EOF after a complete newline-terminated field or at a blank line;
truncated fields still fail closed.
There is no prompt/fallback. Parent must use the token's **real issuer expiry**, renew/revoke
explicitly and never claim this helper turns a broad PAT/deploy key into a short-lived token.
All Git/helper/SSH execution runs as UID 2000, never root. Remote evidence is collected in
a fresh independent verifier with redirects disabled, no checkout-controlled HTTP/credential
configuration, and no replacement refs/grafts. Absent the staged file, public Git uses no helper.

Upload a distinct root-owned 0600 **one-time** `/run/minicloud-workspace/enrollment.json`:
`{"url":"https://control.example.invalid/enroll","token":"ONE_TIME_SECRET","vmId":"…",
"generation":7,"operationId":"start-operation","expiresAt":1790000000}`.
Optional `caFile` selects a private trust CA. Then invoke over **pinned management SSH**:

```sh
python3 -B /opt/minicloud-workspace/bootstrap.py bootstrap
```

It mounts/initializes the declared layout, starts services, verifies that the loopback
T3 descriptor matches the installed image's T3 version and protocol, and POSTs HTTPS JSON
`{schemaVersion,vmId,generation,kind,operationId,imageManifestSha256,bootstrapReceipt}`
with the one-time token as Bearer authorization. HTTP 200 is explicit acceptance; no redirects.
Intent is persisted **before** enrollment I/O, and token input is deleted after an attempt.
An ambiguous result is never automatically replayed: reconcile/reissue through the parent.
No tools are reinstalled. Parent must not publish Ready/gateway routes until acceptance and
its own generation/endpoint checks succeed.

## Paths and supervision

`/workspace/repos` includes all tracked, untracked and ignored primary edits;
`/workspace/t3` is T3's `--base-dir`; `/workspace/transcripts` holds explicitly file-backed
task transcripts; `/workspace/home` holds intentionally selected coding-agent state
(`CODEX_HOME=/workspace/home/.codex`); `config` and `artifacts` hold workspace
configuration/publication files. Users choose any additional application directories
under their workspace. Root home, `/run`, temporary files and caches are **not durable**.
There is no assumed database engine, emulator export format or application migration.
Application-specific lifecycle integration must be explicit, not inferred from a path.

### Storage tiers

`/workspace` is the **retained** tier: the provider volume that survives VM replacement.
Regenerable data lives on the **scratch** tier, `/var/lib/minicloud-scratch` on the VM's
local disk, which is never under `/workspace` and may be empty after any VM replacement:

| Path | Owner, mode | Holds |
| --- | --- | --- |
| `/var/lib/minicloud-scratch` | root, 0755 | scratch root |
| `cache` | coding, 0700 | package, build and tool caches (below) |
| `tmp` | coding, 0700 | `TMPDIR`; emptied at every boot |
| `docker` | root, 0711 | reserved for a container runtime's data root |
| `repos` | root, 0700 | sources of build-output bind mounts into `/workspace/repos` |

`/etc/tmpfiles.d/minicloud-scratch.conf` creates the layout at every boot
(`systemd-tmpfiles-setup.service`, before `sysinit.target` and therefore before T3), and
`storage_tiers.py prepare` re-applies it before each T3 start.

`/opt/minicloud-workspace/scratch.env` is the single list of redirected variables. The T3
service reads it with `EnvironmentFile=`, so T3, its terminals and agents inherit it;
processes started by the runtime with `systemd-run` get the same `EnvironmentFile=`; and
`/etc/profile.d/minicloud-scratch.sh` exports it in login shells of UID 2000 only (for
example `su - coding` or `runuser -l coding` from the root management SSH session).
Root and other users are unchanged. Values are fixed absolute paths, parsed line by line,
never sourced.

| Variable | Scratch path (under `/var/lib/minicloud-scratch`) |
| --- | --- |
| `XDG_CACHE_HOME` | `cache` (also Yarn 1, pip/uv fallbacks, Playwright fallback, …) |
| `TMPDIR` | `tmp` |
| `npm_config_cache` | `cache/npm` |
| `pnpm_config_store_dir` | `cache/pnpm/store` (pnpm 11; pnpm 10 follows the store link below) |
| `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` | `cache/nuget/packages`, `cache/nuget/http-cache` |
| `PIP_CACHE_DIR`, `UV_CACHE_DIR` | `cache/pip`, `cache/uv` |
| `GOCACHE`, `GOMODCACHE` | `cache/go-build`, `cache/go-mod` |
| `PLAYWRIGHT_BROWSERS_PATH` | `cache/ms-playwright` |
| `PYTHONPYCACHEPREFIX` | `cache/pycache` |

Tools whose home directory mixes caches with configuration or credentials keep that home
under `/workspace/home`; only their cache subdirectories become symlinks into
`scratch/cache`, created by `storage_tiers.py` as the coding user when absent:
`~/.cache` (the cache root itself), `~/.cargo/{registry,git}`,
`~/.gradle/{caches,wrapper/dists}`, `~/.m2/repository`, `~/.local/share/pnpm/store` and
`~/.yarn/berry/cache`. An existing real directory or a user-chosen link is left alone.
Deliberately **not** set:

* `CARGO_HOME` and `GRADLE_USER_HOME`: they also hold `credentials.toml`/`config.toml`,
  `gradle.properties` and installed binaries, which must stay retained.
* `CARGO_TARGET_DIR`: one shared target directory breaks project-relative `./target`
  paths and lets projects collide; build outputs use per-repository bind mounts instead.
* `YARN_CACHE_FOLDER`: it would also override Yarn Berry's per-project (zero-install) cache.
* `PNPM_HOME`: it holds globally installed pnpm binaries.
* `/etc/environment`: pam_env would apply it to root and every other user.

Consequences: Playwright browsers, `~/.m2/repository` (including artifacts installed with
`mvn install`) and every cache must be re-downloaded or rebuilt after VM replacement.
Tools that keep credentials under `$XDG_CACHE_HOME` now keep them on scratch; notably the
Hugging Face token (`$XDG_CACHE_HOME/huggingface/token` by default). To keep it retained,
export `HF_TOKEN_PATH=/workspace/home/.config/huggingface/token` (or `HF_HOME`) before
signing in, or supply `HF_TOKEN` per session.

**Container runtimes.** The image ships no Docker, containerd or Podman. If one is added,
its data root must be under `/var/lib/minicloud-scratch/docker` (for Docker,
`"data-root"` in `/etc/docker/daemon.json`); native qualification fails with
`CONTAINER_RUNTIME_DATA_ROOT_UNQUALIFIED` until that is qualified.

**One-time cache migration.** Before T3 starts, `minicloud-t3.service` runs
`storage_tiers.py prepare` as root (`ExecStartPre=-+`, so a failure is logged and never
blocks T3). Unless the root-only marker
`/var/lib/minicloud-workspace/storage-tiers-migrated.json` (0600) records completion, a
child that has dropped to UID/GID 2000 renames known cache directories in
`/workspace/home` (`.cache`, `.npm`, `.nuget/packages`, NuGet's `http-cache`, `v3-cache`
and `plugins-cache`, the pnpm and Yarn Berry stores, `go/pkg/mod`,
`.cargo/{registry,git}`, `.gradle/{caches,wrapper/dists}`, `.m2/repository`,
`.local/share/docker`) into `/workspace/home/.minicloud-cache-trash` and deletes them.
Every path component is opened with `O_NOFOLLOW` relative to its parent; only real
directories owned by UID 2000 on the `/workspace` filesystem are moved; symlinks, other
owners and other mounts are skipped. Configuration and credential files
(`.cargo/credentials.toml`, `.gradle/gradle.properties`, `.npmrc`, `NuGet.Config`, …) are
never touched, and `~/.cache` stays in place when it holds a Hugging Face token. Deletion
is bounded to 40 seconds per T3 start; anything left is retried at the next start (the
trash folder can also be deleted from the folder-size view). The marker is on the root
disk, so a replacement VM repeats the migration, which is then a no-op. The helper logs
one bounded JSON line to the journal and always exits 0.

`runtime.py serve` owns `/run/minicloud-workspace/control.sock` (0600, root peer UID checked).
The agent is outside the freezable coding slice. State/atomic fsynced receipts live in
`/var/lib/minicloud-workspace`; parent must durably persist returned lifecycle receipts
**before deleting compute**. File presence on the disposable VM alone is not off-VM durability.

### T3 service recovery

`minicloud-t3.service` restarts on unexpected exit, signal, or OOM kill
(`Restart=on-failure`, `RestartSec=5`). The default `OOMPolicy=stop` turns a kernel or
cgroup OOM kill into a failed run that restarts on the same VM generation and retained
volume, so environment identity, projects, threads, and sign-ins persist. Interrupted
agent turns are not resumed. Exit 130, T3's graceful stop code, is a success and does not
restart. `StartLimitIntervalSec=300` and `StartLimitBurst=5` bound the retries, including
explicit starts: a fresh start followed by five SIGKILLs within the window ends in
`failed` (`Result=signal` on Ubuntu systemd `255.4-1ubuntu8.10`). The scratch watcher
has its own five-start budget; `PartOf` restart propagation can exhaust it in the same
burst. Resetting T3 alone does not restore an exhausted watcher. For explicit recovery
on an authorized disposable qualification fixture, first preserve the exhausted T3
and watcher observations and verify both drained, with no jobs or control processes.
Only a watcher with the shipping policy, `failed/failed`, `Result=start-limit-hit`,
zero watcher-owned automatic retries (`NRestarts=0`), and a clean previous exit
(`ExecMainCode=1`, `ExecMainStatus=0`) following the observed five T3 starts qualifies
as synchronized dependency exhaustion. After an owned stop and another drained-state
check, run `systemctl reset-failed minicloud-t3.service minicloud-scratch-watch.service`
and `systemctl start minicloud-t3.service`; positively verify both services and retained
identity. Other watcher faults or unknown history require investigation, not a reset.
This does not add automatic recovery or grant authority on a hosted workspace.

`systemctl stop` cancels a pending restart. While a dependency keeps the stopped unit loaded,
`inactive/dead` can retain the previous `Result=signal` even with no job or processes.
Freezing the work slice is not a restart barrier: the restart timer can create a start job
and an `ExecStartPre` control process.
The runtime queues the stop before thawing, waits for managed T3's cgroup to drain, then
refreezes. It checks for no job, no main or control process, and an empty cgroup before clearing any
residual signal/OOM failure from the canceled restart and requiring `inactive`. An unrelated
failure or any remaining work blocks the lifecycle stop. After unmount,
`ConditionPathIsDirectory=/workspace/t3` also refuses a start. Upstream's `t3 service install`
user unit is not used; it would add a second supervisor outside the workspace lifecycle.

The native probe `t3-oom-crash-restart-stop-race-and-retry-budget` separately verifies
automatic SIGKILL and cgroup OOM recovery: same environment ID, retained project, filesystem
UUID and VM generation; authenticated HTTP and WebSocket access; and Connect management.
It also checks exit 130, the exact five-failure budget, and job and freezer state when a
stop lands during the restart delay or frozen `ExecStartPre`. The probe runs before terminal
probes on the authorized isolated synthetic workspace, with no editor, agent turn or
active terminal child. Each independent case stops and positively verifies a drained unit
through a full restart window, then resets the shared start-limit accounting and starts
once. HTTP readiness alone is not an idle precondition: after that start, the same guarded
positive-idle polling used after recovery must observe `IdleCandidate` before SIGKILL,
OOM injection, intentional exit or frozen-stop setup. The uninterrupted retry-budget
case also requires positive idle after each automatic start; it never resets mid-case.
This waits for startup provider-detection helpers to exit naturally, without restarting,
classifying them as infrastructure, hiding them or signaling surviving children.
Persistent busy/unknown work, identity drift or a late idle observation fails closed before
fault injection. Positive idle is advisory, not a promise that no later child can appear:
the separate positive cgroup-empty and six-second stopped/restart-window assertions remain
mandatory. `KillMode=process`, `SendSIGKILL=no` and active-child lifecycle/classifier gates
are unchanged; retained work must block, not be killed to make qualification pass.
The scratch watcher is `PartOf=minicloud-t3.service` and independently ships the same
five-start/300-second accounting (with `RestartSec=10`). Independent cases therefore
inspect its failure/policy/identity **before stop**, positively verify it stopped and
drained after the owned stop, then reset accounting for **both** units at that boundary.
An independently failed or pending watcher is never cleared to obtain a pass. No reset occurs between
automatic starts or during the uninterrupted retry-budget assertion; only after
exhaustion has remained visible through the complete stopped-window assertion does
the next explicit owned case boundary reestablish the fixture. Normally recovered
cases and automatic budget starts must have the real root watcher active in its
owned service cgroup with the shipping policy, as must the later scratch-bind probe
before and after its work. Recovery receipts retain those verified watcher observations
for each case start, SIGKILL/OOM recovery, automatic budget start and explicit post-budget
recovery. The explicit post-budget boundary alone accepts the synchronized dependency
exhaustion described above, after all five starts had healthy watcher observations
with no watcher-owned automatic retries. It rechecks the exhausted T3 state before
stop, retains the failed watcher evidence in the receipt, checks the watcher drained
again after stop, and then uses the existing named two-unit reset. Missing exit evidence,
independent retries, unrelated results, policy/identity mismatches, jobs, or remaining
processes still block recovery before reset. These changes do
not relax either shipping unit's limits or change runtime lifecycle gates; native
execution remains necessary to establish the actual product restart consequences.
For the frozen prestart fixture, systemd `255.4-1ubuntu8.10` can send SIGKILL
and then report auxiliary-process `EINVAL`; the command's exit status alone is not delivery
evidence. The native-only fixture requires the exact owned cgroup, the same main process's
SIGKILL exit, an empty service cgroup, and the unrestarted `auto-restart` timer before it
continues. A remaining process, wrong exit, or missed transition still blocks qualification.
The frozen prestart, stop-completion and retry-budget assertions are unchanged.
Before rollout, a disposable hosted workspace must also show that a Minicloud gateway
launch and a previously linked T3 Connect relay recover after a T3 crash.

### Build outputs on local scratch

`scratch_mounts.py` (`minicloud-scratch-mounts`) is a root helper that keeps regenerable,
git-ignored build outputs off the retained volume. It bind-mounts
`/var/lib/minicloud-scratch/repos/<repo-id>/<relative-path>` over the output directory inside
`/workspace/repos`. Paths in the checkout stay the same: no symlinks and no tool configuration.
`<repo-id>` is the checkout's directory name plus a hash of its path below `repos` and its
inode, so ids are stable and two checkouts never share one. Relative paths are validated
component by component: no `..`, no symlinks, and no other mounts. `repos` under the scratch
root is root-owned `0700` and is bind-mounted onto itself with `nosuid,nodev`. Each output
directory below it is owned by the coding user.

A directory moves to scratch only when **all** of the following hold:

* Its name is one of `node_modules`, `bin`, `obj`, `dist`, `build`, `out`, `target`, `.next`,
  `.nuxt`, `.svelte-kit`, `.turbo`, `.parcel-cache`, `.angular`, `.gradle`, `.venv`, `venv`,
  `__pycache__`, `coverage`, `.pytest_cache` or `.mypy_cache`.
* `git check-ignore` reports it ignored. Git runs as the coding user, who owns the checkout,
  with a minimal environment, no system or global config, `core.hooksPath=/dev/null` and
  `core.fsmonitor=false`. A directory that contains tracked files is never moved, even if it
  also matches an ignore rule.
* It is a real directory owned by the coding user on the `/workspace` filesystem, not
  already a mount point, and not inside `.git`.

Tracked, non-ignored and non-allowlisted directories are never touched. Discovery covers
checkouts directly below `repos` and nested projects in monorepos. It goes at most 12
levels deep and does not descend into `.git`, `.hg`, `.svn`, `.jj`, `vendor` or
allowlisted output trees.

Manifests also create the expected outputs before the first build, as empty bind mounts, but
only when Git would ignore them:

| Manifest | Outputs |
| --- | --- |
| `package.json` | `node_modules` |
| `*.csproj`, `*.fsproj`, `*.vbproj` | `bin`, `obj` |
| `Cargo.toml` | `target` (outermost Cargo project only) |
| `pyproject.toml`, `setup.py`, `requirements*.txt` (checkout root) | `.venv` |
| `build.gradle*` | `build`, `.gradle` |
| `settings.gradle*` | `.gradle` |
| `pom.xml` | `target` |
| `angular.json` | `.angular` |
| `next.config.*` | `.next` |
| `nuxt.config.*` | `.nuxt` |
| `svelte.config.*` | `.svelte-kit` |
| `turbo.json` | `.turbo` |

The helper runs at three points:

1. `minicloud-scratch-mounts.service`, a oneshot ordered before `minicloud-t3.service`.
   T3 pulls it in with `Wants=`. It also runs before any coding process starts.
2. Runtime `bootstrap`, after initial clones and before T3 starts.
3. `minicloud-scratch-watch.service`, a long-running inotify watcher (via `ctypes`, with a
   bounded polling fallback). It handles new checkouts, manifests, output directories and
   removed mount points. It also runs a full pass every 5 minutes, or every minute while work
   is deferred.

An existing non-empty output relocates only after 60 seconds without changes, and only when
no process has its cwd, root or an open file inside it (checked through `/proc`). The helper:

1. Copies the directory to a staging directory on scratch. Owners, modes, timestamps,
   symlinks (as symlinks) and hardlinks within the tree are preserved. A root-only journal
   is written under `/workspace/.minicloud-scratch-trash`.
2. Moves the volume copy into that trash directory, then re-checks the moved copy and
   mounts scratch.
3. Deletes the volume copy.

After a crash, the next run finishes or rolls back each journal. Data is never lost:
conflicting or unjournaled trash stays in place and is reported. Busy or changing
directories are retried later. If a mount point is removed after it is unmounted, the next
pass can recreate the ignored output directory and its mount.

**Mount propagation.** The helper units use no mount namespacing, so mounts are made in the
host namespace. `minicloud-t3.service` (`ProtectSystem=strict`, `PrivateTmp=yes`), task units
and folder-size jobs run in their own namespaces, which systemd creates as slaves of the host.
Mounts and unmounts below `/workspace` therefore propagate into T3, its terminals and agents,
including those made after T3 started. Propagated mounts keep the host's read-write flags.
`minicloudwork.slice` changes nothing here. T3 is ordered `After=` the oneshot only so the
first build already sees the mounts; the native probe checks the mounts from inside a T3
terminal.

**Lifecycle.** Before the first idle check, `quiesce`, `git-save` and `discard` run
`scratch-suspend`: the helper is marked suspended and both helper units stop. Otherwise its
short-lived Git calls as the coding user would count as work outside the slice. After
`stop-services` and before the read-only remount, backup and unmount, `scratch-unmount`
unmounts every scratch mount, deepest first. A lazy unmount is used only as a last resort
and is reported. `PrimaryQuiesced` reports `scratchMounts:{unmounted,lazy,lazyPaths}`, and
any remaining mount blocks quiescence. `Linux.unmount` repeats `unmount-all` defensively.
`resume`, `cancel-drain` and `bootstrap` clear the suspension and restart the units, so
mounts come back without user action. On images without the helper, all of these steps do
nothing.

**Backups and folder sizes.** Backups never enter nested mounts. Archiving, validation and
representative hashes use a one-filesystem walk that records a mount point as an empty
directory. Scratch is already unmounted before a backup is taken. `disk_usage.py` reports
scratch mounts as `excluded` with `separate_mount` and never deletes across them. Confirmed
checkout deletion first releases only verified, owned scratch mounts inside its target,
as described below; it does not traverse their scratch contents. The
resource sampler counts them in `nestedMountsExcluded`. The trash directory stays in backups
because it can hold user data mid-relocation. After a restore it belongs to the coding user,
so the helper renames it to `.minicloud-scratch-trash.untrusted-*` without reading its
journals.

**Pressure safety net.** When the retained volume is more than 85% full (override with
`--pressure-percent 50..99`), each pass evicts allowlisted, ignored output directories still
on the volume. Only checkouts with no process inside them qualify, and the least recently
modified directories go first, until usage falls 5 points below the threshold. Each
eviction is journaled before deletion. Tracked, non-ignored, non-allowlisted and open
content is never deleted. Actions go to the journal (`journalctl -u
minicloud-scratch-watch`) and to `/run/minicloud-scratch-mounts/status.json`
(`scratch_mounts.py status`). The threshold is a command-line option because `runtime.json`
rejects unknown fields.

**Caveats.**

* `rename(2)` between a mounted output and the rest of the checkout fails with `EXDEV`. Most
  tools fall back to copying.
* pnpm hardlinks from its store fall back to copies unless the store is on scratch too.
* File watchers see the delete and recreate during a relocation. Run the dev server again if
  it does not recover.
* `rm -rf node_modules` empties the mount, then reports `Device or resource busy` for the
  directory itself, which stays mounted. Ordinary coding-user `rm -rf <checkout>` can
  likewise exit nonzero with `EBUSY` and leave a partly removed checkout. Watcher cleanup
  does not change that original exit status; there is no shell alias or automatic retry.
* `python -m venv .venv` fills a pre-created empty `.venv`. Tools that treat any existing
  `.venv` as an environment may need to be told to create it there.
* Containers that bind-mount a checkout before relocation do not see the new mount.
* Extended attributes, ACLs and sparseness are not copied.
* A global gitignore is not honored. Only repository ignore rules decide.
* After a checkout is renamed, its old mounts stay until the next unmount, and unused scratch
  is collected.
* After a raw-shell checkout removal leaves orphaned mounts, a later complete watcher pass
  can unmount them (retrying while busy). Removal/reclone then requires another operation.
  Release after 2 seconds and 9 seconds was observed in two disposable hosted-fixture removals
  for #327; neither time is a deadline or a guarantee for another workspace.
* Scratch is disposable: it survives a reboot on the same VM but is empty on a new VM, where
  outputs are rebuilt.
* Limits: 512 mounts, depth 12 and 100,000 scanned directories per pass.

### Confirmed checkout deletion

The supported single-operation path for a checkout with scratch binds is the existing
dashboard **Delete folder** operation, not raw `rm`. It requires a newly built and
qualified image containing this implementation; existing hosted images are not patched:

1. Finish builds/agents and close terminals, previews and other processes using the
   checkout. Deletion does not stop them for you.
2. On the running VM, select **Fetch disk usage**, open `/workspace/repos`, and select
   **Delete folder...** on the checkout itself, not an excluded scratch output.
3. Check the workspace, VM generation and exact folder in the dialog. Type its full
   `/workspace/repos/...` path and acknowledge permanent loss, then select
   **Delete folder permanently**.
4. Wait for **Folder deleted** before cloning to that path again. If deletion fails,
   is cancelled, partly completes or its result is unknown, scan again to inspect what
   remains; do not treat it as success.

The selection binds the workspace, VM generation, path and measured device/inode.
The runtime serializes scratch remounts during the target operation, checks active use,
and normally unmounts only verified helper-owned binds inside that exact checkout before
bounded deletion as the coding user. It never lazily unmounts for this operation, traverses
scratch contents, stops unrelated work, or removes another checkout's tracked files.
Unrelated mounts, changed identity/path, active or unobservable use and unsupported image
state refuse deletion; cancellation or a change after work starts can leave a partial
result. A root-only, target-specific remount inhibition remains after interrupted or
partly completed work. A fresh scan and confirmed retry can complete deletion when the
same target and ancestors still match; only verified success releases that inhibition.
Lost or changed identity stays inhibited rather than guessing. The inhibition is on
`/run` and clears on VM reboot, after all old deletion processes have ended. This is not
an atomic filesystem transaction or an undo mechanism.

**Acceptance correction for #228 / #247.** Replace the requirement that ordinary `rm -rf`
must succeed within one watcher pass with two distinct checks on a disposable checkout:
record the raw shell's actual exit code and remaining mounts (including `EBUSY`), then
verify the confirmed folder-delete job succeeds in one operation on an idle supported
checkout with owned scratch binds, leaving no target mounts and permitting reclone.
Also verify busy use, unrelated mounts, changed identity and cancellation are refused or
reported partial, with another checkout's tracked files unchanged. Public synthetic tests
do not qualify a hosted image; repeat these checks on the exact newly built image before
claiming hosted acceptance or rollout. No hosted/provider changes are part of #327.

### JSON control protocol

Run `python3 -B /opt/minicloud-workspace/runtime.py call`, sending one newline-terminated
JSON object on stdin. All requests require `vmId`, integer `generation`, `operationId`
(`[A-Za-z0-9_-]{1,128}`), and `action`. Responses are `{"ok":true,"result":{…}}` or
`{"ok":false,"state":"Blocked|AwaitingGitSave","reason":"sanitized actionable reason"}`.
Inspect `ok`, not only SSH exit status. Never log enrollment or T3 secret output.

* `status`: phase, revision, tasks/lease deadlines, useful activity, backup freshness.
  `freezeCompleted` and `filesystemReadOnly` distinguish a kernel-frozen-but-blocked operation
  from fully stopped/read-only data. Neither status flag is itself a deletion receipt.
  Optional `includeWorkInventory:true` adds read-only `workInventory.state` of `IdleCandidate`
  or `BusyOrUnknown`, with a sanitized blocking reason. This unfrozen observation is advisory,
  never authorization for deletion.
* `git-check`: Ready ephemeral only; inspects every checkout without freezing or changing the
  checkout, returning `GitSaveCandidate` / `deletionEligible:false` or `AwaitingGitSave`.
  Keep the gateway open while users resolve/push. After the candidate passes, fence and drain
  the gateway, verify the real T3 work snapshot, then request `git-save`, which independently
  rechecks after freezing. On frozen Git failure, cancel the parent's deletion intent and
  explicitly `resume` the same operation before reopening the gateway for another edit/push.
* `task-start`: unique `taskId`, **argv array**, `leaseSeconds` 1–3600. Persists launch intent
  before `systemd-run`. A repeated/ambiguous task ID is **never replayed**, including after
  agent restart. All Minicloud builds/commands must enter through this path. Unknown launches
  are not retroactively made safe by an expired lease.
* `task-renew` / `task-complete`: known `taskId`; completion requires observed process exit.
  Lease renewal does not itself count as useful activity or terminate a task at expiry.
* `activity`: `source:user-input`, or `source:task-progress` with a live registered `taskId`.
  Keepalives, passive sockets, probes and log streams do not count.
* `quiesce` (primary), `git-save` (ephemeral), or `discard` (ephemeral): fence admissions,
  enumerate live work, freeze the coding cgroup, enumerate again, stop/flush T3 and
  explicitly registered user workloads, verify read-only workspace. Unknown live coding
  processes, active tasks, ambiguous launches or unreachable tools block; no arbitrary user
  task is killed. Primary then fsyncs/unmounts (no forced/lazy unmount).
  Bootstrap starts the coding environment only, not a database or sample application.
  Requires explicit root-orchestrator attestations `gatewayAdmissionsFenced:true` and
  `unobservableToolsAbsent:true`, plus nonnegative integer `t3SnapshotSequence` from the actual
  authenticated idle T3 HTTP snapshot; do not assert these without actual admission/in-flight
  fencing and complete tool/task integration.
* `discard` additionally requires **both** `authorizedDiscard:true` and
  `acknowledgeLostIgnoredAndRootData:true`. It is not a process-quiescence bypass.
* `receipt-validate`: `receiptId`; rechecks freeze, work, generation/operation, and fresh Git
  evidence. Call immediately before deletion. Receipts bind `schemaVersion,type,vmId,kind,
  generation,operationId,revision,issuedAt,receiptId`; their SHA256 IDs detect stale/tampered
  metadata, **not a substitute for authenticated SSH/control-plane authorization**.
* `cancel-drain`: same operation, only before actual freeze.
* `resume`: same operation plus `deletionNotCommitted:true`, only while Frozen (not unmounted).
  Parent must revoke/cancel deletion intent first. Receipts are durably invalidated **before**
  writes/services/admissions reopen. This lets users resolve Awaiting Git save; use a new
  shutdown operation after editing/pushing. Failed partial resumes remain blocked.

* `status` also returns `capabilities` (`resources-v1`, plus `disk-usage-v1` and
  `directory-delete-v1` when the adapter can run folder jobs) and a cached, read-only
  `resources` sample (CPU utilization over a delta window, memory, `/` and `/workspace`
  filesystem bytes and inodes). Each filesystem entry lists `roles` (`system`,
  `workspace`, `scratch`): filesystems are deduplicated by device, so a scratch tier on the
  root disk adds `scratch` to the `/` entry instead of being counted twice, and
  `scratch` is never added to `paths`. A scratch tier on its own device is reported as
  `unavailable["filesystems.scratch"]="separate_device"` (older control planes accept only
  `/` and `/workspace` entries). The workspace entry's `scratchBindMounts` counts
  bind mounts into `/workspace` whose source is under `/var/lib/minicloud-scratch/repos`;
  they are not included in `nestedMountsExcluded`. Sampling never walks directories,
  never sleeps on the request path and does not change `revision`; a sampling failure
  reports `resources.unavailable` instead of failing status.
* `fs-job-start`: `jobId`, `mode` `scan` or `delete`, `path` (component list under
  `/workspace`) and, for delete, `expect:{dev,ino}` from the scan. The job record,
  including VM/generation identity, is persisted before launch. For deletion under `repos`,
  root orchestration first verifies the checkout, inhibits target remounts and normally
  unmounts only its owned scratch binds in the host namespace; preparation failure does
  not launch recursive deletion. `systemd-run` then launches `disk_usage.py` as the coding user with a
  time and entry budget; a repeated `jobId` returns the same job and is never relaunched.
  One job runs at a time and jobs are refused while stopping. The helper opens each path
  component with `O_NOFOLLOW` relative to the parent descriptor, stays on one filesystem,
  and deletes only strict descendants of `repos`, `home` or `artifacts` owned by the
  coding user whose device/inode and full ancestor chain still match. A mounted target,
  nested directory or file mount is refused, including same-device binds; the job's
  systemd writable binds on the three user-data roots are not checkout mounts.
  Scratch bind mounts (sources under
  `/var/lib/minicloud-scratch/repos`) are listed as excluded, non-selectable rows with
  `storage:"scratch"` and the `separate_mount` error, and are never walked or counted, even
  when scratch shares the workspace device.
* `fs-job-status` / `fs-job-cancel`: report `running`, `completed` (with the helper's
  JSON result), `lost` or `not_found`. Quiesce, Git save and discard stop running
  folder jobs before freezing.

Normal stop checkpoints a bounded, sanitized runtime rejection in the operation's
`providerEvidenceJson.lastRuntimeRejection` **before** attempting to reopen a failed
freeze. A refused recovery `status`/`resume`/`cancel-drain` is retained separately as
`lastRuntimeRecoveryRejection`; it does not overwrite the original failure.
Worker warning logs contain the same safe diagnostic, and a blocked operation's
error message includes the original reason code. Only allowlisted static reason
codes, freeze stages, failure categories, tools/subcommands and bounded numeric
exit/errno values cross this diagnostic boundary. Unknown text is withheld, not
truncated: no command output, exception messages, paths, credentials or repository
contents are persisted or logged. Older runtimes still yield known reason codes;
the updated runtime additionally identifies the failing freeze stage and adapter
failure category. Missing diagnostics never authorize deletion or bypass SSH,
generation, task, filesystem, Git-save or provider-ownership checks.

T3 is the known infrastructure main process, **not a quiescence API**. By default child
terminals, providers/builds and every other coding process block unless finished. Parent must fence
gateway/session mutation admission and account for in-flight work before requesting freeze;
unobservable/in-process autonomous tools require explicit task tracking or remain a live
integration blocker. Do not enable automatic destruction for an uninstrumented tool. Freeze
remains in force until deletion or explicit authorized resume. For the known idle T3 main,
the supervisor queues graceful stop while frozen, briefly thaws to permit its flush, then
refreezes and inventories again. The unit targets **only its main process**, with no fallback
SIGKILL; unexpected children remain alive and block the receipt rather than being killed.

### Operator-qualified preview services

Only explicitly registered `minicloud-preview-[a-z0-9-]{1,24}.service` units may join
`infrastructureUnits`; every infrastructure unit must also appear in `stopUnits`.
The default image contains no preview application or preview service unit. Users must
explicitly create and register a workload; registration does not turn a bundled test
application into product infrastructure.

Preview admission exempts **only the unit's MainPID**, with all four process UIDs equal to
2000 and exact cgroup `0::/minicloudwork.slice/<unit>`. The loaded system unit must report
`Slice=minicloudwork.slice`, `KillMode=process`, and `SendSIGKILL=no`. Its fragment and every
drop-in, including their directory ancestry, must be root/operator-owned and not group/other
writable. Transient/unknown fragments and unsafe settings block, including before any preview
stop. Children (even other UIDs), additional preview-cgroup processes and leftovers after
MainPID exits block; preview ancestry never qualifies an idle terminal shell.

Preview units use the same queued main-only graceful stop, controlled thaw, mandatory
refreeze and post-stop inventory as T3. Failed stops/refreezes cannot yield a quiescence
receipt. Fresh bootstrap and authorized resume start only T3. Registration alone
does not start an application; users choose when to start their workloads.

Registration and a healthy HTTP probe are **not** proof that a general application has no
in-process work. The operator must explicitly qualify each application: the initial test
preview is static HTTP with no autonomous/future jobs, proxy admissions fenced, and the
maximum 60-second requests drained for 75 seconds before freeze. Uninstrumented schedulers,
background threads doing useful work, queues or other invisible activity remain unsupported;
do not assert `unobservableToolsAbsent` for them. Native/live service qualification remains
required; the default tests exercise fixtures only.

### Pinned T3 committed-state proof

After the kernel freeze, a read-only `mode=ro`, `query_only` SQLite transaction checks
`/workspace/t3/userdata/statev2.sqlite` (never `immutable=1`; committed WAL must be observed).
Pinned v0.1.9 `ProjectionStore.ts` defines the HTTP cursor as
`MAX(sequence)` in `orchestration_events` for `application_event_version=2` and
`aggregate_kind='thread'`; it must match `t3SnapshotSequence`. `EventSink.ts` updates only
the relevant metadata row `thread-projections`, schema version 2, against the **global V2**
event cursor, including project events. Unrelated metadata rows are not compared. Empty
thread sets with project events are valid. Verified run/attempt/node/session/provider-thread/
turn/subagent/runtime-request/turn-item/outbox status columns must contain only known inactive
states; pending, active, missing schema, unknown statuses or lagging projections block.
The same committed-idle checks run after service stop/flush (allowing its final cursor to
advance); receipts bind the original HTTP cursor plus final `t3State` cursor/count evidence.
This proves committed state, not invisible RAM work: parent admission fencing, its complete
real HTTP snapshot and the independent process guard remain mandatory.

An inspection failure after freeze leaves `Frozen`, with no deletion receipt. Parent must
cancel/revoke its deletion intent, `resume` the same operation, then reopen the gateway so
users can resolve work. Retry of an already completed read-only freeze revalidates the
recorded final state and does not replay user workload hooks or shutdown commands.

### Qualified unused terminals

Only after the gateway's source-verified terminal-input codec, authenticated activity delivery,
and per-generation tunnel have been qualified may the parent set root-owned runtime config
`observedTerminalInputs:true` (default false). Parent must first revoke routes/sessions, wait
75 seconds for its maximum HTTP/WS authorization windows, and prove a complete idle real T3
orchestration-shell snapshot; unknown/active/pending runs block. Passive observe/ping is not
input activity. A live lease/process still blocks, irrespective of expiry or shell classification.

The opt-in OS adapter permits only single-threaded UID-2000 root-owned image bash/dash processes
descended from the known `minicloud-t3.service` MainPID in that exact service cgroup. The shell
must be an interactive/automatic-TTY invocation, a foreground session/process-group leader,
have matching stdin/stderr PTY slave character devices and controlling-terminal device number,
have no child, zombie, stopped or background job, and be sleeping in kernel `read(0, buffer,
positive-count)` with a recognized TTY-read wait channel. Kernel `/proc/PID/stat` supplies the
foreground PGID/session; foreign-terminal ioctl results are not guessed. PID/start-time, process
tree, executable ownership, descriptors and syscall evidence are rechecked, including the
second **frozen** inventory. All other coding processes remain unknown work and block.

These are process-fixture tests, **not live PTY proof**. Before enabling automatic idle deletion,
qualify actual unused PTYs, foreground/background/stopped jobs and active agents on the exact
amd64 image/kernel, through gateway fencing and cgroup freeze/stop. A `pselect6`/`poll` shell,
unreadable syscall/children file, unknown wait channel or freezer-obscured read is deliberately
unsupported and blocks; low CPU, `S` state alone and a remembered pre-freeze read are insufficient.
Such a kernel/shell needs a separately reviewed pidfd-bound native syscall-stop adapter
(ptrace plus stopped-register/FD-set inspection for polling waits, or equivalent kernel
instrumentation retaining continuous read-wait evidence across freezer entry). No such native
adapter is installed or silently substituted here.

Git save inventories **every** discovered checkout and linked worktree, canonical authorized
origin, exact base SHA/branch/ref, clean tracked/untracked state, full/non-shallow history,
and unsupported LFS/submodules. Fresh exact-ref/default-branch evidence and fetched remote
objects in a separate disposable verifier prove final HEAD reachability; the frozen checkout
is never modified by fetch. It runs twice and is repeated on receipt validation. It never
commits, pushes, force-pushes, writes default branches, opens PRs or uploads workspace data.
Ignored files/root databases are **not saved by Git**. Failures mean Awaiting Git save and
continued billing, never deletion or a hidden filesystem backup.

## Primary encrypted backup / separate-volume restore

A retained volume is not a backup and this runtime makes **no provider-volume encryption
claim**. Configure primary `backup` with `transport:"management-ssh-stream"`, public age
`recipient`, independent external `keyRecoveryId`, independent `destinationIdentity`,
positive `retentionDays`, and optional `maxAgeSeconds` (default 86400).
**No recovery private key is accepted or sent to VMs/T3.**

1. `quiesce` with `backupRequired:true` (or configured `requireFreshBackup:true`) freezes,
   stops and encrypts, then blocks unmount pending off-host acknowledgement. `backup` returns
   `PrimaryBackupPrepared` with object ID, ciphertext SHA256, representative hashes,
   retention deadline and `AwaitingOffHostAcknowledgement`. No plaintext archive is staged.
2. `runtime.py export-backup --receipt-id <prepared-id>` streams **ciphertext only** to
   stdout over pinned SSH. Parent/operator stores and fsyncs it on an independently owned
   machine/store, verifies SHA256, and confirms the separately recoverable private key.
3. Root authenticated `backup-ack` with the same operation, `preparedReceiptId`,
   `ciphertextSha256`, `destinationIdentity`, `durablyStoredOffHost:true`,
   `independentRecoveryKeyConfirmed:true` returns `PrimaryBackup` / `VerifiedOffHost`,
   and removes the local encrypted staging file. Resume `quiesce` with that operation.

An optional direct pinned-SSH ciphertext uploader is also implemented: omit streaming
transport and provide `host,user,directory,knownHostsFile,sshIdentityFile,destinationIdentity`
in the backup policy. It requires a genuinely off-host target and exclusive remote create,
fsync/checksum. This needs separately authorized outbound SSH and is **not enabled by the
default provider egress rules**. Streaming avoids any backup-store login on coding VMs.

Status distinguishes not configured, never backed up, awaiting acknowledgement, failed,
fresh and stale. Retention deadline is an operator retention contract, **not proof a remote
object still exists**. Remote retention enforcement/freshness polling belong to the backup
operator; these hooks never silently prune independent copies. Default stop need not make
a full backup; opt-in fresh-backup policy blocks stop if it cannot complete.

Restore decryption occurs **on the operator machine**:

1. Authenticate `restore` to a new VM generation with a **separate**, empty, correctly owned
   ext4 volume and `authorizedRestore:true`, `independentRecoveryKeyConfirmed:true`,
   `backupReceipt:<parent-persisted verified PrimaryBackup>`. Original-volume restore,
   nonempty targets, corrupt receipts and ephemeral kind are rejected.
2. Verify ciphertext SHA256 off VM, then stream `age --decrypt --identity <operator-key> …`
   into pinned SSH running `runtime.py restore-import --operation-id <restore-operation>`.
   Only the decrypted application archive crosses this channel; the key never does.
3. Safe tar extraction rejects traversal/device/unsafe links, preserves the target ownership
   marker, restores the coding workspace ownership, verifies representative hashes, fsyncs and
   returns `PrimaryRestored`. Partial/failed restores cannot be blindly replayed over data.
   Persist the receipt, then bootstrap normally. Live separate-volume restore qualification
   is still required before claiming a backup is operationally recoverable.

Symlinks are saved as metadata, never followed: data outside the workspace is not backed up.
Restore defers all symlinks until regular-file extraction, hash verification, ownership and
layout are complete. Archive writes beneath a link, conflicting links and hardlinks to
volume metadata or unresolved links are rejected. This preserves ordinary tool/virtualenv
links without following them during privileged extraction or excluding user tool directories.
The source volume's ownership marker and empty root `lost+found` are not restored over the
target's metadata; nonempty recovery data blocks backup instead of being silently omitted.
Archives stay on the volume's filesystem: a nested mount (such as a scratch build-output
mount) is recorded as an empty directory and never entered.

## T3 session issuance / outstanding qualification

`t3 serve --host 127.0.0.1 --port 3773 --base-dir /workspace/t3 --no-browser`; no base-path
support. Parent uses separate per-VM/generation HTTP+WS origins/tunnels. The installed
`issue-t3-session.py --vm-id … --generation … --operation-id … --subject … --ttl 300
--scope <authorized-scope> [--scope …]` emits **only the bearer to sensitive stdout**.
Pipe directly into the control-plane secret store. It checks actual CLI help before using
`t3 auth session issue --token-only --ttl 300s --label … --subject … --scope … --base-dir …`.
Upstream uses **singular repeatable `--scope`**, not `--scopes`; no DPoP binding is requested.

Not executed here: paid image capture, real amd64 image boot, actual T3 headless issuance,
real cgroup freeze/stop behavior, privileged ext4 mount/unmount, real age encryption and
off-host durability, independent-key separate-volume restore, provider network isolation.
The development host is ARM64; the hash-verified amd64 T3 binary cannot execute here.
All these are explicit live qualification gates, not silently successful fake integrations.
