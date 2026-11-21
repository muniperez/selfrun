-- Full SQLite EnsureCreated schema from feb807325b371d813ffb2613135a16da2426f220.
-- Generated with that revision's MinicloudDbContext and EF Core SQLite 10.0.5.
-- The source revision's SqliteTestHarness.InitializeAsync uses this provider/model path.
-- This is a source-built test prerequisite, not a published SQLite server release.
CREATE TABLE "grafana_launch_codes" (
    "id" TEXT NOT NULL CONSTRAINT "PK_grafana_launch_codes" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "minicloud_user_id" TEXT NOT NULL,
    "code_hash" TEXT NOT NULL,
    "destination" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "consumed_at" TEXT NULL,
    "created_at" TEXT NOT NULL
);


CREATE TABLE "grafana_proxy_sessions" (
    "id" TEXT NOT NULL CONSTRAINT "PK_grafana_proxy_sessions" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "minicloud_user_id" TEXT NOT NULL,
    "session_hash" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "last_seen_at" TEXT NOT NULL,
    "revoked_at" TEXT NULL,
    "created_at" TEXT NOT NULL
);


CREATE TABLE "organizations" (
    "id" TEXT NOT NULL CONSTRAINT "PK_organizations" PRIMARY KEY,
    "name" TEXT NOT NULL,
    "slug" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL
);


CREATE TABLE "provider_credential_cleanups" (
    "id" TEXT NOT NULL CONSTRAINT "PK_provider_credential_cleanups" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "provider_account_id" TEXT NOT NULL,
    "store_provider" TEXT NOT NULL,
    "path" TEXT NOT NULL,
    "version" TEXT NULL,
    "not_before" TEXT NOT NULL
);


CREATE TABLE "provider_plan_names" (
    "organization_id" TEXT NOT NULL,
    "normalized_name" TEXT NOT NULL,
    "plan_id" TEXT NOT NULL,
    "retired" INTEGER NOT NULL,
    CONSTRAINT "PK_provider_plan_names" PRIMARY KEY ("organization_id", "normalized_name")
);


CREATE TABLE "registry_garbage_candidates" (
    "id" TEXT NOT NULL CONSTRAINT "PK_registry_garbage_candidates" PRIMARY KEY,
    "repository" TEXT NOT NULL,
    "manifest_digest" TEXT NOT NULL,
    "created_at" TEXT NOT NULL
);


CREATE TABLE "registry_maintenance_states" (
    "id" TEXT NOT NULL CONSTRAINT "PK_registry_maintenance_states" PRIMARY KEY,
    "writes_blocked" INTEGER NOT NULL,
    "owner_token" TEXT NULL,
    "revision" INTEGER NOT NULL,
    "updated_at" TEXT NOT NULL
);


CREATE TABLE "runtime_log_records" (
    "id" TEXT NOT NULL CONSTRAINT "PK_runtime_log_records" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "runtime_server_id" TEXT NOT NULL,
    "source" TEXT NOT NULL,
    "service" TEXT NULL,
    "stream" TEXT NOT NULL,
    "cursor" TEXT NOT NULL,
    "content" TEXT NOT NULL,
    "observed_at" TEXT NOT NULL,
    "received_at" TEXT NOT NULL
);


CREATE TABLE "runtime_status_snapshots" (
    "id" TEXT NOT NULL CONSTRAINT "PK_runtime_status_snapshots" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "runtime_server_id" TEXT NOT NULL,
    "agent_version" TEXT NULL,
    "status_json" TEXT NOT NULL,
    "observed_at" TEXT NOT NULL,
    "received_at" TEXT NOT NULL
);


CREATE TABLE "telemetry_authority_outboxes" (
    "sequence" INTEGER NOT NULL CONSTRAINT "PK_telemetry_authority_outboxes" PRIMARY KEY AUTOINCREMENT,
    "event_type" TEXT NOT NULL,
    "entity_id" TEXT NOT NULL,
    "payload_json" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "processed_at" TEXT NULL
);


CREATE TABLE "telemetry_cleanup_jobs" (
    "id" TEXT NOT NULL CONSTRAINT "PK_telemetry_cleanup_jobs" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "tenant_id" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "cutoff_at" TEXT NULL,
    "attempts" INTEGER NOT NULL,
    "last_error" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "completed_at" TEXT NULL
);


CREATE TABLE "telemetry_daily_usages" (
    "id" TEXT NOT NULL CONSTRAINT "PK_telemetry_daily_usages" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "day" TEXT NOT NULL,
    "admitted_bytes" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL
);


CREATE TABLE "users" (
    "id" TEXT NOT NULL CONSTRAINT "PK_users" PRIMARY KEY,
    "firebase_uid" TEXT NOT NULL,
    "email" TEXT NOT NULL,
    "display_name" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL
);


CREATE TABLE "api_keys" (
    "id" TEXT NOT NULL CONSTRAINT "PK_api_keys" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "key_prefix" TEXT NOT NULL,
    "key_hash" TEXT NOT NULL,
    "scopes_json" TEXT NOT NULL,
    "last_used_at" TEXT NULL,
    "expires_at" TEXT NULL,
    "revoked_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_api_keys_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "applications" (
    "id" TEXT NOT NULL CONSTRAINT "PK_applications" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "slug" TEXT NOT NULL,
    "normalized_slug" TEXT NOT NULL,
    "default_environment_id" TEXT NULL,
    "legacy_root_app_id" TEXT NULL,
    "deletion_status" TEXT NULL,
    "deletion_error" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_applications_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_applications_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "database_backup_artifacts" (
    "id" TEXT NOT NULL CONSTRAINT "PK_database_backup_artifacts" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "database_id" TEXT NOT NULL,
    "repository_app_id" TEXT NOT NULL,
    "backup_id" TEXT NOT NULL,
    "key_version" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "size_bytes" INTEGER NOT NULL,
    "sha256" TEXT NULL,
    "completed_at" TEXT NULL,
    "expires_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_database_backup_artifacts_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "database_backup_policies" (
    "id" TEXT NOT NULL CONSTRAINT "PK_database_backup_policies" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "database_id" TEXT NOT NULL,
    "revision" INTEGER NOT NULL DEFAULT 1,
    "keep_last_copies" INTEGER NOT NULL,
    "enabled" INTEGER NOT NULL,
    "frequency_hours" INTEGER NOT NULL,
    "retention_days" INTEGER NOT NULL,
    "minimum_copies" INTEGER NOT NULL,
    "repository_app_id" TEXT NULL,
    "next_due_at" TEXT NULL,
    "status" TEXT NOT NULL,
    "encrypted_key_material_json" TEXT NULL,
    "key_version" TEXT NULL,
    "last_attempt_at" TEXT NULL,
    "last_success_at" TEXT NULL,
    "last_error_code" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_database_backup_policies_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "database_operations" (
    "id" TEXT NOT NULL CONSTRAINT "PK_database_operations" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "backup_policy_id" TEXT NULL,
    "scheduled_policy_revision" INTEGER NULL,
    "database_id" TEXT NULL,
    "server_id" TEXT NULL,
    "kind" TEXT NOT NULL,
    "actor_json" TEXT NOT NULL,
    "request_json" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "ownership_token" TEXT NULL,
    "error_code" TEXT NULL,
    "error_message" TEXT NULL,
    "completed_at" TEXT NULL,
    "lease_expires_at" TEXT NULL,
    "result_json" TEXT NULL,
    "attempt_count" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_database_operations_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "grafana_organization_bindings" (
    "id" TEXT NOT NULL CONSTRAINT "PK_grafana_organization_bindings" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "grafana_org_id" INTEGER NOT NULL,
    "reconciliation_status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_grafana_organization_bindings_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "grafana_user_bindings" (
    "id" TEXT NOT NULL CONSTRAINT "PK_grafana_user_bindings" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "minicloud_user_id" TEXT NOT NULL,
    "grafana_user_id" INTEGER NOT NULL,
    "expected_role" TEXT NOT NULL,
    "reconciliation_status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_grafana_user_bindings_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "provider_accounts" (
    "id" TEXT NOT NULL CONSTRAINT "PK_provider_accounts" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "provider_type" TEXT NOT NULL,
    "enabled" INTEGER NOT NULL,
    "project_id" TEXT NULL,
    "default_os_id" INTEGER NULL,
    "credential_store_provider" TEXT NOT NULL,
    "credential_path" TEXT NOT NULL,
    "credential_version" TEXT NULL,
    "credential_generation" TEXT NOT NULL,
    "validation_status" TEXT NOT NULL,
    "last_validated_at" TEXT NULL,
    "revision" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_provider_accounts_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_provider_accounts_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "organization_members" (
    "id" TEXT NOT NULL CONSTRAINT "PK_organization_members" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "user_id" TEXT NOT NULL,
    "role" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_organization_members_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_organization_members_users_user_id" FOREIGN KEY ("user_id") REFERENCES "users" ("id") ON DELETE CASCADE
);


CREATE TABLE "cli_login_sessions" (
    "id" TEXT NOT NULL CONSTRAINT "PK_cli_login_sessions" PRIMARY KEY,
    "organization_id" TEXT NULL,
    "approved_by_user_id" TEXT NULL,
    "status" TEXT NOT NULL,
    "scopes_json" TEXT NOT NULL,
    "api_key_id" TEXT NULL,
    "raw_api_key_token" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "approved_at" TEXT NULL,
    "exchanged_at" TEXT NULL,
    CONSTRAINT "FK_cli_login_sessions_api_keys_api_key_id" FOREIGN KEY ("api_key_id") REFERENCES "api_keys" ("id"),
    CONSTRAINT "FK_cli_login_sessions_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id"),
    CONSTRAINT "FK_cli_login_sessions_users_approved_by_user_id" FOREIGN KEY ("approved_by_user_id") REFERENCES "users" ("id")
);


CREATE TABLE "app_storage_volumes" (
    "id" TEXT NOT NULL CONSTRAINT "PK_app_storage_volumes" PRIMARY KEY,
    "app_id" TEXT NOT NULL,
    "organization_id" TEXT NULL,
    "provider_account_id" TEXT NULL,
    "provider" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "region" TEXT NOT NULL,
    "provider_volume_id" TEXT NULL,
    "creation_attempted" INTEGER NOT NULL,
    "size_gb" INTEGER NOT NULL,
    "uid" INTEGER NOT NULL,
    "gid" INTEGER NOT NULL,
    CONSTRAINT "FK_app_storage_volumes_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "managed_database_networks" (
    "id" TEXT NOT NULL CONSTRAINT "PK_managed_database_networks" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "provider_account_id" TEXT NOT NULL,
    "provider_account_revision" INTEGER NOT NULL,
    "name" TEXT NOT NULL,
    "region" TEXT NOT NULL,
    "ip_range" TEXT NOT NULL,
    "subnet_range" TEXT NOT NULL,
    "network_zone" TEXT NULL,
    "provider_network_id" TEXT NULL,
    "creation_attempted" INTEGER NOT NULL,
    "revision" INTEGER NOT NULL,
    "status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_managed_database_networks_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_managed_database_networks_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_managed_database_networks_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "provider_plans" (
    "id" TEXT NOT NULL CONSTRAINT "PK_provider_plans" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "provider_account_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "normalized_name" TEXT NOT NULL,
    "region" TEXT NOT NULL,
    "sku" TEXT NOT NULL,
    "image" TEXT NULL,
    "build_platform" TEXT NOT NULL,
    "enabled" INTEGER NOT NULL,
    "revision" INTEGER NOT NULL,
    "provisioning_locked_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_provider_plans_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_provider_plans_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "apps" (
    "id" TEXT NOT NULL CONSTRAINT "PK_apps" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "application_id" TEXT NULL,
    "name" TEXT NOT NULL,
    "slug" TEXT NOT NULL,
    "parent_app_id" TEXT NULL,
    "branch_name" TEXT NULL,
    "environment_name" TEXT NULL,
    "normalized_environment_name" TEXT NULL,
    "environment_kind" TEXT NOT NULL DEFAULT 'persistent',
    "runtime_parent_slug" TEXT NULL,
    "source_branch" TEXT NULL,
    "source_environment_id" TEXT NULL,
    "environment_key" TEXT NULL,
    "deletion_status" TEXT NULL,
    "deletion_error" TEXT NULL,
    "plan" TEXT NOT NULL,
    "plan_id" TEXT NULL,
    "database" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_apps_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_apps_applications_organization_id_application_id" FOREIGN KEY ("organization_id", "application_id") REFERENCES "applications" ("organization_id", "id") ON DELETE RESTRICT,
    CONSTRAINT "FK_apps_apps_parent_app_id" FOREIGN KEY ("parent_app_id") REFERENCES "apps" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_apps_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_apps_provider_plans_organization_id_plan_id" FOREIGN KEY ("organization_id", "plan_id") REFERENCES "provider_plans" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "managed_database_servers" (
    "id" TEXT NOT NULL CONSTRAINT "PK_managed_database_servers" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "mode" TEXT NOT NULL,
    "engine" TEXT NOT NULL,
    "engine_version" TEXT NOT NULL,
    "plan_id" TEXT NOT NULL,
    "runtime_app_id" TEXT NULL,
    "runtime_server_id" TEXT NULL,
    "revision" INTEGER NOT NULL,
    "status" TEXT NOT NULL,
    "runtime_deployment_id" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_managed_database_servers_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_managed_database_servers_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_managed_database_servers_provider_plans_organization_id_plan_id" FOREIGN KEY ("organization_id", "plan_id") REFERENCES "provider_plans" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "app_secrets" (
    "id" TEXT NOT NULL CONSTRAINT "PK_app_secrets" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "encrypted_value" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_app_secrets_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_app_secrets_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "app_service_secret_services" (
    "id" TEXT NOT NULL CONSTRAINT "PK_app_service_secret_services" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "deleted_at" TEXT NULL,
    CONSTRAINT "FK_app_service_secret_services_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_app_service_secret_services_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "app_service_secrets" (
    "id" TEXT NOT NULL CONSTRAINT "PK_app_service_secrets" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "store_provider" TEXT NOT NULL,
    "store_path" TEXT NOT NULL,
    "store_version" TEXT NULL,
    "status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "deleted_at" TEXT NULL,
    CONSTRAINT "FK_app_service_secrets_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_app_service_secrets_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "app_telemetry_settings" (
    "id" TEXT NOT NULL CONSTRAINT "PK_app_telemetry_settings" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "tenant_id" TEXT NOT NULL,
    "enabled" INTEGER NOT NULL,
    "deleting" INTEGER NOT NULL,
    "retention_policy" TEXT NOT NULL,
    "last_accepted_at" TEXT NULL,
    "provisioning_status" TEXT NOT NULL,
    "provisioning_version" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "deleted_at" TEXT NULL,
    CONSTRAINT "FK_app_telemetry_settings_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_app_telemetry_settings_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "audit_events" (
    "id" TEXT NOT NULL CONSTRAINT "PK_audit_events" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NULL,
    "actor" TEXT NOT NULL,
    "action" TEXT NOT NULL,
    "details" TEXT NULL,
    "created_at" TEXT NOT NULL,
    CONSTRAINT "FK_audit_events_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id"),
    CONSTRAINT "FK_audit_events_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "custom_domain_registrations" (
    "id" TEXT NOT NULL CONSTRAINT "PK_custom_domain_registrations" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NULL,
    "hostname" TEXT NOT NULL,
    "verification_token" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "failure_message" TEXT NULL,
    "last_checked_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_custom_domain_registrations_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_custom_domain_registrations_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "deployment_artifacts" (
    "id" TEXT NOT NULL CONSTRAINT "PK_deployment_artifacts" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "file_name" TEXT NOT NULL,
    "content_type" TEXT NOT NULL,
    "expected_size_bytes" INTEGER NULL,
    "expected_sha256" TEXT NULL,
    "size_bytes" INTEGER NULL,
    "sha256" TEXT NULL,
    "manifest_json" TEXT NOT NULL,
    "storage_path" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "failure_code" TEXT NULL,
    "failure_message" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "uploaded_at" TEXT NULL,
    "deleted_at" TEXT NULL,
    CONSTRAINT "FK_deployment_artifacts_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_deployment_artifacts_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "grafana_data_source_bindings" (
    "id" TEXT NOT NULL CONSTRAINT "PK_grafana_data_source_bindings" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "grafana_uid" TEXT NOT NULL,
    "credential_reference" TEXT NOT NULL,
    "reconciliation_status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "deleted_at" TEXT NULL,
    CONSTRAINT "FK_grafana_data_source_bindings_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_grafana_data_source_bindings_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "image_publications" (
    "id" TEXT NOT NULL CONSTRAINT "PK_image_publications" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "idempotency_key" TEXT NOT NULL,
    "repository" TEXT NOT NULL,
    "candidate_tag" TEXT NOT NULL,
    "target_version" TEXT NOT NULL,
    "platform" TEXT NOT NULL,
    "input_sha256" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "manifest_digest" TEXT NULL,
    "manifest_json" TEXT NULL,
    "size_bytes" INTEGER NULL,
    "failure_code" TEXT NULL,
    "revision" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "ready_at" TEXT NULL,
    "retired_at" TEXT NULL,
    CONSTRAINT "FK_image_publications_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE RESTRICT
);


CREATE TABLE "runtime_servers" (
    "id" TEXT NOT NULL CONSTRAINT "PK_runtime_servers" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "provider_account_id" TEXT NULL,
    "provider" TEXT NOT NULL,
    "provider_server_id" TEXT NULL,
    "hostname" TEXT NULL,
    "agent_version" TEXT NULL,
    "last_seen_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_runtime_servers_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_runtime_servers_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_runtime_servers_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "server_instances" (
    "id" TEXT NOT NULL CONSTRAINT "PK_server_instances" PRIMARY KEY,
    "provider_account_id" TEXT NULL,
    "plan_id" TEXT NULL,
    "sku" TEXT NULL,
    "image" TEXT NULL,
    "build_platform" TEXT NULL,
    "build_platform_observed_at" TEXT NULL,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "provider" TEXT NOT NULL,
    "provider_server_id" TEXT NULL,
    "ip_address" TEXT NULL,
    "region" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "bootstrap_status" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_server_instances_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_server_instances_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_server_instances_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "telemetry_ingestion_credentials" (
    "id" TEXT NOT NULL CONSTRAINT "PK_telemetry_ingestion_credentials" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "key_prefix" TEXT NOT NULL,
    "key_hash" TEXT NOT NULL,
    "credential_version" INTEGER NOT NULL,
    "created_by_actor" TEXT NULL,
    "last_used_at" TEXT NULL,
    "expires_at" TEXT NULL,
    "revoked_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_telemetry_ingestion_credentials_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_telemetry_ingestion_credentials_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "managed_databases" (
    "id" TEXT NOT NULL CONSTRAINT "PK_managed_databases" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "engine" TEXT NOT NULL,
    "engine_version" TEXT NOT NULL,
    "placement" TEXT NOT NULL,
    "server_id" TEXT NULL,
    "recovery_source_app_id" TEXT NULL,
    "app_id" TEXT NULL,
    "access_mode" TEXT NOT NULL,
    "network_id" TEXT NULL,
    "private_ip" TEXT NULL,
    "allowed_cidrs_json" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "revision" INTEGER NOT NULL,
    "encrypted_credentials_json" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "AK_managed_databases_organization_id_id" UNIQUE ("organization_id", "id"),
    CONSTRAINT "FK_managed_databases_managed_database_networks_organization_id_network_id" FOREIGN KEY ("organization_id", "network_id") REFERENCES "managed_database_networks" ("organization_id", "id") ON DELETE RESTRICT,
    CONSTRAINT "FK_managed_databases_managed_database_servers_organization_id_server_id" FOREIGN KEY ("organization_id", "server_id") REFERENCES "managed_database_servers" ("organization_id", "id") ON DELETE RESTRICT,
    CONSTRAINT "FK_managed_databases_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE RESTRICT
);


CREATE TABLE "domains" (
    "id" TEXT NOT NULL CONSTRAINT "PK_domains" PRIMARY KEY,
    "custom_domain_registration_id" TEXT NULL,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "hostname" TEXT NOT NULL,
    "kind" TEXT NOT NULL,
    "path_prefix" TEXT NOT NULL,
    "verification_token" TEXT NULL,
    "last_checked_at" TEXT NULL,
    "target_ip" TEXT NULL,
    "dns_status" TEXT NOT NULL,
    "apply_status" TEXT NOT NULL,
    "ssl_status" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "is_primary" INTEGER NOT NULL,
    "failure_code" TEXT NULL,
    "failure_message" TEXT NULL,
    "last_applied_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_domains_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_domains_custom_domain_registrations_custom_domain_registration_id" FOREIGN KEY ("custom_domain_registration_id") REFERENCES "custom_domain_registrations" ("id"),
    CONSTRAINT "FK_domains_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE
);


CREATE TABLE "asset_releases" (
    "id" TEXT NOT NULL CONSTRAINT "PK_asset_releases" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "artifact_id" TEXT NOT NULL,
    "input_sha256" TEXT NOT NULL,
    "output_path" TEXT NOT NULL,
    "manifest_json" TEXT NULL,
    "manifest_sha256" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "ready_at" TEXT NULL,
    CONSTRAINT "FK_asset_releases_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_asset_releases_deployment_artifacts_artifact_id" FOREIGN KEY ("artifact_id") REFERENCES "deployment_artifacts" ("id") ON DELETE RESTRICT
);


CREATE TABLE "runtime_tokens" (
    "id" TEXT NOT NULL CONSTRAINT "PK_runtime_tokens" PRIMARY KEY,
    "runtime_server_id" TEXT NOT NULL,
    "token_prefix" TEXT NOT NULL,
    "token_hash" TEXT NOT NULL,
    "last_used_at" TEXT NULL,
    "expires_at" TEXT NULL,
    "revoked_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_runtime_tokens_runtime_servers_runtime_server_id" FOREIGN KEY ("runtime_server_id") REFERENCES "runtime_servers" ("id") ON DELETE CASCADE
);


CREATE TABLE "managed_database_bindings" (
    "id" TEXT NOT NULL CONSTRAINT "PK_managed_database_bindings" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "database_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "service_name" TEXT NOT NULL,
    "host" TEXT NULL,
    "port" INTEGER NOT NULL,
    "ssl_mode" TEXT NULL,
    "status" TEXT NOT NULL,
    "revision" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_managed_database_bindings_apps_organization_id_app_id" FOREIGN KEY ("organization_id", "app_id") REFERENCES "apps" ("organization_id", "id") ON DELETE RESTRICT,
    CONSTRAINT "FK_managed_database_bindings_managed_databases_organization_id_database_id" FOREIGN KEY ("organization_id", "database_id") REFERENCES "managed_databases" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "deployments" (
    "id" TEXT NOT NULL CONSTRAINT "PK_deployments" PRIMARY KEY,
    "plan_id" TEXT NULL,
    "plan_revision" INTEGER NULL,
    "provider_account_id" TEXT NULL,
    "provider_type" TEXT NULL,
    "provider_region" TEXT NULL,
    "provider_sku" TEXT NULL,
    "provider_image" TEXT NULL,
    "provider_build_platform" TEXT NULL,
    "provider_creation_attempted" INTEGER NOT NULL,
    "cancellation_requested_at" TEXT NULL,
    "cancellation_requested_by" TEXT NULL,
    "requested_service_names_json" TEXT NULL,
    "asset_release_id" TEXT NULL,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "database" TEXT NOT NULL,
    "status" TEXT NOT NULL,
    "domain" TEXT NULL,
    "website_url" TEXT NULL,
    "github_run_id" TEXT NULL,
    "github_run_url" TEXT NULL,
    "commit_sha" TEXT NULL,
    "failure_code" TEXT NULL,
    "failure_message" TEXT NULL,
    "worker_id" TEXT NULL,
    "ownership_token" TEXT NULL,
    "ownership_acquired_at" TEXT NULL,
    "created_at" TEXT NOT NULL,
    "started_at" TEXT NULL,
    "completed_at" TEXT NULL,
    "updated_at" TEXT NOT NULL,
    CONSTRAINT "FK_deployments_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_deployments_asset_releases_asset_release_id" FOREIGN KEY ("asset_release_id") REFERENCES "asset_releases" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_deployments_organizations_organization_id" FOREIGN KEY ("organization_id") REFERENCES "organizations" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_deployments_provider_accounts_organization_id_provider_account_id" FOREIGN KEY ("organization_id", "provider_account_id") REFERENCES "provider_accounts" ("organization_id", "id") ON DELETE RESTRICT
);


CREATE TABLE "deployment_events" (
    "id" TEXT NOT NULL CONSTRAINT "PK_deployment_events" PRIMARY KEY,
    "deployment_id" TEXT NOT NULL,
    "level" TEXT NOT NULL,
    "message" TEXT NOT NULL,
    "metadata_json" TEXT NULL,
    "created_at" TEXT NOT NULL,
    CONSTRAINT "FK_deployment_events_deployments_deployment_id" FOREIGN KEY ("deployment_id") REFERENCES "deployments" ("id") ON DELETE CASCADE
);


CREATE TABLE "deployment_logs" (
    "id" TEXT NOT NULL CONSTRAINT "PK_deployment_logs" PRIMARY KEY,
    "deployment_id" TEXT NOT NULL,
    "source" TEXT NOT NULL,
    "content" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    CONSTRAINT "FK_deployment_logs_deployments_deployment_id" FOREIGN KEY ("deployment_id") REFERENCES "deployments" ("id") ON DELETE CASCADE
);


CREATE TABLE "deployment_services" (
    "id" TEXT NOT NULL CONSTRAINT "PK_deployment_services" PRIMARY KEY,
    "image_publication_id" TEXT NULL,
    "image_digest" TEXT NULL,
    "image_platform" TEXT NULL,
    "asset_mounts_json" TEXT NULL,
    "storage_json" TEXT NULL,
    "deployment_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "image" TEXT NOT NULL,
    "artifact_id" TEXT NULL,
    "port" INTEGER NOT NULL,
    "public" INTEGER NOT NULL,
    "path" TEXT NOT NULL,
    "health_path" TEXT NOT NULL,
    "env_json" TEXT NULL,
    "secret_env_json" TEXT NULL,
    "sha256" TEXT NULL,
    CONSTRAINT "FK_deployment_services_deployment_artifacts_artifact_id" FOREIGN KEY ("artifact_id") REFERENCES "deployment_artifacts" ("id"),
    CONSTRAINT "FK_deployment_services_deployments_deployment_id" FOREIGN KEY ("deployment_id") REFERENCES "deployments" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_deployment_services_image_publications_image_publication_id" FOREIGN KEY ("image_publication_id") REFERENCES "image_publications" ("id") ON DELETE RESTRICT
);


CREATE TABLE "registry_sessions" (
    "id" TEXT NOT NULL CONSTRAINT "PK_registry_sessions" PRIMARY KEY,
    "organization_id" TEXT NOT NULL,
    "app_id" TEXT NOT NULL,
    "image_publication_id" TEXT NULL,
    "deployment_id" TEXT NULL,
    "server_id" TEXT NULL,
    "ownership_token" TEXT NULL,
    "subject_id" TEXT NOT NULL,
    "subject_kind" TEXT NOT NULL,
    "kind" TEXT NOT NULL,
    "credential_hash" TEXT NOT NULL,
    "reserved_bytes" INTEGER NOT NULL,
    "charged_bytes" INTEGER NOT NULL,
    "revision" INTEGER NOT NULL,
    "created_at" TEXT NOT NULL,
    "expires_at" TEXT NOT NULL,
    "revoked_at" TEXT NULL,
    "reclaimed_at" TEXT NULL,
    CONSTRAINT "FK_registry_sessions_apps_app_id" FOREIGN KEY ("app_id") REFERENCES "apps" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_registry_sessions_deployments_deployment_id" FOREIGN KEY ("deployment_id") REFERENCES "deployments" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_registry_sessions_image_publications_image_publication_id" FOREIGN KEY ("image_publication_id") REFERENCES "image_publications" ("id") ON DELETE RESTRICT
);


CREATE TABLE "registry_uploads" (
    "id" TEXT NOT NULL CONSTRAINT "PK_registry_uploads" PRIMARY KEY,
    "session_id" TEXT NOT NULL,
    "created_at" TEXT NOT NULL,
    CONSTRAINT "FK_registry_uploads_registry_sessions_session_id" FOREIGN KEY ("session_id") REFERENCES "registry_sessions" ("id") ON DELETE CASCADE
);


CREATE INDEX "IX_api_keys_key_prefix" ON "api_keys" ("key_prefix");


CREATE INDEX "IX_api_keys_organization_id_revoked_at" ON "api_keys" ("organization_id", "revoked_at");


CREATE UNIQUE INDEX "IX_app_secrets_app_id_name" ON "app_secrets" ("app_id", "name");


CREATE INDEX "IX_app_secrets_organization_id" ON "app_secrets" ("organization_id");


CREATE UNIQUE INDEX "IX_app_service_secret_services_app_id_service_name" ON "app_service_secret_services" ("app_id", "service_name");


CREATE INDEX "IX_app_service_secret_services_organization_id_app_id" ON "app_service_secret_services" ("organization_id", "app_id");


CREATE INDEX "IX_app_service_secrets_app_id_service_name" ON "app_service_secrets" ("app_id", "service_name");


CREATE UNIQUE INDEX "IX_app_service_secrets_app_id_service_name_name" ON "app_service_secrets" ("app_id", "service_name", "name");


CREATE INDEX "IX_app_service_secrets_organization_id_app_id" ON "app_service_secrets" ("organization_id", "app_id");


CREATE UNIQUE INDEX "IX_app_storage_volumes_app_id_name" ON "app_storage_volumes" ("app_id", "name");


CREATE INDEX "IX_app_storage_volumes_organization_id_provider_account_id" ON "app_storage_volumes" ("organization_id", "provider_account_id");


CREATE UNIQUE INDEX "IX_app_storage_volumes_provider_account_id_provider_volume_id" ON "app_storage_volumes" ("provider_account_id", "provider_volume_id");


CREATE UNIQUE INDEX "IX_app_telemetry_settings_app_id" ON "app_telemetry_settings" ("app_id");


CREATE UNIQUE INDEX "IX_app_telemetry_settings_organization_id_app_id" ON "app_telemetry_settings" ("organization_id", "app_id");


CREATE UNIQUE INDEX "IX_app_telemetry_settings_tenant_id" ON "app_telemetry_settings" ("tenant_id");


CREATE UNIQUE INDEX "IX_applications_legacy_root_app_id" ON "applications" ("legacy_root_app_id");


CREATE UNIQUE INDEX "IX_applications_organization_id_normalized_slug" ON "applications" ("organization_id", "normalized_slug");


CREATE UNIQUE INDEX "IX_applications_organization_id_slug" ON "applications" ("organization_id", "slug");


CREATE UNIQUE INDEX "IX_apps_application_id_normalized_environment_name" ON "apps" ("application_id", "normalized_environment_name");


CREATE UNIQUE INDEX "IX_apps_environment_key" ON "apps" ("environment_key");


CREATE INDEX "IX_apps_organization_id_application_id" ON "apps" ("organization_id", "application_id");


CREATE INDEX "IX_apps_organization_id_plan_id" ON "apps" ("organization_id", "plan_id");


CREATE UNIQUE INDEX "IX_apps_organization_id_slug" ON "apps" ("organization_id", "slug");


CREATE UNIQUE INDEX "IX_apps_parent_app_id_branch_name" ON "apps" ("parent_app_id", "branch_name");


CREATE INDEX "IX_asset_releases_app_id_name_input_sha256" ON "asset_releases" ("app_id", "name", "input_sha256");


CREATE INDEX "IX_asset_releases_artifact_id" ON "asset_releases" ("artifact_id");


CREATE INDEX "IX_audit_events_app_id_created_at" ON "audit_events" ("app_id", "created_at");


CREATE INDEX "IX_audit_events_organization_id_created_at" ON "audit_events" ("organization_id", "created_at");


CREATE INDEX "IX_cli_login_sessions_api_key_id" ON "cli_login_sessions" ("api_key_id");


CREATE INDEX "IX_cli_login_sessions_approved_by_user_id" ON "cli_login_sessions" ("approved_by_user_id");


CREATE INDEX "IX_cli_login_sessions_expires_at" ON "cli_login_sessions" ("expires_at");


CREATE INDEX "IX_cli_login_sessions_organization_id" ON "cli_login_sessions" ("organization_id");


CREATE INDEX "IX_cli_login_sessions_status" ON "cli_login_sessions" ("status");


CREATE INDEX "IX_custom_domain_registrations_app_id" ON "custom_domain_registrations" ("app_id");


CREATE UNIQUE INDEX "IX_custom_domain_registrations_hostname" ON "custom_domain_registrations" ("hostname");


CREATE INDEX "IX_custom_domain_registrations_organization_id" ON "custom_domain_registrations" ("organization_id");


CREATE INDEX "IX_database_backup_artifacts_organization_id" ON "database_backup_artifacts" ("organization_id");


CREATE UNIQUE INDEX "IX_database_backup_policies_database_id" ON "database_backup_policies" ("database_id");


CREATE INDEX "IX_database_backup_policies_organization_id" ON "database_backup_policies" ("organization_id");


CREATE INDEX "IX_database_operations_organization_id" ON "database_operations" ("organization_id");


CREATE INDEX "IX_deployment_artifacts_app_id" ON "deployment_artifacts" ("app_id");


CREATE INDEX "IX_deployment_artifacts_organization_id_app_id_created_at" ON "deployment_artifacts" ("organization_id", "app_id", "created_at");


CREATE INDEX "IX_deployment_artifacts_status_expires_at" ON "deployment_artifacts" ("status", "expires_at");


CREATE INDEX "IX_deployment_events_deployment_id_created_at" ON "deployment_events" ("deployment_id", "created_at");


CREATE INDEX "IX_deployment_logs_deployment_id_created_at" ON "deployment_logs" ("deployment_id", "created_at");


CREATE INDEX "IX_deployment_services_artifact_id" ON "deployment_services" ("artifact_id");


CREATE UNIQUE INDEX "IX_deployment_services_deployment_id_name" ON "deployment_services" ("deployment_id", "name");


CREATE INDEX "IX_deployment_services_image_publication_id" ON "deployment_services" ("image_publication_id");


CREATE UNIQUE INDEX "IX_deployments_app_id" ON "deployments" ("app_id") WHERE ownership_token IS NOT NULL;


CREATE INDEX "IX_deployments_app_id_created_at" ON "deployments" ("app_id", "created_at");


CREATE INDEX "IX_deployments_asset_release_id" ON "deployments" ("asset_release_id");


CREATE INDEX "IX_deployments_organization_id_created_at" ON "deployments" ("organization_id", "created_at");


CREATE INDEX "IX_deployments_organization_id_provider_account_id" ON "deployments" ("organization_id", "provider_account_id");


CREATE UNIQUE INDEX "IX_deployments_ownership_token" ON "deployments" ("ownership_token");


CREATE INDEX "IX_deployments_status_created_at" ON "deployments" ("status", "created_at");


CREATE INDEX "IX_domains_app_id_service_name" ON "domains" ("app_id", "service_name");


CREATE INDEX "IX_domains_custom_domain_registration_id" ON "domains" ("custom_domain_registration_id");


CREATE UNIQUE INDEX "IX_domains_hostname" ON "domains" ("hostname");


CREATE INDEX "IX_domains_organization_id" ON "domains" ("organization_id");


CREATE INDEX "IX_grafana_data_source_bindings_app_id_deleted_at" ON "grafana_data_source_bindings" ("app_id", "deleted_at");


CREATE UNIQUE INDEX "IX_grafana_data_source_bindings_grafana_uid" ON "grafana_data_source_bindings" ("grafana_uid");


CREATE INDEX "IX_grafana_data_source_bindings_organization_id" ON "grafana_data_source_bindings" ("organization_id");


CREATE UNIQUE INDEX "IX_grafana_launch_codes_code_hash" ON "grafana_launch_codes" ("code_hash");


CREATE INDEX "IX_grafana_launch_codes_expires_at" ON "grafana_launch_codes" ("expires_at");


CREATE UNIQUE INDEX "IX_grafana_organization_bindings_grafana_org_id" ON "grafana_organization_bindings" ("grafana_org_id");


CREATE UNIQUE INDEX "IX_grafana_organization_bindings_organization_id" ON "grafana_organization_bindings" ("organization_id");


CREATE INDEX "IX_grafana_proxy_sessions_organization_id_minicloud_user_id" ON "grafana_proxy_sessions" ("organization_id", "minicloud_user_id");


CREATE UNIQUE INDEX "IX_grafana_proxy_sessions_session_hash" ON "grafana_proxy_sessions" ("session_hash");


CREATE UNIQUE INDEX "IX_grafana_user_bindings_organization_id_minicloud_user_id" ON "grafana_user_bindings" ("organization_id", "minicloud_user_id");


CREATE UNIQUE INDEX "IX_image_publications_app_id_idempotency_key" ON "image_publications" ("app_id", "idempotency_key");


CREATE INDEX "IX_image_publications_organization_id_app_id_service_name" ON "image_publications" ("organization_id", "app_id", "service_name");


CREATE UNIQUE INDEX "IX_image_publications_repository_candidate_tag" ON "image_publications" ("repository", "candidate_tag");


CREATE INDEX "IX_image_publications_status_expires_at" ON "image_publications" ("status", "expires_at");


CREATE UNIQUE INDEX "IX_managed_database_bindings_app_id_service_name" ON "managed_database_bindings" ("app_id", "service_name");


CREATE INDEX "IX_managed_database_bindings_organization_id_app_id" ON "managed_database_bindings" ("organization_id", "app_id");


CREATE INDEX "IX_managed_database_bindings_organization_id_database_id" ON "managed_database_bindings" ("organization_id", "database_id");


CREATE UNIQUE INDEX "IX_managed_database_networks_organization_id_name" ON "managed_database_networks" ("organization_id", "name");


CREATE INDEX "IX_managed_database_networks_organization_id_provider_account_id" ON "managed_database_networks" ("organization_id", "provider_account_id");


CREATE UNIQUE INDEX "IX_managed_database_networks_provider_account_id_provider_network_id" ON "managed_database_networks" ("provider_account_id", "provider_network_id");


CREATE INDEX "IX_managed_database_servers_organization_id" ON "managed_database_servers" ("organization_id");


CREATE UNIQUE INDEX "IX_managed_database_servers_organization_id_name" ON "managed_database_servers" ("organization_id", "name");


CREATE INDEX "IX_managed_database_servers_organization_id_plan_id" ON "managed_database_servers" ("organization_id", "plan_id");


CREATE UNIQUE INDEX "IX_managed_databases_app_id" ON "managed_databases" ("app_id");


CREATE INDEX "IX_managed_databases_organization_id" ON "managed_databases" ("organization_id");


CREATE INDEX "IX_managed_databases_organization_id_network_id" ON "managed_databases" ("organization_id", "network_id");


CREATE INDEX "IX_managed_databases_organization_id_server_id" ON "managed_databases" ("organization_id", "server_id");


CREATE UNIQUE INDEX "IX_managed_databases_server_id_name" ON "managed_databases" ("server_id", "name");


CREATE UNIQUE INDEX "IX_organization_members_organization_id_user_id" ON "organization_members" ("organization_id", "user_id");


CREATE INDEX "IX_organization_members_user_id" ON "organization_members" ("user_id");


CREATE UNIQUE INDEX "IX_organizations_slug" ON "organizations" ("slug");


CREATE UNIQUE INDEX "IX_provider_plans_organization_id_normalized_name" ON "provider_plans" ("organization_id", "normalized_name");


CREATE INDEX "IX_provider_plans_organization_id_provider_account_id" ON "provider_plans" ("organization_id", "provider_account_id");


CREATE INDEX "IX_registry_garbage_candidates_repository_manifest_digest" ON "registry_garbage_candidates" ("repository", "manifest_digest");


CREATE INDEX "IX_registry_sessions_app_id" ON "registry_sessions" ("app_id");


CREATE UNIQUE INDEX "IX_registry_sessions_credential_hash" ON "registry_sessions" ("credential_hash");


CREATE INDEX "IX_registry_sessions_deployment_id" ON "registry_sessions" ("deployment_id");


CREATE INDEX "IX_registry_sessions_image_publication_id" ON "registry_sessions" ("image_publication_id");


CREATE INDEX "IX_registry_sessions_organization_id_app_id_expires_at" ON "registry_sessions" ("organization_id", "app_id", "expires_at");


CREATE INDEX "IX_registry_uploads_session_id" ON "registry_uploads" ("session_id");


CREATE INDEX "IX_runtime_log_records_app_id_observed_at" ON "runtime_log_records" ("app_id", "observed_at");


CREATE UNIQUE INDEX "IX_runtime_log_records_runtime_server_id_source_cursor_observed_at" ON "runtime_log_records" ("runtime_server_id", "source", "cursor", "observed_at");


CREATE INDEX "IX_runtime_servers_app_id" ON "runtime_servers" ("app_id");


CREATE INDEX "IX_runtime_servers_organization_id_provider_account_id" ON "runtime_servers" ("organization_id", "provider_account_id");


CREATE INDEX "IX_runtime_servers_provider_account_id_provider_provider_server_id" ON "runtime_servers" ("provider_account_id", "provider", "provider_server_id");


CREATE INDEX "IX_runtime_status_snapshots_app_id_received_at" ON "runtime_status_snapshots" ("app_id", "received_at");


CREATE INDEX "IX_runtime_tokens_runtime_server_id_revoked_at" ON "runtime_tokens" ("runtime_server_id", "revoked_at");


CREATE INDEX "IX_runtime_tokens_token_prefix" ON "runtime_tokens" ("token_prefix");


CREATE INDEX "IX_server_instances_app_id" ON "server_instances" ("app_id");


CREATE INDEX "IX_server_instances_organization_id_provider_account_id" ON "server_instances" ("organization_id", "provider_account_id");


CREATE INDEX "IX_telemetry_authority_outboxes_event_type_entity_id" ON "telemetry_authority_outboxes" ("event_type", "entity_id");


CREATE INDEX "IX_telemetry_authority_outboxes_processed_at" ON "telemetry_authority_outboxes" ("processed_at");


CREATE INDEX "IX_telemetry_cleanup_jobs_app_id_status" ON "telemetry_cleanup_jobs" ("app_id", "status");


CREATE INDEX "IX_telemetry_cleanup_jobs_status" ON "telemetry_cleanup_jobs" ("status");


CREATE UNIQUE INDEX "IX_telemetry_daily_usages_app_id_day" ON "telemetry_daily_usages" ("app_id", "day");


CREATE INDEX "IX_telemetry_daily_usages_organization_id_day" ON "telemetry_daily_usages" ("organization_id", "day");


CREATE INDEX "IX_telemetry_ingestion_credentials_app_id_service_name_revoked_at" ON "telemetry_ingestion_credentials" ("app_id", "service_name", "revoked_at");


CREATE INDEX "IX_telemetry_ingestion_credentials_key_prefix" ON "telemetry_ingestion_credentials" ("key_prefix");


CREATE INDEX "IX_telemetry_ingestion_credentials_organization_id_app_id" ON "telemetry_ingestion_credentials" ("organization_id", "app_id");


CREATE UNIQUE INDEX "IX_users_email" ON "users" ("email");


CREATE UNIQUE INDEX "IX_users_firebase_uid" ON "users" ("firebase_uid");
