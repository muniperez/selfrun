using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    /// end first. Calls outside the window return the unchanged session, so concurrent tabs coalesce safely.
    /// </summary>
    [Authorize]
    [HttpPost("session/renew")]
    public async Task<IResult> RenewSession(CancellationToken cancellationToken)
    {
        NoStore();
        var (session, _, csrfToken) = await LoadCurrentSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthenticated();
        }

        var target = sessions.GetRenewalTarget(session);
        if (target is not null)
        {
            var upstream = await kratosSessions.GetActiveSessionAsync(session.KratosSessionId, session.KratosIdentityId, cancellationToken);
            if (upstream?.ExpiresAt is { } upstreamExpiry && upstreamExpiry < target)
            {
                upstream = await kratosSessions.ExtendSessionAsync(session.KratosSessionId, session.KratosIdentityId, cancellationToken);
            }
            if (upstream is null)
            {
                await sessions.RevokeByIdAsync(session.Id, cancellationToken);
                ClearAuthCookies();
                return Unauthenticated();
            }
            if (upstream.ExpiresAt is { } cappedExpiry && cappedExpiry < target)
            {
                target = cappedExpiry;
            }
            // Compare with the effective deadline: a shortened policy leaves the stored deadline later than it.
            if (target > sessions.GetDeadlines(session).IdleExpiresAt &&
                !await sessions.RenewAsync(session.Id, target.Value, cancellationToken))
            {
                return Unauthenticated();
            }
        }

        return Results.Ok(BrowserSessionCookie.ToResponse(session, sessions.GetDeadlines(session), csrfToken!));
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

        var updated = await sessions.SetRememberedAsync(session.Id, request.KeepSignedIn, cancellationToken);
        if (updated is null || updated.RevokedAt is not null)
        {
            try
            {
                await kratosSessions.RevokeSessionAsync(session.KratosSessionId, cancellationToken);
            }
            catch (KratosUnavailableException)
            {
                // The local session is already revoked; the upstream session expires on its own.
            }
            ClearAuthCookies();
            return Unauthenticated();
        }

        var deadlines = sessions.GetDeadlines(updated);
        BrowserSessionCookie.Append(Response, cookie!, deadlines);
        return Results.Ok(BrowserSessionCookie.ToResponse(updated, deadlines, csrfToken!));
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
