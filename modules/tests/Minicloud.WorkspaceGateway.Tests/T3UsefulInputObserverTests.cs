using System.Text;
using Minicloud.WorkspaceGateway;

namespace Minicloud.WorkspaceGateway.Tests;

public sealed class T3UsefulInputObserverTests
{
    public const string TerminalWrite = """{"_tag":"Request","id":"7","tag":"terminal.write","payload":{"threadId":"thread-1","terminalId":"term-1","data":"echo safe\n"},"headers":[]}""";
    public const string Observe = """{"_tag":"Request","id":"8","tag":"terminal.observe","payload":{"threadId":"thread-1","terminalId":"term-1"},"headers":[]}""";

    [Theory]
    [InlineData(TerminalWrite)]
    [InlineData("[" + Observe + "," + TerminalWrite + "]")]
    [InlineData("""{"_tag":"Request","id":9,"tag":"orchestration.dispatchCommand","payload":{"type":"message.dispatch","createdBy":"user","creationSource":"web","commandId":"cmd-1","threadId":"thread-1","messageId":"msg-1","text":"please continue","attachments":[],"dispatchMode":{"type":"start_immediately"}},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":"10","tag":"orchestration.dispatchCommand","payload":{"type":"prepared-run.release","commandId":"cmd-2","threadId":"thread-1","runId":"run-1"},"headers":[]}""")]
    public void RecognizesPinnedEffectRequests(string message) =>
        Assert.True(T3UsefulInputObserver.IsUsefulInput(Encoding.UTF8.GetBytes(message)));

    [Theory]
    [InlineData(Observe)]
    [InlineData("""{"_tag":"Ping"}""")]
    [InlineData("""{"_tag":"Ack","requestId":"7"}""")]
    [InlineData("""{"_tag":"Request","id":"7","tag":"terminal.resize","payload":{"threadId":"thread-1","terminalId":"term-1","cols":100,"rows":40},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":"7","tag":"terminal.write","payload":{"threadId":"thread-1","terminalId":"term-1","data":""},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":"7","tag":"terminal.write","payload":{"data":"input"},"headers":[]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"terminal.write","params":{"threadId":"thread-1","terminalId":"term-1","data":"input"}}""")]
    [InlineData("""{"_tag":"Request","id":7,"tag":"orchestration.dispatchCommand","payload":{"type":"thread.visit","commandId":"cmd-1","threadId":"thread-1"},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":7,"tag":"orchestration.dispatchCommand","payload":{"type":"message.dispatch","createdBy":"user","creationSource":"web","commandId":"cmd-1","threadId":"thread-1","messageId":"msg-1","text":"","attachments":[],"dispatchMode":{"type":"start_immediately"}},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":7,"tag":"orchestration.dispatchCommand","payload":{"type":"message.dispatch","createdBy":"user","creationSource":"web","commandId":"cmd-1","threadId":"thread-1","messageId":"msg-1","text":"input","attachments":[],"dispatchMode":{"type":"steer_active"}},"headers":[]}""")]
    [InlineData("""{"_tag":"Request","id":7,"tag":"orchestration.dispatchCommand","payload":{"type":"message.dispatch","createdBy":"agent","creationSource":"server","commandId":"cmd-1","threadId":"thread-1","messageId":"msg-1","text":"background continuation","attachments":[],"dispatchMode":{"type":"start_immediately"}},"headers":[]}""")]
    [InlineData("{malformed")]
    public void PassiveUnknownAndInvalidFramesAreNotActivity(string message) =>
        Assert.False(T3UsefulInputObserver.IsUsefulInput(Encoding.UTF8.GetBytes(message)));

    [Fact]
    public void OversizeTerminalInputAndMessagesAreNotActivity()
    {
        var oversized = TerminalWrite.Replace("echo safe\\n", new string('x', 65537));
        Assert.False(T3UsefulInputObserver.IsUsefulInput(Encoding.UTF8.GetBytes(oversized)));
        Assert.False(T3UsefulInputObserver.IsUsefulInput(new byte[T3UsefulInputObserver.MaximumMessageBytes + 1]));
    }
}
