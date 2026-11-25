using Selfrun.Common.Data;

namespace Selfrun.Tests;

public sealed class CommonDatabaseTests
{
    [Fact]
    public void NormalizePostgresUrlDefaultsPort()
    {
        Assert.Equal(
            "Host=db.example.com;Port=5432;Database=selfrun;Username=app;Password=secret",
            SelfrunDatabase.NormalizeDatabaseUrl("postgresql://app:secret@db.example.com/selfrun"));
    }

    [Fact]
    public void NormalizePostgresUrlKeepsExplicitPort()
    {
        Assert.Equal(
            "Host=db.example.com;Port=5433;Database=selfrun;Username=app;Password=secret",
            SelfrunDatabase.NormalizeDatabaseUrl("postgres://app:secret@db.example.com:5433/selfrun"));
    }

    [Fact]
    public void NormalizeSqliteUrl()
    {
        Assert.Equal(
            "Data Source=/tmp/selfrun.db",
            SelfrunDatabase.NormalizeDatabaseUrl("sqlite:///tmp/selfrun.db"));
    }

    [Fact]
    public void NormalizePassthroughAndNull()
    {
        Assert.Equal("Host=localhost;Database=x", SelfrunDatabase.NormalizeDatabaseUrl("Host=localhost;Database=x"));
        Assert.Null(SelfrunDatabase.NormalizeDatabaseUrl(null));
        Assert.Null(SelfrunDatabase.NormalizeDatabaseUrl("  "));
    }

    [Fact]
    public void ResolveConnectionStringPrefersExplicitKeysOverUrl()
    {
        var options = new SelfrunDatabaseOptions
        {
            SelfrunConnectionString = "Host=a",
            DefaultConnectionString = "Host=b",
            DatabaseConnectionString = "Host=c",
            DatabaseUrl = "postgresql://u:p@h/d"
        };
        Assert.Equal("Host=a", SelfrunDatabase.ResolveConnectionString(options));

        options.SelfrunConnectionString = null;
        Assert.Equal("Host=b", SelfrunDatabase.ResolveConnectionString(options));

        options.DefaultConnectionString = null;
        Assert.Equal("Host=c", SelfrunDatabase.ResolveConnectionString(options));

        options.DatabaseConnectionString = null;
        Assert.Equal(
            "Host=h;Port=5432;Database=d;Username=u;Password=p",
            SelfrunDatabase.ResolveConnectionString(options));
    }

    [Fact]
    public void ResolveConnectionStringFallsBackToLocalhost()
    {
        Assert.Equal(
            "Host=localhost;Database=selfrun;Username=selfrun;Password=selfrun",
            SelfrunDatabase.ResolveConnectionString(new SelfrunDatabaseOptions()));
    }
}
