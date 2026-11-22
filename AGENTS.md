# Minicloud repository instructions

This repository contains the Minicloud product source and builds its public CLI releases.
Keep changes focused. Never push code unless explicitly asked.

- Start with the README and the user documentation under `docs/`.
- Keep behavior and tests together. Run the affected .NET or dashboard checks from the README.
- Keep hosted operations and operator credentials out of this repo. Use synthetic fixtures and example configuration.
- Keep the public installer paths, CLI package names, and reusable deployment workflow compatible with existing callers.

## GitHub issues and agent coordination

### Work ledger

- Track every actionable item as a GitHub issue in the owning repository before assigning implementation. Search for an existing issue first. Give independently deliverable changes separate issues and link related work and prerequisites.
- Write each issue so an agent can pick it up without the original chat. Include the expected outcome, current behavior and relevant code or evidence, scope and boundaries, dependencies, and observable acceptance criteria with verification steps. Keep small issues short.
- For small or bounded work, include an effort and complexity note. For example, **Small / low complexity** means one scoped edit and a focused check. Call out factors that materially change the assignment, such as multiple components, migration, visual verification, or live operations.
- Keep assignments, blockers, decisions, status, PR links, and verification results on the issues. Use repository specs for durable design context and link them from issues as needed. GitHub replaces execution-plan files, sub-spec task IDs, and Markdown backlog updates as the work ledger.

### Issue status comments

- **Started.** When any agent or subagent picks up an issue, comment on the issue before beginning implementation. Say that work has started and identify yourself with your agent name or role and a machine label or host alias. Use a stable session or run ID if a shareable host label is unavailable.
- **PR ready.** When your work is ready to merge, comment on the issue with the PR link, verification results, and any remaining merge prerequisites.
- **Coordinator takeover.** When you close a subagent's PR and take ownership of the work, comment on the subagent's issue. Link the closed PR, state that you are taking over, and say what happens next. Keep the issue open while implementation remains.

### Coordinator and implementation PRs

1. The user starts the coordinator. The coordinator confirms the issue scope and dependencies, creates an integration branch from the intended target, and opens a coordinator PR against that target as soon as the branch has changes. The coordinator PR links the issues in scope.
2. The coordinator assigns implementation to subagents, including for small issues. Give each agent its issue, file or component ownership, verification expectations, and the coordinator branch. Run independent items in parallel when useful and sequence dependent ones. Select the lowest-cost available model suited to the issue’s effort and complexity. For example, use Luna for a bounded edit if available, Sol for implementation that requires more reasoning, and Terra when visual or UI inspection is central.
3. Each implementation agent starts its own branch or worktree from the coordinator branch and opens a focused PR with the coordinator branch as its base. Link the issue with a reference such as `Refs #123` and include the verification results. Record blockers and newly discovered follow-up work on GitHub.
4. The coordinator reviews each child PR, resolves findings, verifies its checks, and merges it into the coordinator branch. The coordinator then verifies the combined change, updates the linked issues, and hands the coordinator PR to the user for final review. Put closing references on the coordinator PR for issues it completes. Leave the final merge for the user unless they explicitly authorize it.

## Generated artifacts

- Put all intermediate files and generated execution output under the owning repository's `.local/artifacts/` directory, which must be git-ignored before writing outputs.
- This includes screenshots and browser recordings, benchmark results, SQL dumps and execution plans, logs, traces, coverage/build reports, and agent implementation/validation/handoff reports. Never put these outputs in `docs/`, source folders, or the Git index; never force-add ignored artifacts.
- Keep reusable scripts/tests and concise, maintained documentation in source control. Deliberately authored product assets and explicitly requested deliverables are not intermediate execution output.
- Before finishing, check `git status` and `git check-ignore` to ensure generated outputs cannot enter the change set. Move any misplaced outputs into `.local/artifacts/` and update producers and references.

## Disk cleanup

Agent worktrees share one limited disk volume. Each checkout's `node_modules`, `bin`/`obj`, and artifacts can grow to several gigabytes.

- Reuse one fixed directory per check, such as `.local/artifacts/<check>/`, and clear it before rerunning. Don't create a new artifact directory for every attempt.
- Delete bulky intermediate output, such as publish folders, package archives, extracted install trees, and copied build output, once its result is recorded. Keep only the small logs and reports cited on the issue or PR.
- Check free space with `df -h` before heavy builds, test runs, or package smokes. If less than about 5 GB is free, clean up your own artifacts first. If that isn't enough, record a blocker on the issue instead of deleting other agents' checkouts.
- When your PR is merged or your assignment ends, delete `.local/artifacts/`, `bin/`, `obj/`, and `node_modules/` in your worktree.
- After a child PR is merged or closed and its agent has stopped, the coordinator removes the child's worktree with `git worktree remove` and runs `git worktree prune`. Never remove a worktree that has uncommitted or unpushed work. Push it or record it on the issue first.
