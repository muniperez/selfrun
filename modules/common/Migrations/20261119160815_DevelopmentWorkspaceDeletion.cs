using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <summary>Releases the name and create idempotency key of permanently deleted workspaces.</summary>
    public partial class DevelopmentWorkspaceDeletion : Migration
    {
        private const string Table = "development_workspaces";
        private const string Filter = "volume_status <> 'deleted'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ReplaceIndex(migrationBuilder, "name", Filter);
            ReplaceIndex(migrationBuilder, "idempotency_key", Filter);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A retired name or key may have been reused. Keep tombstones unique under their own IDs on rollback.
            migrationBuilder.Sql("UPDATE development_workspaces SET name = id, idempotency_key = id WHERE volume_status = 'deleted'");
            ReplaceIndex(migrationBuilder, "name", null);
            ReplaceIndex(migrationBuilder, "idempotency_key", null);
        }

        private static void ReplaceIndex(MigrationBuilder migrationBuilder, string column, string filter)
        {
            var name = $"IX_development_workspaces_organization_id_{column}";
            migrationBuilder.DropIndex(name, Table);
            migrationBuilder.CreateIndex(name, Table, new[] { "organization_id", column }, unique: true, filter: filter);
        }
    }
}
