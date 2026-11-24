# MiniCloud to Selfrun Rename and Migration Guide

MiniCloud is being renamed to **Selfrun** (`https://selfrun.dev`), tracked in [issue #345](https://github.com/muniventures/minicloud/issues/345).

To avoid breaking existing callers, signed release verification, server installations, and concurrent feature work, the rename is executed in four compatibility-safe stages.

## Rollout stages

| Stage | Scope | Status |
| --- | --- | --- |
| **Stage 1 — Brand, logo, docs, and landing site** | Approved monochrome "Split S" logo mark, `selfrun.dev` landing site copy/metadata, architecture diagram, and updated documentation. | **Complete** |
| **Stage 2 — Compatibility-safe user-facing layer** | Dashboard Selfrun branding/favicon/env aliases, CLI `selfrun` help/version/`selfrun.yml`/`SELFRUN_*`/config-dir fallback, `install.sh` & `install.ps1` dual env vars + `selfrun` command alias, and `customer-deploy.yml` `selfrun_*` input/secret aliases. | **Complete** |
| **Stage 3 — Server packaging, runtime, CLI packages, and repo/domain cutover** | Runtime templates (`templates/runtime/`), server bundle (`packaging/server/`), local compose defaults, wire/protocol identifiers, CLI release package names (`minicloud-*` -> `selfrun-*`), GitHub repository rename (`muniventures/minicloud` -> `muniventures/selfrun`), and GitHub Pages custom domain (`selfrun.dev` `CNAME` + DNS). | **Planned (explicit migration phase)** |
| **Stage 4 — Internal .NET solution and namespaces** | `Minicloud.slnx`, `modules/*/*.csproj`, C# namespaces (`Minicloud.*` -> `Selfrun.*`), and internal symbols (`MinicloudDbContext`). Kept separate to avoid sweeping merge conflicts with concurrent branches. | **Planned** |

## Compatibility aliases active today (Stage 1 + Stage 2)

### CLI environment variables

All user-facing CLI environment variables accept `SELFRUN_*` as primary and fall back to `MINICLOUD_*` when the `SELFRUN_*` variable is unset or blank:

| Primary (`SELFRUN_*`) | Legacy alias (`MINICLOUD_*`) | Purpose |
| --- | --- | --- |
| `SELFRUN_TOKEN` | `MINICLOUD_TOKEN` | API authentication token |
| `SELFRUN_API_URL` | `MINICLOUD_API_URL` | Control-plane API origin override |
| `SELFRUN_APP_DOMAIN` | `MINICLOUD_APP_DOMAIN` | Generated application domain suffix |
| `SELFRUN_LOCAL_ORGANIZATION_SLUG` | `MINICLOUD_LOCAL_ORGANIZATION_SLUG` | Local development organization slug |
| `SELFRUN_CLI_TIMING_TRACE_OUTPUT` | `MINICLOUD_CLI_TIMING_TRACE_OUTPUT` | Optional CLI deployment timing trace output path |

When both variables are set, `SELFRUN_*` takes precedence. `selfrun seed register-provider` refuses to run if either `SELFRUN_TOKEN` or `MINICLOUD_TOKEN` is set in the environment.

### CLI project configuration and local secrets

- `selfrun init` creates `selfrun.yml` by default (use `--config <path>` to target a specific file).
- Default config lookup (`deploy`, `branch`, `status`, `logs`, `environments`, `plans`, `assets`) resolves in order:
  1. `selfrun.yml`
  2. `minicloud.yml` (legacy alias)
  3. `minicloudconfig.yml` (legacy alias)
- Local service secret files resolve `selfrun.secrets.env` first, then `minicloud.secrets.env`. Both filenames are unconditionally excluded from uploaded deployment artifacts.

### CLI local configuration directory

- Default configuration directory is `$XDG_CONFIG_HOME/selfrun` (or `~/.config/selfrun`).
- If the `selfrun` directory does not yet exist and a legacy `$XDG_CONFIG_HOME/minicloud` (or `~/.config/minicloud`) directory exists, the CLI automatically continues reading from the legacy directory so existing saved instances, tokens, and seed journals are not lost.
- To migrate manually:

  ```sh
  mv "${XDG_CONFIG_HOME:-$HOME/.config}/minicloud" "${XDG_CONFIG_HOME:-$HOME/.config}/selfrun"
  ```

### CLI command name, installers, and release packages

- Public installer URLs and filenames (`install.sh`, `install.ps1`) are preserved at the repository root.
- Existing signed CLI release package names (`minicloud-<version>-<rid>.tar.gz`, `minicloud-<version>-<rid>.zip`) and seed review archives (`minicloud-seed-<rid>.*`) are preserved until Stage 3 so existing Sigstore verification, provenance manifests, and callers continue to work without breakage.
- `install.sh` and `install.ps1` accept `SELFRUN_INSTALL_MODE`, `SELFRUN_REPO`, `SELFRUN_VERSION`, and `SELFRUN_INSTALL_DIR` with automatic fallback to the corresponding `MINICLOUD_*` variables.
- After installing `minicloud` (`minicloud.exe` on Windows), the installers also create a `selfrun` command alias (`selfrun -> minicloud` relative symlink on Linux/macOS; `selfrun.cmd` shim on Windows) so both `selfrun` and `minicloud` are immediately available on `PATH`.
- Installer smoke verification accepts both `selfrun version <X.Y.Z>` and historical `minicloud version <X.Y.Z>` output.

### Dashboard runtime configuration

`modules/dashboard/dashboard-startup.mjs` reads `SELFRUN_*` with fallback to `MINICLOUD_*`:

- `SELFRUN_API_URL` / `MINICLOUD_API_URL`
- `SELFRUN_APP_DOMAIN` / `MINICLOUD_APP_DOMAIN`
- `SELFRUN_SOURCE_URL` / `MINICLOUD_SOURCE_URL`
- `SELFRUN_DASHBOARD_HTML_ROOT` / `MINICLOUD_DASHBOARD_HTML_ROOT`

### Reusable GitHub Actions deployment workflow

`.github/workflows/customer-deploy.yml` preserves all existing inputs (`minicloud_environment`, `minicloud_version`, `minicloud_api_url`, `minicloud_app_domain`) and secrets (`minicloud_api_key`) and adds optional Selfrun equivalents (`selfrun_environment`, `selfrun_version`, `selfrun_api_url`, `selfrun_app_domain`, `selfrun_api_key`). When both are supplied, the `selfrun_*` value takes precedence. The workflow exports both `SELFRUN_*` and `MINICLOUD_*` variables to the CLI so pinned historical CLI commits remain compatible.

## Intentionally preserved surfaces (until Stage 3 / Stage 4)

The following surfaces remain unchanged in Stage 1–2 because changing them requires coordinated server/operator cutover, repository renaming, or an isolated refactor PR:

- **GitHub repository & Sigstore identity:** `muniventures/minicloud` in `install.sh`, `install.ps1`, `.github/workflows/cli-release.yml`, `.github/workflows/customer-deploy.yml`, `scripts/cli-release/package.py`, and default dashboard `sourceUrl`.
- **GitHub Pages custom domain:** `site/public/` keeps relative `./` asset links so the landing page renders cleanly at both `https://muniventures.github.io/minicloud/` and `https://selfrun.dev/`. Adding a `CNAME` file is coordinated with DNS configuration.
- **API & wire contracts:** `/api/instance` `product` field (CLI now accepts both `"selfrun"` and `"minicloud"` so servers can switch safely in Stage 3), `X-Minicloud-CSRF` header, `minicloud_subdomain` domain binding kind, `_minicloud-verification` DNS TXT record prefix, `MINICLOUD_OTLP_LOGS_HEADERS` reserved secret name, and `minicloud-agent` log source identifier.
- **Server packaging, runtime, and local compose:** `packaging/server/`, `templates/runtime/` (`apply-minicloud-routing.py`, `install-minicloud-agent.sh`, `minicloud-agent.service`), `scripts/github-actions/minicloud-deploy/`, OpenBao mount `minicloud-apps`, `/opt/minicloud`, `compose.*.yml`, `.env.example`, and `ConnectionStrings__Minicloud`.
- **Internal .NET identifiers:** `Minicloud.slnx`, `modules/*/*.csproj`, C# `Minicloud.*` namespaces, and `MinicloudDbContext`.

## Migrating an existing project to Selfrun naming

1. Rename `minicloud.yml` to `selfrun.yml` (and `minicloud.secrets.env` to `selfrun.secrets.env`, if used).
2. Update CI/CD environment variables from `MINICLOUD_*` to `SELFRUN_*`.
3. In GitHub Actions calling `.github/workflows/customer-deploy.yml`, switch inputs/secrets from `minicloud_*` to `selfrun_*` whenever convenient (existing `minicloud_*` names continue to work).
4. Move your local CLI config directory from `~/.config/minicloud` to `~/.config/selfrun` (optional; the CLI automatically uses `~/.config/minicloud` if `~/.config/selfrun` does not exist).
