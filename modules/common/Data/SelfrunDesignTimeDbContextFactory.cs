using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Selfrun.Common.Data;

public sealed class SelfrunDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SelfrunDbContext>
{
    public SelfrunDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SelfrunDbContext>();
        var provider = Environment.GetEnvironmentVariable("Database__Provider") ?? "Postgres";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Selfrun") ?? "Host=localhost;Port=55433;Database=selfrun_dev;Username=selfrun;Password=selfrun";

        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            options.UseNpgsql(connectionString);
        }
        else
        {
            options.UseSqlite(connectionString);
        }

        return new SelfrunDbContext(options.Options);
    }
}
