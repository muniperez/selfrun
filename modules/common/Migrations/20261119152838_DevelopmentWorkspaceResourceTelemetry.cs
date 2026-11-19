using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentWorkspaceResourceTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "resource_sample_generation",
                table: "development_workspace_vms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource_sample_json",
                table: "development_workspace_vms",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resource_sample_received_at",
                table: "development_workspace_vms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "runtime_capabilities",
                table: "development_workspace_vms",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "development_workspace_filesystem_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    path_json = table.Column<string>(type: "text", nullable: false),
                    target_device = table.Column<long>(type: "bigint", nullable: true),
                    target_inode = table.Column<long>(type: "bigint", nullable: true),
                    source_job_id = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_filesystem_jobs", x => x.id);
                    table.CheckConstraint("ck_development_filesystem_job_kind", "kind IN ('disk_usage', 'directory_deletion')");
                    table.ForeignKey(
                        name: "FK_development_workspace_filesystem_jobs_development_workspace~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_expires_at",
                table: "development_workspace_filesystem_jobs",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_state_created_at",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_vm_id_kind_idempotenc~",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "vm_id", "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_vm_id_state",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "vm_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_workspace_id_vm_id",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "workspace_id", "vm_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "development_workspace_filesystem_jobs");

            migrationBuilder.DropColumn(
                name: "resource_sample_generation",
                table: "development_workspace_vms");

            migrationBuilder.DropColumn(
                name: "resource_sample_json",
                table: "development_workspace_vms");

            migrationBuilder.DropColumn(
                name: "resource_sample_received_at",
                table: "development_workspace_vms");

            migrationBuilder.DropColumn(
                name: "runtime_capabilities",
                table: "development_workspace_vms");
        }
    }
}
