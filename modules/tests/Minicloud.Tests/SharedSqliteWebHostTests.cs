using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Minicloud.Api.Services;

namespace Minicloud.Tests;

public sealed class SharedSqliteWebHostTests
{
    [Fact]
    public async Task SharedConnectionHostRunsNoMinicloudBackgroundServices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var api = Factory(_ => { });
        await using var shared = Factory(services => services.UseSharedSqliteConnection(connection));

        var defaults = api.Services.GetServices<IHostedService>().Select(x => x.GetType()).ToList();
        Assert.Contains(typeof(TelemetryAuthoritySyncService), defaults);
        Assert.Contains(typeof(MaintenanceParticipantRegistrationService), defaults);

        var remaining = shared.Services.GetServices<IHostedService>().Select(x => x.GetType()).ToList();
        Assert.NotEmpty(remaining);
        Assert.DoesNotContain(remaining, type => type.Assembly == typeof(TelemetryAuthoritySyncService).Assembly ||
            type.Namespace?.StartsWith("Minicloud.", StringComparison.Ordinal) == true);
    }

    private static WebApplicationFactory<Program> Factory(Action<IServiceCollection> configure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:"
            }));
            builder.ConfigureTestServices(configure);
        });
}
