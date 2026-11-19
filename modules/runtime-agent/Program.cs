using Minicloud.RuntimeAgent;

const string defaultConfigPath = "/opt/minicloud/agent/config.json";
var configPath = args.Length > 0 ? args[0] : defaultConfigPath;
var config = AgentConfig.Load(configPath);
var token = (await File.ReadAllTextAsync(config.TokenPath)).Trim();
var cursorStore = new CursorStore(config.StatePath);
await cursorStore.LoadAsync(CancellationToken.None);

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};
var api = new RuntimeApiClient(httpClient, config, token);
var processRunner = new ProcessRunner();
var routing = new RoutingApplier(config, processRunner);
var statusPublisher = new RuntimeStatusPublisher();

while (true)
{
    await RunCycleAsync();
    await Task.Delay(TimeSpan.FromSeconds(15));
}

async Task RunCycleAsync()
{
    const string agentVersion = "0.2.0";
    using var collector = new RuntimeCollector(config, processRunner);
    await AgentOperations.TryAsync("heartbeat", () => api.SendHeartbeatAsync(agentVersion, CancellationToken.None));
    await AgentOperations.TryAsync("status", async () =>
    {
        var status = await collector.CollectStatusAsync(agentVersion, CancellationToken.None);
        await statusPublisher.PublishAsync(status, api.SendStatusAsync, CancellationToken.None);
    });
    await AgentOperations.TryAsync("routing", async () =>
    {
        RuntimeDesiredRoutingResponse desired;
        try
        {
            desired = await api.GetRoutingAsync(CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            routing.Invalidate();
            throw;
        }
        return await routing.ReconcileAsync(desired, api.SendRoutingApplyResultsAsync, CancellationToken.None);
    }, results => results.Any(result => result.ApplyStatus == "failed"));
    await AgentOperations.TryAsync("logs", async () =>
    {
        var logs = await collector.CollectLogsAsync(CancellationToken.None);
        if (logs.Count > 0)
        {
            var response = await api.SendLogsAsync(logs, CancellationToken.None);
            await cursorStore.SaveAsync(response.LastAcceptedCursorBySource, CancellationToken.None);
        }
    });
}
