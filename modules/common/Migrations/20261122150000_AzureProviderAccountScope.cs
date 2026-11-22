using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations;

[DbContext(typeof(MinicloudDbContext))]
[Migration("20261122150000_AzureProviderAccountScope")]
public sealed class AzureProviderAccountScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "tenant_id",
            table: "provider_accounts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "subscription_id",
            table: "provider_accounts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "resource_group_name",
            table: "provider_accounts",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "tenant_id", table: "provider_accounts");
        migrationBuilder.DropColumn(name: "subscription_id", table: "provider_accounts");
        migrationBuilder.DropColumn(name: "resource_group_name", table: "provider_accounts");
    }
}
