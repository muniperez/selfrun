using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Selfrun.Common.Data;

/// <summary>
/// Explicit database options. Each host supplies its intentional defaults;
/// Common never guesses a provider or connection string on its own.
/// Inventoried host differences preserved here:
/// - API defaults to Sqlite in Development, Postgres otherwise; the worker
///   always defaults to Postgres.
/// - API reads Selfrun, DefaultConnection, then DATABASE_URL. The worker
///   additionally honors the legacy Database key between DefaultConnection
///   and DATABASE_URL.
/// - Only the API understood sqlite:// URLs; only the worker defaulted an
///   unspecified postgres port to 5432. The canonical normalizer does both;
///   hosts opt in by using it.
/// </summary>
public sealed class SelfrunDatabaseOptions
{
    public string Provider { get; set; } = "Postgres";

    public string? SelfrunConnectionString { get; set; }

    public string? DefaultConnectionString { get; set; }

    public string? DatabaseConnectionString { get; set; }

    public string? DatabaseUrl { get; set; }

    public string? PasswordFile { get; set; }

    public string FallbackConnectionString { get; set; } =
        "Host=localhost;Database=selfrun;Username=selfrun;Password=selfrun";
}

public static class SelfrunDatabase
{
    public static string ResolveConnectionString(SelfrunDatabaseOptions options) =>
        ApplyPasswordFile(
            options.SelfrunConnectionString
            ?? options.DefaultConnectionString
            ?? options.DatabaseConnectionString
            ?? NormalizeDatabaseUrl(options.DatabaseUrl)
            ?? options.FallbackConnectionString,
            options.PasswordFile);

    public static string ApplyPasswordFile(string connectionString, string? passwordFile)
    {
        if (string.IsNullOrWhiteSpace(passwordFile))
        {
            return connectionString;
        }

        var password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        if (password.Length == 0)
        {
            throw new InvalidOperationException("The database password file is empty.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = password
        };
        return builder.ConnectionString;
    }

    public static string? NormalizeDatabaseUrl(string? databaseUrl)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            return null;
        }

        if (databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(databaseUrl);
            var userInfo = uri.UserInfo.Split(':', 2);
            var username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? "");
            var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? "");
            var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var port = uri.Port > 0 ? uri.Port : 5432;
            return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password}";
        }

        if (databaseUrl.StartsWith("sqlite://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(databaseUrl);
            return $"Data Source={uri.AbsolutePath}";
        }

        return databaseUrl;
    }

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string provider, string connectionString)
    {
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(SqliteDatabaseConfigurator.ConfigureConnectionString(connectionString));
        }
        else
        {
            options.UseNpgsql(connectionString);
        }
    }

    public static IServiceCollection AddSelfrunDbContext(this IServiceCollection services, SelfrunDatabaseOptions options)
    {
        var provider = options.Provider;
        var connectionString = ResolveConnectionString(options);
        services.AddDbContext<SelfrunDbContext>(
            (serviceProvider, builder) =>
            {
                if (serviceProvider.GetService<Selfrun.Common.Diagnostics.DbCommandDiagnosticsInterceptor>() is { } interceptor)
                    builder.AddInterceptors(interceptor);
                ConfigureDbContext(builder, provider, connectionString);
            });
        return services;
    }
}
