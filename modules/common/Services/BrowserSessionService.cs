using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed record CreatedBrowserSession(string Cookie, string CsrfToken, BrowserSession Session);

public sealed record BrowserSessionRotation(BrowserSessionWriteOutcome Outcome, CreatedBrowserSession? Created);

/// <summary>Effective deadlines of a session under the current operator policy.</summary>
/// <param name="RenewAfter">Earliest time a renewal is accepted; null once the idle window can no longer grow.</param>
public sealed record BrowserSessionDeadlines(
    bool Remembered,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset? RenewAfter);

/// <param name="EndedKratosSessionId">Set when this validation ended the local session because it expired.</param>
public sealed record BrowserSessionValidation(BrowserSession? Session, string? EndedKratosSessionId = null);

/// <summary>How a conditional write to a session generation was resolved at commit time.</summary>
public enum BrowserSessionWriteOutcome
{
    /// <summary>This call committed the change.</summary>
    Applied,
    /// <summary>The committed session already satisfied the request (for example a competing renewal won).</summary>
    Unchanged,
    /// <summary>The cookie generation was rotated by a newer sign-in or settings flow; that generation owns the row.</summary>
    Superseded,
    /// <summary>The session was revoked, logged out or removed by someone else.</summary>
    Revoked,
    /// <summary>The session had passed a deadline; this call revoked it.</summary>
    Expired,
    /// <summary>The initiating state changed; the caller must start again from the committed session.</summary>
    Conflict
}

/// <param name="Session">The committed state when the generation is still active; otherwise null.</param>
public sealed record BrowserSessionWrite(BrowserSessionWriteOutcome Outcome, BrowserSession? Session);

public sealed class BrowserSessionService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<PortalSessionOptions>? options = null)
{
    // Bounds compare-and-swap retries; each retry means another writer committed to the same row in between.
    private const int WriteAttempts = 4;
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

        var created = NewSession(userId, kratosIdentityId, kratosSessionId, remembered);
        db.BrowserSessions.Add(created.Session);
        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    private CreatedBrowserSession NewSession(
        string userId, string kratosIdentityId, string kratosSessionId, bool remembered)
    {
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

        var tokenHash = HashSecret(cookie);
        var session = await db.BrowserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        for (var attempt = 0; session is not null && attempt < WriteAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await db.Entry(session).ReloadAsync(cancellationToken);
                if (db.Entry(session).State == EntityState.Detached)
                {
                    break;
                }
            }
            if (session.TokenHash != tokenHash || session.RevokedAt is not null)
            {
                break;
            }

            var now = timeProvider.GetUtcNow();
            var deadlines = GetDeadlines(session);
            var expired = deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now;
            if (!expired && session.User.KratosIdentityId == session.KratosIdentityId)
            {
                session.LastSeenAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new BrowserSessionValidation(session);
            }

            // End only the generation and deadlines that were judged; a concurrent rotation or renewal is re-read.
            if (await WhereUnchanged(session).ExecuteUpdateAsync(
                    update => update.SetProperty(x => x.RevokedAt, now), cancellationToken) == 1)
            {
                await db.Entry(session).ReloadAsync(cancellationToken);
                return new BrowserSessionValidation(null, expired ? session.KratosSessionId : null);
            }
        }

        return new BrowserSessionValidation(null);
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
    public DateTimeOffset? GetRenewalTarget(BrowserSession session) =>
        GetRenewalTarget(GetDeadlines(session), timeProvider.GetUtcNow());

    private DateTimeOffset? GetRenewalTarget(BrowserSessionDeadlines deadlines, DateTimeOffset now)
    {
        if (deadlines.RenewAfter is null || now < deadlines.RenewAfter || deadlines.IdleExpiresAt <= now ||
            deadlines.ExpiresAt <= now)
            return null;
        var target = Min(deadlines.ExpiresAt, now.Add(_policy.IdleTimeout(deadlines.Remembered)));
        return target > deadlines.IdleExpiresAt ? target : null;
    }

    /// <summary>
    /// Renews the idle deadline of one cookie generation, deciding at commit time rather than when the request began.
    /// The committed row is re-read and must still carry <paramref name="tokenHash"/>, be unrevoked, be before its idle
    /// and absolute deadlines and be inside its renewal window under its current remembered flag; the write is then a
    /// compare-and-swap on every column that decision read. A renewal that raced a logout, revocation, rotation,
    /// expiry or preference change therefore cannot restore or extend the session, and competing renewals coalesce:
    /// one is <see cref="BrowserSessionWriteOutcome.Applied"/>, the others observe the renewed state as
    /// <see cref="BrowserSessionWriteOutcome.Unchanged"/> without extending it again.
    /// </summary>
    /// <param name="upstreamExpiresAt">Expiry of the upstream session, which caps the renewed idle deadline.</param>
    /// <exception cref="DbUpdateConcurrencyException">The session kept changing under the request.</exception>
    public async Task<BrowserSessionWrite> RenewAsync(
        string sessionId,
        string tokenHash,
        DateTimeOffset? upstreamExpiresAt,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < WriteAttempts; attempt++)
            {
                var current = await ReadCommittedAsync(sessionId, cancellationToken);
                if (Ended(current, tokenHash) is { } ended)
                {
                    return ended;
                }

                var now = timeProvider.GetUtcNow();
                var deadlines = GetDeadlines(current!);
                if (deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now)
                {
                    if (await WhereUnchanged(current!).ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.RevokedAt, now), cancellationToken) == 1)
                    {
                        return new BrowserSessionWrite(BrowserSessionWriteOutcome.Expired, null);
                    }
                    continue;
                }

                var target = GetRenewalTarget(deadlines, now);
                if (target is { } wanted && upstreamExpiresAt is { } upstream && upstream < wanted)
                {
                    target = upstream;
                }
                // Not due, already renewed by a competing request, or capped by the upstream session: report as is.
                if (target is not { } renewed || renewed <= deadlines.IdleExpiresAt)
                {
                    return new BrowserSessionWrite(BrowserSessionWriteOutcome.Unchanged, current);
                }

                if (await WhereUnchanged(current!).ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.IdleExpiresAt, renewed)
                        .SetProperty(x => x.RenewedAt, now)
                        .SetProperty(x => x.LastSeenAt, now), cancellationToken) == 1)
                {
                    current!.IdleExpiresAt = renewed;
                    current.RenewedAt = now;
                    current.LastSeenAt = now;
                    return new BrowserSessionWrite(BrowserSessionWriteOutcome.Applied, current);
                }
            }
            throw new DbUpdateConcurrencyException("The session changed while it was being renewed.");
        }
        finally
        {
            await ReloadTrackedAsync(sessionId, cancellationToken);
        }
    }

    /// <summary>
    /// Applies the user's "Keep me signed in" choice to the current session generation. Turning it on extends the
    /// maximum to the remembered lifetime from sign-in; turning it off applies standard limits measured from sign-in.
    /// The change is a compare-and-swap like <see cref="RenewAsync"/>, so it serializes deterministically with
    /// renewals, revocation and rotation; the stored preference follows the session change in the same transaction.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException">The session kept changing under the request.</exception>
    public async Task<BrowserSessionWrite> SetRememberedAsync(
        string sessionId,
        string tokenHash,
        bool remembered,
        CancellationToken cancellationToken)
    {
        var effective = remembered && _policy.KeepSignedInEnabled;
        try
        {
            for (var attempt = 0; attempt < WriteAttempts; attempt++)
            {
                var current = await ReadCommittedAsync(sessionId, cancellationToken);
                if (Ended(current, tokenHash) is { } ended)
                {
                    return ended;
                }

                var now = timeProvider.GetUtcNow();
                var next = Copy(current!);
                if (next.Persistent != effective)
                {
                    next.Persistent = effective;
                    next.ExpiresAt = next.CreatedAt.Add(_policy.MaxLifetime(effective));
                    next.IdleExpiresAt = effective
                        ? next.IdleExpiresAt
                        : Min(next.IdleExpiresAt, now.Add(_policy.IdleTimeout(false)));
                }
                var deadlines = GetDeadlines(next);
                DateTimeOffset? revokedAt = deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now ? now : null;
                next.RevokedAt = revokedAt;
                next.User.KeepSignedIn = remembered;
                next.User.UpdatedAt = now;

                var applied = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    if (await WhereUnchanged(current!).ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.Persistent, next.Persistent)
                            .SetProperty(x => x.ExpiresAt, next.ExpiresAt)
                            .SetProperty(x => x.IdleExpiresAt, next.IdleExpiresAt)
                            .SetProperty(x => x.RevokedAt, revokedAt), cancellationToken) != 1)
                    {
                        return false;
                    }
                    await db.Users.Where(x => x.Id == next.UserId).ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.KeepSignedIn, remembered)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return true;
                });
                if (applied)
                {
                    return revokedAt is null
                        ? new BrowserSessionWrite(BrowserSessionWriteOutcome.Applied, next)
                        : new BrowserSessionWrite(BrowserSessionWriteOutcome.Expired, null);
                }
            }
            throw new DbUpdateConcurrencyException("The session changed while its preference was being applied.");
        }
        finally
        {
            await ReloadTrackedAsync(sessionId, cancellationToken);
        }
    }

    private Task<BrowserSession?> ReadCommittedAsync(string sessionId, CancellationToken cancellationToken) =>
        db.BrowserSessions.AsNoTracking().Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

    private static BrowserSessionWrite? Ended(BrowserSession? current, string tokenHash) =>
        current is null ? new BrowserSessionWrite(BrowserSessionWriteOutcome.Revoked, null)
        : !string.Equals(current.TokenHash, tokenHash, StringComparison.Ordinal)
            ? new BrowserSessionWrite(BrowserSessionWriteOutcome.Superseded, null)
        : current.RevokedAt is not null || current.User.KratosIdentityId != current.KratosIdentityId
            ? new BrowserSessionWrite(BrowserSessionWriteOutcome.Revoked, null)
        : null;

    /// <summary>
    /// Matches the row only while it is still the same unrevoked generation with the lifetime the caller judged.
    /// <see cref="BrowserSession.LastSeenAt"/> is excluded so request bookkeeping never conflicts with a decision.
    /// </summary>
    private IQueryable<BrowserSession> WhereUnchanged(BrowserSession expected)
    {
        var (id, tokenHash, persistent) = (expected.Id, expected.TokenHash, expected.Persistent);
        var (createdAt, expiresAt, idleExpiresAt, renewedAt) =
            (expected.CreatedAt, expected.ExpiresAt, expected.IdleExpiresAt, expected.RenewedAt);
        return db.BrowserSessions.Where(x => x.Id == id && x.TokenHash == tokenHash && x.RevokedAt == null &&
            x.Persistent == persistent && x.CreatedAt == createdAt && x.ExpiresAt == expiresAt &&
            x.IdleExpiresAt == idleExpiresAt && x.RenewedAt == renewedAt);
    }

    private async Task ReloadTrackedAsync(string sessionId, CancellationToken cancellationToken)
    {
        var tracked = db.BrowserSessions.Local.FirstOrDefault(x => x.Id == sessionId);
        if (tracked is null)
        {
            return;
        }
        await db.Entry(tracked).ReloadAsync(cancellationToken);
        if (tracked.User is { } user && db.Entry(user).State != EntityState.Detached)
        {
            await db.Entry(user).ReloadAsync(cancellationToken);
        }
    }

    private static BrowserSession Copy(BrowserSession session) => new()
    {
        Id = session.Id,
        UserId = session.UserId,
        KratosIdentityId = session.KratosIdentityId,
        KratosSessionId = session.KratosSessionId,
        TokenHash = session.TokenHash,
        CsrfTokenHash = session.CsrfTokenHash,
        Persistent = session.Persistent,
        CreatedAt = session.CreatedAt,
        LastSeenAt = session.LastSeenAt,
        RenewedAt = session.RenewedAt,
        ExpiresAt = session.ExpiresAt,
        IdleExpiresAt = session.IdleExpiresAt,
        RevokedAt = session.RevokedAt,
        User = session.User
    };

    /// <summary>
    /// Reauthenticates only the live state captured before identity confirmation. A competing write wins without
    /// retrying against its newer state. Retiring the old binding and inserting a new one commit together.
    /// </summary>
    public async Task<BrowserSessionRotation> ReauthenticateAsync(
        BrowserSession expected,
        string kratosSessionId,
        CancellationToken cancellationToken)
    {
        // Keep the original decision immutable even when this context also tracks the session.
        expected = Copy(expected);
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var current = await ReadCommittedAsync(expected.Id, cancellationToken);
                if (Ended(current, expected.TokenHash) is { } ended)
                    return new BrowserSessionRotation(ended.Outcome, null);

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var now = timeProvider.GetUtcNow();
                if (IsExpired(expected))
                {
                    if (await WhereUnchanged(expected).ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.RevokedAt, now), cancellationToken) != 1)
                        return new BrowserSessionRotation(BrowserSessionWriteOutcome.Conflict, null);
                    await transaction.CommitAsync(cancellationToken);
                    return new BrowserSessionRotation(BrowserSessionWriteOutcome.Expired, null);
                }

                CreatedBrowserSession created;
                if (expected.KratosSessionId == kratosSessionId)
                {
                    var next = Copy(expected);
                    var cookie = CreateSecret();
                    var csrfToken = CreateCsrfToken(cookie, next.Id);
                    next.TokenHash = HashSecret(cookie);
                    next.CsrfTokenHash = HashSecret(csrfToken);
                    StartLifetime(next, expected.Persistent, now);
                    if (await WhereUnchanged(expected).ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.TokenHash, next.TokenHash)
                            .SetProperty(x => x.CsrfTokenHash, next.CsrfTokenHash)
                            .SetProperty(x => x.Persistent, next.Persistent)
                            .SetProperty(x => x.CreatedAt, next.CreatedAt)
                            .SetProperty(x => x.LastSeenAt, next.LastSeenAt)
                            .SetProperty(x => x.RenewedAt, next.RenewedAt)
                            .SetProperty(x => x.ExpiresAt, next.ExpiresAt)
                            .SetProperty(x => x.IdleExpiresAt, next.IdleExpiresAt), cancellationToken) != 1)
                        return new BrowserSessionRotation(BrowserSessionWriteOutcome.Conflict, null);
                    created = new CreatedBrowserSession(cookie, csrfToken, next);
                }
                else
                {
                    if (await db.BrowserSessions.AnyAsync(x => x.KratosSessionId == kratosSessionId, cancellationToken))
                        return new BrowserSessionRotation(BrowserSessionWriteOutcome.Superseded, null);
                    if (await WhereUnchanged(expected).ExecuteUpdateAsync(
                            update => update.SetProperty(x => x.RevokedAt, now), cancellationToken) != 1)
                        return new BrowserSessionRotation(BrowserSessionWriteOutcome.Conflict, null);
                    created = NewSession(expected.UserId, expected.KratosIdentityId, kratosSessionId, expected.Persistent);
                    db.BrowserSessions.Add(created.Session);
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    finally
                    {
                        // A rolled-back insert must not remain pending if the execution strategy retries.
                        db.Entry(created.Session).State = EntityState.Detached;
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                return new BrowserSessionRotation(BrowserSessionWriteOutcome.Applied, created);
            });
        }
        finally
        {
            await ReloadTrackedAsync(expected.Id, cancellationToken);
        }
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

    /// <summary>Reads the committed row, bypassing any instance tracked earlier in the request.</summary>
    public Task<BrowserSession?> FindCommittedAsync(string sessionId, CancellationToken cancellationToken) =>
        ReadCommittedAsync(sessionId, cancellationToken);

    public bool IsExpired(BrowserSession session)
    {
        var now = timeProvider.GetUtcNow();
        var deadlines = GetDeadlines(session);
        return deadlines.ExpiresAt <= now || deadlines.IdleExpiresAt <= now;
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
