# Minicloud

Your apps and coding environments, on your own cloud servers. Minicloud brings deployment, databases, domains and logs into one dashboard, with a CLI and API for automation.

Connect **Hetzner, DigitalOcean or Vultr**, choose a server plan, and deploy your projects. Minicloud provisions the VPS, runs your Docker services and sets up HTTPS.

## What you can do

- **Deploy apps:** Run public web services and private workers together. Detect Node.js, .NET and Spring Boot projects, generate Dockerfiles, build locally or on the server, or deploy existing images.
- **Separate environments:** Give production, staging and Git branch previews their own services, configuration, secrets, databases and domains.
- **Connect domains:** Use generated service URLs or custom domains with automatic HTTPS and optional Cloudflare DNS management.
- **Manage data:** Use SQLite or app-local, shared or dedicated PostgreSQL. Manage credentials, migrations, encrypted scheduled backups and restores. Private database networks require Hetzner.
- **Keep files and assets:** Attach expandable, persistent Hetzner volumes; publish versioned assets independently of service deployments.
- **Code in the cloud:** Launch operator-enabled T3 Code workspaces with retained storage, temporary VMs, start/stop controls and configurable idle shutdown.
- **See what's happening:** Follow deployment progress, history and build/runtime logs; cancel deployments and inspect health. Optional OpenTelemetry and Grafana integration adds application log exploration.
- **Automate securely:** Manage environment variables and OpenBao-backed secrets; use scoped organization API keys, multiple provider accounts, named plans, the CLI, HTTP API and GitHub Actions.

## Run your own instance

**Self-hosting is still in development.** No signed server release has been published or qualified yet. The current setup path is an explicit **local-source test installation**, not a production-ready release.

1. **Prepare your account and domains.** You need a funded Hetzner, DigitalOcean or Vultr account, an SSH key, a dashboard hostname such as `cloud.example.com`, its API hostname `api.cloud.example.com`, and a separate app suffix such as `apps.example.com`. The setup provisions a dedicated Ubuntu 24.04 VPS; cloud resources are billed by your provider.
2. **Build the installer input and CLI.** Follow the [local-source setup guide](docs/stateless-seed.md#explicit-local-source-alternative) to package a clean source checkout and build the matching CLI with .NET 10. The guide includes the protected configuration, credentials and SSH prerequisites.
3. **Plan and provision.** Use the guide's `minicloud seed plan` and `minicloud seed apply` flow with the explicit local-source archive and reviewed hash. Review the provider cost, approve the plan and verify the new host's SSH identity before continuing installation.
4. **Finish setup.** Point DNS at the new server, complete HTTPS and browser onboarding, and retain recovery material off-host. In the dashboard, connect a [provider account and create a plan](docs/providers.md), then deploy your first app below.

For configuration details, see [self-hosting](docs/self-hosting.md). The [signed server installer guide](docs/server-installer.md) describes the release-based path once a compatible release is available. Local-source test installations currently have no supported migration to a signed release.

## Deploy your first app

Already have a running Minicloud instance? Install the CLI on your computer using Git and the **.NET 10 SDK**:

```sh
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh |
  MINICLOUD_INSTALL_MODE=source sh
```

This uses the installer's pinned source revision. For Windows, exact-version selection and compatibility notes, see [CLI installation](docs/cli-distribution.md). If you built the matching CLI for your test instance above, use that build instead.

Replace the example hostname below with yours, then run these commands from your app's directory with Docker running:

```sh
minicloud instance add personal cloud.example.com
minicloud login
minicloud init
minicloud deploy
```

`login` opens browser sign-in (or prints a sign-in link). `init` selects or creates an app and writes `minicloud.yml`. `deploy` builds and publishes your services, waits for deployment and prints their URLs. Configure any required [secrets](docs/cli.md#runtime-environment-variables-and-secrets) before deploying; with multiple environments, select one using `--environment <name>`.

## Learn more

[CLI reference](docs/cli.md) · [GitHub Actions](docs/github-actions.md) · [GitHub App setup](docs/github-app.md) · [Persistent storage](docs/durable-storage.md) · [API reference](docs/api.md) · [T3 Connect](docs/t3-connect.md)

## Develop from source

Use .NET 10, Node.js 22 and Docker with Compose. From the repository root:

```sh
dotnet test Minicloud.slnx --configuration Release -m:1
npm --prefix modules/dashboard ci
npm --prefix modules/dashboard test
npm --prefix modules/dashboard run build
dotnet run --project modules/cli -- --help
```

Some integration tests require Docker. Start local PostgreSQL and OpenBao with `docker compose -f compose.dev.yml up -d postgres openbao openbao-init`. This Compose file is for development only.

[Licensing](docs/licensing.md) · [Report a vulnerability](SECURITY.md)
