using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController]
[Route("api/account")]
public sealed class AccountController(
    BootstrapService bootstrap,
    BrowserSessionService sessions,
    GrafanaSessionService grafanaSessions,
    IKratosSessionValidator kratosSessions,
    IOptions<KratosOptions> kratosOptions,
    IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("capabilities")]
    public async Task<IResult> GetCapabilities(CancellationToken cancellationToken)
    {
        NoStore();
        var installationId = configuration["Bootstrap:InstallationId"];
        string state;
        try
        {
            state = await GetBootstrapStateAsync(installationId, cancellationToken);
        }
        catch (BootstrapException exception) when (exception.Code == "auth_unavailable")
        {
            return EndpointResults.Problem("auth_unavailable", "Identity service is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(new AccountCapabilitiesResponse(
            Password: true,
            Google: kratosOptions.Value.GoogleEnabled,
            EmailRecovery: kratosOptions.Value.EmailRecoveryEnabled,
            BootstrapState: state,
            KeepSignedIn: sessions.Policy.KeepSignedInEnabled));
    }

    [Authorize]
    [HttpGet("session")]
    public async Task<IResult> GetSession(CancellationToken cancellationToken)
    {
        NoStore();
        var (session, _, csrfToken) = await LoadCurrentSessionAsync(cancellationToken);
        return session is null
            ? Unauthenticated()
            : Results.Ok(BrowserSessionCookie.ToResponse(session, sessions.GetDeadlines(session), csrfToken!));
    }

    /// <summary>
    /// Renews the idle deadline of the current session once it is inside its renewal window (the last half of the
    /// idle timeout), never past its maximum lifetime, and extends the upstream Kratos session when it would otherwise
    /// end first. Eligibility is decided again when the renewal commits, against the cookie generation that made the
    /// request: calls outside the window, or that lost to a competing renewal, return the committed session
    /// unchanged, and a session logged out, revoked, rotated or expired meanwhile is never restored. The cookie and
    /// CSRF token are not rotated.
    /// </summary>
    [Authorize]
    [HttpPost("session/renew")]
    public async Task<IResult> RenewSession(CancellationToken cancellationToken)
    {
        NoStore();
        var (session, cookie, csrfToken) = await LoadCurrentSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthenticated();
        }

        var target = sessions.GetRenewalTarget(session);
        if (target is null)
        {
            return Results.Ok(BrowserSessionCookie.ToResponse(session, sessions.GetDeadlines(session), csrfToken!));
        }

        var generation = BrowserSessionService.HashSecret(cookie!);
        var upstream = await kratosSessions.GetActiveSessionAsync(session.KratosSessionId, session.KratosIdentityId, cancellationToken);
        var extended = false;
        if (upstream?.ExpiresAt is { } upstreamExpiry && upstreamExpiry < target)
        {
            // From here on the upstream session may change, so finish (or compensate) even if the client goes away.
            extended = true;
            try
            {
                upstream = await kratosSessions.ExtendSessionAsync(session.KratosSessionId, session.KratosIdentityId, CancellationToken.None);
            }
            catch (KratosUnavailableException)
            {
                // The extension may still have been applied; never let it outlive a session that ended meanwhile.
                await EndUpstreamIfLocallyEndedAsync(session.Id, session.KratosSessionId);
                throw;
            }
        }
        if (upstream is null)
        {
            await sessions.RevokeByIdAsync(session.Id, CancellationToken.None);
            ClearAuthCookies();
            return Unauthenticated();
        }

        BrowserSessionWrite renewal;
        try
        {
            renewal = await sessions.RenewAsync(session.Id, generation, upstream.ExpiresAt, CancellationToken.None);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SessionConflict();
        }
        switch (renewal.Outcome)
        {
            case BrowserSessionWriteOutcome.Applied or BrowserSessionWriteOutcome.Unchanged:
                return Results.Ok(BrowserSessionCookie.ToResponse(renewal.Session!, sessions.GetDeadlines(renewal.Session!), csrfToken!));
            case BrowserSessionWriteOutcome.Expired:
                await RevokeUpstreamAsync(session.KratosSessionId);
                return Unauthenticated();
            default:
                // Revoked or superseded meanwhile. Whoever ended the session may have revoked upstream before this
                // extension landed, so revoke again unless a newer generation still uses the upstream session.
                if (extended)
                {
                    await EndUpstreamIfLocallyEndedAsync(session.Id, session.KratosSessionId);
                }
                return Unauthenticated();
        }
    }

    /// <summary>Stores the user's "Keep me signed in" preference and applies it to the current session.</summary>
    [Authorize]
    [HttpPut("session/preference")]
    public async Task<IResult> SetSessionPreference(
        [FromBody] AccountSessionPreferenceRequest request,
        CancellationToken cancellationToken)
    {
        NoStore();
        var (session, cookie, csrfToken) = await LoadCurrentSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthenticated();
        }
        if (request.KeepSignedIn && !sessions.Policy.KeepSignedInEnabled)
        {
            return EndpointResults.Problem("keep_signed_in_unavailable",
                "This instance does not allow staying signed in.", StatusCodes.Status409Conflict);
        }

        BrowserSessionWrite updated;
        try
        {
            updated = await sessions.SetRememberedAsync(session.Id, BrowserSessionService.HashSecret(cookie!),
                request.KeepSignedIn, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SessionConflict();
        }
        switch (updated.Outcome)
        {
            case BrowserSessionWriteOutcome.Applied:
                var deadlines = sessions.GetDeadlines(updated.Session!);
                BrowserSessionCookie.Append(Response, cookie!, deadlines);
                return Results.Ok(BrowserSessionCookie.ToResponse(updated.Session!, deadlines, csrfToken!));
            case BrowserSessionWriteOutcome.Expired:
                await RevokeUpstreamAsync(session.KratosSessionId);
                ClearAuthCookies();
                return Unauthenticated();
            default:
                // Revoked or superseded meanwhile: the revoker or the newer generation owns the upstream session.
                return Unauthenticated();
        }
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IResult> Logout(CancellationToken cancellationToken)
    {
        NoStore();
        var principal = RequirePrincipal(HttpContext);
        var cookie = Request.Cookies["__Host-minicloud-session"];
        if (!principal.IsDashboardUser || principal.UserId is null ||
            string.IsNullOrWhiteSpace(cookie) || principal.BrowserSessionId is null)
        {
            ClearAuthCookies();
            return EndpointResults.Problem("unauthenticated", "A browser session is required.", StatusCodes.Status401Unauthorized);
        }

        var session = await sessions.FindByIdAsync(principal.BrowserSessionId, cancellationToken);
        await sessions.RevokeByIdAsync(principal.BrowserSessionId, cancellationToken);
        await grafanaSessions.RevokeUserSessionsAsync(principal.UserId, cancellationToken);
        ClearAuthCookies();
        if (session is not null)
        {
            try
            {
                await kratosSessions.RevokeSessionAsync(session.KratosSessionId, cancellationToken);
            }
            catch (KratosUnavailableException)
            {
                return EndpointResults.Problem("auth_unavailable", "Identity service is temporarily unavailable.", StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.NoContent();
    }

    private async Task<(Minicloud.Common.Models.BrowserSession? Session, string? Cookie, string? CsrfToken)> LoadCurrentSessionAsync(
        CancellationToken cancellationToken)
    {
        var principal = RequirePrincipal(HttpContext);
        var cookie = Request.Cookies[BrowserSessionCookie.Name];
        var sessionId = principal.BrowserSessionId ?? User.FindFirstValue("browser_session_id");
        if (!principal.IsDashboardUser || principal.UserId is null ||
            string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(sessionId))
        {
            return (null, null, null);
        }

        var session = await sessions.FindByIdAsync(sessionId, cancellationToken);
        var csrfToken = session is null ? null : sessions.GetCsrfToken(session, cookie);
        return session is null || session.UserId != principal.UserId || session.RevokedAt is not null || csrfToken is null
            ? (null, null, null)
            : (session, cookie, csrfToken);
    }

    private static IResult Unauthenticated() =>
        EndpointResults.Problem("unauthenticated", "A browser session is required.", StatusCodes.Status401Unauthorized);

    private static IResult SessionConflict() =>
        EndpointResults.Problem("session_conflict", "The session changed. Try again.", StatusCodes.Status409Conflict);

    /// <summary>
    /// Revokes the upstream session when no active local session is bound to it any more. A row keeps its upstream
    /// binding across rotations, so the row is re-read by ID whatever generation it now holds. Local revocation stays
    /// authoritative either way: a revoked generation is never reactivated, and binding the upstream session again
    /// requires a fresh sign-in.
    /// </summary>
    private async Task EndUpstreamIfLocallyEndedAsync(string sessionId, string kratosSessionId)
    {
        var current = await sessions.FindCommittedAsync(sessionId, CancellationToken.None);
        if (current is null || current.RevokedAt is not null || sessions.IsExpired(current))
        {
            await RevokeUpstreamAsync(kratosSessionId);
        }
    }

    private async Task RevokeUpstreamAsync(string kratosSessionId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await kratosSessions.RevokeSessionAsync(kratosSessionId, timeout.Token);
        }
        catch (Exception exception) when (exception is KratosUnavailableException or OperationCanceledException)
        {
            // The local session has ended and cannot be restored; the upstream session expires on its own.
        }
    }

    private async Task<string> GetBootstrapStateAsync(string? installationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installationId))
        {
            return "blocked";
        }

        try
        {
            var status = await bootstrap.StatusAsync(installationId, cancellationToken);
            return status.State switch
            {
                "required" or "in_progress" => "required",
                "ready" => "ready",
                _ => "blocked"
            };
        }
        catch (BootstrapException exception) when (exception.Code == "setup_state_conflict")
        {
            return "blocked";
        }
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private void ClearAuthCookies()
    {
        var names = Request.Cookies.Keys
            .Where(name => name.StartsWith("csrf_token_", StringComparison.Ordinal))
            .Append("__Host-minicloud-session")
            .Append("ory_kratos_session")
            .Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            Response.Cookies.Delete(name, new CookieOptions
            {
                Path = "/",
                Secure = true,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax
            });
        }
    }
}
