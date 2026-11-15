using System.Text;
using System.Text.Json;
using Minicloud.Common.Models;

namespace Minicloud.Worker.CodingWorkspaces;

public static class CodingWorkspaceBootstrap
{
    public const string CodexVersion = CodingWorkspaceVersions.Codex;
    public const string T3Version = CodingWorkspaceVersions.T3;
    public const string Revision = CodingWorkspaceVersions.BootstrapRevision;

    public static string BuildCloudInit(CodingWorkspace workspace)
    {
        var script = $$"""
            #!/bin/bash
            set -euo pipefail
            install -d -o coding -g coding -m 700 /home/coding/repos /home/coding/worktrees /home/coding/.t3 /home/coding/.codex
            install -d -o coding -g coding -m 700 /home/coding/.t3/userdata
            install -d -o root -g root -m 755 /usr/local/bin
            npm install --global --no-audit --no-fund @openai/codex@{{CodexVersion}}
            runuser -u coding -- env HOME=/home/coding T3CODE_VERSION={{T3Version}} PATH=/home/coding/.local/bin:/usr/local/bin:/usr/bin:/bin /bin/bash -lc 'curl -fsSL https://t3.codes/install.sh | sh'
            printf '%s\n' 'T3CODE_HOST=127.0.0.1' 'T3CODE_PORT=3773' > /home/coding/.t3/service.env
            chown coding:coding /home/coding/.t3/service.env
            chmod 600 /home/coding/.t3/service.env
            loginctl enable-linger coding
            runuser -u coding -- env HOME=/home/coding PATH=/home/coding/.local/bin:/usr/local/bin:/usr/bin:/bin t3 service install --yes
            """;

        var publicKey = JsonSerializer.Serialize(workspace.SshPublicKey);
        var lines = new StringBuilder();
        foreach (var line in script.Split('\n'))
            lines.Append("      ").AppendLine(line);

        return $$"""
            #cloud-config
            users:
              - name: coding
                gecos: Minicloud coding workspace
                shell: /bin/bash
                lock_passwd: true
                groups: [users]
                ssh_authorized_keys:
                  - {{publicKey}}
            package_update: true
            packages:
              - ca-certificates
              - curl
              - git
              - nodejs
              - npm
              - gh
            write_files:
              - path: /usr/local/sbin/minicloud-coding-workspace-bootstrap
                owner: root:root
                permissions: '0755'
                content: |
            {{lines.ToString().TrimEnd()}}
            runcmd:
              - [bash, /usr/local/sbin/minicloud-coding-workspace-bootstrap]
            """;
    }
}
