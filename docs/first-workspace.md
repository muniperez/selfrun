# Your first MiniCloud workspace

MiniCloud gives you an **agentic cloud coding environment**: a VM for T3 and your coding tools, with a primary disk that keeps your workspace files when compute stops.

## Before you start

- Sign in to a MiniCloud instance where **Dev Environments** are enabled.
- Ask an organization administrator to configure an approved workspace image and plan if none are available.
- You need permission to manage dev environments to create or start a VM. Choose a plan and storage size that fit your work; the dashboard shows estimates before creation.

## Create and open a workspace

1. In the dashboard, open **Dev Environments** and select **Create dev environment**.
2. Give it a name, such as `api-sandbox`, choose an available **Size and location**, and set the storage size. Creating the environment starts its **Primary VM** automatically.
3. Open the new environment and wait for the primary VM to show **Running**. If it is stopped later, select **Start VM** and wait for it to become ready.
4. Select **Connect to T3**, then open the **Open T3** link. In T3, open a project or use its terminal to check out a repository you control.
5. In T3, open **Settings → Providers**, choose your coding provider and complete its sign-in. This is your provider account; it is separate from your MiniCloud sign-in.

## Keep work and open a preview

The primary VM uses the workspace's retained storage for repositories, T3 state and your saved files. Idle shutdown removes VM compute but keeps this storage attached to the dev environment. Storage is billed separately, including while the VM is stopped.

To see an app running in a workspace, start a preview-enabled service and open **Open preview** on that VM. Enter the service's registered preview name, choose **Get preview link**, then open the link. If the service is not available, ask your administrator to register it for previews. Preview links are temporary; request a new link when one expires.

## Use a temporary VM

From an environment's overview, select **Launch temporary VM**. Choose an approved image, size and plan, then enter a repository URL, base ref and resolved commit SHA. Add a repository credential reference only if the repository needs one, authorize task-scoped pushes if needed, and acknowledge the data-loss notice before launching.

A temporary VM gets its own disk and a fresh checkout; it cannot see the primary VM's files or retained storage. Untracked files, Git-ignored files and unpushed work can be lost when the VM is deleted. Changes survive through the authorized Git branch after a verified push. Launching starts compute and incurs charges while the VM runs.

## Set idle behavior

Open the environment's **Settings** tab and review **Idle timeout**. The effective timeout is shown there; the initial default is six hours. You can enter 1–10,080 whole minutes and select **Save idle timeout**, or choose **Keep running** to turn off automatic idle shutdown. Keep running continues compute charges until you stop the machines yourself. The primary and each temporary VM use the timeout independently.

For full lifecycle and access details, see [Projects in managed development workspaces](workspace-projects.md), [T3 Connect](t3-connect.md), and the [prepared image guide](../templates/development-workspaces/README.md).
