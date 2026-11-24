# Providers and plans

Each organization owns its provider accounts and named plans. You can connect
multiple Hetzner, DigitalOcean, Vultr, and AWS accounts. AWS support is
experimental and has not passed live provider qualification; do not treat it as
production-qualified.

CLI plan listing and app creation require an API key with `plans:read`.
Existing keys do not gain new permissions automatically. Refresh an older CLI
login with `minicloud login` or use a key with the required scope. Managing
provider credentials requires the separate `providers:write` operator scope.

## Configure an account

Open **Plans** in the dashboard and select **Provider accounts** (or **Add plan**
and **Connect account**). Add an account name, select a supported
provider, and enter its API credential. DigitalOcean accounts can also specify a
project; Vultr accounts can specify an OS ID. Credentials are write-only and
stored in OpenBao. An organization Owner or Admin manages accounts and plans.

The connection check uses read-only provider requests. It does not allocate a
server or guarantee available quota. Accounts with identical provider types
remain independent entries with separate credentials and plans.

### AWS (experimental; live qualification pending)

An AWS provider account is pinned to one AWS account, region, subnet, and
security group. Create a public subnet in the selected region and a security
group in that subnet's VPC before connecting AWS. The subnet must assign public
IPv4 addresses and have a route to an internet gateway. Configure inbound SSH
from the worker's egress addresses and the application ports you intend to
serve; permit the outbound traffic needed for bootstrap packages and image
registries. MiniCloud does not create or modify VPCs, subnets, routes, security
groups, Elastic IPs, IAM roles, or instance profiles.

Use temporary AWS credentials where practical. The dashboard accepts an access
key ID, secret access key, and optional session token; the CLI reads
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, and optional `AWS_SESSION_TOKEN`.
These values are stored together as a write-only OpenBao secret. The CLI
requires the non-secret `--region`, `--subnet-id`, and `--security-group-id`
arguments. Credential checks call STS `GetCallerIdentity` and read-only EC2
describes; they do not launch instances or create volumes. MiniCloud records the
12-digit AWS account ID and rejects credential rotation to a different account.
The selected subnet and security group are checked for existence, region, and
VPC consistency.

With the AWS credentials already present in the CLI process environment, add
the account without putting any key material in command arguments:

```sh
minicloud providers add --organization org_example --name "AWS production" --provider aws \
  --region us-east-1 --subnet-id subnet-0123456789abcdef0 \
  --security-group-id sg-0123456789abcdef0
```

The adapter needs these API actions:

| Purpose | AWS actions |
| --- | --- |
| Identity and read-only validation/discovery | `sts:GetCallerIdentity` (AWS requires no grant for this call), `ec2:DescribeInstances`, `ec2:DescribeImages`, `ec2:DescribeSubnets`, `ec2:DescribeSecurityGroups`, `ec2:DescribeVolumes`, `ec2:DescribeVolumesModifications` |
| Instance lifecycle | `ec2:RunInstances`, `ec2:TerminateInstances` |
| Retained durable storage | `ec2:CreateVolume`, `ec2:AttachVolume`, `ec2:ModifyVolume` |
| Resource identity tags | `ec2:CreateTags` |

Constrain write access to the configured region, subnet, security group, AMI,
instance types, and MiniCloud resource tags with IAM conditions/resource ARNs
where each action supports them. EC2 `Describe*` actions generally require
`Resource: "*"`. Review the current [EC2 service authorization reference](https://docs.aws.amazon.com/service-authorization/latest/reference/list_amazonec2.html)
before applying a policy. The adapter does not request `iam:PassRole` and does
not attach an instance profile. AWS quotas and instance availability vary by
account and region; read-only validation cannot guarantee quota or capacity.

Create an AWS plan with the account's exact region, an EC2 instance type such as
`t3.micro`, and a regional AMI ID such as `ami-0123456789abcdef0`. AWS plans
require an explicit AMI ID and `linux/amd64` or `linux/arm64`; the adapter checks
that the AMI exists, is available, and matches the selected architecture before
launch. Use a Linux AMI with cloud-init and the packages/network access required
by MiniCloud bootstrap. AMI catalogs and automatic price comparisons are not
provided.

The adapter launches one instance into the configured subnet, requests a public
IPv4 address, applies MiniCloud identity tags, and uses an EC2 client token for
idempotent retries. Discovery and destructive operations require the expected
MiniCloud identity tags in the pinned AWS account and region. Durable storage uses encrypted
gp3 EBS volumes in the subnet's Availability Zone; volumes can attach only to
instances in that same zone. Instance termination does not delete retained EBS
volumes. There is no MiniCloud EBS delete workflow, so retained volumes continue
to incur AWS charges and keep the provider account in use.

AWS provider SSH-key inventory/registration, cross-region plans, public IP
reservation, instance resizing, snapshots, and automatic cleanup of retained
volumes are not implemented. Selected AWS plans should use the existing
MiniCloud bootstrap SSH key rather than provider-managed key-pair selection.
AWS instances, public IPv4 addresses, EBS volumes, and data transfer can incur
charges. Review [EC2 pricing](https://aws.amazon.com/ec2/pricing/on-demand/),
[EBS pricing](https://aws.amazon.com/ebs/pricing/), and
[VPC public IPv4 pricing](https://aws.amazon.com/vpc/pricing/) for the target
region before allocation.

This adapter has synthetic local lifecycle/failure coverage only. No live AWS
account, credentials, or billable resources were used. Live qualification still
needs separately authorized disposable-account runs for native amd64 and arm64
where offered, bootstrap readiness, deployment reuse/redeploy, quota and
partial-failure reconciliation, retained-volume reattachment/resize, and
deletion targeting. Until those checks pass, AWS support remains experimental.

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
`VULTR_API_KEY`/`VULTR_KEY` from the operator process environment. Optional
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
