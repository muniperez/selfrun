using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            var renewed = await sessions.RenewAsync(validated.Id, validated.TokenHash, null, CancellationToken.None);
            Assert.Equal(BrowserSessionWriteOutcome.Applied, renewed.Outcome);
            Assert.Equal(target, renewed.Session!.IdleExpiresAt);
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

        var renewal = await sessions.RenewAsync(created.Session.Id, created.Session.TokenHash, null, CancellationToken.None);
        Assert.Equal(BrowserSessionWriteOutcome.Revoked, renewal.Outcome);
        Assert.Null(renewal.Session);
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

        var remembered = (await sessions.SetRememberedAsync(created.Session.Id, created.Session.TokenHash, true,
            CancellationToken.None)).Session!;
        Assert.True(remembered.Persistent);
        Assert.True(remembered.User.KeepSignedIn);
        Assert.Equal(start.AddDays(30), sessions.GetDeadlines(remembered).ExpiresAt);

        // Turning it off measures the standard maximum from sign-in, so an older session ends.
        clock.Advance(TimeSpan.FromHours(13));
        var standard = await sessions.SetRememberedAsync(created.Session.Id, created.Session.TokenHash, false,
            CancellationToken.None);
        Assert.Equal(BrowserSessionWriteOutcome.Expired, standard.Outcome);
        var stored = await db.BrowserSessions.AsNoTracking().Include(x => x.User).SingleAsync();
        Assert.False(stored.Persistent);
        Assert.False(stored.User.KeepSignedIn);
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(BrowserSessionWriteOutcome.Superseded, (await sessions.SetRememberedAsync(
            created.Session.Id, "another-generation", true, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task RenewalRacingLogoutCannotRestoreTheSession()
    {
        await using var race = await Race.StartAsync();
        race.Clock.Advance(TimeSpan.FromMinutes(90));
        race.BeforeWrite = () => race.Competitor().RevokeAsync(race.Created.Cookie, CancellationToken.None);

        var renewal = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

        Assert.Equal(BrowserSessionWriteOutcome.Revoked, renewal.Outcome);
        var stored = await race.StoredAsync();
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(race.Created.Session.IdleExpiresAt, stored.IdleExpiresAt);
        Assert.Equal(race.Created.Session.RenewedAt, stored.RenewedAt);
    }

    [Fact]
    public async Task RenewalRacingRotationLeavesTheNewGenerationAlone()
    {
        await using var race = await Race.StartAsync();
        race.Clock.Advance(TimeSpan.FromMinutes(90));
        CreatedBrowserSession? rotated = null;
        race.BeforeWrite = async () => rotated = await race.Competitor().CreateOrRotateAsync(
            "usr_1", "identity_1", "kratos_session_1", remembered: true, CancellationToken.None);

        var renewal = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

        Assert.Equal(BrowserSessionWriteOutcome.Superseded, renewal.Outcome);
        var stored = await race.StoredAsync();
        var now = race.Clock.GetUtcNow();
        Assert.Equal(BrowserSessionService.HashSecret(rotated!.Cookie), stored.TokenHash);
        Assert.Null(stored.RevokedAt);
        Assert.True(stored.Persistent);
        Assert.Equal(now.AddDays(7), stored.IdleExpiresAt);
        Assert.Equal(now.AddDays(30), stored.ExpiresAt);
        Assert.Equal(BrowserSessionWriteOutcome.Superseded,
            (await race.Competitor().RenewAsync(race.Id, race.Generation, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task CompetingRenewalsCoalesceIntoOneExtension()
    {
        await using var race = await Race.StartAsync();
        race.Clock.Advance(TimeSpan.FromMinutes(90));
        var renewedAt = race.Clock.GetUtcNow();
        BrowserSessionWrite? first = null;
        race.BeforeWrite = async () =>
        {
            first = await race.Competitor().RenewAsync(race.Id, race.Generation, null, CancellationToken.None);
            race.Clock.Advance(TimeSpan.FromMinutes(1));
        };

        var second = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

        Assert.Equal(BrowserSessionWriteOutcome.Applied, first!.Outcome);
        Assert.Equal(BrowserSessionWriteOutcome.Unchanged, second.Outcome);
        Assert.Equal(renewedAt.AddHours(2), second.Session!.IdleExpiresAt);
        Assert.Equal(renewedAt, second.Session.RenewedAt);
        var stored = await race.StoredAsync();
        Assert.Equal(renewedAt.AddHours(2), stored.IdleExpiresAt);
        Assert.Equal(renewedAt, stored.RenewedAt);
    }

    [Fact]
    public async Task RenewalReevaluatesExpiryAndTheUpstreamCapWhenItCommits()
    {
        await using var race = await Race.StartAsync();
        race.Clock.Advance(TimeSpan.FromMinutes(90));
        var now = race.Clock.GetUtcNow();
        var capped = await race.Renewing.RenewAsync(race.Id, race.Generation, now.AddHours(1), CancellationToken.None);
        Assert.Equal(BrowserSessionWriteOutcome.Applied, capped.Outcome);
        Assert.Equal(now.AddHours(1), capped.Session!.IdleExpiresAt);
        Assert.Equal(BrowserSessionWriteOutcome.Unchanged,
            (await race.Renewing.RenewAsync(race.Id, race.Generation, now.AddHours(1), CancellationToken.None)).Outcome);

        // The idle deadline passes while the upstream session is being extended.
        race.Clock.Advance(TimeSpan.FromMinutes(45));
        Assert.NotNull(race.Renewing.GetRenewalTarget(await race.StoredAsync()));
        race.Clock.Advance(TimeSpan.FromMinutes(20));
        var expired = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

        Assert.Equal(BrowserSessionWriteOutcome.Expired, expired.Outcome);
        var stored = await race.StoredAsync();
        Assert.Equal(race.Clock.GetUtcNow(), stored.RevokedAt);
        Assert.Equal(now.AddHours(1), stored.IdleExpiresAt);
    }

    [Fact]
    public async Task PreferenceChangeRacingRenewalIsAppliedFirstAndRenewalUsesTheNewPolicy()
    {
        await using (var race = await Race.StartAsync())
        {
            race.Clock.Advance(TimeSpan.FromMinutes(90));
            var start = race.Created.Session.CreatedAt;
            race.BeforeWrite = async () => Assert.Equal(BrowserSessionWriteOutcome.Applied, (await race.Competitor()
                .SetRememberedAsync(race.Id, race.Generation, true, CancellationToken.None)).Outcome);

            var renewal = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

            Assert.Equal(BrowserSessionWriteOutcome.Applied, renewal.Outcome);
            var stored = await race.StoredAsync();
            Assert.True(stored.Persistent);
            Assert.True(stored.User.KeepSignedIn);
            Assert.Equal(start.AddDays(30), stored.ExpiresAt);
            Assert.Equal(race.Clock.GetUtcNow().AddDays(7), stored.IdleExpiresAt);
            Assert.Equal(stored.IdleExpiresAt, renewal.Session!.IdleExpiresAt);
        }

        // Turning it off measures the standard limits from sign-in, which ends this remembered session before it renews.
        await using (var race = await Race.StartAsync(remembered: true))
        {
            race.Clock.Advance(TimeSpan.FromDays(4));
            race.BeforeWrite = async () => Assert.Equal(BrowserSessionWriteOutcome.Expired, (await race.Competitor()
                .SetRememberedAsync(race.Id, race.Generation, false, CancellationToken.None)).Outcome);

            var renewal = await race.Renewing.RenewAsync(race.Id, race.Generation, null, CancellationToken.None);

            Assert.Equal(BrowserSessionWriteOutcome.Revoked, renewal.Outcome);
            var stored = await race.StoredAsync();
            Assert.NotNull(stored.RevokedAt);
            Assert.False(stored.Persistent);
            Assert.Equal(race.Created.Session.RenewedAt, stored.RenewedAt);
            Assert.Equal(race.Clock.GetUtcNow().AddHours(2), stored.IdleExpiresAt);
        }
    }

    [Fact]
    public async Task ValidationExpiryDoesNotEndARotatedGeneration()
    {
        await using var race = await Race.StartAsync();
        race.Clock.Advance(TimeSpan.FromHours(3));
        CreatedBrowserSession? rotated = null;
        race.BeforeWrite = async () => rotated = await race.Competitor().CreateOrRotateAsync(
            "usr_1", "identity_1", "kratos_session_1", CancellationToken.None);

        var validation = await race.Renewing.ValidateAsync(race.Created.Cookie, CancellationToken.None);

        Assert.Null(validation.Session);
        Assert.Null(validation.EndedKratosSessionId);
        var stored = await race.StoredAsync();
        Assert.Null(stored.RevokedAt);
        Assert.Equal(BrowserSessionService.HashSecret(rotated!.Cookie), stored.TokenHash);
        Assert.NotNull((await race.Competitor().ValidateAsync(rotated.Cookie, CancellationToken.None)).Session);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PostgresSerializesConcurrentRenewalsRevocationsRotationsAndPreferenceChanges()
    {
        await WithPostgres(async options =>
        {
            await using (var setup = new MinicloudDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
                await setup.SaveChangesAsync();
            }
            // Sub-microsecond ticks: PostgreSQL keeps microseconds, so commit-time matches must use the stored values.
            var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00.1234567Z"));
            async Task<T> With<T>(Func<BrowserSessionService, Task<T>> action)
            {
                await using var db = new MinicloudDbContext(options);
                return await action(new BrowserSessionService(db, new IdGenerator(), clock));
            }
            async Task<BrowserSession> Stored(string id) => (await With(s => s.FindCommittedAsync(id, CancellationToken.None)))!;
            static void Near(DateTimeOffset expected, DateTimeOffset actual) =>
                Assert.True((expected - actual).Duration() < TimeSpan.FromMicroseconds(1), $"{expected:O} != {actual:O}");

            for (var round = 0; round < 10; round++)
            {
                // Competing renewals: exactly one extends; the others report the state it committed.
                var renewed = await With(s => s.CreateAsync("usr_1", "identity_1", $"renew_{round}", CancellationToken.None));
                clock.Advance(TimeSpan.FromMinutes(90));
                var renewals = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => With(s =>
                    s.RenewAsync(renewed.Session.Id, renewed.Session.TokenHash, null, CancellationToken.None)))));
                Assert.Single(renewals, x => x.Outcome == BrowserSessionWriteOutcome.Applied);
                Assert.All(renewals, x => Assert.Contains(x.Outcome,
                    new[] { BrowserSessionWriteOutcome.Applied, BrowserSessionWriteOutcome.Unchanged }));
                var stored = await Stored(renewed.Session.Id);
                Near(clock.GetUtcNow().AddHours(2), stored.IdleExpiresAt);
                Assert.All(renewals, x => Near(stored.IdleExpiresAt, x.Session!.IdleExpiresAt));

                // Renewal racing logout: the session ends revoked, whichever commits first.
                var revoked = await With(s => s.CreateAsync("usr_1", "identity_1", $"revoke_{round}", CancellationToken.None));
                clock.Advance(TimeSpan.FromMinutes(90));
                var renewRevoked = Task.Run(() => With(s =>
                    s.RenewAsync(revoked.Session.Id, revoked.Session.TokenHash, null, CancellationToken.None)));
                var logout = Task.Run(() => With(async s => { await s.RevokeAsync(revoked.Cookie, CancellationToken.None); return 0; }));
                await Task.WhenAll(renewRevoked, logout);
                Assert.Contains(renewRevoked.Result.Outcome,
                    new[] { BrowserSessionWriteOutcome.Applied, BrowserSessionWriteOutcome.Revoked });
                Assert.NotNull((await Stored(revoked.Session.Id)).RevokedAt);

                // Renewal racing rotation: the remembered lifetime of the new generation is never overwritten.
                var rotated = await With(s => s.CreateAsync("usr_1", "identity_1", $"rotate_{round}", CancellationToken.None));
                clock.Advance(TimeSpan.FromMinutes(90));
                var renewRotated = Task.Run(() => With(s =>
                    s.RenewAsync(rotated.Session.Id, rotated.Session.TokenHash, null, CancellationToken.None)));
                var rotation = Task.Run(() => With(s => s.CreateOrRotateAsync(
                    "usr_1", "identity_1", $"rotate_{round}", remembered: true, CancellationToken.None)));
                await Task.WhenAll(renewRotated, rotation);
                Assert.Contains(renewRotated.Result.Outcome,
                    new[] { BrowserSessionWriteOutcome.Applied, BrowserSessionWriteOutcome.Superseded });
                stored = await Stored(rotated.Session.Id);
                Assert.Equal(BrowserSessionService.HashSecret(rotation.Result.Cookie), stored.TokenHash);
                Assert.Null(stored.RevokedAt);
                Assert.True(stored.Persistent);
                Near(clock.GetUtcNow().AddDays(7), stored.IdleExpiresAt);

                // Renewal racing a preference change: the result is one of the two serial orders, never a mix.
                var preferred = await With(s => s.CreateAsync("usr_1", "identity_1", $"prefer_{round}", CancellationToken.None));
                clock.Advance(TimeSpan.FromMinutes(90));
                var renewPreferred = Task.Run(() => With(s =>
                    s.RenewAsync(preferred.Session.Id, preferred.Session.TokenHash, null, CancellationToken.None)));
                var preference = Task.Run(() => With(s =>
                    s.SetRememberedAsync(preferred.Session.Id, preferred.Session.TokenHash, true, CancellationToken.None)));
                await Task.WhenAll(renewPreferred, preference);
                Assert.Equal(BrowserSessionWriteOutcome.Applied, renewPreferred.Result.Outcome);
                Assert.Equal(BrowserSessionWriteOutcome.Applied, preference.Result.Outcome);
                stored = await Stored(preferred.Session.Id);
                Assert.True(stored.Persistent);
                Assert.True(stored.User.KeepSignedIn);
                Near(preferred.Session.CreatedAt.AddDays(30), stored.ExpiresAt);
                Near(renewPreferred.Result.Session!.IdleExpiresAt, stored.IdleExpiresAt);
                Assert.Contains(true, new[]
                {
                    (stored.IdleExpiresAt - clock.GetUtcNow().AddHours(2)).Duration() < TimeSpan.FromMicroseconds(1),
                    (stored.IdleExpiresAt - clock.GetUtcNow().AddDays(7)).Duration() < TimeSpan.FromMicroseconds(1)
                });
            }
        });
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

    /// <summary>
    /// Two requests on one database: <see cref="Renewing"/> runs <see cref="BeforeWrite"/> just before its first
    /// conditional session write, so a competing request commits between its read and its write.
    /// </summary>
    private sealed class Race : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly BeforeSessionWrite _interceptor;
        private readonly IOptions<PortalSessionOptions> _policy = Options.Create(new PortalSessionOptions());
        private readonly List<MinicloudDbContext> _contexts = [];

        private Race(SqliteConnection connection, BeforeSessionWrite interceptor, MinicloudDbContext renewingDb)
        {
            _connection = connection;
            _interceptor = interceptor;
            _contexts.Add(renewingDb);
            Renewing = new BrowserSessionService(renewingDb, new IdGenerator(), Clock, _policy);
        }

        public TestTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        public BrowserSessionService Renewing { get; }
        public CreatedBrowserSession Created { get; private set; } = default!;
        public string Id => Created.Session.Id;
        public string Generation => Created.Session.TokenHash;
        public Func<Task>? BeforeWrite { set => _interceptor.Competing = value; }

        public static async Task<Race> StartAsync(bool remembered = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var interceptor = new BeforeSessionWrite();
            var renewingDb = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseSqlite(connection).AddInterceptors(interceptor).Options);
            await renewingDb.Database.EnsureCreatedAsync();
            renewingDb.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
            await renewingDb.SaveChangesAsync();
            var race = new Race(connection, interceptor, renewingDb);
            race.Created = await race.Competitor().CreateAsync(
                "usr_1", "identity_1", "kratos_session_1", remembered, CancellationToken.None);
            return race;
        }

        /// <summary>A separate request, with its own change tracker.</summary>
        public BrowserSessionService Competitor()
        {
            var db = CreateDb(_connection);
            _contexts.Add(db);
            return new BrowserSessionService(db, new IdGenerator(), Clock, _policy);
        }

        public async Task<BrowserSession> StoredAsync()
        {
            await using var db = CreateDb(_connection);
            return await db.BrowserSessions.AsNoTracking().Include(x => x.User).SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
                await context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class BeforeSessionWrite : DbCommandInterceptor
    {
        public Func<Task>? Competing { get; set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Competing is { } competing && command.CommandText.Contains("UPDATE \"browser_sessions\"", StringComparison.Ordinal))
            {
                Competing = null;
                await competing();
            }
            return result;
        }
    }

    private static async Task WithPostgres(Func<DbContextOptions<MinicloudDbContext>, Task> test)
    {
        var container = "mc-sessions-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker("run", "--rm", "-d", "--name", container, "-e", "POSTGRES_PASSWORD=" + container,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={container}").Options;
            var ready = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) { ready = true; break; }
                await Task.Delay(500);
            }
            Assert.True(ready, "Isolated PostgreSQL did not become ready.");
            await test(options);
        }
        finally { await Docker("rm", "-f", container); }
    }

    private static async Task<string> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
