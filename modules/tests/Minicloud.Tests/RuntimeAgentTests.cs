using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Minicloud.RuntimeAgent;

namespace Minicloud.Tests;

public sealed class RuntimeAgentTests
{
    [Fact]
    public void ParseDockerLine_SplitsMultilineContentAndPersistsCursorShape()
    {
        var observedAt = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var records = LogParsing.ParseDockerLine("api", "service:api", 7, "2026-05-04T10:00:01Z stderr first line\nsecond line", observedAt);

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal("service:api", record.Source));
        Assert.All(records, record => Assert.Equal("api", record.Service));
        Assert.All(records, record => Assert.Equal("stderr", record.Stream));
        Assert.Equal("service:api:000000000007", records[0].Cursor);
        Assert.Equal("service:api:000000000008", records[1].Cursor);
        Assert.Equal("first line", records[0].Message);
        Assert.Equal("second line", records[1].Message);
    }

    [Fact]
    public async Task CursorStore_ReloadsAcceptedCursors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-agent-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new CursorStore(path);
            await store.SaveAsync(new Dictionary<string, string> { ["service:api"] = "service:api:000000000010" }, CancellationToken.None);

            var reloaded = new CursorStore(path);
            await reloaded.LoadAsync(CancellationToken.None);

            Assert.Equal("service:api:000000000010", reloaded.Cursors["service:api"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Redactor_RemovesCommonSecretShapesBeforeEgress()
    {
        var redacted = RuntimeLogRedactor.Redact("Authorization: Bearer abc.def password=hunter2 token=secret api_key=key");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=key", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    private const string Containers = """
        [{"Id":"abc","Name":"/minicloud-demo-api-1","Config":{"Image":"api:v1","Labels":{"com.docker.compose.service":"api"}},"State":{"Status":"running","Health":{"Status":"healthy"}},"RestartCount":2},
         {"Id":"def","Name":"/minicloud-demo-worker-1","Config":{"Image":"worker:v1","Labels":{"com.docker.compose.service":"worker"}},"State":{"Status":"exited"},"RestartCount":0}]
        """;

    [Fact]
    public async Task CollectorReadsPrivateAndStoppedContainersWithoutComposeFile()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "minicloud-demo", "/missing/app.compose.json", "/tmp/token", "/tmp/state");
        var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
        {
            ["docker ps -aq --filter label=com.docker.compose.project=minicloud-demo"] = new(0, "abc\ndef", ""),
            ["docker inspect abc def"] = new(0, Containers, ""),
            ["systemctl is-active caddy"] = new(0, "active", ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, "2026-05-04T10:00:01Z booted", ""),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "", "")
        });
        var collector = new RuntimeCollector(config, runner);
        var status = await collector.CollectStatusAsync("0.2.0", CancellationToken.None);
        Assert.Equal(2, status.Services.Count);
        Assert.Equal("healthy", status.Services[0].Health);
        Assert.Equal(2, status.Services[0].RestartCount);
        Assert.Equal("exited", status.Services[1].State);
        Assert.Null(status.Services[1].Health);
        Assert.True(status.Caddy.Active);
        var logs = await collector.CollectLogsAsync(CancellationToken.None);
        Assert.Contains(logs, log => log.Service == "api" && log.Message == "booted");
    }

    [Fact]
    public async Task CollectionFailureDoesNotBecomeEmptySuccessfulSnapshot()
    {
        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "minicloud-demo", "/missing", "/tmp/token", "/tmp/state");
        var collector = new RuntimeCollector(config, new FakeProcessRunner(new Dictionary<string, ProcessResult>()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectStatusAsync("0.2.0", CancellationToken.None));
        var reached = false;
        await AgentOperations.TryAsync("routing", () => throw new IOException("Denied"));
        await AgentOperations.TryAsync("heartbeat", () => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);
    }

    [Fact]
    public async Task DiagnosticsRecordBoundedOutcomesAndSyntheticCollectionMetrics()
    {
        var points = new ConcurrentBag<MetricPoint>();
        using var listener = CreateListener(points, RuntimeAgentDiagnostics.MeterName);

        await AgentOperations.TryAsync("heartbeat", () => Task.CompletedTask);
        await AgentOperations.TryAsync("status", () => throw new IOException("failed"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AgentOperations.TryAsync("routing", () => Task.FromCanceled(cancelled.Token));

        var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "minicloud-demo", "/missing", "/tmp/token", "/tmp/state");
        var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
        {
            ["docker ps -aq --filter label=com.docker.compose.project=minicloud-demo"] = new(0, "abc", ""),
            ["docker inspect abc"] = new(0, Containers, ""),
            ["docker logs --timestamps --since 20s --tail 100 abc"] = new(0, "2026-05-04T10:00:01Z booted", ""),
            ["docker logs --timestamps --since 20s --tail 100 def"] = new(0, "", ""),
            ["journalctl -u caddy --since 20 seconds ago --output short-iso --no-pager"] = new(0, "", "")
        });
        var collector = new RuntimeCollector(config, runner);
        await AgentOperations.TryAsync("logs", () => collector.CollectLogsAsync(CancellationToken.None), _ => false);

        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.operation.calls" && point.Operation == "heartbeat" && point.Outcome == "success");
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.operation.calls" && point.Operation == "status" && point.Outcome == "failure");
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.operation.calls" && point.Operation == "routing" && point.Outcome == "cancelled");
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.operation.duration" && point.Operation == "logs" && point.Value is double duration && duration >= 0);
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.child.launches" && point.Operation == "docker.inspect");
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.bytes" && point.Operation == "docker.inspect" && (long)point.Value > 0);
        Assert.Contains(points, point => point.Name == "minicloud.runtime.agent.records" && point.Operation == "logs" && (long)point.Value > 0);
        Assert.All(points.Where(point => point.Operation is not null), point =>
            Assert.Contains(point.Operation, new[] { "heartbeat", "status", "routing", "logs", "docker.inspect", "docker.logs", "routing.apply", "journal.logs", "systemctl.status" }));
    }

    [Theory]
    [InlineData(0, true, "applied", "ready", "demo.app.muni.dev")]
    [InlineData(0, true, "applied", "ready", "example.com")]
    [InlineData(0, true, "applied", "ready", "api.example.com")]
    [InlineData(0, false, "applied", "pending")]
    [InlineData(1, true, "failed", "pending")]
    public async Task RoutingReportsApplyAndCertificateResults(int exitCode, bool ready, string applyStatus, string sslStatus, string hostname = "demo.app.muni.dev")
    {
        var root = Path.Combine(Path.GetTempPath(), $"routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new AgentConfig("srv", "org", "app_1", "https://api.example", "minicloud-demo", "/missing", "/tmp/token", Path.Combine(root, "state.json"));
            var runner = new FakeProcessRunner(new Dictionary<string, ProcessResult>
            {
                ["sudo -n /opt/minicloud/agent/apply-minicloud-routing"] = new(exitCode, "", "")
            });
            var desired = new RuntimeDesiredRoutingResponse("v1", "app_1", "minicloud-demo", [new("dom", hostname, "/", "api", 8080)]);
            var result = Assert.Single(await new RoutingApplier(config, runner, new FakeProbe(ready)).ApplyAsync(desired, CancellationToken.None));
            Assert.Equal(applyStatus, result.ApplyStatus);
            Assert.Equal(sslStatus, result.SslStatus);
            Assert.Equal(applyStatus == "failed" ? "routing_apply_failed" : ready ? null : "https_not_ready", result.FailureCode);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeProbe(bool ready) : IHttpsProbe
    {
        public Task<bool> IsReadyAsync(string hostname, CancellationToken cancellationToken) => Task.FromResult(ready);
    }

    private sealed class FakeProcessRunner(IReadOnlyDictionary<string, ProcessResult> responses) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var key = $"{fileName} {string.Join(" ", args)}";
            return Task.FromResult(responses.TryGetValue(key, out var response) ? response : new ProcessResult(127, "", key));
        }
    }

    private sealed class RecordingProcessRunner(IReadOnlyDictionary<string, ProcessResult> responses) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var key = $"{fileName} {string.Join(" ", args)}";
            Calls.Add(key);
            return Task.FromResult(responses.TryGetValue(key, out var response) ? response : new ProcessResult(127, "", key));
        }
    }

    private static MeterListener CreateListener(ConcurrentBag<MetricPoint> points, string meterName)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            points.Add(new MetricPoint(instrument.Name, measurement, GetTag(tags, "operation"), GetTag(tags, "outcome"))));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            points.Add(new MetricPoint(instrument.Name, measurement, GetTag(tags, "operation"), GetTag(tags, "outcome"))));
        listener.Start();
        return listener;
    }

    private static string? GetTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == name)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private sealed record MetricPoint(string Name, object Value, string? Operation, string? Outcome);
}
