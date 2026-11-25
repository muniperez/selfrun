using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Selfrun.Common.Migrations
{
    /// <summary>
    /// Adds the "Keep me signed in" preference and remembered/renewal state to portal sessions. Existing sessions stay
    /// standard sessions whose current idle window started at their last authenticated request.
    /// </summary>
    public partial class PortalSessionRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "keep_signed_in",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "persistent",
                table: "browser_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "renewed_at",
                table: "browser_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("UPDATE browser_sessions SET renewed_at = last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "keep_signed_in",
                table: "users");

            migrationBuilder.DropColumn(
                name: "persistent",
                table: "browser_sessions");

            migrationBuilder.DropColumn(
                name: "renewed_at",
                table: "browser_sessions");
        }
    }
}
