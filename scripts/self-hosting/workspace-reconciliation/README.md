# Offline legacy workspace retirement (#341)

Use only on the workspace-bearing `muniventures/minicloud` schema at
`3ca6728556232e323a85f95d52ad31851b371d9e` (same migrations as
`server-v0.0.1-rc.4`), before the app-only upgrade. This is not a normal
stop/delete path or a way to override a live-work guard. It is for an authorized,
irreversible retirement where the exact primary server and retained disk have
**already** been removed. The command never writes to Hetzner, talks to the
guest, fabricates quiescence, or changes EF history. Review it and the private
scope before running it.

## Execution order

1. Confirm the deployment revision/schema, irreversible-delete authorization,
   exact organization, workspace, Hetzner account and project, primary VM
   generation, allocation and current stop operation, and disk size/ID. Use
   change-controlled records and a protected operator-only scope file, not an
   issue, PR or shared log. Record the ID and lowercase fingerprint of a
   **live project SSH key** independently verified in the correct Hetzner
   Console project. A credential that cannot read that exact key cannot prove
   absence, even if it gets 404s for all missing resource IDs.
2. Fence ingress/admissions, stop **all** legacy API, worker and workspace
   gateway processes (including replicas, refresh loops and tunnels), and stop
   other automation that can write to the database or create resources in the
   project. Wait for in-flight work to finish; take and verify an offline
   PostgreSQL backup. Keep PostgreSQL and OpenBao reachable privately. Do not
   merely stop one worker or rely on expiration to stop a live process.
3. Check in that same Hetzner project for leftover primary IPv4/firewall,
   including unlabelled resources from the exact original VPS. If anything is
   present, stop. Separately reconcile only a positively identified,
   operator-authorized leftover: confirm exact ID, `managed-by`, organization,
   workspace, VM, generation and **allocation** operation ownership, and that
   the IP is unassigned or the firewall has no attachments (including label
   selectors). Do not relabel or delete an ambiguous/shared object. Keep the
   original IDs in the scope even after deletion. If an ancillary ID is null,
   the command can check labelled inventory but cannot identify an unlabelled,
   unrecorded resource; independent project-side inspection is mandatory.
4. Build the command from the reviewed checkout. Set `RECONCILIATION_DB_FILE`
   to an owner-only file containing the exact legacy PostgreSQL connection
   string, and set `OpenBao__Address`, `OpenBao__MountPath`,
   `OpenBao__EnvironmentPrefix`, `OpenBao__TokenFile` and, for the packaged
   private CA, `OpenBao__CaCertificateFile` to the corresponding trusted
   deployment settings. Use a short-lived OpenBao token that can read the
   recorded provider credential version and delete metadata only for the
   target workspace VM generation paths. Do not pass an ad hoc Hetzner token.
   The DB role needs appropriate read/write and table-lock privileges.

   From the worktree root:

   ```sh
   dotnet build -c Release scripts/self-hosting/workspace-reconciliation --artifacts-path .local/artifacts/reconciliation
   dotnet .local/artifacts/reconciliation/bin/WorkspaceReconciliation/release/WorkspaceReconciliation.dll --check /private/scope.json
   dotnet .local/artifacts/reconciliation/bin/WorkspaceReconciliation/release/WorkspaceReconciliation.dll --apply-offline /private/scope.json
   dotnet .local/artifacts/reconciliation/bin/WorkspaceReconciliation/release/WorkspaceReconciliation.dll --check /private/scope.json
   ```

   Apply **only** after check passes and the same scope and offline fence have
   been reviewed. Apply repeats every check under the legacy admission lock
   and write locks. The supported drain stages are `wait-for-tasks`,
   `gateway-drain` and `quiesce`. Active task leases (even expired ones),
   queued/running filesystem work, current worker leases, queued restarts,
   live sibling VMs and other nonterminal operations cause refusal. Do not
   clear these records by hand to make the check pass.
5. Expect `Reconciled` on commit and `Already reconciled` on the final check.
   The transaction uses the existing terminal deletion identity guard,
   supersedes the scoped stop, records a `delete-workspace` tombstone with
   the exact scope and proof time, and removes only the target's routes,
   sessions, terminal task leases and resource samples. Scoped OpenBao
   revocation is idempotent and occurs **after** the DB writes and constraints
   are checked but **before** commit; it is not rolled back with PostgreSQL.
   On failure or a lost response, keep writers stopped and retry `--check`
   with the exact same scope; if still eligible, retry apply with the same
   recovery ID. Never restore only the database and assume deleted secrets
   or provider resources will return. After confirmed retirement, keep the
   legacy writers fenced through the #339 upgrade. The app-only migrator
   still refuses if **any other** workspace remains unretired.

Synthetic scope example (every field is required; nullable IDs must match DB):

```json
{
  "organizationId": "org_test",
  "workspaceId": "workspace_test",
  "providerAccountId": "account_test",
  "credentialGeneration": "cred_test",
  "region": "hel1",
  "vmId": "vm_test",
  "generation": 2,
  "allocationOperationId": "allocation_test",
  "stopOperationId": "stop_test",
  "recoveryOperationId": "recovery_test",
  "serverId": "170",
  "volumeId": "240",
  "volumeSizeGb": 40,
  "primaryIpId": "310",
  "firewallId": null,
  "projectWitnessSshKeyId": "510",
  "projectWitnessFingerprint": "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff",
  "authorizationReference": "synthetic-approval"
}
```

## Disposable PostgreSQL tests

Start a dedicated local cluster with Unix sockets only, database superuser
`recovery_test`, and no production configuration. Set `RECOVERY_TEST_PG_SOCKET`
to its absolute socket directory and `RECOVERY_TEST_PG_PORT` to its port.
Tests create and drop uniquely named synthetic databases, run the real legacy
migrations, and fake OpenBao and the Hetzner HTTP API. Optionally point
`RECOVERY_TEST_EXTRACTED_API` at the built `Minicloud.Api.dll` from #339 to test
that the actual extracted migrator rejects the fixture before recovery and
accepts it afterwards without changing its migration history.

```sh
dotnet test scripts/self-hosting/workspace-reconciliation-tests --artifacts-path .local/artifacts/reconciliation
```
