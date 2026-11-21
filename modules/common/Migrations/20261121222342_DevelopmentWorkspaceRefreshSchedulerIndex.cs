using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentWorkspaceRefreshSchedulerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_desired_state_phase_id",
                table: "development_workspace_vms",
                columns: new[] { "desired_state", "phase", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_development_workspace_vms_desired_state_phase_id",
                table: "development_workspace_vms");
        }
    }
}
