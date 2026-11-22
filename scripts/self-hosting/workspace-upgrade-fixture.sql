-- SPDX-License-Identifier: AGPL-3.0-only
BEGIN;
INSERT INTO organizations (id, name, slug, created_at, updated_at)
VALUES ('org_test', 'Synthetic tenant', 'synthetic', now(), now());
INSERT INTO users (id, email, kratos_identity_id, created_at, updated_at)
VALUES ('user_test', 'user@example.test', 'identity_test', now(), now());
INSERT INTO organization_members (id, organization_id, user_id, role, created_at, updated_at)
VALUES ('member_test', 'org_test', 'user_test', 'owner', now(), now());
INSERT INTO browser_sessions
    (id, user_id, kratos_identity_id, kratos_session_id, token_hash, csrf_token_hash,
     created_at, last_seen_at, expires_at, idle_expires_at)
VALUES ('session_test', 'user_test', 'identity_test', 'kratos_test', 'synthetic_token_hash',
        'synthetic_csrf_hash', now(), now(), now() + interval '1 day', now() + interval '1 hour');
INSERT INTO provider_accounts
    (id, organization_id, name, provider_type, enabled, credential_store_provider,
     credential_path, credential_version, credential_generation, validation_status, revision, created_at, updated_at)
VALUES ('account_test', 'org_test', 'Synthetic account', 'hetzner', true, 'openbao',
        'synthetic/provider/reference', '1', 'synthetic_generation', 'unchecked', 1, now(), now());
INSERT INTO provider_plans
    (id, organization_id, provider_account_id, name, normalized_name, region, sku, build_platform,
     enabled, revision, created_at, updated_at)
VALUES ('plan_test', 'org_test', 'account_test', 'Synthetic plan', 'synthetic plan', 'test-region',
        'test-sku', 'linux/amd64', true, 1, now(), now());
INSERT INTO applications (id, organization_id, name, slug, normalized_slug, created_at, updated_at)
VALUES ('application_test', 'org_test', 'Synthetic app', 'test-app', 'test-app', now(), now());
INSERT INTO apps (id, organization_id, application_id, name, slug, plan, database, created_at, updated_at)
VALUES ('app_test', 'org_test', 'application_test', 'Synthetic app', 'test-app', 'test-plan', 'none', now(), now());
INSERT INTO app_secrets (id, organization_id, app_id, name, encrypted_value, created_at, updated_at)
VALUES ('secret_test', 'org_test', 'app_test', 'TEST', 'synthetic_ciphertext', now(), now());
INSERT INTO deployments
    (id, organization_id, app_id, database, status, number, provider_account_id, created_at, updated_at)
VALUES ('deploy_test', 'org_test', 'app_test', 'none', 'completed', '1', 'account_test', now(), now());
INSERT INTO registry_sessions
    (id, organization_id, app_id, deployment_id, subject_id, kind, credential_hash,
     reserved_bytes, charged_bytes, revision, created_at, expires_at)
VALUES ('registry_test', 'org_test', 'app_test', 'deploy_test', 'user_test', 'pull',
        'synthetic_registry_hash', 0, 0, 1, now(), now() + interval '1 hour');
INSERT INTO development_workspaces
    (id, organization_id, created_by, name, primary_vm_id, provider_account_id, region, volume_size_gb,
     volume_status, volume_filesystem, storage_monthly_rate, currency, idempotency_key, request_digest,
     revision, created_at, updated_at)
VALUES ('workspace_test', 'org_test', 'user_test', 'Retired fixture', 'vm_test', 'account_test',
        'test-region', 10, 'deleted', 'ext4', 0, 'USD', 'workspace_key', 'workspace_digest', 1, now(), now());
INSERT INTO development_workspace_vms
    (id, workspace_id, organization_id, kind, idempotency_key, request_digest, generation, desired_state,
     phase, image_id, profile, plan_id, provider_account_id, region, sku, provider_image, image_manifest_sha256,
     architecture, cpu_count, memory_gi_b, compute_hourly_rate, compute_reserved, start_after_stop, gateway_ready,
     push_authorized, git_status, revision, created_at, updated_at, operation_id)
VALUES ('vm_test', 'workspace_test', 'org_test', 'primary', 'vm_key', 'vm_digest', 1, 'stopped',
        'deleted', 'test-image', 'test-profile', 'plan_test', 'account_test', 'test-region', 'test-sku',
        'test-image', 'test-digest', 'amd64', 1, 1, 0, false, false, false, false, 'unknown', 1, now(), now(), 'op_test');
INSERT INTO development_workspace_operations
    (id, workspace_id, vm_id, generation, kind, status, step, attempts, plan_id, provider_account_id,
     region, sku, image_id, provider_image, image_manifest_sha256, compute_hourly_rate, revision, created_at, updated_at)
VALUES ('op_test', 'workspace_test', 'vm_test', 1, 'delete-workspace', 'completed', 'provider-absence-confirmed',
        1, 'plan_test', 'account_test', 'test-region', 'test-sku', 'test-image', 'test-image', 'test-digest', 0, 1, now(), now());
COMMIT;
