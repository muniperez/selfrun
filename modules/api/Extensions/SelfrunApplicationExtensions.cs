using Microsoft.EntityFrameworkCore;
using Selfrun.Api.Auth;
using Selfrun.Api.Services;
using Selfrun.Common.Data;
using Selfrun.Api.Providers;

namespace Selfrun.Api.Extensions;

public static class SelfrunApplicationExtensions
{
    public static async Task ConfigureSelfrunApiAsync(this WebApplication app)
    {
        var pathBase = app.Configuration["Http:PathBase"];
        if (!string.IsNullOrWhiteSpace(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SelfrunDbContext>();
            if (db.Database.IsSqlite())
            {
                await SqliteDatabaseConfigurator.ConfigureDatabaseAsync(db);
            }

            if (app.Configuration.GetValue("Database:RunMigrationsOnStartup", app.Environment.IsDevelopment()))
            {
                if (db.Database.IsSqlite())
                    await SqliteDatabaseInitializer.InitializeAsync(db);
                else if (db.Database.IsNpgsql())
                    await DatabaseMigrationCommand.MigratePostgresAsync(db);
                else
                    await db.Database.MigrateAsync();
            }
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseSetupTransport();
        app.UseHttpsRedirection();
        app.UseRouting();
        app.UseCors("Dashboard");
        app.UseRateLimiter();
        app.UseMiddleware<KratosUnavailableMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<BrowserRequestSecurityMiddleware>();
        app.UseAuthorization();
    }
}
