# Historical public candidate handoff (T80 #37)

This records the prelaunch candidate. For current artifacts and version selection, see the [server release contract](../packaging/server/README.md) and [CLI delivery guide](cli-distribution.md).

## Selected identifiers, not a qualified release

- Server candidate: **`0.0.1-rc.3`**, unpublished metadata pending operator
  review (#261). Both previous `server-v0.0.1-rc.1` and `server-v0.0.1-rc.2`
  tags exist; their publishers failed. Never move or reuse either tag.
  [The rc2 run](https://github.com/muniperez/selfrun/actions/runs/44935673888)
  failed on its first image push with `permission_denied: read_package` because
  its image name collided with a pre-existing private package.
- CLI source: **`2a5ae0ccea6c3dd680b4146586ec0668c60c8b93`**, actual canonical
  public `main` containing the merged authentication, SSH and numbered-deployment
  clients. Public prerequisite repairs #70–#75 and ledger #76 were already
  merged at this source. This is not a self-reference to the metadata commit.
- `packaging/server/release.json` is the strict schema-1 pipeline input, not a
  generated/signed release manifest. The existing producer adds the actual
  checked-out server source SHA and real product-image digests at release time.
- CLI protocol `1`, the source SHA above plus stateless seed CLI source
  **`7a4b16130215ea8b2c74221cb02c5e45344d4427`** (#102) in `sourceRevisions`, and both
  version bounds `null`: an explicit candidate allowlist, **not measured runtime
  compatibility** or a claim that arbitrary CLI versions work.
- Latest application migration: `20261120154929_PortalSessionRenewal`.
  Identity and configuration schema versions are both `1`, the initial bundled
  `identity.schema.json` and `configuration.schema.json` contracts (including
  the merged optional Google configuration); JSON Schema draft identifiers and
  Kratos's own software version are not these Selfrun schema versions.

The public-only scope decision in #37 supersedes the older private-integration
prerequisite: #36 is backlog, not passed; #44 is later optional-provider/auth
validation, not a code-completion blocker. No private source, readiness commit
or private candidate identifier was inspected or recreated.

This candidate predates the current Selfrun CLI and reusable workflows.

## Dependency index evidence

On 2026-11-12, anonymous read-only HTTPS requests to
`https://registry-1.docker.io/v2/<repository>/manifests/<tag>` returned the
following multi-architecture indexes. For each, SHA-256 of the **raw response
bytes** matched `Docker-Content-Digest`, and the index listed both `linux/amd64`
and `linux/arm64`. No image layers were downloaded and no images were pulled/run.
The release input pins index digests, not these mutable discovery tags.

| Dependency / discovery tag | Index digest | Required platforms |
| --- | --- | --- |
| `docker.io/postgres:16-alpine` | `sha256:aa6cb33cf4c19c765cefcf48d96176258800870a9a1e930ad126200f9d825139` | `linux/amd64`, `linux/arm64` |
| `docker.io/openbao/openbao:2.2.1` | `sha256:62f582f85e405ef00d48f678c19863d096a2bc0c05e0e5a9bbccd676a1c7316c` | `linux/amd64`, `linux/arm64` |
| `docker.io/oryd/kratos:v1.3.1` | `sha256:fe2428f103a6240c064b6ea77d3088610865c55fcebccb30355857eccdc25b4b` | `linux/amd64`, `linux/arm64` |
| `docker.io/caddy:2.6.2-alpine` | `sha256:25a0097607868fb05a89a5ab9fea2f2ea4cecdc89d887d7dcee8c778a21b9e1f` | `linux/amd64`, `linux/arm64` |

Version provenance: PostgreSQL follows `compose.dev.yml` and the database
initializer contract; OpenBao follows `bundle/openbao-handoff.md` and its
focused contract; Kratos follows `bundle/kratos.yml`; Caddy preserves the
existing `scripts/github-actions/selfrun-deploy/test-caddy-permissions.py`
baseline. This is not a dependency-upgrade or vulnerability qualification.

Raw index responses, response headers and timestamped resolution summaries
remain only in git-ignored `.local/artifacts/t80-registry/` in the implementation
worktree. No credentials or anonymous authorization tokens are recorded.
Index membership proves neither runnable platform images nor stack compatibility,
publisher provenance, security support or registry availability at installation.

## Deferred validation and release gates

The original T80 handoff performed no manual tests, builds, dependency installs, CI reruns, image pulls/runs,
credentials, hosted changes, deployment, tags or publication were performed.
The focused checked-in candidate/payload/source-pin regression is authored
**unrun** at that handoff. For #251, the release contracts passed (21 tests),
self-hosting/installer contracts passed (93 tests, 3 opt-in Docker/Compose cases skipped),
and focused .NET metadata/discovery checks passed (24 tests) on local Linux arm64.
These synthetic checks are not T90/T91 acceptance or release qualification.

For #261, local Linux arm64 checks passed: release contracts (24 tests),
self-hosting/installer/maintenance contracts (93 tests, 3 opt-in Docker/Compose
cases skipped), CLI seed/release checks (188 tests), and metadata/discovery
checks (24 tests). The self-hosting suite initially lacked `age-keygen`; the
official age 1.2.1 arm64 binary was supplied only under ignored artifacts for
the successful rerun.

The seed source pin is retained after executing **the exact historical
`SeedRelease.cs`** from `7a4b16130215ea8b2c74221cb02c5e45344d4427`
(Git blob `3b9404c2a340da33c3ae7d9830543808783c444f`) in a .NET 10 harness
with that informational source SHA. `VerifyAsync` accepted producer-generated
synthetic release-only manifest/bundle assets, and refused tampered inventory,
incorrect bundle hash, incompatible source, altered archive member, runtime-core
disagreement and simulated signature failure. Historical file/guard logic was
retained; only external Compose/Cosign behavior was mocked, and all assets
already existed locally (no downloads). The harness asserted exact canonical
workflow identity and production issuer arguments, **not cryptographic validity**.
Historical source and harness outputs stay under ignored
`.local/artifacts/historical-seed/` and `.local/artifacts/verification-261/`;
maintained CLI regressions cover current/historical archive bindings and
incompatible-source refusal. No CLI repair or staged source-pin update is needed
for this packaging contract. The customer workflow pin is unchanged.
This remains an **unqualified TEST prerelease**, not measured live seed/server
compatibility.

After explicit authorization, the focused commands already documented in
[server packaging](../packaging/server/README.md#focused-checks-and-remaining-evidence)
remain:

```sh
python3 -B -m unittest discover -s scripts/server-release -p 'test_*.py' -v
python3 -B -m unittest discover -s scripts/self-hosting -p 'test_*.py' -v
dotnet test modules/tests/Selfrun.Cli.Tests/Selfrun.Cli.Tests.csproj \
  --configuration Release --artifacts-path .local/artifacts/t80-cli-dotnet \
  --results-directory .local/artifacts/t80-cli-results \
  --filter 'FullyQualifiedName~Seed|FullyQualifiedName~Release' -m:1
dotnet test modules/tests/Selfrun.Tests/Selfrun.Tests.csproj \
  --configuration Release --artifacts-path .local/artifacts/t80-dotnet \
  --results-directory .local/artifacts/t80-test-results \
  --filter 'FullyQualifiedName~ServerReleaseMetadataTests|FullyQualifiedName~Instance_discovery' -m:1
```

T90 #38 must still establish combined/runtime compatibility of this exact
source/protocol pairing, real PostgreSQL/OpenBao/Kratos/Compose behavior,
identity/auth flows, recovery and both control-plane/target architecture
combinations. Existing backend CI fixture failures are not repaired or
presented as passing by this metadata change. T91 #39 owns disposable-host
acceptance; #44 retains later auth/optional-provider validation.

Real two-architecture product builds, agent ELF verification, signing/provenance,
public GHCR visibility and anonymous pulls, installation and recovery acceptance
remain later authorized gates. The existing rc1 and rc2 tags are not moved or reused;
no rc3 tag, assets or product
image digests are supplied or claimed. The metadata allowlist does not satisfy
the historical tested-only qualification requirement: record actual evidence
before publication rather than weakening the validator or inferring a pass.
