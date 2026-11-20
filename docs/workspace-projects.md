# Projects in managed development workspaces

## Current limitation (#202)

The pinned T3 **0.1.9** cannot provide the requested owner-only,
root-constrained Clone and New Project flows with Minicloud's reduced gateway
session. GitHub CLI sign-in is independent of T3 authorization: repository
discovery can succeed while both destination choices and Clone remain disabled.
The authenticated production session has exactly `terminal:operate`,
`terminal:read`, `orchestration:operate` and `orchestration:read` for these
operations. This change does **not** make Clone available in that session.

The prepared image now seeds T3's supported `addProjectBaseDirectory` setting
to `/workspace/repos` when absent or blank. It runs as `coding` before T3 starts,
preserves explicit user defaults and unrelated settings, and reports setup
failures in the service journal without preventing existing T3 access.
It does not move imported projects, change T3's `/workspace/t3` data directory,
replace its database, change filesystem ownership, or make `/workspace` writable.
Existing nonblank defaults are left unchanged, including `/workspace`.
This is a clone/add-folder UI default, **not an authorization boundary**.
It does not control T3's named **New Project** action.

Only a newly built, explicitly qualified and selected image receives the helper.
No running VM is patched or restarted by this source change.

## Exact upstream dependency

Evidence below is from the image's pinned
[`24e77473ecf41bc1f7142529c7911c1c1aae1d76`](https://github.com/pingdotgg/t3code/tree/24e77473ecf41bc1f7142529c7911c1c1aae1d76),
not a newer release:

| Upstream source | Relevant behavior |
| --- | --- |
| [`packages/contracts/src/auth.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/packages/contracts/src/auth.ts) | `AuthEnvironmentScope` is an enum of whole-environment permissions, not root-bound grants. `sessionGrantsScope` uses explicit `permissions` when present; legacy-parent fallback must not be used to fabricate permissions. |
| [`packages/contracts/src/clientRpcPermissions.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/packages/contracts/src/clientRpcPermissions.ts) | Clone start/retry/cancel and source-control clone require `source-control:write`; that same permission also authorizes publish, pull, ref/worktree mutations and pull-request writes. |
| [`apps/server/src/auth/RpcAuthorization.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/auth/RpcAuthorization.ts) | The server enforces those client-guarded scopes. `filesystemBrowse`, metadata and project file reads all require `filesystem:read`; `projectsCreateNew` requires `orchestration:operate`. |
| [`packages/contracts/src/project.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/packages/contracts/src/project.ts) | `ProjectReadFileInput.relativePath` supports absolute host paths. Granting filesystem read is not just permission to list clone destinations. |
| [`apps/server/src/workspace/WorkspacePaths.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/workspace/WorkspacePaths.ts) | `normalizeWorkspaceRoot` resolves an input path and checks/creates a directory; it does not bind that root to a Minicloud session or `/workspace/repos`. |
| [`packages/contracts/src/settings.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/packages/contracts/src/settings.ts), [`packages/client-runtime/src/operations/projects.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/packages/client-runtime/src/operations/projects.ts) | `addProjectBaseDirectory` defaults to an empty string; `getAddProjectInitialQuery` otherwise falls back to `~/`. Clone destination construction uses this base. |
| [`apps/server/src/project/ManagedProjectFolders.ts`](https://github.com/pingdotgg/t3code/blob/24e77473ecf41bc1f7142529c7911c1c1aae1d76/apps/server/src/project/ManagedProjectFolders.ts) | `namedProjectsRoot = path.resolve(config.baseDir, "projects")`; New Project takes a name, not a configurable destination. With the retained T3 base it creates under `/workspace/t3/projects`, independently of the clone setting and service working directory. |

A supported upstream change must expose a restricted project-management
capability to the normal UI, authorize clone/browse/create on the server without
granting unrelated filesystem reads or repository writes, and support a named
project parent of `/workspace/repos` independently of the T3 data directory.
Destination enforcement must reject traversal, sibling-prefix paths, symlink
escapes and replacement races, not just compare text prefixes.
Existing project IDs, paths and threads must remain usable without relocation.

Minicloud can adopt that capability only after qualifying and pinning it, with
an explicit grant restricted to an authorized primary workspace creator or
organization Owner, the current organization/workspace, opaque gateway session
and VM generation. Other members, API keys, read-only and ephemeral sessions
must not inherit the new authority. Existing gateway reauthorization,
revocation, expiry and drain fencing must remain effective.

Do not add `filesystem:read` or `source-control:write` to the shared VM bearer,
rewrite the session descriptor to claim them, proxy privileged RPC with a second
token, or substitute terminal commands for the normal UI. Changing the service
working directory cannot fix these authorization checks or the named-project
parent. A symlink from T3's projects directory is not a scoped capability and
could hide existing projects.

## Retained disk accounting

`/workspace/repos` and `/workspace/home` share the retained filesystem, and
`ResourceSampler` counts both with a single `statvfs('/workspace')` probe.
The retained volume holds only what is needed to rebuild a Dev Environment on a
new VM: T3 state, credentials/config and repository sources with `.git`.
Caches, container data and git-ignored build outputs live on the VM's local
scratch disk and are reported separately; see the storage tiers in
[`templates/development-workspaces/README.md`](../templates/development-workspaces/README.md)
and #202.

## Validation and outstanding acceptance

Focused synthetic regressions cover default seeding/reapplication, preservation
of explicit defaults/settings and imported project state, malformed/symlinked
settings rejection, sanitized setup errors, unchanged explicit session scopes
despite a synthetic GitHub token in the issuer environment, and exactly one
retained-filesystem probe as both repo and home allocations grow.

**Still blocked by upstream:** normal authenticated Clone and named New Project
under `/workspace/repos`, reopen of that UI clone, and live rejection of a
destination outside the approved root. No public-fixture UI acceptance,
authenticated browser session, image qualification or runtime rollout is
claimed. After the scoped upstream contract exists, qualify those flows plus
owner/non-owner, other-workspace, expired/revoked-session, stale-generation,
read-only and ephemeral denials through the real gateway. Confirm GitHub
sign-in does not change the grant and existing imported projects still reopen.
