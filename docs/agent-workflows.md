# Deploy and validate with a coding agent

Give the agent a target, a change to make, and a way to tell whether it worked. The same workflow works from an agent-enabled development environment or a local checkout with the MiniCloud CLI.

## Prepare the project once

Install a CLI compatible with your instance. From the application repository, register your dashboard hostname and sign in. Adding the instance makes it active.

```sh
minicloud instance add personal cloud.example.com
minicloud login
```

For a new project, run `minicloud init` to select or create an application and write `minicloud.yml`. For unattended use, supply a scoped organization API key through the process environment as `MINICLOUD_TOKEN`. Keep the key in your secret store. [CLI installation](cli-distribution.md) · [Authentication and scopes](cli.md#authenticate)

## Give the agent a task

> Fix the contact form in this repository. Run the project’s tests, deploy to the environment named in my request, and submit the form using synthetic data. Verify that the confirmation appears. If a check fails, use the logs to diagnose it, fix the problem, and try again. Report the deployment number, URL, and test result.

Supply the actual environment name, test fixture, and expected result with the task. Use `minicloud environments list` to see the application's available targets.

## Deploy and check the result

In these examples, set `TARGET_ENV` to your chosen environment. Run the project's tests, then deploy from the checkout containing the changes. To build on the server:

```sh
minicloud deploy --environment "$TARGET_ENV" --build-mode source
```

On a machine with Docker and Buildx, omit `--build-mode source` to build locally. The default service selection skips unchanged services. When a deployment runs, the CLI waits for the result and prints the deployment number and the assigned URLs of public services. Run the project's smoke or browser tests against the URL for the relevant service. [Deployment options](cli.md#deploy)

## Diagnose a failure and retry

- **Build or deployment failed.** Set `DEPLOYMENT_NUMBER` to the number printed for this attempt. Run `minicloud logs "$DEPLOYMENT_NUMBER"` for deployment output. If the client disconnected during a deployment, `minicloud status "$DEPLOYMENT_NUMBER" --watch` follows that same attempt.
- **The service deployed but the form failed.** Use the application name and service name from your project. For an app called `sample-app` with a `web` service, run `minicloud logs sample-app --environment "$TARGET_ENV" --service web --tail 50` just after reproducing the failure.

Use the error to make a focused change, rerun the tests, deploy to the same target, and repeat the form submission. The final report can be as short as the environment, service URL, deployment number, and observed result. [Status and logs](cli.md#status-logs-and-apps)
