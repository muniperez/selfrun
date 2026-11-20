using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed record CreatedBrowserSession(string Cookie, string CsrfToken, BrowserSession Session);

/// <summary>Effective deadlines of a session under the current operator policy.</summary>
/// <param name="RenewAfter">Earliest time a renewal is accepted; null once the idle window can no longer grow.</param>
public sealed record BrowserSessionDeadlines(
    bool Remembered,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset? RenewAfter);

/// <param name="EndedKratosSessionId">Set when this validation ended the local session because it expired.</param>
public sealed record BrowserSessionValidation(BrowserSession? Session, string? EndedKratosSessionId = null);

public sealed class BrowserSessionService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<PortalSessionOptions>? options = null)
{
    private readonly PortalSessionOptions _policy = options?.Value ?? new PortalSessionOptions();

    public PortalSessionOptions Policy => _policy;

    public Task<CreatedBrowserSession> CreateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        CancellationToken cancellationToken) =>
        CreateAsync(userId, kratosIdentityId, kratosSessionId, remembered: false, cancellationToken);

    public async Task<CreatedBrowserSession> CreateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        bool remembered,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || user.KratosIdentityId != kratosIdentityId)
        {
            throw new InvalidOperationException("The identity is not bound to this Minicloud user.");
        }

        var now = timeProvider.GetUtcNow();
        var cookie = CreateSecret();
        var sessionId = ids.NewId("bsess");
        var csrfToken = CreateCsrfToken(cookie, sessionId);
        var session = new BrowserSession
        {
            Id = sessionId,
            UserId = userId,
            KratosIdentityId = kratosIdentityId,
            KratosSessionId = kratosSessionId,
            TokenHash = HashSecret(cookie),
            CsrfTokenHash = HashSecret(csrfToken)
        };
        StartLifetime(session, remembered, now);
        db.BrowserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedBrowserSession(cookie, csrfToken, session);
    }

    /// <summary>
    /// Validates the cookie without extending it. Idle windows move only through <see cref="RenewAsync"/>, so
    /// background reads cannot keep an unattended session alive.
    /// </summary>
    public async Task<BrowserSessionValidation> ValidateAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return new BrowserSessionValidation(null);
        }

        var session = await db.BrowserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == HashSecret(cookie), cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return new BrowserSessionValidation(null);
        }

        var now = timeProvider.GetUtcNow();
        var deadlines = GetDeadlines(session);
        var expired = deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now;
        if (expired || session.User.KratosIdentityId != session.KratosIdentityId)
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new BrowserSessionValidation(null, expired ? session.KratosSessionId : null);
        }

        session.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new BrowserSessionValidation(session);
    }

    public BrowserSessionDeadlines GetDeadlines(BrowserSession session)
    {
        var remembered = session.Persistent && _policy.KeepSignedInEnabled;
        var idleTimeout = _policy.IdleTimeout(remembered);
        // Shortened operator policy applies to existing sessions immediately; longer policy applies at renewal.
        var expiresAt = Min(session.ExpiresAt, session.CreatedAt.Add(_policy.MaxLifetime(remembered)));
        var idleExpiresAt = Min(Min(session.IdleExpiresAt, session.RenewedAt.Add(idleTimeout)), expiresAt);
        DateTimeOffset? renewAfter = idleExpiresAt < expiresAt
            ? Max(session.RenewedAt, idleExpiresAt - idleTimeout / 2)
            : null;
        return new BrowserSessionDeadlines(remembered, expiresAt, idleExpiresAt, renewAfter);
    }

    /// <summary>The idle deadline an accepted renewal would set now, or null when renewal is not yet due.</summary>
    public DateTimeOffset? GetRenewalTarget(BrowserSession session)
    {
        var now = timeProvider.GetUtcNow();
        var deadlines = GetDeadlines(session);
        if (deadlines.RenewAfter is null || now < deadlines.RenewAfter || deadlines.IdleExpiresAt <= now ||
            deadlines.ExpiresAt <= now)
            return null;
        var target = Min(deadlines.ExpiresAt, now.Add(_policy.IdleTimeout(deadlines.Remembered)));
        return target > deadlines.IdleExpiresAt ? target : null;
    }

    /// <summary>
    /// Moves the idle deadline of a still-active session. The update is conditional on the session not being revoked,
    /// so a renewal racing a logout or revocation can never restore it.
    /// </summary>
    public async Task<bool> RenewAsync(string sessionId, DateTimeOffset idleExpiresAt, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var updated = await db.BrowserSessions
            .Where(x => x.Id == sessionId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.IdleExpiresAt, idleExpiresAt)
                .SetProperty(x => x.RenewedAt, now)
                .SetProperty(x => x.LastSeenAt, now), cancellationToken);
        var tracked = db.BrowserSessions.Local.FirstOrDefault(x => x.Id == sessionId);
        if (tracked is not null)
        {
            await db.Entry(tracked).ReloadAsync(cancellationToken);
        }
        return updated == 1;
    }

    /// <summary>
    /// Applies the user's "Keep me signed in" choice to their current session. Turning it on extends the maximum to the
    /// remembered lifetime from sign-in; turning it off applies standard limits measured from sign-in.
    /// </summary>
    public async Task<BrowserSession?> SetRememberedAsync(string sessionId, bool remembered, CancellationToken cancellationToken)
    {
        var session = await db.BrowserSessions.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return null;
        }

        var effective = remembered && _policy.KeepSignedInEnabled;
        var now = timeProvider.GetUtcNow();
        session.User.KeepSignedIn = remembered;
        session.User.UpdatedAt = now;
        if (session.Persistent != effective)
        {
            session.Persistent = effective;
            session.ExpiresAt = session.CreatedAt.Add(_policy.MaxLifetime(effective));
            session.IdleExpiresAt = effective
                ? session.IdleExpiresAt
                : Min(session.IdleExpiresAt, now.Add(_policy.IdleTimeout(false)));
        }
        var deadlines = GetDeadlines(session);
        if (deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now)
        {
            session.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<CreatedBrowserSession> CreateOrRotateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        CancellationToken cancellationToken) =>
        await CreateOrRotateAsync(userId, kratosIdentityId, kratosSessionId, remembered: false, cancellationToken);

    public async Task<CreatedBrowserSession> CreateOrRotateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        bool remembered,
        CancellationToken cancellationToken)
    {
        var existing = await db.BrowserSessions.SingleOrDefaultAsync(
            x => x.KratosSessionId == kratosSessionId, cancellationToken);
        if (existing is null)
            return await CreateAsync(userId, kratosIdentityId, kratosSessionId, remembered, cancellationToken);

        if (existing.UserId != userId || existing.KratosIdentityId != kratosIdentityId ||
            !await db.Users.AnyAsync(x => x.Id == userId && x.KratosIdentityId == kratosIdentityId, cancellationToken))
            throw new InvalidOperationException("The identity is not bound to this Minicloud user.");

        // Kratos settings can retain its session ID; rotate the local secret, not the unique upstream binding.
        var now = timeProvider.GetUtcNow();
        var cookie = CreateSecret();
        var csrfToken = CreateCsrfToken(cookie, existing.Id);
        existing.TokenHash = HashSecret(cookie);
        existing.CsrfTokenHash = HashSecret(csrfToken);
        StartLifetime(existing, remembered, now);
        existing.RevokedAt = null;
        db.Entry(existing).Property(x => x.RevokedAt).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedBrowserSession(cookie, csrfToken, existing);
    }

    private void StartLifetime(BrowserSession session, bool remembered, DateTimeOffset now)
    {
        var effective = remembered && _policy.KeepSignedInEnabled;
        session.Persistent = effective;
        session.CreatedAt = now;
        session.LastSeenAt = now;
        session.RenewedAt = now;
        session.ExpiresAt = now.Add(_policy.MaxLifetime(effective));
        session.IdleExpiresAt = Min(session.ExpiresAt, now.Add(_policy.IdleTimeout(effective)));
    }

    public async Task RevokeAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        var session = await db.BrowserSessions
            .SingleOrDefaultAsync(x => x.TokenHash == HashSecret(cookie), cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RevokeByIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await db.BrowserSessions
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public string? GetCsrfToken(BrowserSession session, string cookie)
    {
        var csrfToken = CreateCsrfToken(cookie, session.Id);
        return VerifySecret(session.CsrfTokenHash, csrfToken) ? csrfToken : null;
    }

    public async Task<BrowserSession?> FindByIdAsync(string sessionId, CancellationToken cancellationToken) =>
        await db.BrowserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

    public async Task RevokeUserSessionsAsync(string userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.BrowserSessions
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    public bool ValidateCsrf(BrowserSession session, string? csrfToken)
    {
        return !string.IsNullOrWhiteSpace(csrfToken) && VerifySecret(session.CsrfTokenHash, csrfToken);
    }

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string CreateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string CreateCsrfToken(string cookie, string sessionId)
    {
        var key = Encoding.UTF8.GetBytes(cookie);
        var value = Encoding.UTF8.GetBytes(sessionId);
        return Convert.ToBase64String(HMACSHA256.HashData(key, value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool VerifySecret(string storedHash, string secret)
    {
        var expected = Convert.FromHexString(storedHash);
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left < right ? left : right;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left > right ? left : right;
}
