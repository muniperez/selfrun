using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Selfrun.Api.Auth;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers.Dns;
using Selfrun.Common.Services;
using Selfrun.RuntimeAgent;
using YamlDotNet.RepresentationModel;

namespace Selfrun.Tests;

[Collection("Runtime ingestion diagnostics")]
public sealed class RuntimeAgentProfilingTests
{
    [Fact]
    public async Task ProfilingDisabled_StartsCleanlyWithZeroExportsEvenIfReceiverUrlIsInvalid()
    {
        var root = Directory.CreateTempSubdirectory("selfrun-agent-profiling-off-");
        try
        {
            var configPath = Path.Combine(root.FullName, "config.json");
            await File.WriteAllTextAsync(configPath, """
                {
                  "serverId": "srv_off",
                  "organizationId": "org_off",
                  "appId": "app_off",
                  "apiBaseUrl": "https://api.example.invalid",
                  "composeProject": "selfrun-off",
                  "composeFile": "/unused",
                  "tokenPath": "/unused",
                  "statePath": "/unused/state.json",
                  "profiling": {
                    "enabled": false,
                    "receiverUrl": "https://non-loopback.example.invalid:4318",
                    "exportOtlp": true
                  }
                }
                """);

            var config = AgentConfig.Load(configPath);
            Assert.NotNull(config.Profiling);
            Assert.False(config.Profiling!.Enabled);

            var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
            using var http = new HttpClient(handler);
            using var journal = new StringWriter();
            await using var profiler = RuntimeAgentProfiler.Start(
                config.Profiling,
                journalWriter: journal,
                exportHttpClient: http,
                startBackgroundLoop: false);

            Assert.False(profiler.IsEnabled);
            Assert.Null(profiler.FlushSummary());
            Assert.Empty(journal.ToString());
            Assert.Equal(0, handler.Calls);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("http://127.0.0.1:4318", true)]
    [InlineData("http://localhost:4318", true)]
    [InlineData("https://[::1]:4318", true)]
    [InlineData("http://10.0.0.5:4318", false)]
    [InlineData("https://collector.example.com:4318", false)]
    [InlineData("http://0.0.0.0:4318", false)]
    [InlineData("http://user:pass@127.0.0.1:4318", false)]
    [InlineData("ftp://127.0.0.1:4318", false)]
    [InlineData("not-a-uri", false)]
    public void ProfilingContract_EnforcesLoopbackOnlyReceiver(string receiverUrl, bool expectedAllowed)
    {
        var config = new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: receiverUrl,
            EmitJournalSummary: true,
            ExportOtlp: true);

        if (expectedAllowed)
        {
            config.Validate();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => config.Validate());
        }
    }

    [Theory]
    [InlineData(0, 2000, 16)]
    [InlineData(3601, 2000, 16)]
    [InlineData(60, 50, 16)]
    [InlineData(60, 20000, 16)]
    [InlineData(60, 2000, 0)]
    [InlineData(60, 2000, 129)]
    public void ProfilingContract_EnforcesBoundedIntervalsTimeoutsAndQueues(int interval, int timeoutMs, int maxQueue)
    {
        var config = new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: "http://127.0.0.1:4318",
            SummaryIntervalSeconds: interval,
            ExportTimeoutMs: timeoutMs,
            MaxQueueSize: maxQueue,
            EmitJournalSummary: true,
            ExportOtlp: false);

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public async Task Profiler_EmitsAllowlistedAggregateSummaryAndNeverExportsSyntheticSecrets()
    {
        const string syntheticSecret = "synthetic-secret-DO-NOT-EXPORT-987654";
        var exportedPayloads = new ConcurrentBag<string>();
        var handler = new CountingHandler(request =>
        {
            exportedPayloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        using var journal = new StringWriter();
        var config = new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: "http://127.0.0.1:4318",
            SummaryIntervalSeconds: 60,
            ExportTimeoutMs: 1000,
            MaxQueueSize: 8,
            EmitJournalSummary: true,
            ExportOtlp: true);

        await using var profiler = RuntimeAgentProfiler.Start(
            config,
            journalWriter: journal,
            exportHttpClient: http,
            startBackgroundLoop: false);

        await AgentOperations.TryAsync("heartbeat", () => Task.CompletedTask);
        await AgentOperations.TryAsync("status", () => throw new InvalidOperationException($"Bearer {syntheticSecret} password={syntheticSecret}"));

        var summary = profiler.FlushSummary();
        await profiler.DrainExportsAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(summary);
        Assert.Equal(RuntimeAgentDiagnosticSummary.SchemaVersion, summary!.Schema);
        Assert.Equal(RuntimeAgentDiagnosticSummary.ServiceName, summary.Service);
        Assert.Contains(summary.Operations, op => op.Operation == "heartbeat" && op.Outcome == "success" && op.Count >= 1);
        Assert.Contains(summary.Operations, op => op.Operation == "status" && op.Outcome == "failure" && op.Count >= 1);
        Assert.NotNull(summary.TraceId);

        var journalLine = journal.ToString().Trim();
        Assert.NotEmpty(journalLine);
        RuntimeAgentDiagnosticSummary.ValidateAggregateOnlyJson(journalLine);
        Assert.DoesNotContain(syntheticSecret, journalLine, StringComparison.Ordinal);

        var exported = Assert.Single(exportedPayloads);
        Assert.DoesNotContain(syntheticSecret, exported, StringComparison.Ordinal);
        Assert.Contains("selfrun-runtime-agent", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Profiler_UnavailableOrSaturatedCollectorDropsBoundedlyWithoutDelayingProductTraffic()
    {
        var drops = new ConcurrentBag<string>();
        using var dropListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeAgentDiagnostics.MeterName &&
                    instrument.Name == "selfrun.runtime.agent.exporter.drops")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        dropListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    drops.Add(reason);
                }
            }
        });
        dropListener.Start();

        var releaseSlowExport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async (request, ct) =>
        {
            await releaseSlowExport.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler);
        using var journal = new StringWriter();
        var config = new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: "http://127.0.0.1:4318",
            SummaryIntervalSeconds: 60,
            ExportTimeoutMs: 150,
            MaxQueueSize: 1,
            EmitJournalSummary: true,
            ExportOtlp: true);

        await using var profiler = RuntimeAgentProfiler.Start(
            config,
            journalWriter: journal,
            exportHttpClient: http,
            startBackgroundLoop: false);

        // Saturate the 1-slot bounded queue while the first export is stalled.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            await AgentOperations.TryAsync("heartbeat", () => Task.CompletedTask);
            profiler.FlushSummary();
        }
        sw.Stop();

        // Product heartbeat loop must never block on the stalled/saturated exporter.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Product operations were delayed by {sw.Elapsed.TotalMilliseconds}ms.");
        Assert.True(profiler.TotalExporterDrops >= 1);

        // Wait for the stalled request to hit ExportTimeoutMs and record a timeout drop.
        await profiler.DrainExportsAsync(TimeSpan.FromSeconds(2));
        releaseSlowExport.TrySetResult();

        Assert.Contains("queue_full", drops);
        Assert.Contains("timeout", drops);
    }

    [Fact]
    public async Task AgentAndApi_CorrelateTraceIdEnforceTenantDenialAndIngestLogsDuringOperatorExportFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var factory = new ProfilingRuntimeApiFactory(connection);
        factory.CreateClient().Dispose();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfrunDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var now = new DateTimeOffset(2026, 11, 24, 5, 0, 0, TimeSpan.Zero);
        var tenantA = await SeedTenantAsync(factory, "org_prof_a", "app_prof_a", now);
        var tenantB = await SeedTenantAsync(factory, "org_prof_b", "app_prof_b", now);

        // Capture server-side RuntimeIngestion Activity trace IDs to prove agent -> API correlation.
        var serverTraceIds = new ConcurrentBag<string>();
        using var serverActivityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Selfrun.RuntimeIngestion",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => serverTraceIds.Add(activity.TraceId.ToString())
        };
        ActivitySource.AddActivityListener(serverActivityListener);

        // Point operator profiler at a failing loopback exporter (HTTP 503).
        var failingExporter = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var exporterHttp = new HttpClient(failingExporter);
        using var journal = new StringWriter();
        var profilingConfig = new AgentProfilingConfig(
            Enabled: true,
            ReceiverUrl: "http://127.0.0.1:4318",
            SummaryIntervalSeconds: 60,
            ExportTimeoutMs: 500,
            MaxQueueSize: 4,
            EmitJournalSummary: true,
            ExportOtlp: true);

        await using var profiler = RuntimeAgentProfiler.Start(
            profilingConfig,
            journalWriter: journal,
            exportHttpClient: exporterHttp,
            startBackgroundLoop: false);

        var root = Directory.CreateTempSubdirectory("selfrun-agent-corr-");
        try
        {
            var statePath = Path.Combine(root.FullName, "state.json");
            using var apiHttp = factory.CreateClient();
            var agentConfig = new AgentConfig("srv_a", tenantA.OrgId, tenantA.AppId, apiHttp.BaseAddress!.ToString(),
                "selfrun-prof-a", "/unused", "/unused", statePath, profilingConfig);
            var runtimeApi = new RuntimeApiClient(apiHttp, agentConfig, tenantA.RuntimeToken);
            var cursorStore = new CursorStore(statePath);

            // Trigger an operator export failure first, then verify product log ingestion still succeeds.
            profiler.FlushSummary();
            await profiler.DrainExportsAsync(TimeSpan.FromSeconds(2));
            Assert.True(profiler.TotalExporterDrops >= 1);

            string? agentTraceId = null;
            RuntimeLogIngestResponse? ingestResponse = null;
            await AgentOperations.TryAsync("logs", async () =>
            {
                agentTraceId = Activity.Current?.TraceId.ToString();
                var records = new[]
                {
                    new AgentLogRecord("service:api", "api", "stdout", now, "service:api:000000000001",
                        RuntimeLogRedactor.Redact("booted password=synthetic-secret-value")),
                    new AgentLogRecord("caddy", null, "stdout", now.AddSeconds(1), "caddy:000000000001", "proxy ready")
                };
                ingestResponse = await runtimeApi.SendLogsAsync(records, CancellationToken.None);
                await cursorStore.SaveAsync(ingestResponse.LastAcceptedCursorBySource, CancellationToken.None);
            });

            Assert.NotNull(agentTraceId);
            Assert.NotNull(ingestResponse);
            Assert.Equal(2, ingestResponse!.Accepted);
            Assert.Equal(0, ingestResponse.Rejected);
            Assert.Equal(1, cursorStore.WriteCount);
            Assert.Contains(agentTraceId!, serverTraceIds);

            // Denied tenant access: tenant B API key cannot read tenant A logs; invalid runtime token gets 401.
            apiHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenantB.ApiKeyToken);
            using var crossTenantRead = await apiHttp.GetAsync($"/api/runtime/logs?appId={tenantA.AppId}&tail=10");
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantRead.StatusCode);

            apiHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-runtime-token");
            using var unauthorizedIngest = await apiHttp.PostAsJsonAsync("/api/runtime/heartbeat", new { agentVersion = "0.2.0" });
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedIngest.StatusCode);

            // Tenant A API key can read redacted logs.
            apiHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenantA.ApiKeyToken);
            var body = await apiHttp.GetStringAsync($"/api/runtime/logs?appId={tenantA.AppId}&tail=10");
            Assert.DoesNotContain("synthetic-secret-value", body, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", body, StringComparison.Ordinal);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void OperatorProfilingAssets_EnforceLoopbackRestrictedUnitTenantHeaderAndRequiredDashboardPanels()
    {
        var repoRoot = FindRepositoryRoot();

        // 1. Remote agent example collector: loopback-only receiver, restricted journald unit, loopback tunnel exporter.
        var remoteCollectorPath = Path.Combine(repoRoot, "observability", "profiling", "remote-agent-collector.example.yml");
        var remoteYaml = LoadYaml(remoteCollectorPath);
        var remoteReceivers = (YamlMappingNode)remoteYaml.Children[new YamlScalarNode("receivers")];
        var remoteOtlpEndpoint = ((YamlScalarNode)((YamlMappingNode)((YamlMappingNode)((YamlMappingNode)remoteReceivers.Children[new YamlScalarNode("otlp")])
            .Children[new YamlScalarNode("protocols")])
            .Children[new YamlScalarNode("http")])
            .Children[new YamlScalarNode("endpoint")]).Value!;
        Assert.StartsWith("127.0.0.1:", remoteOtlpEndpoint, StringComparison.Ordinal);

        var journalUnits = (YamlSequenceNode)((YamlMappingNode)remoteReceivers.Children[new YamlScalarNode("journald/runtime_agent")])
            .Children[new YamlScalarNode("units")];
        var unit = Assert.Single(journalUnits.Children);
        Assert.Equal("selfrun-runtime-agent.service", ((YamlScalarNode)unit).Value);

        var remoteExporters = (YamlMappingNode)remoteYaml.Children[new YamlScalarNode("exporters")];
        var tunnelEndpoint = ((YamlScalarNode)((YamlMappingNode)remoteExporters.Children[new YamlScalarNode("otlphttp/operator_tunnel")])
            .Children[new YamlScalarNode("endpoint")]).Value!;
        Assert.StartsWith("http://127.0.0.1:", tunnelEndpoint, StringComparison.Ordinal);

        // 2. Central collector: static operator tenant header, Selfrun.RuntimeAgent allowlisted, body cleared.
        var centralCollectorPath = Path.Combine(repoRoot, "observability", "profiling", "collector.yml");
        var centralText = File.ReadAllText(centralCollectorPath);
        Assert.Contains("X-Scope-OrgID: operator", centralText, StringComparison.Ordinal);
        Assert.Contains("Selfrun.RuntimeAgent", centralText, StringComparison.Ordinal);
        Assert.Contains("Selfrun.OperatorDiagnostics", centralText, StringComparison.Ordinal);
        Assert.Contains("set(body, \"\")", centralText, StringComparison.Ordinal);

        // 3. Example agent profiling JSON: disabled by default, valid loopback config when toggled on.
        var exampleAgentPath = Path.Combine(repoRoot, "observability", "profiling", "runtime-agent.profiling.example.json");
        var exampleConfig = AgentConfig.Load(exampleAgentPath);
        Assert.NotNull(exampleConfig.Profiling);
        Assert.False(exampleConfig.Profiling!.Enabled);
        (exampleConfig.Profiling with { Enabled = true }).Validate();

        // 4. Provisioned operator Grafana dashboard: valid JSON, all 7 required #119 panels, operator datasources only.
        var providerPath = Path.Combine(repoRoot, "observability", "profiling", "grafana-provisioning", "dashboards", "provider.yaml");
        var providerYaml = LoadYaml(providerPath);
        Assert.True(providerYaml.Children.ContainsKey(new YamlScalarNode("providers")));

        var dashboardPath = Path.Combine(repoRoot, "observability", "profiling", "grafana-provisioning", "dashboards", "selfrun-operator-profiling.json");
        using var dashboard = JsonDocument.Parse(File.ReadAllText(dashboardPath));
        var panels = dashboard.RootElement.GetProperty("panels").EnumerateArray().ToArray();
        var titles = panels.Select(p => p.GetProperty("title").GetString()!).ToArray();

        Assert.Contains("CPU seconds per idle host-hour", titles);
        Assert.Contains("Subprocesses per cycle", titles);
        Assert.Contains("DB commands per batch", titles);
        Assert.Contains("Allocated bytes per accepted MiB", titles);
        Assert.Contains("Operation p95 latency", titles);
        Assert.Contains("Exporter drops", titles);
        Assert.Contains("Queue lag", titles);

        var allowedDatasources = new HashSet<string>(StringComparer.Ordinal)
        {
            "selfrun-operator-prometheus",
            "selfrun-operator-loki",
            "selfrun-operator-tempo"
        };
        foreach (var panel in panels)
        {
            var uid = panel.GetProperty("datasource").GetProperty("uid").GetString()!;
            Assert.Contains(uid, allowedDatasources);
        }
    }

    internal static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Selfrun.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root containing Selfrun.slnx.");
    }

    private static YamlMappingNode LoadYaml(string path)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(path);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static async Task<(string OrgId, string AppId, string RuntimeToken, string ApiKeyToken)> SeedTenantAsync(
        ProfilingRuntimeApiFactory factory, string orgId, string appId, DateTimeOffset now)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SelfrunDbContext>();
        var apiKeyTokenService = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var runtimeTokenService = scope.ServiceProvider.GetRequiredService<IRuntimeTokenService>();

        var apiKeyToken = apiKeyTokenService.Generate();
        var runtimeToken = runtimeTokenService.Generate();

        var organization = new Organization
        {
            Id = orgId,
            Name = orgId,
            Slug = orgId.Replace('_', '-'),
            CreatedAt = now,
            UpdatedAt = now
        };
        var app = new SelfrunApp
        {
            Id = appId,
            OrganizationId = organization.Id,
            Name = appId,
            Slug = appId.Replace('_', '-'),
            Plan = AppPlan.P0,
            Database = DatabaseMode.Sqlite,
            CreatedAt = now,
            UpdatedAt = now
        };
        var deployment = new Deployment
        {
            Id = $"dep_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            AppId = app.Id,
            Database = DatabaseMode.Sqlite,
            Status = DeploymentStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now
        };
        deployment.Services.Add(new DeploymentService
        {
            Id = $"svc_{Guid.NewGuid():N}",
            DeploymentId = deployment.Id,
            Name = "api",
            Image = "ghcr.io/example/api:latest",
            Port = 8080,
            Public = true,
            Path = "/",
            HealthPath = "/health"
        });
        var server = new RuntimeServer
        {
            Id = $"rtsrv_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            AppId = app.Id,
            Provider = "hetzner",
            ProviderServerId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Organizations.Add(organization);
        db.Apps.Add(app);
        db.Deployments.Add(deployment);
        db.RuntimeServers.Add(server);
        db.ApiKeys.Add(new ApiKey
        {
            Id = $"key_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            Name = "runtime-read",
            KeyPrefix = apiKeyToken.Prefix,
            KeyHash = apiKeyTokenService.Hash(apiKeyToken.Token),
            ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.DeploymentsRead }),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.RuntimeTokens.Add(new RuntimeToken
        {
            Id = $"rttok_{Guid.NewGuid():N}",
            RuntimeServerId = server.Id,
            TokenPrefix = runtimeToken.Prefix,
            TokenHash = runtimeTokenService.Hash(runtimeToken.Token),
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return (organization.Id, app.Id, runtimeToken.Token, apiKeyToken.Token);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class NoopDns : ICustomDomainDnsResolver
    {
        public Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class ProfilingRuntimeApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Firebase:ProjectId"] = "selfrun-test",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Selfrun"] = "Data Source=:memory:"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICustomDomainDnsResolver>();
                services.AddSingleton<ICustomDomainDnsResolver>(new NoopDns());
                services.UseSharedSqliteConnection(connection);
            });
        }
    }
}
