using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class CodingWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "coding_workspaces",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    plan_name = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: true),
                    provider_ssh_key_id = table.Column<string>(type: "text", nullable: false),
                    ssh_public_key = table.Column<string>(type: "text", nullable: false),
                    ssh_fingerprint = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    agent = table.Column<string>(type: "text", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    t3_version = table.Column<string>(type: "text", nullable: false),
                    workspace_user = table.Column<string>(type: "text", nullable: false),
                    provider_server_id = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    stage = table.Column<string>(type: "text", nullable: false),
                    connectivity = table.Column<string>(type: "text", nullable: false),
                    bootstrap_revision = table.Column<string>(type: "text", nullable: true),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coding_workspaces", x => x.id);
                    table.UniqueConstraint("AK_coding_workspaces_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_coding_workspaces_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coding_workspaces_provider_plans_organization_id_plan_id",
                        columns: x => new { x.organization_id, x.plan_id },
                        principalTable: "provider_plans",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coding_workspaces_provider_ssh_keys_organization_id_provide~",
                        columns: x => new { x.organization_id, x.provider_account_id, x.provider_ssh_key_id },
                        principalTable: "provider_ssh_keys",
                        principalColumns: new[] { "organization_id", "provider_account_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coding_workspace_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    stage = table.Column<string>(type: "text", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    ownership_token = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coding_workspace_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_coding_workspace_operations_coding_workspaces_organization_~",
                        columns: x => new { x.organization_id, x.workspace_id },
                        principalTable: "coding_workspaces",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspace_operations_organization_id_workspace_id_cr~",
                table: "coding_workspace_operations",
                columns: new[] { "organization_id", "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspace_operations_ownership_token",
                table: "coding_workspace_operations",
                column: "ownership_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspace_operations_status_created_at",
                table: "coding_workspace_operations",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspaces_organization_id_normalized_name",
                table: "coding_workspaces",
                columns: new[] { "organization_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspaces_organization_id_plan_id",
                table: "coding_workspaces",
                columns: new[] { "organization_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspaces_organization_id_provider_account_id_provi~",
                table: "coding_workspaces",
                columns: new[] { "organization_id", "provider_account_id", "provider_ssh_key_id" });

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspaces_provider_account_id_provider_provider_ser~",
                table: "coding_workspaces",
                columns: new[] { "provider_account_id", "provider", "provider_server_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "coding_workspace_operations");

            migrationBuilder.DropTable(
                name: "coding_workspaces");
        }
    }
}
