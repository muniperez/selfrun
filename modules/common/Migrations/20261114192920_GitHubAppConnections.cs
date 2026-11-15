using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class GitHubAppConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_app_connections",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    owner_login = table.Column<string>(type: "text", nullable: false),
                    owner_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    setup_actor_id = table.Column<string>(type: "text", nullable: true),
                    setup_state_hash = table.Column<string>(type: "text", nullable: true),
                    conversion_code_hash = table.Column<string>(type: "text", nullable: true),
                    setup_expires_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    installation_state_hash = table.Column<string>(type: "text", nullable: true),
                    installation_actor_id = table.Column<string>(type: "text", nullable: true),
                    installation_expires_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    installation_state_used = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    app_id = table.Column<long>(type: "bigint", nullable: true),
                    owner_id = table.Column<long>(type: "bigint", nullable: true),
                    app_slug = table.Column<string>(type: "text", nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: true),
                    secret_provider = table.Column<string>(type: "text", nullable: true),
                    secret_path = table.Column<string>(type: "text", nullable: true),
                    secret_version = table.Column<string>(type: "text", nullable: true),
                    credential_generation = table.Column<string>(type: "text", nullable: false),
                    repositories_json = table.Column<string>(type: "text", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_app_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_app_connections_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_receipts",
                columns: table => new
                {
                    connection_id = table.Column<string>(type: "text", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_receipts", x => new { x.connection_id, x.delivery_id });
                    table.ForeignKey(
                        name: "FK_github_webhook_receipts_github_app_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "github_app_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_app_connections_organization_id",
                table: "github_app_connections",
                column: "organization_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "github_webhook_receipts");

            migrationBuilder.DropTable(
                name: "github_app_connections");
        }
    }
}
