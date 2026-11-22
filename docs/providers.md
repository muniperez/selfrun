# Providers and plans

Each organization owns its provider accounts and named plans. You can connect
multiple Hetzner, DigitalOcean, Vultr, and Microsoft Azure accounts at the same
time, including several accounts of one provider.

CLI plan listing and app creation require an API key with `plans:read`.
Existing keys do not gain new permissions automatically. Refresh an older CLI
login with `minicloud login` or use a key with the required scope. Managing
provider credentials requires the separate `providers:write` operator scope.

## Configure an account

Open **Plans** in the dashboard and select **Provider accounts** (or **Add plan**
and **Connect account**). Add an account name, select a supported
provider, and enter its API credential. DigitalOcean accounts can also specify a
project; Vultr accounts can specify an OS ID. Azure accounts require a tenant ID,
subscription ID, an existing resource-group name, and a Microsoft Entra service
principal client ID and client secret. Credentials are write-only and stored in
OpenBao; Azure tenant/subscription/resource-group values are non-secret account
scope metadata. An organization Owner or Admin manages accounts and plans.

The connection check uses read-only provider requests. It does not allocate a
server or guarantee available quota. Accounts with identical provider types
remain independent entries with separate credentials and plans.

Add a plan in **Plans** and select an enabled provider account. Configure the
provider's exact location (region code) and server type (SKU), and under
**Advanced settings**, the image and `linux/amd64` or `linux/arm64`. For Vultr, the image field is an optional
numeric OS ID override; omission uses the account default. The initial plan name
is a UUID; replace it with a name you want to use. Names may contain spaces and
Unicode and must be unique across the organization's accounts, ignoring case
and canonical Unicode differences.

Choose an image that fits the plan's memory requirements. Managed Debian images
install Docker Engine, Compose and Buildx from Docker's signed Debian repository;
Ubuntu images retain their distribution packages.
Fresh-server deployments wait for cloud-init and activate Docker group membership
before using the Docker socket. A fatal bootstrap failure stops deployment.

### Microsoft Azure

Azure accounts are explicitly scoped to one Entra tenant, subscription, and
existing resource group. The adapter authenticates only with a service
principal's client ID and client secret using the OAuth 2.0 client-credentials
flow and the Azure Resource Manager `.default` scope. It does not use a local
developer login, Azure CLI credential, managed identity, certificate, or
workload-identity federation. The provider connection check reads the selected
subscription, resource group, and VM inventory; it does not allocate resources
or verify that quota is available.

The service principal needs a custom Azure RBAC role scoped to the target
resource group, plus subscription read access to verify its tenant/scope. Its
resource-group actions must cover:

- `Microsoft.Compute/virtualMachines/{read,write,delete,instanceView/read}`
- `Microsoft.Compute/disks/{read,write,delete}`
- `Microsoft.Network/networkInterfaces/{read,write,delete}`
- `Microsoft.Network/publicIPAddresses/{read,write,delete}`
- `Microsoft.Network/networkSecurityGroups/{read,write,delete}`
- `Microsoft.Network/virtualNetworks/{read,write,delete}` and
  `Microsoft.Network/virtualNetworks/subnets/{read,write,delete}`
- `Microsoft.Resources/subscriptions/resourceGroups/read`

Grant `Microsoft.Resources/subscriptions/read` at subscription scope. Avoid
assigning Owner or subscription-wide Contributor. Azure's custom-role model and
operation names can change; verify the role's actions against the target
subscription's provider operations before assigning it. The adapter never
creates a resource group and does not change role assignments.

Azure plans use the exact Azure region name (for example `eastus`), VM SKU
(for example `Standard_B2s`), and a Marketplace image URN in
`publisher:offer:sku:version` form. The default image is
`Canonical:0001-com-ubuntu-server-jammy:22_04-lts:latest`. This integration does
not yet discover or quote regional SKU/image availability. Azure `linux/arm64`
plans are gated pending native image/SKU validation.

Each VM receives a dedicated VNet/subnet, NSG, NIC, and Standard static IPv4
address. The NSG permits inbound TCP 22, 80, and 443 from any IPv4 source;
restrict SSH access further with network controls outside MiniCloud when
required. Other inbound ports and IPv6 are not configured. VM deletion also
removes the VM's managed OS disk, NIC, public IP, VNet, and NSG. Azure durable
data volumes, private networks, snapshots, and provider SSH-key inventory are
not supported; app data stored only on the VM is not retained after deletion.
Standard public IPv4 addresses, VM compute, managed OS disks, outbound transfer,
and any other Azure services are billed by Azure. Regional/family vCPU quotas,
SKU availability, and capacity vary by subscription and region; quota or
capacity errors do not trigger account failover.

Add Azure credentials without placing the client secret in command arguments:

```sh
AZURE_CLIENT_ID=<application-client-id> \
AZURE_CLIENT_SECRET=<service-principal-secret> \
minicloud providers add --organization org_example --name "Azure production" \
  --provider azure --tenant-id <tenant-guid> --subscription-id <subscription-guid> \
  --resource-group <existing-resource-group>
```

This integration has synthetic HTTP contract coverage only. It has not been
qualified against a disposable live Azure subscription. Before treating Azure
as a generally supported provider, qualify native amd64 and arm64 image/SKU
combinations where available, cloud-init/bootstrap, redeploy and discovery,
quota/API failure recovery, credential rotation/scope preservation, and full
deletion of VM, managed OS disk, NIC, public IP, VNet, and NSG.

Azure API references: [service principal authentication for .NET](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/),
[VM create/update](https://learn.microsoft.com/en-us/rest/api/compute/virtual-machines/create-or-update?view=rest-compute-2024-03-01),
[VM instance view](https://learn.microsoft.com/en-us/rest/api/compute/virtual-machines/instance-view?view=rest-compute-2024-03-01),
[network interface create/update](https://learn.microsoft.com/en-us/rest/api/virtualnetwork/network-interfaces/create-or-update?view=rest-virtualnetwork-2026-03-01),
[Azure custom roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles), and
[VM quotas](https://learn.microsoft.com/en-us/azure/virtual-machines/quotas).

```yaml
app: example-api
appId: app_example
plan: "Hetzner secondary small"
services:
  api:
    image: example/api:1
    port: 8080
    public: true
```

```sh
minicloud plans list
minicloud deploy --plan "Hetzner secondary small"
minicloud deploy --environment staging --plan "Hetzner secondary small"
```

The command-line plan overrides YAML. Omitting both uses the selected
environment's saved plan. Provider names, provider SKUs, and cloud credentials
are not required in deployment YAML. A plan always selects one account; quota
failure never automatically dispatches to another account.

## Editing and existing resources

Renaming a plan preserves existing app/environment bindings. Update YAML and
commands that contain its previous name. Retired names remain reserved, so a
stale configuration cannot select a different plan later. The original plan can
rename back to one of its former names.

After a plan's first provisioning reservation, its placement mapping is locked.
Duplicate it to change its account, region, SKU, image or architecture. A
provisioned environment cannot change plans through ordinary deploy or app
update. Moving existing servers or data requires a separate migration.

Disabling an account blocks additional allocations while preserving management
of existing servers and volumes. Disabling a plan prevents new selections but
allows deployment to its existing server. Account deletion is blocked by plans,
servers, reservations, runtimes and retained volumes. Removing an app does not
delete its durable volumes or their provider-account ownership.

Credential replacement verifies access to the original registered resources.
If that scope cannot be verified, the previous credential reference remains in
place. Provider account entries must not be repurposed for a different cloud
account/project. Create another entry for another scope.

Conservative rotation boundary: an account with no verifiable server or volume
cannot replace credentials after any provisioning reservation, including a
canceled build or a definite quota rejection. This protects against a canceled
worker still completing a discovery request with captured credentials. Use a new
provider entry and plan for an unprovisioned environment, or reconcile a known
original resource before rotating the existing entry.

If DigitalOcean creates a droplet but then rejects its project assignment, the
allocation remains unresolved. Minicloud keeps its original account reservation
and checks that account for the existing droplet before any retry; project errors
do not permit another allocation or a plan switch.

Vultr discovery returns only a matching server label; unrelated instances in a
provider response do not count as this application's server.

## Upgrade from global provider credentials

Stop new admissions and drain active deployments, preparations and deletion
work. Back up PostgreSQL and OpenBao together. Upgrade API and workers together
and apply the provider-account database migration before publishing the new CLI.
The additive migration preserves old app/server/volume rows; it does not guess
which organization owns a global cloud account.

The default API and worker configuration requires explicit account bindings.
`Providers__AllowLegacyConfiguration=true` is a temporary, explicit upgrade
bridge for an installation still using its old global credentials. Leave it
disabled after import; custom named plans never fall back to global credentials.

Application and environment deletion checks registered server and runtime account
bindings before canceling deployments or cleaning up stored data and access.
`legacy_provider_import_required` identifies an environment that needs the
import/reconciliation workflow below; retrying deletion cannot establish ownership.
Worker ownership checks remain independent, and this blocker is also shown in
deletion status if an already queued cleanup encounters it.

Add an account in the portal, or import an existing locally configured credential
without passing its value in a command argument:

```sh
minicloud providers add --organization org_example --name "Original Hetzner" --provider hetzner
```

The command reads `HCLOUD_TOKEN`/`HETZNER_API_TOKEN`,
`DIGITALOCEAN_ACCESS_TOKEN`/`DIGITALOCEAN_TOKEN`, or
`VULTR_API_KEY`/`VULTR_KEY` from the operator process environment. For Azure it
reads `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET`; pass the non-secret
`--tenant-id`, `--subscription-id`, and `--resource-group` scope values. Optional
`--project-id` and `--os-id` are non-secret account settings. Do not put provider
credentials in the manifest, source control, or ordinary workflow inputs.

Create an explicit, non-secret ownership manifest using the returned account ID:

```json
{
  "organizationId": "org_example",
  "providerAccountId": "provider_example",
  "appBindings": [
    { "appId": "app_existing", "planName": "p0-h-0", "buildPlatform": "linux/amd64" }
  ],
  "retainedVolumeIds": []
}
```

```sh
minicloud providers import-legacy --manifest legacy-bindings.json
```

Import verifies the original server/volume identities through read-only requests
in the selected account and binds them atomically. It creates/reuses legacy plan
names from the installation's effective mapping only when provider and region
agree with persisted server evidence. Supply the original effective mapping in
the API configuration; do not replace old SKUs with assumed current defaults.
Supply `buildPlatform` when no verified server architecture is recorded. Existing
historical SKU/image fields remain unknown if they were never recorded.

Each app/environment is assigned explicitly; include each one in `appBindings`.
Volumes belonging to those apps are included automatically. List orphaned retained
volume registry IDs explicitly in `retainedVolumeIds`. Mismatched ownership,
missing resources, active work and unverifiable scope stop import without changing
ownership. The same manifest can be rerun without duplicating plans or bindings.

If deletion stopped because a legacy server has no provider-account binding,
pause admissions and deletion retries, then set `"allowFailedDeletion": true`
on that environment's entry in `appBindings` and run the same import command.
This opt-in accepts only terminal `deletion_status = failed`, with no active
deployment or owned deployment lease; queued/preparing deletion still blocks
import. Import and deletion retry serialize on the environment row lock.
All account, resource identity and placement checks still apply. Import preserves
the original server and volumes and leaves the deletion status and error unchanged,
so the environment remains fenced. Inspect the resulting bindings, then explicitly
retry deletion through the normal app/environment deletion command when ready.
Do not clear deletion state or edit provider-account IDs directly in the database.

Verify all live resource bindings before reopening admissions. Once account-aware
allocations begin, old workers are not a safe rollback target.

The [specification](specs/provider-accounts-and-plans.md) describes the contracts
and acceptance matrix. Source/local tests and live provider qualification are
separate checks; no live cloud acceptance is implied by installation or import
command availability.
