using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class CodingWorkspaceObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "codex_authentication_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "git_hub_authentication_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bootstrap_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "allocation_reconciliation_required",
                table: "coding_workspaces",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bootstrap_status",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "codex_authentication",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "github_authentication",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_status",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "repository_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_status",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "requested_agent_version",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "0.169.0");

            migrationBuilder.AddColumn<string>(
                name: "requested_t3_version",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "0.1.9");

            migrationBuilder.AddColumn<string>(
                name: "t3_health",
                table: "coding_workspaces",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "t3_observed_at",
                table: "coding_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by",
                table: "coding_workspace_operations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_coding_workspace_operations_organization_id_workspace_id",
                table: "coding_workspace_operations",
                columns: new[] { "organization_id", "workspace_id" },
                unique: true,
                filter: "status IN ('queued', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_coding_workspace_operations_organization_id_workspace_id",
                table: "coding_workspace_operations");

            migrationBuilder.DropColumn(
                name: "codex_authentication_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "git_hub_authentication_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "bootstrap_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "allocation_reconciliation_required",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "bootstrap_status",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "codex_authentication",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "git_hub_authentication",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "provider_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "provider_status",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "repository_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "repository_status",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "requested_agent_version",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "requested_t3_version",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "t3_health",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "t3_observed_at",
                table: "coding_workspaces");

            migrationBuilder.DropColumn(
                name: "requested_by",
                table: "coding_workspace_operations");
        }
    }
}
