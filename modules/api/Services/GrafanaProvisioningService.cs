using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Selfrun.Api.Auth;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Services;

namespace Selfrun.Api.Services;

public sealed record GrafanaProvisioningResult(
    bool Ready,
    string? UnavailableReason,
    long GrafanaOrgId,
    long GrafanaUserId,
    string GrafanaLogin,
    string DataSourceUid);

/// <summary>
/// T20 idempotent Grafana provisioning: one Grafana organization per Selfrun
/// organization, one Grafana user identity per (Selfrun user, organization) with
/// exactly the Viewer role in exactly that organization, and one non-editable
/// per-app Loki data source pointing at the private query gateway.
/// Desired state is durable in the Grafana*Binding tables; every step verifies
/// actual Grafana state before reporting ready. Any mismatch is repaired when
/// possible, otherwise recorded as unavailable and sessions stay denied.
/// </summary>
public sealed class GrafanaProvisioningService(
    SelfrunDbContext db,
    IGrafanaApiClient grafana,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ControlPlaneAuthorization authorization,
    AuditEventService audit,
    IOptions<GrafanaOptions> options,
    ILogger<GrafanaProvisioningService> logger)
{
    public const string ViewerRole = "Viewer";
    public const string ReadTokenPrefixRoot = "mcl_grafana_read";

    public async Task<GrafanaProvisioningResult> EnsureProvisionedAsync(
        ControlPlanePrincipal principal,
        string appId,
        string queryGatewayBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return Unavailable(0, "Grafana access requires an authenticated browser session.");
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return Unavailable(0, "Unknown app.");
        }

        if (!await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadApplicationLogs, cancellationToken))
        {
            return Unavailable(0, "Unknown app.");
        }

        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is null || !settings.Enabled || settings.Deleting || settings.OrganizationId != app.OrganizationId)
        {
            return Unavailable(0, "Telemetry is not enabled for this app.");
        }

        try
        {
            var version = await grafana.GetVersionAsync(cancellationToken);
            if (!string.Equals(version, options.Value.ExpectedVersion, StringComparison.Ordinal))
            {
                logger.LogWarning("Grafana version mismatch: expected {Expected} got {Actual}.", options.Value.ExpectedVersion, version);
                await MarkBindingsUnavailableAsync(app.OrganizationId, principal.UserId, appId, "grafana_version_mismatch", cancellationToken);
                return Unavailable(0, "Log browsing is temporarily unavailable.");
            }

            var orgBinding = await EnsureOrganizationBindingAsync(app.OrganizationId, cancellationToken);
            var userBinding = await EnsureUserBindingAsync(app.OrganizationId, principal.UserId, orgBinding.GrafanaOrgId, cancellationToken);
            var dataSourceUid = await EnsureDataSourceBindingAsync(app, settings, orgBinding.GrafanaOrgId, queryGatewayBaseUrl, cancellationToken);

            await audit.AppendAsync(
                app.OrganizationId, app.Id, AuditEventService.ActorFor(principal.ToActorIdentity()),
                "grafana.provisioned", $"org={orgBinding.GrafanaOrgId} uid={dataSourceUid}", cancellationToken);

            return new GrafanaProvisioningResult(true, null, orgBinding.GrafanaOrgId, userBinding.GrafanaUserId, ExpectedLogin(principal.UserId, app.OrganizationId), dataSourceUid);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grafana provisioning failed for app {AppId}; failing closed.", appId);
            return Unavailable(0, "Log browsing is temporarily unavailable.");
        }
    }

    public static string ExpectedLogin(string selfrunUserId, string organizationId) =>
        Selfrun.Common.Services.GrafanaProxyGuard.IdentityFor(selfrunUserId, organizationId);

    public static string DataSourceUidForApp(string appId) => $"mcl-{appId}";

    private static GrafanaProvisioningResult Unavailable(long orgId, string reason) =>
        new(false, reason, orgId, 0, string.Empty, string.Empty);

    private async Task<GrafanaOrganizationBinding> EnsureOrganizationBindingAsync(
        string organizationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var binding = await db.GrafanaOrganizationBindings
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (binding is null)
        {
            binding = new GrafanaOrganizationBinding
            {
                Id = ids.NewId("gorg"),
                OrganizationId = organizationId,
                GrafanaOrgId = 0,
                ReconciliationStatus = "pending",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.GrafanaOrganizationBindings.Add(binding);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (binding.ReconciliationStatus == "ready" && binding.GrafanaOrgId > 0)
        {
            return binding;
        }

        var org = await grafana.EnsureOrganizationAsync($"selfrun-{organizationId}", cancellationToken);
        binding.GrafanaOrgId = org.Id;
        binding.ReconciliationStatus = "ready";
        binding.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return binding;
    }

    private async Task<GrafanaUserBinding> EnsureUserBindingAsync(
        string organizationId, string selfrunUserId, long grafanaOrgId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var binding = await db.GrafanaUserBindings
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.SelfrunUserId == selfrunUserId, cancellationToken);
        if (binding is null)
        {
            binding = new GrafanaUserBinding
            {
                Id = ids.NewId("gusr"),
                OrganizationId = organizationId,
                SelfrunUserId = selfrunUserId,
                GrafanaUserId = 0,
                ExpectedRole = ViewerRole,
                ReconciliationStatus = "pending",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.GrafanaUserBindings.Add(binding);
            await db.SaveChangesAsync(cancellationToken);
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selfrunUserId, cancellationToken);
        var login = ExpectedLogin(selfrunUserId, organizationId);
        var grafanaUser = await grafana.EnsureUserAsync(
            login, user?.Email ?? $"{selfrunUserId}@invalid.local", user?.DisplayName ?? selfrunUserId, cancellationToken);

        if (binding.GrafanaUserId == 0)
        {
            binding.GrafanaUserId = grafanaUser.Id;
            binding.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (binding.GrafanaUserId != grafanaUser.Id)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana user identity mismatch; refusing to proceed.");
        }

        await EnsureExactMembershipAsync(grafanaOrgId, grafanaUser.Id, cancellationToken);

        binding.ReconciliationStatus = "ready";
        binding.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return binding;
    }

    /// <summary>
    /// The identity must hold exactly the Viewer role in exactly the expected
    /// organization. Extra memberships (including the default Main org) are removed;
    /// a wrong role is downgraded. Anything left over fails closed.
    /// </summary>
    private async Task EnsureExactMembershipAsync(long grafanaOrgId, long grafanaUserId, CancellationToken cancellationToken)
    {
        var members = await grafana.GetOrgUsersAsync(grafanaOrgId, cancellationToken);
        var member = members.SingleOrDefault(x => x.UserId == grafanaUserId);
        if (member is null)
        {
            await grafana.AddOrgUserAsync(grafanaOrgId, grafanaUserId, ViewerRole, cancellationToken);
        }
        else if (!string.Equals(member.Role, ViewerRole, StringComparison.Ordinal))
        {
            await grafana.UpdateOrgUserRoleAsync(grafanaOrgId, grafanaUserId, ViewerRole, cancellationToken);
        }

        var orgs = await grafana.GetUserOrganizationsAsync(grafanaUserId, cancellationToken);
        foreach (var other in orgs.Where(x => x != grafanaOrgId))
        {
            await grafana.RemoveOrgUserAsync(other, grafanaUserId, cancellationToken);
        }

        var verifyMembers = await grafana.GetOrgUsersAsync(grafanaOrgId, cancellationToken);
        var verified = verifyMembers.SingleOrDefault(x => x.UserId == grafanaUserId);
        if (verified is null || !string.Equals(verified.Role, ViewerRole, StringComparison.Ordinal))
        {
            throw new GrafanaProvisioningUnavailableException("Grafana membership did not converge to exactly Viewer in the expected organization.");
        }

        var verifyOrgs = await grafana.GetUserOrganizationsAsync(grafanaUserId, cancellationToken);
        if (verifyOrgs.Count != 1 || verifyOrgs[0] != grafanaOrgId)
        {
            throw new GrafanaProvisioningUnavailableException("Grafana identity belongs to unexpected organizations; refusing to proceed.");
        }
    }

    private async Task<string> EnsureDataSourceBindingAsync(
        SelfrunApp app,
        AppTelemetrySettings settings,
        long grafanaOrgId,
        string queryGatewayBaseUrl,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var uid = DataSourceUidForApp(app.Id);
        var liveBindings = await db.GrafanaDataSourceBindings
            .Where(x => x.AppId == app.Id && x.DeletedAt == null)
            .ToListAsync(cancellationToken);
        if (liveBindings.Count > 1)
        {
            throw new GrafanaProvisioningUnavailableException("Multiple live data-source bindings exist for this app; refusing to proceed.");
        }

        var binding = liveBindings.SingleOrDefault();
        string? rawReadToken = null;
        if (binding is null)
        {
            var token = GenerateReadToken();
            rawReadToken = token.Token;
            binding = new GrafanaDataSourceBinding
            {
                Id = ids.NewId("gds"),
                OrganizationId = app.OrganizationId,
                AppId = app.Id,
                GrafanaUid = uid,
                CredentialReference = $"{token.Prefix}|{HashToken(token.Token)}",
                ReconciliationStatus = "pending",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.GrafanaDataSourceBindings.Add(binding);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            if (binding.OrganizationId != app.OrganizationId || !string.Equals(binding.GrafanaUid, uid, StringComparison.Ordinal))
            {
                throw new GrafanaProvisioningUnavailableException("Grafana data-source binding mismatch; refusing to proceed.");
            }

            var parts = binding.CredentialReference.Split('|');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new GrafanaProvisioningUnavailableException("Grafana data-source credential reference is corrupt.");
            }
        }

        var headerValue = rawReadToken is not null ? $"Bearer {rawReadToken}" : null;
        var dataSourceUrl = queryGatewayBaseUrl.TrimEnd('/') + "/";

        var state = await grafana.EnsureDataSourceAsync(grafanaOrgId, uid, app.Slug, dataSourceUrl, headerValue, cancellationToken);
        if (!string.Equals(state.Access, "proxy", StringComparison.Ordinal)
            || state.IsDefault
            || !string.Equals(state.Url, dataSourceUrl, StringComparison.Ordinal))
        {
            state = await grafana.EnsureDataSourceAsync(grafanaOrgId, uid, app.Slug, dataSourceUrl, headerValue, cancellationToken);
            if (!string.Equals(state.Access, "proxy", StringComparison.Ordinal)
                || state.IsDefault
                || !string.Equals(state.Url, dataSourceUrl, StringComparison.Ordinal))
            {
                throw new GrafanaProvisioningUnavailableException("Grafana data source did not converge to server-side (proxy) non-default access.");
            }
        }

        binding.ReconciliationStatus = "ready";
        binding.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return uid;
    }

    private async Task MarkBindingsUnavailableAsync(
        string organizationId, string selfrunUserId, string appId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow();
            var org = await db.GrafanaOrganizationBindings.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
            if (org is not null)
            {
                org.ReconciliationStatus = "unavailable";
                org.UpdatedAt = now;
            }

            var user = await db.GrafanaUserBindings.SingleOrDefaultAsync(
                x => x.OrganizationId == organizationId && x.SelfrunUserId == selfrunUserId, cancellationToken);
            if (user is not null)
            {
                user.ReconciliationStatus = "unavailable";
                user.UpdatedAt = now;
            }

            var ds = await db.GrafanaDataSourceBindings.SingleOrDefaultAsync(
                x => x.AppId == appId && x.DeletedAt == null, cancellationToken);
            if (ds is not null)
            {
                ds.ReconciliationStatus = "unavailable";
                ds.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record Grafana unavailability ({Reason}).", reason);
        }
    }

    internal sealed record ReadToken(string Prefix, string Token);

    internal ReadToken GenerateReadToken()
    {
        var visible = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var prefix = $"{ReadTokenPrefixRoot}_{visible}";
        return new ReadToken(prefix, $"{prefix}_{secret}");
    }

    internal static string HashToken(string token)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(token), salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2_sha256$210000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    internal static bool VerifyToken(string token, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(token), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
