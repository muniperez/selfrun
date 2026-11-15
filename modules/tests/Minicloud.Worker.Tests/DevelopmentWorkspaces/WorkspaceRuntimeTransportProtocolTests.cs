using System.Buffers;
using System.Text.Json;
using Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceRuntimeTransportProtocolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("first line\nsecond line\r\n'$(synthetic-only)'")]
    public void RuntimeCallProducesExactlyOneTerminatedJsonRequestBoundToCurrentGeneration(string? text)
    {
        var target = new WorkspaceRuntimeTarget("vm-current", 7, "primary", "203.0.113.8", "");
        var command = WorkspaceRuntimeTransport.CallCommand(target, "operation-current", "status",
            text is null ? null : new
            {
                vmId = "stale-vm", generation = -1, operationId = "stale-operation", action = "stop", text
            });
        const string prefix = "printf '%s' '";
        const string suffix = "' | base64 -d | python3 -B /opt/minicloud-workspace/runtime.py call";
        Assert.StartsWith(prefix, command);
        Assert.EndsWith(suffix, command);
        var bytes = Convert.FromBase64String(command[prefix.Length..^suffix.Length]);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));

        Assert.True(reader.TryReadTo(out ReadOnlySequence<byte> frame, (byte)'\n'));
        Assert.True(reader.End);
        Assert.InRange(frame.Length, 1, 262143);
        using var request = JsonDocument.Parse(frame);
        Assert.Equal(target.VmId, request.RootElement.GetProperty("vmId").GetString());
        Assert.Equal(target.Generation, request.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal("operation-current", request.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("status", request.RootElement.GetProperty("action").GetString());
        if (text is not null)
            Assert.Equal(text, request.RootElement.GetProperty("text").GetString());
    }
}
