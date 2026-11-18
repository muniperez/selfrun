using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

namespace Minicloud.Common.Migrations;

[DbContext(typeof(MinicloudDbContext))]
[Migration("20261118170000_DevelopmentWorkspaceDeletion")]
public sealed class DevelopmentWorkspaceDeletion : Migration
{
    private const string Index = "IX_development_workspaces_organization_id_name";
    private const string Table = "development_workspaces";

    protected override void Up(MigrationBuilder migrationBuilder) => ReplaceIndex(migrationBuilder, "volume_status <> 'deleted'");

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A retired name may have been reused. Preserve tombstones under their unique IDs on rollback.
        migrationBuilder.Sql("UPDATE development_workspaces SET name = id WHERE volume_status = 'deleted'");
        ReplaceIndex(migrationBuilder, null);
    }

    private static void ReplaceIndex(MigrationBuilder migrationBuilder, string? filter)
    {
        migrationBuilder.DropIndex(Index, Table);
        migrationBuilder.CreateIndex(Index, Table, new[] { "organization_id", "name" }, unique: true, filter: filter);
    }
}
