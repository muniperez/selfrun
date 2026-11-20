using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Auth;

public static class BrowserSessionCookie
{
    public const string Name = "__Host-minicloud-session";

    /// <summary>
    /// Remembered sessions persist across browser restarts until their maximum lifetime; standard sessions use a
    /// browser-session cookie. The server-side record remains authoritative for both.
    /// </summary>
    public static void Append(HttpResponse response, string cookie, BrowserSessionDeadlines deadlines)
    {
        response.Cookies.Append(Name, cookie, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/",
            Expires = deadlines.Remembered ? deadlines.ExpiresAt : null
        });
    }

    public static AccountSessionResponse ToResponse(BrowserSession session, BrowserSessionDeadlines deadlines, string csrfToken) =>
        new(session.UserId, deadlines.ExpiresAt, deadlines.IdleExpiresAt, csrfToken,
            deadlines.Remembered, deadlines.RenewAfter, session.User?.KeepSignedIn ?? false);
}
