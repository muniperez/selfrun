# Portable development VM runtime (protocol 1)

Opt-in issue #110 implementation, **not a published/qualified provider image**. Python 3.12,
Ubuntu 24.04, linux/amd64, systemd/cgroup v2, existing ext4. Default tests need only Python
and Git; they never mount disks, contact providers, install tools, or start T3.

```sh
templates/development-workspaces/test.sh
```

Outputs: ignored `.local/artifacts/issue110/runtime/`. Fixtures use two real independent
clones and a local bare remote; injected process/mount/encryption/transport adapters test
failure handling. This is not a real ext4, systemd-freezer, age, or live restore qualification.

Optional [T3 Connect enrollment](../../docs/t3-connect.md) uses the existing
primary VM and managed T3 unit, a private memory-only approval page on loopback
774, and a distinct root-only management credential. Normal gateway scopes do
not expand. Pending approval fences shutdown. Its updated native probe and
hosted cross-device/lifecycle acceptance remain unqualified; never run the
upstream service installer or show device approval in T3 terminal history.

## Prepared image

`image/manifest.lock.json` pins the Ubuntu cloud-image **serial and SHA256**, immutable
Ubuntu apt snapshot, Node 24.11.1, .NET SDK 10.0.100, Codex 0.169.0 (npm integrity lock),
GitHub CLI **2.83.1** and GitLab CLI **1.74.0** (official linux/amd64 release
archives, SHA256-verified before copying only their executables to `/usr/local/bin`),
T3 0.1.9 / commit `24e77473ecf41bc1f7142529c7911c1c1aae1d76`
and Chromium shared libraries. The default supplies the coding environment, not an
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

### Rolling out to existing workspaces

Build, qualify, generalize, power off, capture/register a **new immutable image revision**;
retain the previous image for rollback. Update the approved image selection for new
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

* `/opt/minicloud-workspace/{runtime.py,bootstrap.py,issue-t3-session.py,git-credential.py,copilot_provider.py}`
* `/etc/systemd/system/minicloud{work.slice,-workspace.service,-t3.service}`
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

It mounts/initializes the declared layout, starts services, verifies the loopback T3
descriptor (`serverVersion:0.1.9`, `orchestrationProtocolVersion:2`), and POSTs HTTPS JSON
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

`runtime.py serve` owns `/run/minicloud-workspace/control.sock` (0600, root peer UID checked).
The agent is outside the freezable coding slice. State/atomic fsynced receipts live in
`/var/lib/minicloud-workspace`; parent must durably persist returned lifecycle receipts
**before deleting compute**. File presence on the disposable VM alone is not off-VM durability.

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
