using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations;

[DbContext(typeof(MinicloudDbContext))]
[Migration("20261120020000_DevelopmentWorkspaceTerminalDeletionIdentity")]
public sealed class DevelopmentWorkspaceTerminalDeletionIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => ReplaceGuard(migrationBuilder, allowTerminalDeletion: true);

    protected override void Down(MigrationBuilder migrationBuilder) => ReplaceGuard(migrationBuilder, allowTerminalDeletion: false);

    private void ReplaceGuard(MigrationBuilder migrationBuilder, bool allowTerminalDeletion)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;

        // Provider absence and operation fencing remain the worker/service's responsibility.
        var terminalException = allowTerminalDeletion
            ? "AND NOT (OLD.volume_status = 'deleting' AND NEW.volume_status = 'deleted' AND NEW.provider_volume_id IS NULL)"
            : "";
        migrationBuilder.Sql($$"""
            CREATE OR REPLACE FUNCTION development_workspace_identity_guard() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF TG_TABLE_NAME = 'development_workspace_vms' THEN
                IF NEW.kind <> OLD.kind OR NEW.workspace_id <> OLD.workspace_id OR NEW.organization_id <> OLD.organization_id THEN
                  RAISE EXCEPTION 'VM kind and ownership are immutable' USING ERRCODE = '23514';
                END IF;
              ELSE
                IF NEW.primary_vm_id <> OLD.primary_vm_id OR NEW.organization_id <> OLD.organization_id
                  OR NEW.provider_account_id <> OLD.provider_account_id OR NEW.region <> OLD.region
                  OR (OLD.provider_volume_id IS NOT NULL AND NEW.provider_volume_id IS DISTINCT FROM OLD.provider_volume_id
                    {{terminalException}}) THEN
                  RAISE EXCEPTION 'Primary storage identity and ownership are immutable' USING ERRCODE = '23514';
                END IF;
              END IF;
              RETURN NEW;
            END $$;
            """);
    }
}
