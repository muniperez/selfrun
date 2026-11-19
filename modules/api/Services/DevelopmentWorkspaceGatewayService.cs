using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class DevelopmentWorkspaceGatewayService(
    MinicloudDbContext db, ControlPlaneAuthorization authorization, ISecretStore secrets,
    IOptions<DevelopmentWorkspaceGatewayOptions> configured,
    IOptions<DevelopmentWorkspaceOptions> workspaces, TimeProvider time, DevelopmentWorkspaceService lifecycle)
{
    private static readonly JsonSerializerOptions SecretReferenceJson = new(JsonSerializerDefaults.Web);
    private DevelopmentWorkspaceGatewayOptions Options => configured.Value;

    public async Task<bool> AuthorizeCertificateAsync(string domain, CancellationToken ct)
    {
        if (!workspaces.Value.Enabled || !Options.TryParseHost(domain, out var generation, out var service)) return false;
        var now = time.GetUtcNow();
        var heartbeatDeadline = now.AddSeconds(-Math.Clamp(workspaces.Value.HeartbeatTimeoutSeconds, 10, 300));
        var routes = db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking()
            .Where(x => x.Generation == generation && x.Service == service && x.RevokedAt == null &&
                x.LoopbackPort >= Options.MinimumRelayPort && x.LoopbackPort <= Options.MaximumRelayPort)
            .Join(db.DevelopmentWorkspaceVms.AsNoTracking(),
                route => new { route.OrganizationId, route.WorkspaceId, route.VmId, route.Generation },
                vm => new { vm.OrganizationId, vm.WorkspaceId, VmId = vm.Id, vm.Generation },
                (route, vm) => new { Route = route, Vm = vm })
            .Where(x => x.Vm.GatewayReady && x.Vm.QuiescedGeneration != x.Vm.Generation &&
                (x.Vm.Phase == "ready" && x.Vm.DesiredState == "running" ||
                 x.Vm.DesiredState == "stopped" && (x.Vm.Phase == "draining" || x.Vm.Phase == "awaiting_git_save")))
            .Select(x => new { x.Route.VmId, x.Route.ExpiresAt, x.Route.T3CredentialReference, x.Vm })
            .Take(1024);
        // DNS-safe host hashes are intentionally not reversible; keep this private lookup bounded.
        foreach (var route in await routes.ToListAsync(ct))
            if (route.ExpiresAt > now && route.Vm.LastHeartbeatAt > heartbeatDeadline &&
                (service is not ("t3" or "connect") || !string.IsNullOrEmpty(route.T3CredentialReference)) &&
                Options.Host(route.VmId, generation, service) == domain && await IsAdmittedVmAsync(route.Vm, ct))
                return true;
        return false;
    }

    public async Task<DevelopmentWorkspaceGatewayLaunchResponse?> LaunchAsync(
        ControlPlanePrincipal principal, string workspaceId, string vmId,
        DevelopmentWorkspaceGatewayLaunchRequest request, CancellationToken ct)
    {
        if (!Options.IsConfigured || !workspaces.Value.Enabled) return null;
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == vmId && x.WorkspaceId == workspaceId, ct);
        if (vm is null || !await authorization.CanAccessOrganizationAsync(
            principal.ToActorIdentity(), vm.OrganizationId, OrganizationAction.WriteApps, ct)) return null;
        var host = Options.Host(vmId, request.Generation, request.Service);
        if (host is null) return null;
        var now = time.GetUtcNow();
        var receipt = new DevelopmentWorkspaceGatewaySession
        {
            Id = "wgs_" + Guid.NewGuid().ToString("N"),
            OrganizationId = vm.OrganizationId, WorkspaceId = workspaceId, VmId = vmId,
            Generation = request.Generation, Service = request.Service,
            UserId = principal.IsDashboardUser ? principal.UserId : null, ApiKeyId = principal.ApiKeyId,
            LaunchDigest = "", LaunchExpiresAt = now.AddSeconds(Math.Clamp(Options.LaunchLifetimeSeconds, 10, 120)),
            SessionExpiresAt = now.AddSeconds(Math.Clamp(Options.SessionLifetimeSeconds, 60, 3600))
        };
        if (await AuthorizedRouteAsync(receipt, host, ct) is null) return null;
        var token = DevelopmentWorkspaceGatewayOptions.NewToken();
        receipt.LaunchDigest = DevelopmentWorkspaceGatewayOptions.Digest(token);
        db.DevelopmentWorkspaceGatewaySessions.Add(receipt);
        await db.SaveChangesAsync(ct);
        return new($"https://{host}/__minicloud/launch?code={token}", receipt.LaunchExpiresAt);
    }

    public async Task<DevelopmentWorkspaceGatewayRedemption?> RedeemAsync(DevelopmentWorkspaceGatewayReceiptRequest request, CancellationToken ct)
    {
        if (!ValidRequest(request)) return null;
        var hash = DevelopmentWorkspaceGatewayOptions.Digest(request.Receipt);
        var receipt = await db.DevelopmentWorkspaceGatewaySessions.SingleOrDefaultAsync(x => x.LaunchDigest == hash, ct);
        var now = time.GetUtcNow();
        if (receipt is null || receipt.RedeemedAt is not null || receipt.LaunchExpiresAt <= now ||
            await AuthorizedRouteAsync(receipt, request.Host, ct) is null) return null;
        var token = DevelopmentWorkspaceGatewayOptions.NewToken();
        receipt.SessionDigest = DevelopmentWorkspaceGatewayOptions.Digest(token);
        receipt.RedeemedAt = now;
        receipt.Revision++;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return null; }
        return new(token, receipt.SessionExpiresAt);
    }

    public async Task<DevelopmentWorkspaceGatewayTarget?> ResolveAsync(DevelopmentWorkspaceGatewayReceiptRequest request, CancellationToken ct)
    {
        if (!ValidRequest(request)) return null;
        var hash = DevelopmentWorkspaceGatewayOptions.Digest(request.Receipt);
        var receipt = await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking().SingleOrDefaultAsync(x => x.SessionDigest == hash, ct);
        if (receipt is null || receipt.RedeemedAt is null) return null;
        var route = await AuthorizedRouteAsync(receipt, request.Host, ct);
        if (route is null) return null;
        string? bearer = null;
        if (receipt.Service is "t3" or "connect")
        {
            if (string.IsNullOrEmpty(route.T3CredentialReference)) return null;
            StoredSecretReference? reference;
            try { reference = JsonSerializer.Deserialize<StoredSecretReference>(route.T3CredentialReference, SecretReferenceJson); }
            catch (JsonException) { return null; }
            if (reference is null || string.IsNullOrWhiteSpace(reference.Provider) || string.IsNullOrWhiteSpace(reference.Path)) return null;
            bearer = await secrets.GetAsync(reference, ct);
            if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 8192 || bearer.Any(char.IsControl)) return null;
        }
        return new(route.LoopbackPort, bearer);
    }

    private bool ValidRequest(DevelopmentWorkspaceGatewayReceiptRequest request) =>
        Options.IsConfigured && workspaces.Value.Enabled && request.Receipt.Length == 64 && Options.AcceptHost(request.Host);

    public async Task<(string WorkspaceId, string OrganizationId, string VmId, long Generation)?> AuthorizeActivityAsync(
        DevelopmentWorkspaceGatewayReceiptRequest request, CancellationToken ct)
    {
        if (!ValidRequest(request)) return null;
        var digest = DevelopmentWorkspaceGatewayOptions.Digest(request.Receipt);
        var receipt = await db.DevelopmentWorkspaceGatewaySessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SessionDigest == digest, ct);
        if (receipt is null || receipt.RedeemedAt is null || receipt.Service != "t3" ||
            await AuthorizedRouteAsync(receipt, request.Host, ct) is null) return null;
        return (receipt.WorkspaceId, receipt.OrganizationId, receipt.VmId, receipt.Generation);
    }

    private async Task<DevelopmentWorkspaceGatewayRoute?> AuthorizedRouteAsync(DevelopmentWorkspaceGatewaySession receipt, string host, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (receipt.RevokedAt is not null || receipt.SessionExpiresAt <= now ||
            Options.Host(receipt.VmId, receipt.Generation, receipt.Service) != host) return null;
        ActorIdentity actor;
        if (receipt.ApiKeyId is { } keyId)
        {
            var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == keyId, ct);
            if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now) return null;
            actor = new("api_key", null, key.Id, key.OrganizationId,
                (JsonSerializer.Deserialize<string[]>(key.ScopesJson) ?? []).ToHashSet(StringComparer.Ordinal));
        }
        else actor = new("browser_session", receipt.UserId, null, null, new HashSet<string>());
        if (!await authorization.CanAccessOrganizationAsync(actor, receipt.OrganizationId, OrganizationAction.WriteApps, ct)) return null;
        var vm = await db.DevelopmentWorkspaceVms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == receipt.VmId &&
            x.WorkspaceId == receipt.WorkspaceId && x.OrganizationId == receipt.OrganizationId, ct);
        if (vm is null || vm.Generation != receipt.Generation || !await IsAdmittedVmAsync(vm, ct) || vm.LastHeartbeatAt is null ||
            vm.LastHeartbeatAt <= now.AddSeconds(-Math.Clamp(workspaces.Value.HeartbeatTimeoutSeconds, 10, 300))) return null;
        if (receipt.Service == "connect" && (vm.Kind != DevelopmentWorkspaceVmKinds.Primary ||
            vm.DesiredState != "running" || vm.Phase != "ready" ||
            !await CanManageConnectAsync(receipt, ct))) return null;
        var route = await db.DevelopmentWorkspaceGatewayRoutes.AsNoTracking().SingleOrDefaultAsync(x =>
            x.VmId == receipt.VmId && x.Generation == receipt.Generation && x.Service == receipt.Service &&
            x.WorkspaceId == receipt.WorkspaceId && x.OrganizationId == receipt.OrganizationId, ct);
        return route is not null && route.RevokedAt is null && route.ExpiresAt > now &&
            (receipt.Service is not ("t3" or "connect") || !string.IsNullOrEmpty(route.T3CredentialReference)) &&
            route.LoopbackPort >= Options.MinimumRelayPort && route.LoopbackPort <= Options.MaximumRelayPort ? route : null;
    }

    private async Task<bool> CanManageConnectAsync(DevelopmentWorkspaceGatewaySession receipt, CancellationToken ct)
    {
        if (receipt.UserId is null || receipt.ApiKeyId is not null) return false;
        return await db.DevelopmentWorkspaces.AsNoTracking().AnyAsync(x =>
            x.Id == receipt.WorkspaceId && x.OrganizationId == receipt.OrganizationId &&
            x.CreatedBy == receipt.UserId, ct) ||
            await db.OrganizationMembers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == receipt.OrganizationId && x.UserId == receipt.UserId &&
                x.Role == OrganizationRole.Owner, ct);
    }

    private async Task<bool> IsAdmittedVmAsync(DevelopmentWorkspaceVm vm, CancellationToken ct)
    {
        if (!vm.GatewayReady || vm.QuiescedGeneration == vm.Generation) return false;
        if (vm.DesiredState == "running" && vm.Phase == "ready" && vm.ErrorCode is null) return true;
        return await lifecycle.CanRecordRecoveryActivityAsync(vm, ct);
    }
}

public static class DevelopmentWorkspaceGatewayRegistration
{
    public static IServiceCollection AddDevelopmentWorkspaceGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DevelopmentWorkspaceGatewayOptions>(configuration.GetSection(DevelopmentWorkspaceGatewayOptions.SectionName));
        services.PostConfigure<DevelopmentWorkspaceGatewayOptions>(options => options.LoadServiceCredential());
        services.AddScoped<DevelopmentWorkspaceGatewayService>();
        return services;
    }
}
