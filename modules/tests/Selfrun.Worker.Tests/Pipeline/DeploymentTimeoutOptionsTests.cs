using Selfrun.Worker;
using Selfrun.Worker.Pipeline;
using Selfrun.Worker.Ssh;

namespace Selfrun.Worker.Tests.Pipeline;

public sealed class DeploymentTimeoutOptionsTests
{
    [Fact]
    public void DeploymentAndRemoteExecutionAllowThirtyMinutes()
    {
        var expected = TimeSpan.FromMinutes(30);
        Assert.Equal(expected, new WorkerOptions().DeploymentTimeout);
        Assert.Equal(expected, new DeploymentPipelineOptions().SshCommandTimeout);
        Assert.Equal(expected, new SshDeploymentOptions().CommandTimeout);
    }
}
