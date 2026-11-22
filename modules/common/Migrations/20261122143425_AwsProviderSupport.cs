using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AwsProviderSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "aws_region",
                table: "provider_accounts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "aws_security_group_id",
                table: "provider_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "aws_subnet_id",
                table: "provider_accounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_scope_id",
                table: "provider_accounts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aws_region",
                table: "provider_accounts");

            migrationBuilder.DropColumn(
                name: "aws_security_group_id",
                table: "provider_accounts");

            migrationBuilder.DropColumn(
                name: "aws_subnet_id",
                table: "provider_accounts");

            migrationBuilder.DropColumn(
                name: "provider_scope_id",
                table: "provider_accounts");
        }
    }
}
