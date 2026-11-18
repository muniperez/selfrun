using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Common.Data;

public sealed partial class MinicloudDbContext
{
    public DbSet<DevelopmentWorkspaceGatewaySession> DevelopmentWorkspaceGatewaySessions => Set<DevelopmentWorkspaceGatewaySession>();
    public DbSet<DevelopmentWorkspaceGatewayRoute> DevelopmentWorkspaceGatewayRoutes => Set<DevelopmentWorkspaceGatewayRoute>();

    private static void ConfigureDevelopmentWorkspaceGateway(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevelopmentWorkspaceGatewaySession>(entity =>
        {
            entity.ToTable("development_workspace_gateway_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => x.LaunchDigest).IsUnique();
            entity.HasIndex(x => x.SessionDigest).IsUnique();
            entity.HasIndex(x => new { x.VmId, x.Generation });
        });
        modelBuilder.Entity<DevelopmentWorkspaceGatewayRoute>(entity =>
        {
            entity.ToTable("development_workspace_gateway_routes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.VmId, x.Generation, x.Service }).IsUnique();
        });
    }
}
