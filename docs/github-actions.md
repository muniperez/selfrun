# GitHub Actions Workflow

Use the reusable Selfrun workflow when you want GitHub Actions to deploy source artifacts through the Selfrun API.
To create or check your own control-plane VPS from Actions, see
[Seed a control plane](#seed-a-control-plane).

For self-hosting, pass your HTTPS API origin as `selfrun_api_url`. When the
instance has no managed registry, use `buildMode: source` for source-backed
services. Pin the reusable workflow to a reviewed commit and set
`selfrun_version` to a full CLI source commit SHA for repeatable builds.

## Prerequisites

Create a Selfrun API key in the console with these scopes:

- `deployments:create`
- `deployments:read`

Add these secrets to your app repository. Repository or organization secrets work.

| Secret | Required | Description |
| --- | --- | --- |
| `SELFRUN_API_KEY` | Yes | Selfrun API key from the console. |
| `SELFRUN_POSTGRES_PASSWORD` | Only for `database: postgres` | Postgres password used by the deployment. |

Add this repository or organization variable:

| Variable | Required | Description |
| --- | --- | --- |
| `SELFRUN_APP_ID` | Yes | App id from the Selfrun console URL, for example `app_52kqllu8k1nq4kg352kqllu8`. |

## Managed VPS Plans

The workflow deploys to the app selected by `app_id`. The app's managed VPS plan is selected in the Selfrun control plane, not in the customer workflow.

Current plan codes:

| Plan | Provider | Region | Provider SKU | Provider cost basis |
| --- | --- | --- | --- | --- |
| `P0-H-0` | Hetzner | `nbg1` | `cx23` | Original Hetzner plan |
| `P0-V-0` | Vultr | `ewr` | `vc2-1c-0.5gb-v6` | $2.50/month, IPv6-only |
| `P0-V-1` | Vultr | `ewr` | `vc2-1c-0.5gb` | $3.50/month |
| `P0-V-2` | Vultr | `ewr` | `vc2-1c-1gb` | $5.00/month |
| `P0-D-0` | DigitalOcean | `nyc3` | `s-1vcpu-512mb-10gb` | $4.00/month |
| `P0-D-1` | DigitalOcean | `nyc3` | `s-1vcpu-1gb` | $6.00/month |
| `P0-D-2` | DigitalOcean | `nyc3` | `s-1vcpu-2gb` | $12.00/month |

Set workflow permissions:

```yaml
permissions:
  contents: read
```

## Backend App

Create `.github/workflows/selfrun-deploy.yml`:

```yaml
name: Deploy With Selfrun

on:
  push:
    branches:
      - main
  workflow_dispatch:

permissions:
  contents: read

jobs:
  selfrun:
    uses: muniperez/selfrun/.github/workflows/customer-deploy.yml@<reviewed-commit-sha>
    with:
      selfrun_version: <reviewed-cli-source-commit-sha>
      app_id: ${{ vars.SELFRUN_APP_ID }}
      database: postgres
      services: |
        - name: backend
          sourcePath: .
          dockerfile: Dockerfile
          port: 8080
          public: true
          path: /
          healthPath: /health
    secrets:
      selfrun_api_key: ${{ secrets.SELFRUN_API_KEY }}
      postgres_password: ${{ secrets.SELFRUN_POSTGRES_PASSWORD }}
```

## Frontend And Backend App

```yaml
name: Deploy With Selfrun

on:
  push:
    branches:
      - main
  workflow_dispatch:

permissions:
  contents: read

jobs:
  selfrun:
    uses: muniperez/selfrun/.github/workflows/customer-deploy.yml@<reviewed-commit-sha>
    with:
      selfrun_version: <reviewed-cli-source-commit-sha>
      app_id: ${{ vars.SELFRUN_APP_ID }}
      database: postgres
      services: |
        - name: frontend
          sourcePath: ./modules/ui/dashboard
          port: 3000
          public: true
          path: /
          healthPath: /
        - name: backend
          sourcePath: .
          dockerfile: modules/api/Dockerfile
          port: 8080
          public: true
          path: /
          healthPath: /health
          env:
            ASPNETCORE_ENVIRONMENT: Staging
          secretEnv:
            MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN
    secrets:
      selfrun_api_key: ${{ secrets.SELFRUN_API_KEY }}
      postgres_password: ${{ secrets.SELFRUN_POSTGRES_PASSWORD }}
```

## Inputs

| Input | Required | Default | Description |
| --- | --- | --- | --- |
| `app_id` | Yes | | App id from the Selfrun console URL. Pass `${{ vars.SELFRUN_APP_ID }}` from the caller workflow. |
| `database` | No | `sqlite` | `sqlite` or `postgres`. |
| `selfrun_environment` | No | `prod` | `prod` or `staging`. `prod` uses the production API; `staging` uses `https://api.cloud-dev.muni.dev`. |
| `services` | Yes | | YAML service array. Uses the same service fields as `selfrun.yml`, with `name` added because a workflow input cannot receive the keyed `services` map directly. |
| `selfrun_version` | Yes | | Full lowercase CLI source commit SHA or exact CLI version tag (for example `v3.1.0`). Branches, `latest`, and `server-v*` tags are rejected. The workflow builds this source using Git and the .NET 10 SDK. |
| `selfrun_api_url` | No | | HTTPS API origin for a self-hosted instance. |
| `selfrun_app_domain` | No | | Application domain (`APP_DOMAIN`) for a self-hosted instance. |

## Services

`services` accepts a YAML array with one mapping per service:

```yaml
services: |
  - name: backend
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
```

| Field | Required | Description |
| --- | --- | --- |
| `name` | Yes | Service name. Must use lowercase letters, numbers, dashes, and underscores. |
| `sourcePath` | Required for artifact-backed services | Directory bundled into the deployment artifact and used as the internal workflow Docker build context. |
| `dockerfile` | No | Dockerfile path. Defaults to Docker's normal lookup in `sourcePath`. |
| `image` | Required for prebuilt image services; optional when `sourcePath` is set | Full image reference for prebuilt-image deployments. Source-backed services should normally omit this. |
| `port` | Yes | Internal container port, `1` to `65535`. |
| `public` | Yes | Whether the service receives public HTTP traffic. At least one service must be public. |
| `path` | Yes | Public route path. Must start with `/`. |
| `healthPath` | Yes | HTTP health check path. Must start with `/`. |
| `env` | No | String key/value environment variables for the service. |
| `secretEnv` | No | String key/value references to Selfrun app secrets. Values must be created through the CLI or portal before deployment. |

## Runtime Secrets

Do not put `${{ secrets.MY_SECRET }}` inside the `services` input. GitHub does not expose the `secrets` context inside reusable workflow `with` inputs, and secret values could be copied into publish-job outputs.

Runtime secret values are not passed through this GitHub workflow. Add them through the Selfrun CLI or the app portal before deploying:

```bash
selfrun secrets set --app my-api MAPBOX_ACCESS_TOKEN
```

In the portal, open the app, go to **Secrets**, and create the app secret there.

Use `secretEnv` only for non-sensitive references from container environment variable names to stored Selfrun secret names:

```yaml
services: |
  - name: backend
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
    secretEnv:
      MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN
```

The workflow sends `secretEnv` references to the Selfrun API. Selfrun resolves the values server-side from its OpenBao-backed secret store during deployment. Any service can reference an app secret by name. If a referenced secret does not exist, deployment validation fails.

## What The Workflow Does

The workflow runs one deploy job:

- checks out the repository
- clones and verifies the selected CLI source revision, then builds the CLI with the .NET 10 SDK in a separate runner-temporary directory
- converts the `services` YAML input into a temporary `selfrun.yml`
- runs `selfrun deploy --all --config selfrun.yml`

The CLI uploads zip source artifacts to the Selfrun API. The internal
Selfrun deploy workflow downloads, verifies, builds, and publishes those
artifacts before running the same image-based runtime deploy.

The deploy job summary includes:

- App ID
- Customer workflow execution ID and link

When a Selfrun API request fails, the CLI prints the API error message. Validation failures include field-level details where available.

## Postgres

When `database: postgres`, pass:

```yaml
secrets:
  postgres_password: ${{ secrets.SELFRUN_POSTGRES_PASSWORD }}
```

For SQLite, omit `postgres_password`.

## Self-hosted domains

Set `selfrun_api_url` to your HTTPS API origin and `selfrun_app_domain` to
that installation's APP_DOMAIN. The latter sets `SELFRUN_APP_DOMAIN` for
framework host validation. Both are required when selecting a self-hosted instance.
Select a CLI source revision containing these options with `selfrun_version`.

## Seed a control plane

`seed.yml` is a separate reusable workflow that wraps the stateless
`selfrun seed check|plan|apply` CLI so a fresh GitHub-hosted runner can
create or continue your **own** first control-plane VPS. It needs no fork,
existing Selfrun instance, persistent runner, Docker image or external state
service. Progress lives with the provider and on the VPS; every run starts
from the secrets you pass and the live provider/host state. Read the
[seed guide](seed.md) first: the same prerequisites, provider permissions,
costs and security boundaries apply.

This is source functionality, not a release or live-provider qualification.
Select an exact signed Selfrun CLI tag or the full SHA of a Selfrun commit
on canonical `main`. A compatible signed server release is
still required for planning and installation; no example tag is approval to use it.

### Set up

1. Create a GitHub Environment in your repository, for example
   `selfrun-seed`. Add **required reviewers** (strongly recommended for
   `apply`, which can create a billable VPS) and restrict deployment branches
   to your default branch.
2. Add these **Environment** secrets. Repository secrets are not needed.

   | Secret | Required | Contents |
   | --- | --- | --- |
   | `SELFRUN_SEED_CONFIG` | Yes | [Stateless seed JSON](stateless-seed.md#protected-configuration-and-user-owned-key) (`schemaVersion` 1): stable `seedId` from `selfrun seed new-id`, `provider`, **`providerAccount`** (stable account scope, never a credential), `region`, `size`, `domain`, `appDomain`, exact `serverVersion`, and optional `providerCredential` or operator keys. Add explicit initial intent or an existing `target` as described below. Keep the same `seedId` for every run. |
   | `SELFRUN_SEED_SSH_KEY` | For `plan`/`apply` | **Unencrypted** OpenSSH private key used as the seed root identity. |
   | `SELFRUN_SEED_PROVIDER_CREDENTIAL` | For initial creation if not in config | Provider API credential; overrides embedded `providerCredential`. Optional for an explicit existing target. |

   Generate a dedicated key for this purpose, for example
   `ssh-keygen -t ed25519 -N '' -f selfrun-seed`, store the private key only
   in the Environment secret and your own offline backup, then delete local
   copies you do not need. GitHub Actions has no interactive passphrase and the
   workflow does not use an SSH agent, so encrypted keys are rejected. Anyone
   who can run jobs in this Environment can use the key: protect it with
   reviewers and branch rules, and rotate it if exposed.
3. Commit a caller workflow such as `.github/workflows/selfrun-seed.yml`:

```yaml
name: Selfrun control plane

on:
  workflow_dispatch:
    inputs:
      operation:
        type: choice
        options: [check, plan, apply]
        default: check
      approve:
        description: Approval digest from a reviewed plan (apply only)
        type: string
        default: ""
      new_seed:
        description: Allow creating a new VPS (use the same value for plan and apply)
        type: boolean
        default: false

permissions:
  contents: read

jobs:
  seed:
    uses: muniperez/selfrun/.github/workflows/seed.yml@<reviewed-commit-sha>
    with:
      operation: ${{ inputs.operation }}
      approve: ${{ inputs.approve }}
      new_seed: ${{ inputs.new_seed }}
      environment: selfrun-seed
      selfrun_version: v<exact-cli-version>
    secrets:
      SELFRUN_SEED_CONFIG: ${{ secrets.SELFRUN_SEED_CONFIG }}
      SELFRUN_SEED_SSH_KEY: ${{ secrets.SELFRUN_SEED_SSH_KEY }}
      SELFRUN_SEED_PROVIDER_CREDENTIAL: ${{ secrets.SELFRUN_SEED_PROVIDER_CREDENTIAL }}
```

The caller job cannot set `environment`; the reusable workflow binds the
`environment` input on its own executing job, so Environment protection rules
run before the job can use its Environment secrets. Keep the **same names**
in Environment storage, the caller mapping above and the reusable workflow;
do not rename them to lowercase aliases. An Environment-only secret is not
available to the caller job: the executing job resolves it by its original
name from the selected Environment, overriding any caller-supplied value of
that name. No repository-secret duplicates or `secrets: inherit` are needed.
The workflow explicitly fails at runtime if a required secret is empty.

Local synthetic tests check these names and protected-input handling; they
do not prove GitHub-hosted Environment-only resolution. Verify that separately
with no repository-secret duplicates as part of live acceptance, separately
from source integration and publication.

### Inputs and outputs

| Input | Required | Description |
| --- | --- | --- |
| `operation` | Yes | `check` (read-only), `plan` (read-only) or `apply`. |
| `environment` | Yes | GitHub Environment holding the seed secrets and protection rules. |
| `selfrun_version` | Yes | Exact signed CLI tag `vX.Y.Z[-prerelease]` (verified binary install) or full lowercase canonical-`main` commit SHA (explicit source build with the .NET 10 SDK). Branches, `latest` and `server-v*` tags are rejected. There is no fallback between the two modes. |
| `approve` | For `apply` | 64-character approval digest printed by a reviewed `plan`. Rejected for other operations. |
| `new_seed` | No | Default `false`. For initial `plan`/`apply`, also requires explicit `initialCreationIntent` in the protected config. Rejected for `check`. |
| `report_artifact_name` | No | Default `selfrun-seed-report`. Use distinct names when one run calls the workflow more than once. |

Outputs: `result` (`success`, `handoff_pending`, `upgrade_required`,
`reconciliation_required` or `error`), `classification` and `approval_digest`.

`apply` runs only from `workflow_dispatch`; `check` may also run on
`schedule`. Other events, including `push` and pull requests, are refused.

### Flow

1. `check`: inspect an explicit existing target without changing anything.
   Missing target or inventory is not evidence that an earlier create failed.
2. `plan` (with `new_seed: true` for a first creation): review placement,
   prices, release and target in the job summary with its **approval digest**.
   For initial creation, include a provider credential and add
   `"initialCreationIntent": "one-authorized-creator-no-unresolved-create"` to
   `SELFRUN_SEED_CONFIG` with no `target`. This, together with `new_seed: true`,
   asserts that there is exactly one authorized creator and no unresolved prior
   request. Never repeat the assertion after an uncertain create; reconcile first.
3. `apply` with `approve: <digest>` and the same `new_seed` value. The fresh
   runner re-plans; the CLI refuses the digest if the configuration, prices,
   SSH key, release or target changed. A changed plan cannot approve itself:
   plan and review again.
4. Handoff: after creation the run ends with `handoff_pending`. Read the server
   details from the job summary or validated report and verify them in the provider's
   authenticated console. In `SELFRUN_SEED_CONFIG`, map report `target.id` to
   config `target.serverId`, `target.address` to `target.ipv4`, `target.image` to
   `target.image`, and `target.primaryIpId` to `target.primaryIpId` if reported.
   If the report has no address yet, obtain it from the authenticated inventory.
   `target.image` is required for initial host-only preparation without provider
   inventory access. Obtain `target.hostFingerprint` independently from the
   authenticated server console as described in the [seed guide](seed.md);
   never use an unverified scan.
5. Remove `initialCreationIntent`, set `new_seed: false`, and dispatch a new
   `plan` and `apply` with the **new** reviewed digest to continue on that VPS. DNS,
   TLS and browser setup waits are also reported as `handoff_pending`;
   complete them and rerun.
6. `check` to confirm the installation is current.

Retrieve setup tokens and OpenBao recovery material from a trusted
workstation with the CLI as described in the [seed guide](seed.md), using the
same configuration and key over verified SSH. This workflow never exports
them, and a copy that exists only on the VPS is not an independent backup.

### Results

| Exit code | Result | Job | Meaning |
| --- | --- | --- | --- |
| `0` | `success` | Succeeds | Completed (plan computed, apply step done, or check current). |
| `10` | `handoff_pending` | Succeeds with a warning | A user action is required (host fingerprint, DNS, TLS or browser setup). Complete it, then rerun. |
| `11` | `upgrade_required` | Fails | The installed release differs from `serverVersion`. Use the existing coordinated [server maintenance](server-maintenance.md) upgrade. The workflow never upgrades. |
| `12` | `reconciliation_required` | Fails | Provider or host state is ambiguous or conflicting, including an uncertain create. Reconcile in the provider console; do not retry blindly or create another VPS. |
| Other | `error` | Fails | CLI or input error. No automatic retry. |

The job summary shows the operation, result, classification, successful plan
and approval digest, SSH key fingerprint, target details, reasons and next
actions, read from the CLI's `selfrun-seed-report` (`schemaVersion` 1).
Raw CLI output is not published. Invalid reports and reports containing
supplied secrets fail the job, set `result` to `error`, and publish neither an
approval digest nor an artifact. Short credentials can conservatively cause
rejection if they collide with otherwise public report text. The non-secret JSON
report is uploaded as an artifact for 30 days, readable by anyone with read access to
the repository's Actions runs. It is a convenience snapshot, never the source
of truth; losing it does not affect the installation. Artifact transport failure
emits a warning in the log and job summary without changing the seed outcome
or masking CLI/report validation failures. Do not retry `apply` merely to
recover an artifact.

### Security and limits

- Workflow permissions are `contents: read`; actions are pinned by commit
  SHA. The caller repository is not checked out.
- Secrets are written to a fresh owner-only (`0700`/`0600`) runner directory
  without links or group/world-writable ancestors, passed to the CLI as files,
  and deleted in an always-run cleanup step. The configuration is never
  printed; extracted credentials are additionally masked. Raw CLI output
  cannot issue workflow commands. A report containing supplied secret material is
  not uploaded.
- The installer is pinned to a reviewed Git blob and verified by SHA-256, independent of the selected CLI ref.
  Binary installs verify the Sigstore-signed release inventory and compare
  its installed source revision to the resolved tag. Source builds use only
  the exact supported canonical commit. Cosign 2.6.5 is installed from its
  official release with a pinned SHA-256 for server release verification.
- `apply` runs one at a time across **all** Environments within the calling repository
  (`cancel-in-progress: false`; a newer queued apply replaces an older queued
  one). This is not a lock across repositories, workstations or other CLI
  users: keep a single creator per `seedId`. If a create may have happened
  but cannot be confirmed, the CLI stops for reconciliation rather than
  creating again.
- No automatic upgrade, rollback, replacement, DNS change, firewall change or
  retry is performed.
- GitHub-hosted runners use changing public IP addresses. The provider API and
  the VPS (TCP 22) must be reachable from them. Vultr API access-control lists
  must allow the runner, so either adjust them deliberately or use a runner
  with a stable egress address. The workflow never changes access controls.
