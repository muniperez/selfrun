using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentWorkspaceVms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "development_workspace_gateway_routes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    loopback_port = table.Column<int>(type: "integer", nullable: false),
                    t3_credential_reference = table.Column<string>(type: "text", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_gateway_routes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_gateway_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    api_key_id = table.Column<string>(type: "text", nullable: true),
                    launch_digest = table.Column<string>(type: "text", nullable: false),
                    session_digest = table.Column<string>(type: "text", nullable: true),
                    launch_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_gateway_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "development_workspaces",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    primary_vm_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    volume_size_gb = table.Column<int>(type: "integer", nullable: false),
                    provider_volume_id = table.Column<string>(type: "text", nullable: true),
                    volume_status = table.Column<string>(type: "text", nullable: false),
                    volume_filesystem = table.Column<string>(type: "text", nullable: false),
                    volume_filesystem_uuid = table.Column<string>(type: "text", nullable: true),
                    volume_attached_vm_id = table.Column<string>(type: "text", nullable: true),
                    last_backup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    backup_status = table.Column<string>(type: "text", nullable: true),
                    storage_monthly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    pricing_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspaces", x => x.id);
                    table.UniqueConstraint("AK_development_workspaces_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_development_workspaces_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspaces_provider_accounts_organization_id_pr~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_vms",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    image_id = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    provider_image = table.Column<string>(type: "text", nullable: false),
                    image_manifest_sha256 = table.Column<string>(type: "text", nullable: false),
                    architecture = table.Column<string>(type: "text", nullable: false),
                    cpu_count = table.Column<int>(type: "integer", nullable: false),
                    memory_gi_b = table.Column<int>(type: "integer", nullable: false),
                    compute_hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    compute_reserved = table.Column<bool>(type: "boolean", nullable: false),
                    start_after_stop = table.Column<bool>(type: "boolean", nullable: false),
                    start_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_image_id = table.Column<string>(type: "text", nullable: true),
                    next_profile = table.Column<string>(type: "text", nullable: true),
                    next_plan_id = table.Column<string>(type: "text", nullable: true),
                    operation_id = table.Column<string>(type: "text", nullable: true),
                    allocation_operation_id = table.Column<string>(type: "text", nullable: true),
                    provider_server_id = table.Column<string>(type: "text", nullable: true),
                    provider_ip_id = table.Column<string>(type: "text", nullable: true),
                    provider_firewall_id = table.Column<string>(type: "text", nullable: true),
                    provider_action_id = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_digest = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    runtime_token_hash = table.Column<string>(type: "text", nullable: true),
                    runtime_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssh_bootstrap_secret_ref = table.Column<string>(type: "text", nullable: true),
                    runtime_credential_ref = table.Column<string>(type: "text", nullable: true),
                    gateway_ready = table.Column<bool>(type: "boolean", nullable: false),
                    task_secret_references_json = table.Column<string>(type: "text", nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssh_host_key = table.Column<string>(type: "text", nullable: true),
                    ssh_host_key_fingerprint = table.Column<string>(type: "text", nullable: true),
                    ssh_host_key_generation = table.Column<long>(type: "bigint", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shutdown_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shutdown_reason = table.Column<string>(type: "text", nullable: true),
                    last_ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quiesced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quiesced_generation = table.Column<long>(type: "bigint", nullable: true),
                    quiesced_operation_id = table.Column<string>(type: "text", nullable: true),
                    quiescence_receipt = table.Column<string>(type: "text", nullable: true),
                    repository = table.Column<string>(type: "text", nullable: true),
                    base_ref = table.Column<string>(type: "text", nullable: true),
                    base_commit = table.Column<string>(type: "text", nullable: true),
                    work_branch = table.Column<string>(type: "text", nullable: true),
                    push_authorized = table.Column<bool>(type: "boolean", nullable: false),
                    repository_credential_ref = table.Column<string>(type: "text", nullable: true),
                    ignored_data_acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    git_status = table.Column<string>(type: "text", nullable: false),
                    base_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    base_verified_generation = table.Column<long>(type: "bigint", nullable: true),
                    git_saved_generation = table.Column<long>(type: "bigint", nullable: true),
                    git_saved_operation_id = table.Column<string>(type: "text", nullable: true),
                    git_saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    git_save_receipt = table.Column<string>(type: "text", nullable: true),
                    final_head = table.Column<string>(type: "text", nullable: true),
                    verified_remote_ref = table.Column<string>(type: "text", nullable: true),
                    verified_remote_commit = table.Column<string>(type: "text", nullable: true),
                    discard_generation = table.Column<long>(type: "bigint", nullable: true),
                    discard_operation_id = table.Column<string>(type: "text", nullable: true),
                    discard_requested_by = table.Column<string>(type: "text", nullable: true),
                    discard_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_vms", x => x.id);
                    table.UniqueConstraint("AK_development_workspace_vms_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.CheckConstraint("ck_development_vm_kind", "kind IN ('primary', 'ephemeral')");
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_development_workspaces_organizati~",
                        columns: x => new { x.organization_id, x.workspace_id },
                        principalTable: "development_workspaces",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_provider_accounts_organization_id~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_provider_plans_organization_id_pl~",
                        columns: x => new { x.organization_id, x.plan_id },
                        principalTable: "provider_plans",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    step = table.Column<string>(type: "text", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_token = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    image_id = table.Column<string>(type: "text", nullable: false),
                    provider_image = table.Column<string>(type: "text", nullable: false),
                    image_manifest_sha256 = table.Column<string>(type: "text", nullable: false),
                    compute_hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    provider_resource_id = table.Column<string>(type: "text", nullable: true),
                    provider_action_id = table.Column<string>(type: "text", nullable: true),
                    provider_evidence_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_workspace_operations_development_workspace_vms_~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_task_leases",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    task_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_task_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_workspace_task_leases_development_workspace_vms~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_routes_vm_id_generation_servi~",
                table: "development_workspace_gateway_routes",
                columns: new[] { "vm_id", "generation", "service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_launch_digest",
                table: "development_workspace_gateway_sessions",
                column: "launch_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_session_digest",
                table: "development_workspace_gateway_sessions",
                column: "session_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_vm_id_generation",
                table: "development_workspace_gateway_sessions",
                columns: new[] { "vm_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_status_lease_expires_at",
                table: "development_workspace_operations",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_vm_id_generation_kind",
                table: "development_workspace_operations",
                columns: new[] { "vm_id", "generation", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_workspace_id_vm_id",
                table: "development_workspace_operations",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_vm_id_generation_status",
                table: "development_workspace_task_leases",
                columns: new[] { "vm_id", "generation", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_vm_id_generation_task_id",
                table: "development_workspace_task_leases",
                columns: new[] { "vm_id", "generation", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_workspace_id_vm_id",
                table: "development_workspace_task_leases",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_desired_state_phase_shutdown_dead~",
                table: "development_workspace_vms",
                columns: new[] { "desired_state", "phase", "shutdown_deadline" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_plan_id",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_provider_account_~",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_workspace_id",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_provider_account_id_provider_serv~",
                table: "development_workspace_vms",
                columns: new[] { "provider_account_id", "provider_server_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_repository_work_branch",
                table: "development_workspace_vms",
                columns: new[] { "repository", "work_branch" },
                unique: true,
                filter: "kind = 'ephemeral'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_workspace_id",
                table: "development_workspace_vms",
                column: "workspace_id",
                unique: true,
                filter: "kind = 'primary'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_workspace_id_idempotency_key",
                table: "development_workspace_vms",
                columns: new[] { "workspace_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_idempotency_key",
                table: "development_workspaces",
                columns: new[] { "organization_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_name",
                table: "development_workspaces",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_provider_account_id",
                table: "development_workspaces",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_provider_account_id_provider_volume_~",
                table: "development_workspaces",
                columns: new[] { "provider_account_id", "provider_volume_id" },
                unique: true);

            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    CREATE FUNCTION development_workspace_primary_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                    DECLARE target_id text;
                    BEGIN
                      IF TG_TABLE_NAME = 'development_workspaces' THEN
                        target_id := COALESCE(NEW.id, OLD.id);
                      ELSE
                        target_id := COALESCE(NEW.workspace_id, OLD.workspace_id);
                      END IF;
                      IF EXISTS (SELECT 1 FROM development_workspaces WHERE id = target_id) AND NOT EXISTS (
                        SELECT 1 FROM development_workspaces w JOIN development_workspace_vms v
                          ON v.workspace_id = w.id AND v.id = w.primary_vm_id AND v.kind = 'primary'
                          AND v.organization_id = w.organization_id
                          AND v.provider_account_id = w.provider_account_id AND v.region = w.region
                        WHERE w.id = target_id
                      ) THEN
                        RAISE EXCEPTION 'Workspace must retain its exactly one owned primary VM' USING ERRCODE = '23514';
                      END IF;
                      IF EXISTS (
                        SELECT 1 FROM development_workspace_vms v JOIN development_workspaces w ON w.id = v.workspace_id
                        WHERE w.id = target_id AND
                          (v.provider_account_id <> w.provider_account_id OR v.region <> w.region)
                      ) THEN
                        RAISE EXCEPTION 'Workspace VMs must retain account and location ownership' USING ERRCODE = '23514';
                      END IF;
                      RETURN NULL;
                    END $$;
                    CREATE CONSTRAINT TRIGGER development_workspace_primary_check
                      AFTER INSERT OR UPDATE ON development_workspaces
                      DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION development_workspace_primary_guard();
                    CREATE CONSTRAINT TRIGGER development_workspace_vm_primary_check
                      AFTER INSERT OR UPDATE OR DELETE ON development_workspace_vms
                      DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION development_workspace_primary_guard();
                    CREATE FUNCTION development_workspace_identity_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                      IF TG_TABLE_NAME = 'development_workspace_vms' THEN
                        IF NEW.kind <> OLD.kind OR NEW.workspace_id <> OLD.workspace_id OR NEW.organization_id <> OLD.organization_id THEN
                          RAISE EXCEPTION 'VM kind and ownership are immutable' USING ERRCODE = '23514';
                        END IF;
                      ELSE
                        IF NEW.primary_vm_id <> OLD.primary_vm_id OR NEW.organization_id <> OLD.organization_id
                          OR NEW.provider_account_id <> OLD.provider_account_id OR NEW.region <> OLD.region
                          OR (OLD.provider_volume_id IS NOT NULL AND NEW.provider_volume_id IS DISTINCT FROM OLD.provider_volume_id) THEN
                          RAISE EXCEPTION 'Primary storage identity and ownership are immutable' USING ERRCODE = '23514';
                        END IF;
                      END IF;
                      RETURN NEW;
                    END $$;
                    CREATE TRIGGER development_workspace_identity_check BEFORE UPDATE ON development_workspaces
                      FOR EACH ROW EXECUTE FUNCTION development_workspace_identity_guard();
                    CREATE TRIGGER development_workspace_vm_identity_check BEFORE UPDATE ON development_workspace_vms
                      FOR EACH ROW EXECUTE FUNCTION development_workspace_identity_guard();
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("""
                    DROP FUNCTION development_workspace_primary_guard() CASCADE;
                    DROP FUNCTION development_workspace_identity_guard() CASCADE;
                    """);

            migrationBuilder.DropTable(
                name: "development_workspace_gateway_routes");

            migrationBuilder.DropTable(
                name: "development_workspace_gateway_sessions");

            migrationBuilder.DropTable(
                name: "development_workspace_operations");

            migrationBuilder.DropTable(
                name: "development_workspace_task_leases");

            migrationBuilder.DropTable(
                name: "development_workspace_vms");

            migrationBuilder.DropTable(
                name: "development_workspaces");
        }
    }
}
