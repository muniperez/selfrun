# Agent deploy and validation workflow

Use this workflow when you want an agent in a MiniCloud agentic cloud coding
environment to change an app, deploy it, validate the public result, inspect
logs, and make a follow-up fix. You choose the environment and the checks that
define success. Give the agent the target explicitly and review its changes
before deployment.

## Prepare the workspace

Open the app repository in the [MiniCloud workspace](workspace-projects.md).
The repository and its source are available in the workspace; the CLI builds
from that checkout. Ensure Docker is available and install the MiniCloud CLI
using the [CLI installation instructions](cli-distribution.md).

Register and select the intended MiniCloud instance, then authenticate with an
API key scoped for app and deployment work:

```sh
minicloud instance add personal cloud.example.com
minicloud instance use personal
minicloud login --token "$MINICLOUD_API_KEY"
```

The key needs `apps:read`, `apps:write`, `deployments:create`,
`deployments:read`, and `plans:read`. Store it in the workspace's protected
secret or environment-variable facility; do not put it in a prompt, source
file, command history, or logs. See [CLI authentication](cli.md#authenticate).
Instance selection and tokens are local to the workspace user.

## Give the agent a bounded task

State the repository path, requested change, validation commands, public URL
and intended environment. For example:

> In `/workspace/repos/sample-app`, fix the contact form submission error.
> Run `npm ci` and `npm test`, then deploy to the `production` environment on
> the `personal` MiniCloud instance. Verify the contact form on
> `https://sample.example.com` and report the deployment number, checks, and
> any remaining issue. Do not change secrets or infrastructure.

Replace those synthetic values with the values for your app. If you have more
than one active environment, name the target explicitly. The CLI requires
`--environment <name>` for application deployment and logs; it does not infer
the target from the dashboard selection. Confirm the target and expected impact
with the agent before it deploys.

## Deploy and verify

From the repository directory, first inspect the config and the working tree.
Run the app's relevant validation commands before deployment. Then deploy all
changed services to the chosen environment:

```sh
minicloud apps inspect sample-app
git diff --check
npm ci
npm test
minicloud deploy --environment production
```

MiniCloud's CLI builds service images from the workspace checkout, publishes
them, creates a deployment and waits for it. `minicloud deploy` selects changed
services by default; use `minicloud deploy all --environment production` when
you intentionally need to redeploy every configured service. See [deploy
options](cli.md#deploy).

The agent should record the deployment number printed by the command and check
deployment progress and its deployment log:

```sh
minicloud status 20261121.1
minicloud logs 20261121.1
```

Use the actual number from this deployment. These commands show deployment
status and build/deployment output. After it succeeds, check the public URL with
the app's user-facing smoke checks, for example:

```sh
curl --fail --show-error --silent https://sample.example.com/health
```

Choose a URL and response check that make sense for your app; a successful
deployment alone does not prove the app behaves correctly. If a runtime issue
appears, retrieve the app's runtime logs by app name or ID:

```sh
minicloud logs sample-app --environment production
```

Use the runtime error to guide a focused code fix, rerun the relevant local
checks, redeploy to the same explicitly chosen environment, and repeat the
public URL verification. The [CLI reference](cli.md#status-logs-and-apps)
describes deployment status and the difference between deployment and app
runtime logs.

## Review the result

Ask the agent to summarize the files changed, validation commands and results,
deployment number, environment, public checks, and any runtime log findings.
Review the diff and the live behavior yourself. If the check fails or the
result is unexpected, stop the cycle and investigate before requesting another
deployment.
