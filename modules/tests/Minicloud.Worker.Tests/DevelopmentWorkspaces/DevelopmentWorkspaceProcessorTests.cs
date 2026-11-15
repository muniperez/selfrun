using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.DevelopmentWorkspaces;
using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class DevelopmentWorkspaceProcessorTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly FrozenClock clock = new();
    private readonly FakeCloud cloud = new();
    private readonly FakeSecrets secrets = new();
    private readonly FakeRuntime runtime = new();
    private readonly FakeTunnels tunnels = new();
    private readonly DevelopmentWorkspaceWorkerOptions workerOptions = new()
    {
        ApiBaseUrl = "https://control.example.test", ManagementAddresses = ["198.51.100.1/32"],
        RelayBindAddress = "127.0.0.1", GatewayDrainSeconds = 75
    };
    private MinicloudDbContext db = default!;
    private DevelopmentWorkspaceService lifecycle = default!;
    private ServiceProvider services = default!;
    private WebApplication t3 = default!;
    private DevelopmentWorkspaceOptions policy = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = Context();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Synthetic",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic", CredentialGeneration = "fixture" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account",
            Name = "Standard", NormalizedName = "standard", Region = "nbg1", Sku = "standard-test" });
        await db.SaveChangesAsync();
        policy = new()
        {
            Enabled = true, MaxActiveVms = 5, MaxVmsPerWorkspace = 3, MaxVmsPerOrganization = 5,
            MaxEstimatedMonthlySpend = 10000, MaxEstimatedMonthlySpendPerOrganization = 10000,
            VolumeMonthlyRatePerGb = .1m, PricingUpdatedAt = clock.GetUtcNow().AddMinutes(-1),
            Profiles = new()
            {
                ["standard"] = new() { CpuCount = 4, MemoryGiB = 8, ComputeHourlyRate = .1m,
                    AllowedPlanIds = ["plan"], AllowedRegions = ["nbg1"], AllowedSkus = ["standard-test"] }
            },
            Images = new()
            {
                ["image"] = new() { Enabled = true, RuntimeQualified = true, QualifiedAt = clock.GetUtcNow().AddMinutes(-1),
                    ProviderAccountId = "account", AllowedRegions = ["nbg1"], ProviderImageId = "image-qualified",
                    ManifestSha256 = new('a', 64) }
            },
            Repositories = [new() { OrganizationId = "org", Repository = "https://git.example.test/team/project.git",
                AllowedBaseRefs = ["refs/heads/main"], AllowPush = true }]
        };
        var participant = MaintenanceParticipant.CreateLocal("worker", clock);
        lifecycle = new(db, new IdGenerator(), clock, Options.Create(policy), participant);
        services = new ServiceCollection()
            .AddSingleton(db).AddSingleton(lifecycle)
            .AddSingleton<ProviderAccountClientFactory>(new FakeFactory(cloud))
            .AddSingleton<ISecretStore>(secrets).AddSingleton<IWorkspaceRuntimeTransport>(runtime)
            .AddSingleton<IWorkspaceTunnelManager>(tunnels).AddSingleton<IIdGenerator, IdGenerator>()
            .AddSingleton<TimeProvider>(clock).AddSingleton(participant)
            .AddSingleton<IOptions<DevelopmentWorkspaceWorkerOptions>>(Options.Create(workerOptions))
            .AddSingleton<IOptions<DevelopmentWorkspaceOptions>>(Options.Create(policy))
            .AddSingleton<IOptions<DeploymentPipelineOptions>>(Options.Create(new DeploymentPipelineOptions
                { SshPublicKey = "ssh-ed25519 AAAAsynthetic-management-key" }))
            .AddSingleton(new WorkspaceGitAccessService(db, secrets, null!, clock, Options.Create(policy)))
            .BuildServiceProvider();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        t3 = builder.Build();
        t3.UseWebSockets();
        t3.MapGet("/.well-known/t3/environment", () => Results.Json(new
            { serverVersion = "0.1.9", orchestrationProtocolVersion = 2, environmentId = "fixture-environment" }));
        t3.MapGet("/api/auth/session", () => Results.Json(new
            { authenticated = true, expiresAt = clock.GetUtcNow().AddMinutes(30).ToString("O") }));
        t3.MapPost("/api/auth/websocket-ticket", () => Results.Json(new { ticket = "fixture-ticket" }));
        t3.MapGet("/api/orchestration/shell", () => Results.Json(new
            { schemaVersion = 1, snapshotSequence = 7, threads = Array.Empty<object>(), archivedThreads = Array.Empty<object>() }));
        t3.MapGet("/", () => Results.Ok());
        t3.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || context.Request.Query["wsTicket"] != "fixture-ticket")
            { context.Response.StatusCode = 401; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            tunnels.WebSocketConnections++;
            var message = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[128]), context.RequestAborted);
            if (message.MessageType == WebSocketMessageType.Close)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
        });
        await t3.StartAsync();
        var address = t3.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        tunnels.Port = new Uri(address).Port;
    }

    private MinicloudDbContext Context() => new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
    private DevelopmentWorkspaceProcessor Processor() => ActivatorUtilities.CreateInstance<DevelopmentWorkspaceProcessor>(services);

    [Fact]
    public async Task LostAllocationResponsePersistsIntentAndReconcilesExactLabelsWithoutSecondCreate()
    {
        var (workspace, vm, operation) = await Seed("allocate");
        cloud.LoseCreateResponse = true;
        cloud.BeforeCreate = async () =>
        {
            await using var verify = Context();
            var durable = await verify.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == operation.Id);
            Assert.True(JsonDocument.Parse(durable.ProviderEvidenceJson!).RootElement.GetProperty("allocationAttempted").GetBoolean());
            Assert.Equal("allocate", durable.Step);
            Assert.NotNull(durable.LeaseToken);
        };
        await Hop();
        Assert.Equal(1, cloud.Creates);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Null((await CurrentVm(vm.Id)).ProviderServerId);
        Assert.Equal("pending", (await CurrentOperation(operation.Id)).Status);
        await Hop("server-ready");
        var recovered = await CurrentVm(vm.Id);
        Assert.Equal("server-owned", recovered.ProviderServerId);
        Assert.Equal(1, cloud.Creates);
        Assert.Equal(2, cloud.DiscoveryLabels.Count);
        foreach (var labels in cloud.DiscoveryLabels)
        {
            Assert.Equal(workspace.OrganizationId, labels["organization"]);
            Assert.Equal(workspace.Id, labels["workspace"]);
            Assert.Equal(vm.Id, labels["vm"]);
            Assert.Equal("1", labels["generation"]);
            Assert.Equal(operation.Id, labels["operation"]);
            Assert.Equal("primary", labels["kind"]);
        }
        Assert.Equal(cloud.DiscoveryLabels[0].OrderBy(x => x.Key), cloud.DiscoveryLabels[1].OrderBy(x => x.Key));
    }

    [Fact]
    public async Task AmbiguousAllocationNeverAdoptsAnotherWorkspaceOrIssuesSecondCreate()
    {
        var (_, vm, operation) = await Seed("allocate");
        cloud.LoseCreateResponse = true;
        await Hop();
        var created = cloud.Servers["server-owned"];
        cloud.Servers["server-owned"] = created with { Labels = new Dictionary<string, string>(created.Labels) { ["workspace"] = "foreign" } };
        await Hop();
        Assert.Equal(1, cloud.Creates);
        Assert.Null((await CurrentVm(vm.Id)).ProviderServerId);
        Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        Assert.Equal("allocate", (await CurrentOperation(operation.Id)).Step);
        Assert.Equal("blocked", (await CurrentVm(vm.Id)).Phase);
        Assert.Equal(0, cloud.Deletes);
    }

    [Theory]
    [InlineData(true, "IdleCandidate")]
    [InlineData(false, "Busy")]
    [InlineData(false, null)]
    public async Task ActualRuntimeTasksOrUnknownWorkInventoryKeepRecoveryRouteAndCompute(bool taskRunning, string? inventory)
    {
        var (_, vm, _) = await SeedStop("pending");
        runtime.TaskRunning = taskRunning; runtime.Inventory = inventory;
        await Hop("wait-for-tasks");
        var current = await CurrentVm(vm.Id);
        Assert.True(current.GatewayReady);
        Assert.True(current.ComputeReserved);
        Assert.Equal("draining", current.Phase);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Null((await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.True(tunnels.WebSocketConnections > 0);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
        Assert.DoesNotContain("quiesce", runtime.Actions);
    }

    [Fact]
    public async Task DirtyEphemeralGitStateKeepsGenerationRecoverableWithoutAnyVolumeOperation()
    {
        var (_, vm, _) = await SeedStop("pending", ephemeral: true);
        runtime.GitClean = false;
        await Hop("wait-for-save");
        var current = await CurrentVm(vm.Id);
        Assert.Equal("awaiting_git_save", current.Phase);
        Assert.Equal("workspace_git_save_required", current.ErrorCode);
        Assert.True(current.GatewayReady);
        Assert.True(current.ComputeReserved);
        Assert.Null(current.QuiescedGeneration);
        Assert.Null((await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Null((await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Contains("git-check", runtime.Actions);
        Assert.DoesNotContain("git-save", runtime.Actions);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal(0, cloud.Detaches);
        Assert.Equal(0, cloud.VolumeCreates);
        Assert.Equal(0, cloud.Attaches);
    }

    [Fact]
    public async Task ConfirmedPrimaryReceiptDetachesAndDeletesOnlyOwnedComputeThenReleasesReservation()
    {
        var (workspace, vm, operation) = await SeedStop("quiesce");
        runtime.Phase = "Frozen";
        operation = await CurrentOperation(operation.Id);
        var unrelated = cloud.Servers["server-owned"] with { Id = "ordinary-app", Labels = new Dictionary<string, string>() };
        cloud.Servers.Add(unrelated.Id, unrelated);
        foreach (var expected in new[] { "detach", "detached", "delete-intent", "delete", "delete-confirm", "delete", "cleanup" })
        {
            await Hop(expected);
            Assert.True((await CurrentVm(vm.Id)).ComputeReserved);
        }
        var quiesced = await CurrentVm(vm.Id);
        Assert.Equal(1, quiesced.QuiescedGeneration);
        Assert.Equal(operation.Id, quiesced.QuiescedOperationId);
        Assert.Contains("filesystem-retained", quiesced.QuiescenceReceipt);
        await Hop("stopped");
        var stopped = await CurrentVm(vm.Id);
        Assert.Equal("stopped", stopped.Phase);
        Assert.False(stopped.ComputeReserved);
        Assert.Null(stopped.ProviderServerId);
        Assert.Null(stopped.ProviderIpId);
        Assert.Null(stopped.ProviderFirewallId);
        Assert.Equal("completed", (await CurrentOperation(operation.Id)).Status);
        var retained = await db.DevelopmentWorkspaces.AsNoTracking().SingleAsync(x => x.Id == workspace.Id);
        Assert.Equal("volume-retained", retained.ProviderVolumeId);
        Assert.Equal("filesystem-retained", retained.VolumeFilesystemUuid);
        Assert.Equal("retained", retained.VolumeStatus);
        Assert.Null(retained.VolumeAttachedVmId);
        Assert.Equal("server-owned", Assert.Single(cloud.DeletedServers));
        Assert.Equal("volume-retained", Assert.Single(cloud.DetachedVolumes));
        Assert.Equal("ip-owned", Assert.Single(cloud.DeletedIps));
        Assert.Equal("firewall-owned", Assert.Single(cloud.DeletedFirewalls));
        Assert.True(cloud.Servers.ContainsKey("ordinary-app"));
        Assert.NotNull(cloud.Volume);
        Assert.Null(cloud.Volume!.ServerId);
        Assert.Equal(0, cloud.VolumeCreates);
        Assert.Contains((vm.Id, 1L), tunnels.Removed);
    }

    [Theory]
    [InlineData("filesystem")]
    [InlineData("generation")]
    public async Task InvalidPrimaryQuiescenceReceiptCannotDetachOrDeleteCompute(string mismatch)
    {
        var (_, vm, operation) = await SeedStop("quiesce");
        runtime.Phase = "Frozen";
        if (mismatch == "filesystem") runtime.ReceiptFilesystemUuid = "another-volume";
        else runtime.ReceiptGenerationDelta = 1;
        await Hop("quiesce");
        var current = await CurrentVm(vm.Id);
        Assert.Equal("blocked", current.Phase);
        Assert.True(current.ComputeReserved);
        Assert.Null(current.QuiescedGeneration);
        Assert.Equal("server-owned", current.ProviderServerId);
        Assert.Equal("pending", (await CurrentOperation(operation.Id)).Status);
        Assert.Equal(0, cloud.Detaches);
        Assert.Equal(0, cloud.Deletes);
        Assert.Equal("server-owned", cloud.Volume!.ServerId);
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("generation")]
    public async Task StaleAllocationCompletionCannotMutateVmAfterLeaseOrGenerationChanges(string stale)
    {
        var (_, vm, operation) = await Seed("allocate");
        cloud.BeforeCreate = async () =>
        {
            if (stale == "lease") clock.Advance(TimeSpan.FromSeconds(121));
            else
            {
                await using var changed = Context();
                var replacement = await changed.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
                replacement.Generation++; replacement.Revision++;
                await changed.SaveChangesAsync();
            }
        };
        await Hop();
        var current = await CurrentVm(vm.Id);
        Assert.Equal(stale == "generation" ? 2 : 1, current.Generation);
        Assert.Null(current.ProviderServerId);
        Assert.True(current.ComputeReserved);
        Assert.Equal("allocate", (await CurrentOperation(operation.Id)).Step);
        Assert.Single(cloud.Servers);
        Assert.Equal(0, cloud.Deletes);
    }

    [Fact]
    public async Task RecoveryRefreshPublishesOnlyExplicitHealthyPreviewWithoutT3Credential()
    {
        var (_, vm, _) = await SeedStop("pending");
        runtime.TaskRunning = true;
        workerOptions.PreviewServices["web"] = new() { Port = 8080 };
        await Hop("wait-for-tasks");
        var preview = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleAsync(x => x.Service == "web");
        Assert.Equal(vm.Id, preview.VmId);
        Assert.Equal(vm.Generation, preview.Generation);
        Assert.Null(preview.T3CredentialReference);
        Assert.Null(preview.RevokedAt);
        Assert.Contains((8080, "web"), tunnels.Requests);
        Assert.Equal(0, cloud.Deletes);
    }

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation)> Seed(string step, bool ephemeral = false)
    {
        var workspace = await lifecycle.CreateAsync(new("org", "Worker fixture", "image", "standard", "plan", 20, "create"), "actor", default);
        workspace = ephemeral
            ? await lifecycle.LaunchEphemeralAsync(workspace.Id, "org", new("image", "standard", "plan",
                "https://git.example.test/team/project.git", "refs/heads/main", new('b', 40), true, true, "ephemeral"), "actor", default)
            : await lifecycle.StartAsync(workspace.Id, "org", default);
        var vm = workspace.Vms.Single(x => x.Kind == (ephemeral ? "ephemeral" : "primary"));
        var operation = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        operation.Step = step;
        var bootstrap = new WorkspaceBootstrapSecret("synthetic-private-key", "ssh-rsa synthetic-host-key",
            "SHA256:" + new string('a', 43), "synthetic-enrollment-token");
        vm.SshBootstrapSecretRef = secrets.Add("bootstrap", JsonSerializer.Serialize(bootstrap, Json));
        vm.RuntimeCredentialRef = secrets.Add("t3", new string('t', 48));
        vm.RuntimeTokenExpiresAt = clock.GetUtcNow().AddMinutes(30);
        vm.SshHostKeyGeneration = 1; vm.SshHostKeyFingerprint = bootstrap.HostFingerprint;
        vm.ProviderFirewallId = "firewall-owned";
        vm.LastHeartbeatAt = clock.GetUtcNow(); vm.LastActivityAt = clock.GetUtcNow();
        if (!ephemeral)
        {
            workspace.ProviderVolumeId = "volume-retained"; workspace.VolumeFilesystemUuid = "filesystem-retained";
            workspace.VolumeStatus = "mounted"; workspace.VolumeAttachedVmId = vm.Id;
            cloud.Volume = new("volume-retained", "retained", 20, "nbg1", "server-owned", "ext4", "/dev/disk/by-id/owned",
                VolumeLabels(workspace));
        }
        await db.SaveChangesAsync();
        cloud.NextLabels = ComputeLabels(workspace, vm);
        return (workspace, vm, operation);
    }

    private async Task<(DevelopmentWorkspace Workspace, DevelopmentWorkspaceVm Vm, DevelopmentWorkspaceOperation Operation)> SeedStop(string step, bool ephemeral = false)
    {
        var (workspace, vm, start) = await Seed("ready", ephemeral);
        vm.Phase = "ready"; vm.ProviderServerId = "server-owned"; vm.ProviderIpId = "ip-owned";
        vm.IpAddress = "198.51.100.10"; vm.GatewayReady = true; vm.EnrolledAt = clock.GetUtcNow();
        start.Status = "completed";
        cloud.Servers["server-owned"] = cloud.Server(cloud.NextLabels);
        await db.SaveChangesAsync();
        workspace = await lifecycle.StopVmAsync(workspace.Id, "org", vm.Id, 1, default);
        vm = workspace.Vms.Single(x => x.Id == vm.Id);
        var stop = await db.DevelopmentWorkspaceOperations.SingleAsync(x => x.Id == vm.OperationId);
        stop.Step = step;
        stop.ProviderEvidenceJson = JsonSerializer.Serialize(new
            { gatewayRevokedAt = clock.GetUtcNow().AddSeconds(-76), t3SnapshotSequence = 7 }, Json);
        db.DevelopmentWorkspaceGatewayRoutes.Add(new() { Id = "t3-route", OrganizationId = "org", WorkspaceId = workspace.Id,
            VmId = vm.Id, Generation = 1, LoopbackPort = tunnels.Port, RegisteredAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddMinutes(30), T3CredentialReference = vm.RuntimeCredentialRef });
        db.DevelopmentWorkspaceGatewaySessions.Add(new() { Id = "t3-session", OrganizationId = "org", WorkspaceId = workspace.Id,
            VmId = vm.Id, Generation = 1, LaunchDigest = "synthetic-launch-digest", SessionDigest = "synthetic-session-digest",
            LaunchExpiresAt = clock.GetUtcNow().AddMinutes(1), SessionExpiresAt = clock.GetUtcNow().AddMinutes(30) });
        await db.SaveChangesAsync();
        return (workspace, vm, stop);
    }

    private async Task Hop(string? expectedStep = null)
    {
        Assert.True(await Processor().ProcessNextAsync(default));
        if (expectedStep is not null)
        {
            var operation = await db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Kind == "stop" || x.Status != "completed");
            Assert.True(operation.Step == expectedStep, $"Expected {expectedStep}; got {operation.Step}/{operation.Status}/{operation.ErrorCode}.");
        }
    }

    private Task<DevelopmentWorkspaceVm> CurrentVm(string id) =>
        db.DevelopmentWorkspaceVms.AsNoTracking().SingleAsync(x => x.Id == id);
    private Task<DevelopmentWorkspaceOperation> CurrentOperation(string id) =>
        db.DevelopmentWorkspaceOperations.AsNoTracking().SingleAsync(x => x.Id == id);
    private static Dictionary<string, string> VolumeLabels(DevelopmentWorkspace workspace) => new()
        { ["managed-by"] = "minicloud-workspaces", ["organization"] = workspace.OrganizationId, ["workspace"] = workspace.Id, ["kind"] = "primary" };
    private static Dictionary<string, string> ComputeLabels(DevelopmentWorkspace workspace, DevelopmentWorkspaceVm vm) =>
        new(VolumeLabels(workspace)) { ["kind"] = vm.Kind, ["vm"] = vm.Id, ["generation"] = "1", ["operation"] = vm.AllocationOperationId! };

    public async Task DisposeAsync()
    {
        if (t3 is not null) await t3.DisposeAsync();
        if (services is not null) await services.DisposeAsync();
        else if (db is not null) await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeTunnels : IWorkspaceTunnelManager
    {
        public int Port { get; set; }
        public int WebSocketConnections { get; set; }
        public List<(int, string)> Requests { get; } = [];
        public List<(string, long)> Removed { get; } = [];
        public Task<int> EnsureAsync(WorkspaceRuntimeTarget target, CancellationToken ct, int remotePort = 3773, string service = "t3")
        { Requests.Add((remotePort, service)); return Task.FromResult(Port); }
        public Task RemoveAsync(string vmId, long generation, CancellationToken ct)
        { Removed.Add((vmId, generation)); return Task.CompletedTask; }
    }

    private sealed class FakeRuntime : IWorkspaceRuntimeTransport
    {
        public bool TaskRunning { get; set; }
        public string? Inventory { get; set; } = "IdleCandidate";
        public bool GitClean { get; set; } = true;
        public string Phase { get; set; } = "Ready";
        public string ReceiptFilesystemUuid { get; set; } = "filesystem-retained";
        public int ReceiptGenerationDelta { get; set; }
        public List<string> Actions { get; } = [];
        public Task<JsonElement> CallAsync(WorkspaceRuntimeTarget target, string operationId, string action, object? arguments, CancellationToken ct)
        {
            Actions.Add(action);
            if (action == "status")
            {
                var result = new Dictionary<string, object?>
                {
                    ["vmId"] = target.VmId, ["generation"] = target.Generation, ["kind"] = target.Kind, ["phase"] = Phase,
                    ["tasks"] = TaskRunning ? new Dictionary<string, object> { ["runtime-task"] = new { state = "Running" } } : new()
                };
                if (Inventory is not null) result["workInventory"] = new { state = Inventory };
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result }, Json));
            }
            if (action == "git-check" && !GitClean)
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = false, state = "AwaitingGitSave" }, Json));
            if (action == "quiesce")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new
                {
                    schemaVersion = 1, vmId = target.VmId, generation = target.Generation + ReceiptGenerationDelta, kind = target.Kind,
                    operationId, receiptId = "receipt-owned", type = "PrimaryQuiesced", unmounted = true,
                    filesystemUuid = ReceiptFilesystemUuid
                } }, Json));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, result = new { receiptId = "receipt-owned" } }, Json));
        }
        public Task<JsonElement> VolumeProbeAsync(WorkspaceRuntimeTarget target, string volumeId, CancellationToken ct) =>
            throw new InvalidOperationException("Volume probe was not expected in this stage.");
        public Task<JsonElement> BootstrapAsync(WorkspaceRuntimeTarget target, object config, object enrollment, CancellationToken ct) =>
            throw new InvalidOperationException("Bootstrap was not expected in this stage.");
        public Task<string> IssueT3SessionAsync(WorkspaceRuntimeTarget target, string operationId, int ttlSeconds, CancellationToken ct) =>
            Task.FromResult(new string('t', 48));
        public Task StageRepositoryCredentialAsync(WorkspaceRuntimeTarget target, JsonElement credential, CancellationToken ct) =>
            throw new InvalidOperationException("Repository authority must not be inherited in this fixture.");
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = [];
        public string Add(string name, string value)
        {
            values[name] = value;
            return JsonSerializer.Serialize(new StoredSecretReference("openbao", name, "1"), Json);
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => Task.FromResult(values[reference.Path]);
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            var path = OpenBaoSecretStore.IntegrationPathFor("tests", request.OrganizationId, request.IntegrationId!, request.CredentialGeneration!);
            values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) { values.Remove(reference.Path); return Task.CompletedTask; }
    }

    private sealed class FakeFactory(FakeCloud cloud) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, NullLoggerFactory.Instance)
    {
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            Assert.Equal("org", organizationId); Assert.Equal("account", providerAccountId);
            return Task.FromResult<IVpsProviderClient>(cloud);
        }
    }

    private sealed class FakeCloud : IVpsProviderClient, IDevelopmentWorkspaceProvider
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Dictionary<string, WorkspaceCloudServer> Servers { get; } = [];
        public WorkspaceCloudVolume? Volume { get; set; }
        public Dictionary<string, string> NextLabels { get; set; } = [];
        public List<Dictionary<string, string>> DiscoveryLabels { get; } = [];
        public List<string> DeletedServers { get; } = [];
        public List<string> DetachedVolumes { get; } = [];
        public List<string> DeletedIps { get; } = [];
        public List<string> DeletedFirewalls { get; } = [];
        public int Creates { get; private set; }
        public int Deletes => DeletedServers.Count;
        public int Detaches => DetachedVolumes.Count;
        public int VolumeCreates { get; private set; }
        public int Attaches { get; private set; }
        public bool LoseCreateResponse { get; set; }
        public Func<Task>? BeforeCreate { get; set; }
        public WorkspaceCloudServer Server(IReadOnlyDictionary<string, string> labels) =>
            new("server-owned", "owned", "running", "nbg1", "standard-test", "image-qualified",
                "198.51.100.10", "ip-owned", new Dictionary<string, string>(labels), ["firewall-owned"], true);
        public Task<IReadOnlyList<WorkspaceCloudServer>> FindWorkspaceServersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            DiscoveryLabels.Add(new(labels));
            return Task.FromResult<IReadOnlyList<WorkspaceCloudServer>>(Servers.Values.Where(x =>
                labels.All(label => x.Labels.TryGetValue(label.Key, out var value) && value == label.Value)).ToArray());
        }
        public Task<WorkspaceCloudServer?> GetWorkspaceServerAsync(string id, CancellationToken ct) => Task.FromResult(Servers.GetValueOrDefault(id));
        public async Task<WorkspaceCloudAllocation> CreateWorkspaceServerAsync(WorkspaceCloudCreate request, CancellationToken ct)
        {
            Creates++;
            if (BeforeCreate is not null) await BeforeCreate();
            var server = Server(request.Labels); Servers[server.Id] = server;
            if (LoseCreateResponse) { LoseCreateResponse = false; throw new HttpRequestException("Synthetic response lost AFTER allocation."); }
            return new(server, new("servers", "created", "running"));
        }
        public Task<WorkspaceCloudAction?> DeleteWorkspaceServerAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Assert.Equal("server-owned", id);
            Assert.All(labels, pair => Assert.Equal(pair.Value, Servers[id].Labels[pair.Key]));
            DeletedServers.Add(id); Servers.Remove(id);
            return Task.FromResult<WorkspaceCloudAction?>(new("servers", "deleted", "running"));
        }
        public Task SecureWorkspacePrimaryIpAsync(WorkspaceCloudServer server, IReadOnlyDictionary<string, string> labels, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteWorkspacePrimaryIpAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { DeletedIps.Add(id); return Task.CompletedTask; }
        public Task<WorkspaceCloudImage?> GetWorkspaceImageAsync(string id, CancellationToken ct) =>
            Task.FromResult<WorkspaceCloudImage?>(new(id, "available", "snapshot", "x86", "ubuntu", new Dictionary<string, string>()));
        public Task<WorkspaceCloudAction> GetWorkspaceActionAsync(string resource, string id, CancellationToken ct) =>
            Task.FromResult(new WorkspaceCloudAction(resource, id, "success"));
        public Task<IReadOnlyList<WorkspaceCloudVolume>> FindWorkspaceVolumesAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkspaceCloudVolume>>(Volume is null ? [] : [Volume]);
        public Task<WorkspaceCloudVolume?> GetWorkspaceVolumeAsync(string id, CancellationToken ct) => Task.FromResult(Volume?.Id == id ? Volume : null);
        public Task<WorkspaceCloudVolumeAllocation> CreateWorkspaceVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { VolumeCreates++; throw new InvalidOperationException("Retained volume must not be replaced."); }
        public Task<WorkspaceCloudAction?> AttachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { Attaches++; throw new InvalidOperationException("Stop must not attach storage."); }
        public Task<WorkspaceCloudAction?> DetachWorkspaceVolumeAsync(string id, string serverId, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Assert.Equal(Volume!.Id, id); Assert.Equal(Volume.ServerId, serverId);
            Assert.All(labels, pair => Assert.Equal(pair.Value, Volume.Labels[pair.Key]));
            DetachedVolumes.Add(id); Volume = Volume with { ServerId = null };
            return Task.FromResult<WorkspaceCloudAction?>(new("volumes", "detached", "running"));
        }
        public Task<IReadOnlyList<WorkspaceCloudFirewall>> FindWorkspaceFirewallsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            throw new InvalidOperationException("Firewall lookup was not expected in this stage.");
        public Task<WorkspaceCloudFirewall> CreateWorkspaceFirewallAsync(string name, IReadOnlyList<string> managementAddresses, IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
            throw new InvalidOperationException("Firewall creation was not expected in this stage.");
        public Task DeleteWorkspaceFirewallAsync(string id, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        { DeletedFirewalls.Add(id); return Task.CompletedTask; }
        public Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Hosted app path is forbidden.");
    }
}
