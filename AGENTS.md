# Selfrun repository instructions

This repository contains the Selfrun product source and builds its public CLI releases.
Keep changes focused. Never push code unless explicitly asked.

- Start with the README and the user documentation under `docs/`.
- Keep behavior and tests together. Run the affected .NET or dashboard checks from the README.
- Keep hosted operations and operator credentials out of this repo. Use synthetic fixtures and example configuration.
- Use Selfrun names for installers, CLI packages, configuration, and deployment workflows.

## GitHub issues and agent coordination

### Work ledger

- Search for an existing GitHub issue in the owning repository before creating one. For a feature, write a Markdown spec in `docs/specs/` before opening the feature coordinator issue. The spec describes the goal, scope, expected behavior, and acceptance criteria. Link it from the coordinator issue and the coordinator PR.
- For a bug, open a GitHub issue with observed and expected behavior, reproduction steps or available evidence, and how to verify the fix. Use one issue for a small bug. Break larger bugs into native GitHub sub-issues under the bug issue.
- The coordinator owns the feature coordinator issue or bug issue through handoff. Break each feature into native GitHub sub-issues with bounded deliverables, file or component ownership, acceptance criteria, and explicit dependencies. Keep scopes independent so as much work as possible can run in parallel.
- Write each issue so an agent can pick it up without the original chat. Include the expected outcome, current behavior and relevant code or evidence, scope and boundaries, dependencies, and observable acceptance criteria with verification steps. Keep small issues short.
- For small or bounded work, include an effort and complexity note. For example, **Small / low complexity** means one scoped edit and a focused check. Call out factors that materially change the assignment, such as multiple components, migration, visual verification, or live operations.
- Keep assignments, blockers, decisions, status, PR links, and verification results on the issues. Use GitHub as the work ledger and repository specs as durable feature descriptions.

### Issue status comments

- **Started.** When any agent or subagent picks up an issue, comment on the issue before beginning implementation. Say that work has started and identify yourself with your agent name or role and a machine label or host alias. Use a stable session or run ID if a shareable host label is unavailable.
- **PR ready.** When your work is ready to merge, comment on the issue with the PR link, verification results, and any remaining merge prerequisites.
- **Coordinator takeover.** When you close a subagent's PR and take ownership of the work, comment on the subagent's issue. Link the closed PR, state that you are taking over, and say what happens next. Keep the issue open while implementation remains.

### Coordinator and implementation PRs

1. The user starts the coordinator. The coordinator confirms the issue scope and dependencies, creates an integration branch from the intended target, and opens one draft coordinator PR against that target as soon as the branch has changes. The coordinator PR links the issues in scope and contains the combined implementation.
2. The coordinator assigns implementation directly to subagents as a swarm. Assign the single issue for a small bug to one implementation agent. Give each agent its issue, file or component ownership, verification expectations, and the coordinator branch. Launch independent assignments in parallel and sequence only work with real dependencies. Select the lowest-cost available model suited to the issue’s effort and complexity. For example, use Luna for a bounded edit if available, Sol for implementation that requires more reasoning, and Terra when visual or UI inspection is central.
3. Each implementation agent starts its own branch or worktree from the coordinator branch and opens a focused PR with the coordinator branch as its base. Link the issue with a reference such as `Refs #123` and include the verification results. Record blockers and newly discovered follow-up work on GitHub.
4. The coordinator reviews each child PR, resolves findings, verifies its checks, and merges it into the coordinator branch. Update each issue with the integration result. Put closing references on the coordinator PR for issues it completes.

### Independent validation and final merge

1. After integrating the implementation, the coordinator creates a **Work validation** issue linked to the coordinator or bug issue, the coordinator PR, and the feature spec when applicable. Describe what was implemented, the acceptance criteria, the code tests and browser flows to run, the staging target, and the evidence required for acceptance.
2. Before final merge, the coordinator starts a validator subagent that did not implement the changes. Using the Work validation issue, the validator runs Selfrun locally, runs the code tests, exercises the relevant flows in a browser, deploys the PR revision to the designated staging environment, and verifies the deployment. Record the tested commit, test results, staging deployment reference, and blockers on the issue.
3. If the validator finds a problem, they open a GitHub issue with reproduction steps and evidence, link it to the validation issue and coordinator PR, and notify the coordinator. The coordinator assigns a different implementation subagent to fix it, verifies the fix PR, and merges it into the coordinator branch. The coordinator sends the updated revision back to the validator to continue, rerun affected checks, and redeploy and verify staging. Repeat until the validator records acceptance for the final PR revision.
4. Keep the PR pending while required validation is blocked. Hand the accepted PR to the user with the validation issue, tested commit, and staging result. The coordinator may perform the final merge only with explicit user authorization and after the final revision passes independent validation and all required checks.

## Generated artifacts

- Put all intermediate files and generated execution output under the owning repository's `.local/artifacts/` directory, which must be git-ignored before writing outputs.
- This includes screenshots and browser recordings, benchmark results, SQL dumps and execution plans, logs, traces, coverage/build reports, and agent implementation/validation/handoff reports. Never put these outputs in `docs/`, source folders, or the Git index; never force-add ignored artifacts.
- Keep reusable scripts/tests and concise, maintained documentation in source control. Deliberately authored product assets and explicitly requested deliverables are not intermediate execution output.
- Before finishing, check `git status` and `git check-ignore` to ensure generated outputs cannot enter the change set. Move any misplaced outputs into `.local/artifacts/` and update producers and references.
