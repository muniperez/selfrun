using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Npgsql;
using WorkspaceReconciliation;

public sealed class RecoveryTests
{
    private static readonly RecoveryScope Scope = new("org_test", "workspace_test", "account_test",
        "cred_test", "hel1", "vm_test", 2, "allocation_test", "stop_test", "recovery_test", "170", "240", 40,
        "310", "420", "510", "00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff", "synthetic-approval");
    private static readonly string AccountPath = OpenBaoSecretStore.ProviderPathFor(
        "synthetic", Scope.OrganizationId, Scope.ProviderAccountId, Scope.CredentialGeneration);
    private static string SecretPath(string name) =>
        OpenBaoSecretStore.IntegrationPathFor("synthetic", Scope.OrganizationId, "workspace-vm-" + Scope.VmId, "2-" + name);

    [Fact]
    public async Task CheckThenApplyAndExactReplayPreserveMigrationHistoryAndNeighbors()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Open();
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await fixture.ExtractedMigrationAsync(expectRetired: false);
        Assert.StartsWith("Check passed", await fixture.RunAsync(apply: false));
        await fixture.AssertUnchangedAsync();
        Assert.Empty(fixture.Secrets.Deleted);

        Assert.StartsWith("Reconciled", await fixture.RunAsync());
        db.ChangeTracker.Clear();
        var workspace = await db.DevelopmentWorkspaces.SingleAsync(x => x.Id == Scope.WorkspaceId);
        Assert.Equal("deleted", workspace.VolumeStatus);
        Assert.Null(workspace.ProviderVolumeId);
        Assert.Null(workspace.VolumeAttachedVmId);
        Assert.Null(workspace.VolumeFilesystemUuid);
        Assert.Equal(0m, workspace.StorageMonthlyRate);
        var vm = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == Scope.VmId);
        Assert.Equal(("deleted", "stopped", false, false, false), (vm.Phase, vm.DesiredState, vm.ComputeReserved, vm.StartAfterStop, vm.GatewayReady));
        Assert.Null(vm.RuntimeCredentialRef);
        Assert.Null(vm.ProviderServerId);
        Assert.Null(vm.ProviderIpId);
        Assert.Null(vm.ProviderFirewallId);
        Assert.Null(vm.QuiescenceReceipt);
        Assert.Equal(Scope.RecoveryOperationId, vm.OperationId);
        Assert.Equal("superseded", (await db.DevelopmentWorkspaceOperations.FindAsync(Scope.StopOperationId))!.Status);
        var recorded = (await db.DevelopmentWorkspaceOperations.FindAsync(Scope.RecoveryOperationId))!;
        Assert.Equal("completed", recorded.Status);
        Assert.Equal(Scope.VolumeId, recorded.ProviderResourceId);
        Assert.Contains("synthetic-approval", recorded.ProviderEvidenceJson);
        Assert.Empty(await db.DevelopmentWorkspaceGatewayRoutes.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceGatewaySessions.ToListAsync());
        Assert.Empty(await db.DevelopmentWorkspaceTaskLeases.ToListAsync());
        Assert.Equal(1L, (await db.DevelopmentWorkspaces.FindAsync("neighbor"))!.Revision);
        Assert.Equal(new[] { SecretPath("bootstrap"), SecretPath("connect"), SecretPath("t3") },
            fixture.Secrets.Deleted.Order(StringComparer.Ordinal));

        var requests = fixture.Provider.Requests;
        Assert.StartsWith("Already reconciled", await fixture.RunAsync());
        Assert.Equal(requests, fixture.Provider.Requests);
        Assert.Equal(3, fixture.Secrets.Deleted.Count);
        await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync(scope: Scope with { VolumeId = "999" }));
        await fixture.ExtractedMigrationAsync(expectRetired: true);
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("wrong-project", "project witness")]
    [InlineData("wrong-fingerprint", "project witness")]
    [InlineData("servers/170", "servers absence")]
    [InlineData("volumes/240", "volumes absence")]
    [InlineData("primary_ips/310", "primary_ips absence")]
    [InlineData("firewalls/420", "firewalls absence")]
    [InlineData("firewalls-inventory", "inventory is not empty")]
    public async Task RefusesUnprovenAbsence(string scenario, string message)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Provider.Scenario = scenario;
        var error = await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync());
        Assert.Contains(message, error.Message);
        if (scenario == "wrong-project") Assert.Equal(1, fixture.Provider.Requests);
        Assert.Empty(fixture.Secrets.Deleted);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task RefusesScopeMismatchExpiredActiveTaskAndCurrentWorkerLeaseBeforeContactingProvider()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Open();
        await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync(scope: Scope with { CredentialGeneration = "other" }));
        await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync(scope: Scope with { Generation = 3 }));
        var lease = await db.DevelopmentWorkspaceTaskLeases.SingleAsync();
        lease.Status = "active";
        await db.SaveChangesAsync();
        Assert.Contains("task leases", (await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync())).Message);
        lease.Status = "released";
        var stop = (await db.DevelopmentWorkspaceOperations.FindAsync(Scope.StopOperationId))!;
        stop.Status = "running";
        stop.LeaseToken = "synthetic-lease";
        stop.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        Assert.Contains("worker lease", (await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync())).Message);
        Assert.Equal(0, fixture.Provider.Requests);
        await fixture.AssertUnchangedAsync();
    }

    [Fact]
    public async Task AdmissionAndOtherWritersAreFencedWhileAbsenceIsChecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeWitness = async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var applying = fixture.RunAsync(ct: timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Contains("admission is busy", (await Assert.ThrowsAsync<RecoveryRefused>(() => fixture.RunAsync())).Message);
            await using var writer = fixture.Open();
            await using var tx = await writer.Database.BeginTransactionAsync(timeout.Token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => writer.Database.ExecuteSqlRawAsync(
                "LOCK TABLE development_workspace_gateway_routes IN ROW EXCLUSIVE MODE NOWAIT", timeout.Token));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }
        finally { release.SetResult(); }
        Assert.StartsWith("Reconciled", await applying);
    }

    [Fact]
    public async Task SecretRevocationFailureRollsBackAndCanBeRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Secrets.FailOn = SecretPath("t3");
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => fixture.RunAsync());
        Assert.Contains(SecretPath("bootstrap"), fixture.Secrets.Deleted);
        await fixture.AssertUnchangedAsync();
        fixture.Secrets.FailOn = null;
        Assert.StartsWith("Reconciled", await fixture.RunAsync());
    }

    private sealed class Fixture(NpgsqlConnectionStringBuilder admin, NpgsqlConnectionStringBuilder target) : IAsyncDisposable
    {
        public readonly FakeProvider Provider = new();
        public readonly FakeSecrets Secrets = new();

        public static async Task<Fixture> CreateAsync()
        {
            var host = Environment.GetEnvironmentVariable("RECOVERY_TEST_PG_SOCKET");
            if (string.IsNullOrEmpty(host) || !Path.IsPathFullyQualified(host) || !Directory.Exists(host))
                throw new InvalidOperationException("Set RECOVERY_TEST_PG_SOCKET to a dedicated disposable PostgreSQL Unix socket directory.");
            var port = int.Parse(Environment.GetEnvironmentVariable("RECOVERY_TEST_PG_PORT") ?? "5432");
            var admin = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = "postgres", Username = "recovery_test", Pooling = false };
            var name = "recovery_fixture_" + Guid.NewGuid().ToString("N");
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {name} ENCODING 'UTF8' TEMPLATE template0", connection);
                await command.ExecuteNonQueryAsync();
            }
            var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name };
            var fixture = new Fixture(admin, target);
            try
            {
                await using var db = fixture.Open();
                await db.Database.MigrateAsync();
                await SeedAsync(db);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public MinicloudDbContext Open() => new(new DbContextOptionsBuilder<MinicloudDbContext>().UseNpgsql(target.ConnectionString).Options);

        public async Task<string> RunAsync(bool apply = true, RecoveryScope? scope = null, CancellationToken ct = default)
        {
            using var http = new HttpClient(Provider, disposeHandler: false);
            await using var db = Open();
            return await new Recovery(db, Secrets, "synthetic", new HetznerAbsence(http)).RunAsync(scope ?? Scope, apply, ct);
        }

        public async Task AssertUnchangedAsync()
        {
            await using var db = Open();
            var workspace = await db.DevelopmentWorkspaces.FindAsync(Scope.WorkspaceId);
            Assert.Equal(("mounted", Scope.VolumeId, 1L), (workspace!.VolumeStatus, workspace.ProviderVolumeId, workspace.Revision));
            Assert.Equal(Scope.StopOperationId, (await db.DevelopmentWorkspaceVms.FindAsync(Scope.VmId))!.OperationId);
            Assert.Null(await db.DevelopmentWorkspaceOperations.FindAsync(Scope.RecoveryOperationId));
            Assert.Single(await db.DevelopmentWorkspaceGatewayRoutes.ToListAsync());
            Assert.Single(await db.DevelopmentWorkspaceGatewaySessions.ToListAsync());
            Assert.Single(await db.DevelopmentWorkspaceTaskLeases.ToListAsync());
        }

        public async Task ExtractedMigrationAsync(bool expectRetired)
        {
            if (Environment.GetEnvironmentVariable("RECOVERY_TEST_EXTRACTED_API") is not { Length: > 0 } api) return;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "DOTNET_ROOT", "HOME" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.ArgumentList.Add(Path.GetFullPath(api));
            start.ArgumentList.Add("migrate");
            start.Environment["ConnectionStrings__Minicloud"] = target.ConnectionString;
            start.Environment["Database__Provider"] = "Postgres";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            var output = await stdout + await stderr;
            if (expectRetired) Assert.True(process.ExitCode == 0, output);
            else
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("workspaces that have not been retired", output);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE {target.Database}", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedAsync(MinicloudDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var s = Scope;
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Organizations.Add(new() { Id = s.OrganizationId, Name = "Synthetic", Slug = "synthetic" });
        db.ProviderAccounts.Add(new()
        {
            Id = s.ProviderAccountId, OrganizationId = s.OrganizationId, Name = "Synthetic",
            CredentialPath = AccountPath, CredentialGeneration = s.CredentialGeneration, CredentialVersion = "1"
        });
        db.ProviderPlans.Add(new()
        {
            Id = "plan_test", OrganizationId = s.OrganizationId, ProviderAccountId = s.ProviderAccountId, Region = s.Region,
            Name = "Synthetic", NormalizedName = "synthetic", Sku = "test-sku"
        });
        var runtimeRef = JsonSerializer.Serialize(new StoredSecretReference("openbao", SecretPath("t3"), "1"), RecoveryScope.Json);
        SeedWorkspace(s.WorkspaceId, s.VmId, true);
        SeedWorkspace("neighbor", "neighbor_vm", false);
        foreach (var (id, kind, status, step) in new[]
            { (s.AllocationOperationId, "start", "completed", "ready"), (s.StopOperationId, "stop", "pending", "quiesce") })
            db.DevelopmentWorkspaceOperations.Add(new()
            {
                Id = id, WorkspaceId = s.WorkspaceId, VmId = s.VmId, Generation = s.Generation, Kind = kind, Status = status, Step = step,
                PlanId = "plan_test", ProviderAccountId = s.ProviderAccountId, Region = s.Region, Sku = "test-sku",
                ImageId = "test", ProviderImage = "test", ImageManifestSha256 = "synthetic"
            });
        db.DevelopmentWorkspaceTaskLeases.Add(new()
        {
            Id = "lease_test", WorkspaceId = s.WorkspaceId, VmId = s.VmId, Generation = s.Generation, TaskId = "task_test",
            Status = "released", ExpiresAt = now.AddMinutes(-1)
        });
        db.DevelopmentWorkspaceGatewayRoutes.Add(new()
        {
            Id = "route_test", OrganizationId = s.OrganizationId, WorkspaceId = s.WorkspaceId, VmId = s.VmId, Generation = s.Generation,
            LoopbackPort = 12345, T3CredentialReference = runtimeRef, RegisteredAt = now, ExpiresAt = now, RevokedAt = now
        });
        db.DevelopmentWorkspaceGatewaySessions.Add(new()
        {
            Id = "session_test", OrganizationId = s.OrganizationId, WorkspaceId = s.WorkspaceId, VmId = s.VmId, Generation = s.Generation,
            LaunchDigest = "synthetic", LaunchExpiresAt = now, SessionExpiresAt = now, RevokedAt = now
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        void SeedWorkspace(string workspace, string vm, bool draining)
        {
            db.DevelopmentWorkspaces.Add(new()
            {
                Id = workspace, OrganizationId = s.OrganizationId, CreatedBy = "actor_test", Name = workspace,
                PrimaryVmId = vm, ProviderAccountId = s.ProviderAccountId, Region = s.Region, VolumeSizeGb = s.VolumeSizeGb,
                ProviderVolumeId = draining ? s.VolumeId : null, VolumeAttachedVmId = draining ? vm : null,
                VolumeFilesystemUuid = draining ? "synthetic-fs" : null, VolumeStatus = draining ? "mounted" : "deleted",
                Currency = "USD", StorageMonthlyRate = draining ? 5 : 0, IdempotencyKey = workspace, RequestDigest = "digest"
            });
            db.DevelopmentWorkspaceVms.Add(new()
            {
                Id = vm, WorkspaceId = workspace, OrganizationId = s.OrganizationId, Kind = "primary", IdempotencyKey = vm,
                RequestDigest = "digest", Generation = s.Generation, Phase = draining ? "draining" : "deleted",
                ImageId = "test", Profile = "test", PlanId = "plan_test", ProviderAccountId = s.ProviderAccountId,
                Region = s.Region, Sku = "test-sku", ProviderImage = "test", ImageManifestSha256 = "synthetic",
                ComputeReserved = draining, ProviderServerId = draining ? s.ServerId : null,
                ProviderIpId = draining ? s.PrimaryIpId : null, ProviderFirewallId = draining ? s.FirewallId : null,
                OperationId = draining ? s.StopOperationId : null, AllocationOperationId = draining ? s.AllocationOperationId : null,
                RuntimeCredentialRef = draining ? runtimeRef : null
            });
        }
    }

    private sealed class FakeSecrets : ISecretStore
    {
        public readonly List<string> Deleted = [];
        public string? FailOn;
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct)
        {
            Assert.Equal(new StoredSecretReference("openbao", AccountPath, "1"), reference);
            return Task.FromResult("synthetic-token");
        }
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct)
        {
            if (reference.Path == FailOn) throw new SecretStoreUnavailableException("synthetic refusal");
            Deleted.Add(reference.Path);
            return Task.CompletedTask;
        }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeProvider : HttpMessageHandler
    {
        public int Requests;
        public string? Scenario;
        public Func<CancellationToken, Task>? BeforeWitness;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.hetzner.cloud", request.RequestUri!.Host);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath["/v1/".Length..];
            if (path == "ssh_keys/" + Scope.ProjectWitnessSshKeyId)
            {
                if (BeforeWitness is not null) await BeforeWitness(ct);
                return Scenario == "wrong-project" ? Missing() : Response(HttpStatusCode.OK, new
                {
                    ssh_key = new { id = 510, fingerprint = Scenario == "wrong-fingerprint" ? "wrong" : Scope.ProjectWitnessFingerprint }
                });
            }
            if (path.Contains('/')) return Scenario == path ? Response(HttpStatusCode.OK, new { }) : Missing();
            Assert.Equal("?label_selector=workspace%3Dworkspace_test&per_page=50&page=1", request.RequestUri.Query);
            return Response(HttpStatusCode.OK, new Dictionary<string, object>
            {
                [path] = Scenario == path + "-inventory" ? new[] { new { id = 999 } } : Array.Empty<object>(),
                ["meta"] = new { pagination = new { next_page = (int?)null } }
            });
        }

        private static HttpResponseMessage Missing() => Response(HttpStatusCode.NotFound, new { error = new { code = "not_found" } });
        private static HttpResponseMessage Response(HttpStatusCode status, object payload) => new(status) { Content = JsonContent.Create(payload) };
    }
}
