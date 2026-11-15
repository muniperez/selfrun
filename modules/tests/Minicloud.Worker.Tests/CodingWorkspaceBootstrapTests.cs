using Minicloud.Common.Models;
using Minicloud.Worker.CodingWorkspaces;

namespace Minicloud.Worker.Tests;

public sealed class CodingWorkspaceBootstrapTests
{
    [Fact]
    public void CloudInitPinsToolsAndKeepsT3LoopbackOnlyUnderTheDedicatedUser()
    {
        var cloudInit = CodingWorkspaceBootstrap.BuildCloudInit(new CodingWorkspace
        {
            Id = "workspace_synthetic",
            SshPublicKey = "ssh-ed25519 synthetic-public-key"
        });

        Assert.Contains("T3CODE_VERSION=0.1.9", cloudInit);
        Assert.Contains("@openai/codex@0.169.0", cloudInit);
        Assert.Contains("T3CODE_HOST=127.0.0.1", cloudInit);
        Assert.Contains("loginctl enable-linger coding", cloudInit);
        Assert.Contains("/home/coding/repos", cloudInit);
        Assert.Contains("/home/coding/worktrees", cloudInit);
        Assert.Contains("ssh-ed25519 synthetic-public-key", cloudInit);
        Assert.DoesNotContain("PRIVATE KEY", cloudInit, StringComparison.OrdinalIgnoreCase);
    }
}
