using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BrowserSessionServiceTests
{
    [Fact]
    public async Task PersistsOnlyHashesAndBindsCsrfToTheSessionCookie()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);

        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        var stored = await db.BrowserSessions.SingleAsync();

        Assert.NotEqual(created.Cookie, stored.TokenHash);
        Assert.Equal(BrowserSessionService.HashSecret(created.Cookie), stored.TokenHash);
        Assert.NotEqual(created.CsrfToken, stored.CsrfTokenHash);
        Assert.True(sessions.ValidateCsrf(stored, created.CsrfToken));
        Assert.False(sessions.ValidateCsrf(stored, created.CsrfToken + "x"));
        Assert.Equal(created.CsrfToken, sessions.GetCsrfToken(stored, created.Cookie));
        Assert.Null(sessions.GetCsrfToken(stored, "different-cookie"));
    }

    [Fact]
    public async Task RequestsDoNotSlideIdleExpiryAndRenewalIsWindowedAndCappedAtTheMaximum()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateSeededDb(connection);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock);
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        var start = clock.GetUtcNow();
        Assert.False(created.Session.Persistent);
        Assert.Equal(start.AddHours(12), created.Session.ExpiresAt);
        Assert.Equal(start.AddHours(2), created.Session.IdleExpiresAt);

        clock.Advance(TimeSpan.FromMinutes(59));
        var validated = (await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session!;
        Assert.Equal(start.AddHours(2), validated.IdleExpiresAt);
        Assert.Null(sessions.GetRenewalTarget(validated));
        Assert.Equal(start.AddHours(1), sessions.GetDeadlines(validated).RenewAfter);

        // Every renewal inside the window moves the idle deadline, but never past the 12-hour maximum.
        clock.Advance(TimeSpan.FromMinutes(31));
        for (var renewal = 0; renewal < 10; renewal++)
        {
            var target = sessions.GetRenewalTarget(validated);
            Assert.NotNull(target);
            Assert.True(await sessions.RenewAsync(validated.Id, target.Value, CancellationToken.None));
            Assert.Null(sessions.GetRenewalTarget(validated));
            clock.Advance(TimeSpan.FromMinutes(60));
        }
        Assert.Equal(start.AddHours(12), sessions.GetDeadlines(validated).IdleExpiresAt);
        Assert.Null(sessions.GetDeadlines(validated).RenewAfter);
        Assert.Null(sessions.GetRenewalTarget(validated));

        clock.Advance(TimeSpan.FromMinutes(30));
        var expired = await sessions.ValidateAsync(created.Cookie, CancellationToken.None);
        Assert.Null(expired.Session);
        Assert.Equal("kratos_session_1", expired.EndedKratosSessionId);
        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task UnrenewedSessionEndsAtItsIdleDeadlineEvenWhenRequestsContinue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateSeededDb(connection);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock);
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);

        for (var minute = 0; minute < 119; minute += 10)
        {
            Assert.NotNull((await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session);
            clock.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.Null((await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session);
    }

    [Fact]
    public async Task RememberedSessionsFollowCurrentOperatorPolicy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateSeededDb(connection);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var policy = new PortalSessionOptions();
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock, Options.Create(policy));
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", remembered: true, CancellationToken.None);
        var start = clock.GetUtcNow();
        Assert.True(created.Session.Persistent);
        Assert.Equal(start.AddDays(30), created.Session.ExpiresAt);
        Assert.Equal(start.AddDays(7), created.Session.IdleExpiresAt);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.NotNull((await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session);

        // Shortening policy applies immediately; disabling remembered sessions applies the standard policy.
        policy.RememberedIdleMinutes = 24 * 60;
        Assert.Null((await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session);

        var second = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_2", remembered: true, CancellationToken.None);
        policy.KeepSignedInEnabled = false;
        var deadlines = sessions.GetDeadlines(second.Session);
        Assert.False(deadlines.Remembered);
        Assert.Equal(clock.GetUtcNow().AddHours(12), deadlines.ExpiresAt);
        Assert.Equal(clock.GetUtcNow().AddHours(2), deadlines.IdleExpiresAt);
        var third = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_3", remembered: true, CancellationToken.None);
        Assert.False(third.Session.Persistent);
    }

    [Fact]
    public async Task RenewalCannotRestoreARevokedSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateSeededDb(connection);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock);
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(90));
        var target = sessions.GetRenewalTarget(created.Session);
        Assert.NotNull(target);

        await sessions.RevokeUserSessionsAsync("usr_1", CancellationToken.None);

        Assert.False(await sessions.RenewAsync(created.Session.Id, target.Value, CancellationToken.None));
        Assert.Null((await sessions.ValidateAsync(created.Cookie, CancellationToken.None)).Session);
    }

    [Fact]
    public async Task ChangingThePreferenceUpdatesTheUserAndCurrentSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateSeededDb(connection);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock);
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        var start = clock.GetUtcNow();

        var remembered = (await sessions.SetRememberedAsync(created.Session.Id, true, CancellationToken.None))!;
        Assert.True(remembered.Persistent);
        Assert.True(remembered.User.KeepSignedIn);
        Assert.Equal(start.AddDays(30), sessions.GetDeadlines(remembered).ExpiresAt);

        // Turning it off measures the standard maximum from sign-in, so an older session ends.
        clock.Advance(TimeSpan.FromHours(13));
        var standard = (await sessions.SetRememberedAsync(created.Session.Id, false, CancellationToken.None))!;
        Assert.False(standard.Persistent);
        Assert.False(standard.User.KeepSignedIn);
        Assert.NotNull(standard.RevokedAt);
    }

    [Fact]
    public async Task RevokingUserSessionsPreservesTheUserAndMembership()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test", CreatedAt = now, UpdatedAt = now });
        db.Organizations.Add(new Organization { Id = "org_1", Name = "Org", Slug = "org", CreatedAt = now, UpdatedAt = now });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "mem_1", UserId = "usr_1", OrganizationId = "org_1", Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);
        await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);

        await sessions.RevokeUserSessionsAsync("usr_1", CancellationToken.None);

        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Equal("usr_1", (await db.Users.SingleAsync()).Id);
        Assert.Equal("mem_1", (await db.OrganizationMembers.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(0, 720, 10080, 43200)]
    [InlineData(120, 60, 10080, 43200)]
    [InlineData(120, 720, 60, 43200)]
    [InlineData(120, 720, 10080, 600)]
    [InlineData(120, 720, 10080, 200000)]
    public void InvalidPortalSessionPolicyIsRejected(int standardIdle, int standardMax, int rememberedIdle, int rememberedMax)
    {
        var options = new PortalSessionOptions
        {
            StandardIdleMinutes = standardIdle,
            StandardMaxMinutes = standardMax,
            RememberedIdleMinutes = rememberedIdle,
            RememberedMaxMinutes = rememberedMax
        };
        Assert.True(new PortalSessionOptionsValidator().Validate(null, options).Failed);
        Assert.True(new PortalSessionOptionsValidator().Validate(null, new PortalSessionOptions()).Succeeded);
    }

    private static async Task<MinicloudDbContext> CreateSeededDb(SqliteConnection connection)
    {
        var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
        await db.SaveChangesAsync();
        return db;
    }

    private static MinicloudDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
