using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Selfrun.Common.Migrations
{
    /// <summary>
    /// Adds per-server SSH host-key trust. Worker-provisioned servers record the Ed25519 fingerprint verified through
    /// their first-boot enrollment proof; the encrypted one-time enrollment secret is kept only until that succeeds.
    /// Existing servers keep no pin and continue to rely on the operator host-pins file.
    /// </summary>
    public partial class ServerHostKeyEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "host_key_enrollment_secret",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_key_fingerprint",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "host_key_verified_at",
                table: "server_instances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "host_key_enrollment_secret",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "host_key_fingerprint",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "host_key_verified_at",
                table: "server_instances");
        }
    }
}
