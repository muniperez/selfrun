using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Selfrun.Api.Services;
using Selfrun.Common.Data;

namespace Selfrun.Tests;

/// <summary>
/// Binds an API test host to one open in-memory <see cref="SqliteConnection"/> owned by the test.
/// </summary>
/// <remarks>
/// A <see cref="SqliteConnection"/> is not thread-safe. Selfrun background services (for example telemetry
/// authority sync and maintenance participant registration) query the database as soon as the host starts and
/// on timers, so they would issue commands on the shared connection concurrently with test seeding and requests.
/// That corrupts the connection's internal command list and later fails <c>SqliteConnection.Close()</c> with a
/// <see cref="NullReferenceException"/>. Hosts sharing one connection therefore run only the HTTP pipeline; tests
/// that need background work invoke it explicitly.
/// </remarks>
internal static class SharedSqliteWebHost
{
    private static readonly Assembly[] SelfrunAssemblies =
        [typeof(TelemetryAuthoritySyncService).Assembly, typeof(SelfrunDbContext).Assembly];

    public static IServiceCollection UseSharedSqliteConnection(this IServiceCollection services, SqliteConnection connection)
    {
        services.RemoveAll<DbContextOptions<SelfrunDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<SelfrunDbContext>>();
        services.AddDbContext<SelfrunDbContext>(options => options.UseSqlite(connection));
        return services.RemoveSelfrunHostedServices();
    }

    public static IServiceCollection RemoveSelfrunHostedServices(this IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService) && !descriptor.IsKeyedService &&
                SelfrunAssemblies.Contains(ImplementationAssembly(descriptor)))
            {
                services.RemoveAt(i);
            }
        }
        return services;
    }

    private static Assembly? ImplementationAssembly(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType?.Assembly
        ?? descriptor.ImplementationInstance?.GetType().Assembly
        ?? descriptor.ImplementationFactory?.Method.DeclaringType?.Assembly;
}
