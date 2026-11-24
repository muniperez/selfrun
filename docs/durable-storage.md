# Customer durable storage (Hetzner and Linode)

## Implementation plan

1. Add named writable storage to service configuration and deployment contracts; validate names, sizes, mount paths, ownership, and collisions before admission.
2. Persist desired mounts with deployments and retain app-owned provider volume identities independently of deployment/container lifetimes.
3. Reconcile Hetzner and Linode volumes by stable app/name identity: create disks, recover ambiguous creation, attach only to the app server, grow only, and wait for provider readiness. Linode volumes are formatted as ext4 on first mount only while the provider-owned `mc-format-pending` tag proves the new disk is eligible; existing unformatted volumes are never formatted.
4. Mount verified block devices before starting containers. Refuse missing disks, wrong mounts, unexpected filesystems and local-directory fallbacks. Preserve existing data and never format on the VPS.
5. Cover config/checksums, scoped deployments, provider requests/recovery, migrations, and runtime data preservation with local tests.

## Customer configuration

Add storage to a service in `minicloud.yml`, then run `minicloud deploy`:

```yaml
services:
  backend:
    image: example/backend:1
    port: 8080
    public: true
    path: /
    healthPath: /health
    storage:
      uploads:
        sizeGb: 20
        mountPath: /app/uploads
        uid: 1000
        gid: 1000
```

A name identifies a volume within the app. Services may share a name with the same size and ownership. Branch apps have separate app IDs and separate disks; data is not cloned. Existing files inside the image at the mount path are hidden by the mount, not copied. UID/GID default to 0 and apply only when creating the volume's data directory; changing ownership later is rejected. Configure the application's container user accordingly.

The dashboard Storage tab lists registered and retained volumes and offers a configuration example. Expanded service details and `minicloud apps inspect <app>` show configured mounts. `GET /api/environments/{appId}/storage` returns the app-scoped stored volume inventory, not a live mount-health check.

Hetzner and Linode are supported. Each app supports up to 16 named volumes and 10,000 GB total, with 10–10,000 GB per volume, subject to provider quota. Increase `sizeGb` to grow; shrinking is rejected. Growth changes the provider disk and extends ext4 on the VPS. Linode's blank new disks are formatted once during first mount only when their pending-initialization tag is present; no existing volume is automatically formatted. A failed deployment or rollback does not undo growth.

Removing a mount stops mounting that volume for the updated service. Scoped deploys retain mounts on unselected services. Renaming creates a new empty volume; reuse the original name to reconnect retained data. Neither mount removal nor app deletion deletes provider volumes. Retained disks continue incurring provider charges and require explicit operator cleanup in the owning provider account. Hetzner labels identify the app and storage name. Linode uses a bounded label and a Minicloud ownership tag; its API-provided device path is validated before mount. No automatic data migration, backup, restore, cross-region transfer, or forced detach is included. Server snapshots do not back up attached volumes; arrange application-consistent backups separately.

Upgrade API and worker together and apply the included database migration before using the updated CLI. This is source/local-test support until separately qualified with disposable Hetzner and Linode resources. No paid provider resources are created by the local tests.

Hetzner behavior was checked against [Hetzner’s volume client](https://github.com/hetznercloud/hcloud-go/blob/main/hcloud/volume.go) and [volume documentation](https://docs.hetzner.com/cloud/volumes/overview/). Linode API shapes were checked against Akamai's [volume API reference](https://techdocs.akamai.com/linode-api/reference/volumes) and [Block Storage guide](https://techdocs.akamai.com/cloud-computing/docs/manage-block-storage-volumes-with-the-api); live qualification remains separate.

## Local validation

The regular .NET and dashboard suites cover configuration, authorization, provider HTTP contracts, scoped service retention, identity recovery and inventory UI. Run the runtime checks directly:

```sh
python3 scripts/github-actions/minicloud-deploy/test-storage-volume.py
docker build -f scripts/github-actions/minicloud-deploy/test-storage.Dockerfile -t minicloud-storage-test:local scripts/github-actions/minicloud-deploy
docker run --rm --privileged --network none minicloud-storage-test:local
```

The privileged container uses only its own temporary loopback file and a test fstab. It verifies real ext4 mount, UID/GID, remount persistence, online filesystem growth and refusal to create a local fallback when the block device disappears. It does not contact Hetzner.

## Validation evidence (2026-11-07)

All five implementation steps above are complete locally. The final regression run passed 733 .NET tests, with three optional external SSH/SFTP tests skipped. The broad Docker deployment integration scenarios were outside this focused run. One existing registry test failed on this machine because available disk space is below its 15% reserve; that test was excluded from the final regression rerun. All 131 dashboard tests and the dashboard production build passed.

The isolated loopback test passed real ext4 mount/remount persistence, UID/GID and online growth. The generated PostgreSQL migration was applied to a disposable database containing an existing service row; that row was preserved and the volume registry accepted a new record. No hosted deployment or paid Hetzner volume was created. Live provider and VPS reboot qualification remain separate.
