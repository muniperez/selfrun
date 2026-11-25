# Upgrade and full-recovery prerequisites

Coordinated maintenance uses the durable maintenance authority
(`dotnet Selfrun.Api.dll maintenance ...`, migration
`20261111134441_MaintenanceAuthority`). Every API write and scheduler pass is
admitted through its fence. It reports drain status for queued, running,
retrying and uncertain work, and it revokes processes registered at a
superseded epoch. The tooling waits for accepted work to finish. It never kills
work.

Before upgrading, the operator must have:

- The **installed** release's verified assets directory, passed as
  `--previous-assets-dir`. Its bundle digest must equal the installed
  `bundleSha256`.
- The independently verified **target** release assets. The target must be
  newer, with application, identity and configuration schema versions no lower
  than the installed release.
- A protected age recipients file and identity file. The upgrade takes a full
  pre-upgrade backup and authenticates it before stopping any service.
- At least 2 GiB free at the backup destination, plus space for the new images.
- A maintenance window. Writes stay fenced from drain until the new release
  reports healthy.

The upgrade never downgrades images. If it fails after migrations start,
writes stay fenced. Either re-run the same upgrade to resume, or restore the
pre-upgrade backup with the previous release's installer.

The portal session migration (`20261120154929_PortalSessionRenewal`) defaults
existing users to `keep_signed_in = false` and browser sessions to
`persistent = false`, and backfills `renewed_at` from `last_seen_at`. It does
not rotate existing tokens or change their stored idle/maximum deadlines,
API keys or CLI sessions. Rehearse the normal `api-migrate` path on a
sanctioned copy of the existing instance database before activation; a
disposable synthetic database rehearsal is not a substitute.

## Choose the installation path

**Fresh app-only install:** Use the normal first-install procedure with a
new PostgreSQL database. There is no workspace retirement step, and the
app-only release does not create workspace tables. Do not initialize a new
database as a substitute for upgrading an installation whose data you need.

**In-place upgrade from a workspace-bearing release:** Keep the existing
PostgreSQL database and follow the retirement and upgrade steps below.

### Retire workspaces before upgrading in place

The in-place PostgreSQL path is tested from `server-v0.0.1-rc.4`
(`7502b06e5f8d378d61245075edbb7d9bc6944e52`). Before the maintenance window,
export any workspace data you need using the old release. Stop all workspace
VMs, wait for their cleanup, then permanently delete the workspaces using
that release and verify that provider resources are gone. A tombstone is
the record left after permanent deletion completes; it is not an active
workspace and does not have to be removed from PostgreSQL. Stopped, failed,
or deleting workspaces are not retired. Before running target migrations,
stop or fence all workspace-era writers and rehearse the upgrade on a
sanctioned copy of the retired installation.

Use the normal installer upgrade with verified assets for a **newer app-only
release** containing the compatibility change. Its `api-migrate` service must
use the target API image and that release's declared `applicationMigration`,
not the old workspace-bearing image. The migrator recognizes the complete
seven-entry workspace history only after retirement; it still rejects partial
workspace history and all other unknown migrations. Do not edit EF history to
bypass a refusal. If the history is incomplete, finish the migration with a
compatible workspace-bearing release before retrying.

The upgrade leaves application data, existing migration entries, and legacy
workspace records and constraints in the same PostgreSQL database. Legacy
tombstones can still restrict deletion of referenced organizations, provider
accounts, or plans. Nothing is imported into Municode. The disposable rehearsal
and its target selection are in `scripts/self-hosting/test-workspace-upgrade.py` and
`scripts/self-hosting/workspace-upgrade.json` in the source checkout. Run it
with .NET 10, Python 3.12+, and PostgreSQL 16 binaries (`PG_BIN` if needed).
The rehearsal compares PostgreSQL dumps before and after the migration and
its replay. API startup runs on a disposable clone so normal maintenance
registration cannot change the database being compared. Any mismatch leaves
a unified diff of synthetic data in `.local/artifacts/workspace-upgrade-diffs/`.

## Recovery

Restore needs the exact release that created the backup, the original root
path and the original configuration file. After a restore or transfer, an old
host is revoked only when it next reaches this database. Shut down any old
host that runs its own database copy or holds live provider, DNS or SSH
credentials, or rotate those credentials.

Independent recovery-dependency archives (`*-recovery`) are not full backups.
Never use them as a pre-upgrade backup.

Email/code recovery is a protected first-install opt-in in releases containing
`compose.email-recovery.yml` and `identity.recovery.schema.json`. Existing
password-only configurations and identities are not activated or backfilled
by upgrade. An already opted-in installation retains its exact original
configuration and protected SMTP input/credential values; the target must
contain the same recovery contract. Toggling recovery and rotating SMTP
credentials on existing installations are not supported by this change.
