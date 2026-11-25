namespace Selfrun.RuntimeAgent;

public static class AgentOperations
{
    public static async Task TryAsync(string operation, Func<Task> action)
    {
        using var measurement = RuntimeAgentDiagnostics.StartOperation(operation);
        try
        {
            await action();
            measurement.Complete("success");
        }
        catch (OperationCanceledException ex)
        {
            measurement.Complete("cancelled");
            Console.Error.WriteLine($"selfrun-agent {operation} failed: {RuntimeLogRedactor.Redact(ex.Message)}");
        }
        catch (Exception ex)
        {
            measurement.Complete("failure");
            Console.Error.WriteLine($"selfrun-agent {operation} failed: {RuntimeLogRedactor.Redact(ex.Message)}");
        }
    }

    public static async Task TryAsync<T>(string operation, Func<Task<T>> action, Func<T, bool> failed)
    {
        using var measurement = RuntimeAgentDiagnostics.StartOperation(operation);
        try
        {
            measurement.Complete(failed(await action()) ? "failure" : "success");
        }
        catch (OperationCanceledException ex)
        {
            measurement.Complete("cancelled");
            Console.Error.WriteLine($"selfrun-agent {operation} failed: {RuntimeLogRedactor.Redact(ex.Message)}");
        }
        catch (Exception ex)
        {
            measurement.Complete("failure");
            Console.Error.WriteLine($"selfrun-agent {operation} failed: {RuntimeLogRedactor.Redact(ex.Message)}");
        }
    }
}
