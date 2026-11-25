using Selfrun.Common.Data;

namespace Selfrun.Api.Providers;

public sealed class DatabaseConnectionStringProvider(IConfiguration configuration, IWebHostEnvironment environment) : IDatabaseConnectionStringProvider
{
    public string GetProviderName() =>
        configuration.GetValue("Database:Provider", environment.IsDevelopment() ? "Sqlite" : "Postgres");

    public string? GetConnectionString()
    {
        var connectionString = configuration.GetConnectionString("Selfrun")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? SelfrunDatabase.NormalizeDatabaseUrl(Environment.GetEnvironmentVariable("DATABASE_URL"));
        return connectionString is null
            ? null
            : SelfrunDatabase.ApplyPasswordFile(connectionString, configuration["Database:PasswordFile"]);
    }
}
