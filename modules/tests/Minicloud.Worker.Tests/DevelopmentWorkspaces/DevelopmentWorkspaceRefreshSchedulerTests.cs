using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.DevelopmentWorkspaces;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed partial class DevelopmentWorkspaceProcessorTests
{
    [Fact]
    public Task MonitorNextRefreshesHealthyNewerVmDespiteRepeatedOlderVmFailures() =>
        VerifyRefreshFairnessAsync();

    internal async Task VerifyRefreshFairnessAsync()
    {
        var (_, older, _) = await Seed("session");
        older.ProviderServerId = "server-owned"; older.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await Hop();
        var newer = await lifecycle.LaunchEphemeralAsync(older.WorkspaceId, "org",
            new("image", "standard", "plan", "https://github.com/fixture-owner/project.git",
                "refs/heads/main", new('b', 40), true, true, "newer"), "actor", default);
        var healthy = newer.Vms.Single(x => x.Kind == "ephemeral");
        var ready = await CurrentVm(older.Id);
        healthy.Phase = "ready";
        healthy.GatewayReady = true;
        healthy.IpAddress = ready.IpAddress;
        healthy.SshHostKeyGeneration = healthy.Generation;
        healthy.SshHostKeyFingerprint = ready.SshHostKeyFingerprint;
        healthy.RuntimeCredentialRef = ready.RuntimeCredentialRef;
        healthy.RuntimeTokenExpiresAt = ready.RuntimeTokenExpiresAt;
        healthy.ProviderServerId = "server-newer";
        healthy.AllocationOperationId ??= healthy.OperationId;
        healthy.OperationId = null;
        healthy.LastHeartbeatAt = clock.GetUtcNow().AddSeconds(1);
        await db.SaveChangesAsync();
        runtime.FailedStatusGenerations.Add((older.Id, older.Generation));
        var initial = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .SingleAsync(x => x.VmId == older.Id && x.Service == "t3");

        for (var pass = 0; pass < 6; pass++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Processor().MonitorNextAsync(default);
        }

        Assert.True(runtime.StatusTargets.Count(x => x.VmId == older.Id) >= 2);
        Assert.True(runtime.StatusTargets.Count(x => x.VmId == healthy.Id) >= 2);
        var healthyRoute = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .SingleAsync(x => x.VmId == healthy.Id && x.Service == "t3");
        Assert.True(healthyRoute.ExpiresAt > clock.GetUtcNow());
        Assert.True((await CurrentVm(healthy.Id)).LastHeartbeatAt > initial.ExpiresAt);
        Assert.Equal(ready.LastHeartbeatAt, (await CurrentVm(older.Id)).LastHeartbeatAt);
        Assert.Equal(initial.ExpiresAt, (await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .SingleAsync(x => x.VmId == older.Id && x.Service == "t3")).ExpiresAt);
        var failures = diagnostics.Entries.Where(x => x.Message.StartsWith("Workspace monitor attempt failed")).ToList();
        Assert.True(failures.Count >= 2);
        Assert.All(failures, entry =>
        {
            Assert.Contains("InvalidOperationException", entry.Message);
            Assert.DoesNotContain("credential-must-not-be-logged", entry.Message);
            Assert.Null(entry.Exception);
        });
    }

    [Fact]
    public async Task MonitorCancellationPropagatesAndTheSameGenerationCanRetry()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await Hop();
        var initial = await CurrentVm(vm.Id);
        clock.Advance(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        runtime.BeforeAction = action =>
        {
            if (action != "status") return;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Processor().MonitorNextAsync(cancellation.Token));
        Assert.Equal(initial.LastHeartbeatAt, (await CurrentVm(vm.Id)).LastHeartbeatAt);
        Assert.DoesNotContain(diagnostics.Entries, x => x.Message.StartsWith("Workspace monitor attempt failed"));
        runtime.BeforeAction = null;
        await Processor().MonitorNextAsync(default);
        Assert.Equal(vm.Generation, (await CurrentVm(vm.Id)).Generation);
        Assert.Equal(clock.GetUtcNow(), (await CurrentVm(vm.Id)).LastHeartbeatAt);
    }

    [Fact]
    public async Task MonitorSchedulerRechecksEligibilityAndSelectsReplacementGenerationWithoutCachedSuppression()
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await Hop();
        var scheduler = services.GetRequiredService<DevelopmentWorkspaceRefreshScheduler>();
        Assert.Equal(vm.Generation, (await scheduler.NextAsync(db, default))!.Generation);
        db.ChangeTracker.Clear();
        var current = await db.DevelopmentWorkspaceVms.SingleAsync(x => x.Id == vm.Id);
        current.Generation++;
        await db.SaveChangesAsync();
        Assert.Equal(current.Generation, (await scheduler.NextAsync(db, default))!.Generation);
        current.Phase = "blocked";
        await db.SaveChangesAsync();
        Assert.Null(await scheduler.NextAsync(db, default));
        current.Phase = "ready";
        current.DesiredState = "stopped";
        await db.SaveChangesAsync();
        Assert.Null(await scheduler.NextAsync(db, default));
        current.DesiredState = "running";
        await db.SaveChangesAsync();
        Assert.Equal(current.Generation, (await scheduler.NextAsync(db, default))!.Generation);
    }

    [Theory]
    [InlineData("fenced")]
    [InlineData("host")]
    [InlineData("epoch")]
    public async Task MonitorSchedulerDoesNotReachRuntimeWithoutMaintenanceAdmission(string rejection)
    {
        var (_, vm, _) = await Seed("session");
        vm.ProviderServerId = "server-owned"; vm.IpAddress = "198.51.100.10";
        await db.SaveChangesAsync();
        await Hop();
        runtime.StatusTargets.Clear();
        var authority = await db.MaintenanceAuthorities.SingleAsync();
        if (rejection == "fenced") authority.State = MaintenanceAuthorityStates.Fenced;
        if (rejection == "host") authority.HostId = "synthetic-other-host";
        if (rejection == "epoch") authority.Epoch++;
        await db.SaveChangesAsync();

        await Processor().MonitorNextAsync(default);

        Assert.Empty(runtime.StatusTargets);
    }
}

// SQLite lacks DateTimeOffset ordering; UTC ticks let the real monitor query run
// against this synthetic fixture without substituting its selection algorithm.
public sealed class WorkspaceMonitorSqliteModelCustomizer(ModelCustomizerDependencies dependencies)
    : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<DevelopmentWorkspaceVm>().Property(x => x.LastHeartbeatAt).HasConversion(converter);
        modelBuilder.Entity<DevelopmentWorkspaceVm>().Property(x => x.UpdatedAt).HasConversion(converter);
    }
}
