using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Api.Auth;
using Minicloud.Api.Controllers;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.WorkspaceGateway.Tests;

public sealed class GatewayAuthorizationTests
{
    [Theory]
    [InlineData("""{"provider":"test","path":"org/ws/vm/g1","version":"1"}""", true)]
    [InlineData("""{"Provider":"test","Path":"org/ws/vm/g1","Version":"1"}""", true)]
    [InlineData("{}", false)]
    [InlineData("{malformed", false)]
    public async Task ResolvesWorkerSecretReferencesWithoutAcceptingMissingIdentity(string reference, bool accepted)
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.Db.DevelopmentWorkspaceGatewayRoutes.SingleAsync()).T3CredentialReference = reference;
        await fixture.Db.SaveChangesAsync();
        var launch = (await fixture.LaunchAsync())!;
        var redemption = (await fixture.Service.RedeemAsync(new(fixture.Host, launch.Url.Split("code=")[1]), default))!;
        var target = await fixture.Service.ResolveAsync(new(fixture.Host, redemption.Session), default);
        Assert.Equal(accepted, target is not null);
    }

    [Fact]
    public async Task PersistsOnlyDigestsAndRedeemsCodeOnceForExactOrigin()
    {
        await using var fixture = await Fixture.CreateAsync();
        var launch = await fixture.LaunchAsync();
        Assert.NotNull(launch);
        Assert.StartsWith("https://vm-", launch.Url);
        var code = launch.Url.Split("code=")[1];
        var receipt = await fixture.Db.DevelopmentWorkspaceGatewaySessions.SingleAsync();
        Assert.Equal(DevelopmentWorkspaceGatewayOptions.Digest(code), receipt.LaunchDigest);
        Assert.DoesNotContain(code, JsonSerializer.Serialize(receipt));
        Assert.Null(await fixture.Service.RedeemAsync(new("vm-forged-g1.workspace.example", code), default));
        var redemption = await fixture.Service.RedeemAsync(new(fixture.Host, code), default);
        Assert.NotNull(redemption);
        Assert.Null(await fixture.Service.RedeemAsync(new(fixture.Host, code), default));
        Assert.Equal(DevelopmentWorkspaceGatewayOptions.Digest(redemption.Session), receipt.SessionDigest);
        Assert.DoesNotContain(redemption.Session, JsonSerializer.Serialize(receipt));
        var target = await fixture.Service.ResolveAsync(new(fixture.Host, redemption.Session), default);
        Assert.Equal(new DevelopmentWorkspaceGatewayTarget(42001, "secret-only-server-side"), target);
        Assert.DoesNotContain("secret-only-server-side", JsonSerializer.Serialize(receipt));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("organization")]
    [InlineData("expired-launch")]
    [InlineData("expired-session")]
    [InlineData("revoked-session")]
    [InlineData("revoked-key")]
    [InlineData("expired-key")]
    [InlineData("removed-scope")]
    [InlineData("draining")]
    [InlineData("heartbeat")]
    [InlineData("route-expired")]
    [InlineData("route-revoked")]
    [InlineData("route-port")]
    public async Task ReauthorizesReceiptsAndCurrentReadiness(string invalidation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var launch = (await fixture.LaunchAsync())!;
        var code = launch.Url.Split("code=")[1];
        var request = new DevelopmentWorkspaceGatewayReceiptRequest(fixture.Host, code);
        var session = await fixture.Db.DevelopmentWorkspaceGatewaySessions.SingleAsync();
        if (invalidation == "expired-launch")
        {
            fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
            Assert.Null(await fixture.Service.RedeemAsync(request, default));
            return;
        }
        var redeemed = (await fixture.Service.RedeemAsync(request, default))!;
        var vm = await fixture.Db.DevelopmentWorkspaceVms.SingleAsync();
        var key = await fixture.Db.ApiKeys.SingleAsync();
        var route = await fixture.Db.DevelopmentWorkspaceGatewayRoutes.SingleAsync();
        switch (invalidation)
        {
            case "generation": vm.Generation++; break;
            case "organization": key.OrganizationId = "other-org"; break;
            case "expired-session": fixture.Clock.Now = fixture.Clock.Now.AddHours(1); break;
            case "revoked-session": session.RevokedAt = fixture.Clock.Now; break;
            case "revoked-key": key.RevokedAt = fixture.Clock.Now; break;
            case "expired-key": key.ExpiresAt = fixture.Clock.Now; break;
            case "removed-scope": key.ScopesJson = "[]"; break;
            case "draining": vm.Phase = "draining"; break;
            case "heartbeat": vm.LastHeartbeatAt = fixture.Clock.Now.AddHours(-1); break;
            case "route-expired": route.ExpiresAt = fixture.Clock.Now; break;
            case "route-revoked": route.RevokedAt = fixture.Clock.Now; break;
            case "route-port": route.LoopbackPort = 22; break;
        }
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await fixture.Service.ResolveAsync(new(fixture.Host, redeemed.Session), default));
        Assert.Null(await fixture.Service.AuthorizeActivityAsync(new(fixture.Host, redeemed.Session), default));
    }

    [Fact]
    public async Task OnlyRegisteredServicesAndAuthorizedVmAreLaunchable()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Null(await fixture.Service.LaunchAsync(fixture.Principal, "ws", "sibling", new(1), default));
        Assert.Null(await fixture.Service.LaunchAsync(fixture.Principal with { OrganizationId = "other" }, "ws", "vm", new(1), default));
        Assert.Null(await fixture.Service.LaunchAsync(fixture.Principal, "ws", "vm", new(2), default));
        Assert.Null(await fixture.Service.LaunchAsync(fixture.Principal, "ws", "vm", new(1, "http://127.0.0.1"), default));
        Assert.Null(await fixture.Service.LaunchAsync(fixture.Principal, "ws", "vm", new(1, "preview"), default));
        fixture.Db.DevelopmentWorkspaceGatewayRoutes.Add(new()
        {
            Id = "preview", WorkspaceId = "ws", VmId = "vm", OrganizationId = "org", Generation = 1,
            Service = "preview", LoopbackPort = 42002, RegisteredAt = fixture.Clock.Now, ExpiresAt = fixture.Clock.Now.AddHours(1)
        });
        await fixture.Db.SaveChangesAsync();
        var preview = await fixture.Service.LaunchAsync(fixture.Principal, "ws", "vm", new(1, "preview"), default);
        Assert.Contains("-s-preview.workspace.example", preview!.Url);
        var code = preview.Url.Split("code=")[1];
        Assert.Null(await fixture.Service.RedeemAsync(new(fixture.Host, code), default));
        fixture.Options.Enabled = false;
        Assert.Null(await fixture.LaunchAsync());
    }

    [Fact]
    public async Task BrowserMembershipRemovalRevokesAnExistingSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var membership = new OrganizationMember
        { Id = "member", OrganizationId = "org", UserId = "user", Role = OrganizationRole.Developer };
        fixture.Db.OrganizationMembers.Add(membership);
        await fixture.Db.SaveChangesAsync();
        var browser = new ControlPlanePrincipal("browser_session", "user", null, null, new HashSet<string>());
        var launch = await fixture.Service.LaunchAsync(browser, "ws", "vm", new(1), default);
        Assert.NotNull(launch);
        var redeemed = await fixture.Service.RedeemAsync(new(fixture.Host, launch.Url.Split("code=")[1]), default);
        Assert.NotNull(redeemed);
        Assert.NotNull(await fixture.Service.ResolveAsync(new(fixture.Host, redeemed.Session), default));
        fixture.Db.OrganizationMembers.Remove(membership);
        await fixture.Db.SaveChangesAsync();
        Assert.Null(await fixture.Service.ResolveAsync(new(fixture.Host, redeemed.Session), default));
    }

    [Fact]
    public async Task InternalReceiptApiRequiresItsDedicatedCredential()
    {
        await using var fixture = await Fixture.CreateAsync();
        var launch = (await fixture.LaunchAsync())!;
        var request = new DevelopmentWorkspaceGatewayReceiptRequest(fixture.Host, launch.Url.Split("code=")[1]);
        var context = new DefaultHttpContext();
        var controller = new DevelopmentWorkspaceGatewayController { ControllerContext = new() { HttpContext = context } };
        context.Request.Headers.Authorization = "Bearer " + fixture.Options.ServiceCredential;
        var denied = await controller.Redeem(request, fixture.Service,
            Microsoft.Extensions.Options.Options.Create(fixture.Options), default);
        Assert.Equal(401, ((IStatusCodeHttpResult)denied).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        context.Request.Headers["X-Minicloud-Gateway-Credential"] = fixture.Options.ServiceCredential;
        var accepted = await controller.Redeem(request, fixture.Service,
            Microsoft.Extensions.Options.Options.Create(fixture.Options), default);
        Assert.Equal(200, ((IStatusCodeHttpResult)accepted).StatusCode);
    }

    [Fact]
    public async Task CertificateAuthorizationRequiresServiceCredentialAndReturnsNoSecrets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = new DefaultHttpContext();
        var controller = new DevelopmentWorkspaceGatewayController { ControllerContext = new() { HttpContext = context } };
        var options = Microsoft.Extensions.Options.Options.Create(fixture.Options);
        var denied = await controller.AuthorizeCertificate(fixture.Host, fixture.Service, options, default);
        Assert.Equal(404, ((IStatusCodeHttpResult)denied).StatusCode);
        context.Request.Headers["X-Minicloud-Gateway-Credential"] = fixture.Options.ServiceCredential;
        var accepted = await controller.AuthorizeCertificate(fixture.Host, fixture.Service, options, default);
        Assert.Equal(200, ((IStatusCodeHttpResult)accepted).StatusCode);
        Assert.False(accepted is IValueHttpResult);
        var unknown = await controller.AuthorizeCertificate(fixture.Host.Replace("-g1.", "-g2."), fixture.Service, options, default);
        Assert.Equal(404, ((IStatusCodeHttpResult)unknown).StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("organization")]
    [InlineData("draining")]
    [InlineData("heartbeat")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("port")]
    [InlineData("credential")]
    public async Task CertificateAuthorizationOnlyAllowsCurrentReadyRegisteredRoute(string invalidation)
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.AuthorizeCertificateAsync(fixture.Host, default));
        var vm = await fixture.Db.DevelopmentWorkspaceVms.SingleAsync();
        var route = await fixture.Db.DevelopmentWorkspaceGatewayRoutes.SingleAsync();
        switch (invalidation)
        {
            case "generation": vm.Generation++; break;
            case "organization": route.OrganizationId = "other"; break;
            case "draining": vm.Phase = "draining"; break;
            case "heartbeat": vm.LastHeartbeatAt = fixture.Clock.Now.AddHours(-1); break;
            case "expired": route.ExpiresAt = fixture.Clock.Now; break;
            case "revoked": route.RevokedAt = fixture.Clock.Now; break;
            case "port": route.LoopbackPort = 22; break;
            case "credential": route.T3CredentialReference = null; break;
        }
        await fixture.Db.SaveChangesAsync();
        Assert.False(await fixture.Service.AuthorizeCertificateAsync(fixture.Host, default));
    }

    [Fact]
    public void ApiRegistrationLoadsProtectedCredentialFileInsteadOfInlineFallback()
    {
        var directory = Path.GetFullPath(".local/artifacts/issue110/gateway/secret-tests");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var token = new string('p', 64);
        File.WriteAllText(file, token + "\n");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentWorkspaceGateway:Enabled"] = "true",
                ["DevelopmentWorkspaceGateway:GatewayDomain"] = "workspace.example",
                ["DevelopmentWorkspaceGateway:ServiceCredential"] = "ignored-fallback",
                ["DevelopmentWorkspaceGateway:ServiceCredentialFile"] = file
            }).Build();
            using var provider = new ServiceCollection().AddDevelopmentWorkspaceGateway(configuration).BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<DevelopmentWorkspaceGatewayOptions>>().Value;
            Assert.True(options.IsConfigured);
            Assert.Equal(token, options.ServiceCredential);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task UsefulActivityRequiresAuthorizedSessionAndCancelsOnlyUnclaimedIdleGrace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workspace = new DevelopmentWorkspace
        { Id = "ws", OrganizationId = "org", PrimaryVmId = "vm" };
        FillRequiredStrings(fixture.Db, workspace);
        fixture.Db.DevelopmentWorkspaces.Add(workspace);
        var vm = await fixture.Db.DevelopmentWorkspaceVms.SingleAsync();
        vm.LastActivityAt = fixture.Clock.Now.AddMinutes(-30);
        vm.OperationId = "idle-op";
        vm.ShutdownReason = "idle";
        vm.ShutdownDeadline = fixture.Clock.Now.AddMinutes(2);
        var operation = new DevelopmentWorkspaceOperation
        {
            Id = "idle-op", WorkspaceId = "ws", VmId = "vm", Generation = 1,
            Kind = "stop", Status = "pending", Attempts = 0
        };
        FillRequiredStrings(fixture.Db, operation);
        fixture.Db.DevelopmentWorkspaceOperations.Add(operation);
        await fixture.Db.SaveChangesAsync();
        var launch = (await fixture.LaunchAsync())!;
        var redeemed = (await fixture.Service.RedeemAsync(new(fixture.Host, launch.Url.Split("code=")[1]), default))!;
        var lifecycle = new DevelopmentWorkspaceService(fixture.Db, new IdGenerator(), fixture.Clock,
            Microsoft.Extensions.Options.Options.Create(new DevelopmentWorkspaceOptions
            {
                Enabled = true, MaxEstimatedMonthlySpend = 1000, MaxEstimatedMonthlySpendPerOrganization = 1000,
                VolumeMonthlyRatePerGb = 1, PricingUpdatedAt = fixture.Clock.Now
            }));
        var context = new DefaultHttpContext();
        var controller = new DevelopmentWorkspaceGatewayController { ControllerContext = new() { HttpContext = context } };
        var options = Microsoft.Extensions.Options.Options.Create(fixture.Options);
        var receipt = new DevelopmentWorkspaceGatewayReceiptRequest(fixture.Host, redeemed.Session);
        var deniedService = await controller.Activity(receipt, fixture.Service, lifecycle, options, default);
        Assert.Equal(401, ((IStatusCodeHttpResult)deniedService).StatusCode);
        context.Request.Headers["X-Minicloud-Gateway-Credential"] = fixture.Options.ServiceCredential;
        var deniedSession = await controller.Activity(receipt with { Receipt = new string('f', 64) }, fixture.Service, lifecycle, options, default);
        Assert.Equal(401, ((IStatusCodeHttpResult)deniedSession).StatusCode);
        var deniedOrigin = await controller.Activity(receipt with { Host = fixture.Host.Replace("-g1.", "-g2.") }, fixture.Service, lifecycle, options, default);
        Assert.Equal(401, ((IStatusCodeHttpResult)deniedOrigin).StatusCode);
        Assert.Equal("pending", (await fixture.Db.DevelopmentWorkspaceOperations.SingleAsync()).Status);
        Assert.Equal(fixture.Clock.Now.AddMinutes(-30), (await fixture.Db.DevelopmentWorkspaceVms.SingleAsync()).LastActivityAt);
        var accepted = await controller.Activity(receipt, fixture.Service, lifecycle, options, default);
        Assert.Equal(204, ((IStatusCodeHttpResult)accepted).StatusCode);
        fixture.Db.ChangeTracker.Clear();
        vm = await fixture.Db.DevelopmentWorkspaceVms.SingleAsync();
        Assert.Equal(fixture.Clock.Now, vm.LastActivityAt);
        Assert.Null(vm.ShutdownDeadline);
        Assert.Null(vm.OperationId);
        Assert.Equal("ready", vm.Phase);
        Assert.Equal("running", vm.DesiredState);
        Assert.Equal("cancelled", (await fixture.Db.DevelopmentWorkspaceOperations.SingleAsync()).Status);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("wait-for-tasks", true)]
    [InlineData("wait-for-save", true)]
    [InlineData("quiesced", false)]
    [InlineData("gateway-drain", false)]
    [InlineData("quiesce", false)]
    [InlineData("deleting", false)]
    [InlineData("blocked", false)]
    [InlineData("wrong-git-code", false)]
    [InlineData("stale-operation", false)]
    [InlineData("completed-operation", false)]
    [InlineData("wrong-kind", false)]
    [InlineData("gateway-not-ready", false)]
    public async Task RecoveryAdmissionMatchesExplicitCurrentStopStages(string state, bool allowed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var launch = (await fixture.LaunchAsync())!;
        var redeemed = (await fixture.Service.RedeemAsync(new(fixture.Host, launch.Url.Split("code=")[1]), default))!;
        var vm = await fixture.Db.DevelopmentWorkspaceVms.SingleAsync();
        vm.DesiredState = "stopped";
        vm.Phase = state is "wait-for-save" or "wrong-git-code" ? "awaiting_git_save" : "draining";
        vm.ErrorCode = state == "wait-for-save" ? "workspace_git_save_required" : null;
        vm.OperationId = "stop-op";
        var operation = new DevelopmentWorkspaceOperation
        {
            Id = "stop-op", WorkspaceId = "ws", VmId = "vm", Generation = 1, Kind = "stop",
            Status = "running", Step = state is "pending" or "wait-for-save" or "gateway-drain" or "quiesce" or "deleting"
                ? state : "wait-for-tasks", Attempts = 1
        };
        FillRequiredStrings(fixture.Db, operation);
        fixture.Db.DevelopmentWorkspaceOperations.Add(operation);
        switch (state)
        {
            case "quiesced": vm.QuiescedGeneration = 1; break;
            case "blocked": vm.Phase = "blocked"; break;
            case "wrong-git-code": vm.ErrorCode = "workspace_unrelated_failure"; operation.Step = "wait-for-save"; break;
            case "stale-operation": operation.Generation = 2; break;
            case "completed-operation": operation.Status = "completed"; break;
            case "wrong-kind": operation.Kind = "start"; break;
            case "gateway-not-ready": vm.GatewayReady = false; break;
        }
        await fixture.Db.SaveChangesAsync();
        var receipt = new DevelopmentWorkspaceGatewayReceiptRequest(fixture.Host, redeemed.Session);
        Assert.Equal(allowed, await fixture.Service.ResolveAsync(receipt, default) is not null);
        Assert.Equal(allowed, await fixture.Service.AuthorizeActivityAsync(receipt, default) is not null);
        Assert.Equal(allowed, await fixture.Service.AuthorizeCertificateAsync(fixture.Host, default));
        Assert.Equal(allowed, await fixture.LaunchAsync() is not null);
    }

    private static void FillRequiredStrings(MinicloudDbContext db, object entity)
    {
        foreach (var property in db.Entry(entity).Properties)
            if (property.Metadata.ClrType == typeof(string) && !property.Metadata.IsNullable && property.CurrentValue is null)
                property.CurrentValue = "fixture";
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public MinicloudDbContext Db { get; private set; } = null!;
        public DevelopmentWorkspaceGatewayService Service { get; private set; } = null!;
        public Clock Clock { get; } = new();
        public DevelopmentWorkspaceGatewayOptions Options { get; } = new()
        { Enabled = true, GatewayDomain = "workspace.example", ServiceCredential = new string('s', 32) };
        public string Host => Options.Host("vm", 1, "t3")!;
        public ControlPlanePrincipal Principal { get; } = new("api_key", null, "key", "org", new HashSet<string> { ApiKeyScopes.AppsWrite });

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseSqlite("Data Source=:memory:;Foreign Keys=False").Options);
            await fixture.Db.Database.OpenConnectionAsync();
            await fixture.Db.Database.EnsureCreatedAsync();
            var vm = new DevelopmentWorkspaceVm
            {
                Id = "vm", WorkspaceId = "ws", OrganizationId = "org", Kind = "ephemeral", Generation = 1,
                Phase = "ready", DesiredState = "running", LastHeartbeatAt = fixture.Clock.Now, GatewayReady = true
            };
            foreach (var property in fixture.Db.Entry(vm).Properties)
                if (property.Metadata.ClrType == typeof(string) && !property.Metadata.IsNullable && property.CurrentValue is null)
                    property.CurrentValue = "fixture";
            fixture.Db.DevelopmentWorkspaceVms.Add(vm);
            fixture.Db.ApiKeys.Add(new()
            {
                Id = "key", OrganizationId = "org", Name = "fixture", KeyPrefix = "fixture", KeyHash = "digest",
                ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsWrite })
            });
            fixture.Db.DevelopmentWorkspaceGatewayRoutes.Add(new()
            {
                Id = "route", OrganizationId = "org", WorkspaceId = "ws", VmId = "vm", Generation = 1,
                LoopbackPort = 42001, RegisteredAt = fixture.Clock.Now, ExpiresAt = fixture.Clock.Now.AddHours(1),
                T3CredentialReference = JsonSerializer.Serialize(new StoredSecretReference("test", "org/ws/vm/g1", "1"))
            });
            await fixture.Db.SaveChangesAsync();
            fixture.Service = new(fixture.Db, new ControlPlaneAuthorization(fixture.Db), new Secrets(),
                Microsoft.Extensions.Options.Options.Create(fixture.Options),
                Microsoft.Extensions.Options.Options.Create(new DevelopmentWorkspaceOptions { Enabled = true }), fixture.Clock,
                new DevelopmentWorkspaceService(fixture.Db, new IdGenerator(), fixture.Clock,
                    Microsoft.Extensions.Options.Options.Create(new DevelopmentWorkspaceOptions { Enabled = true })));
            return fixture;
        }

        public Task<DevelopmentWorkspaceGatewayLaunchResponse?> LaunchAsync() =>
            Service.LaunchAsync(Principal, "ws", "vm", new(1), default);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class Secrets : ISecretStore
    {
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            Assert.Equal(new StoredSecretReference("test", "org/ws/vm/g1", "1"), reference);
            return Task.FromResult("secret-only-server-side");
        }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
