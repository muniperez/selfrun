#!/bin/sh
set -eu

read_secret() {
    path="$1"
    if [ ! -r "$path" ]; then
        printf '%s\n' "Required protected database credential file is unavailable." >&2
        exit 1
    fi
    value="$(cat "$path")"
    if [ -z "$value" ]; then
        printf '%s\n' "A required protected database credential file is empty." >&2
        exit 1
    fi
    printf '%s' "$value"
}

PGPASSWORD="$(read_secret "${SELFRUN_POSTGRES_ADMIN_PASSWORD_FILE:-/run/secrets/postgres-admin-password}")"
SELFRUN_APP_DB_PASSWORD="$(read_secret "${SELFRUN_APP_DB_PASSWORD_FILE:-/run/secrets/app-db-password}")"
SELFRUN_KRATOS_DB_PASSWORD="$(read_secret "${SELFRUN_KRATOS_DB_PASSWORD_FILE:-/run/secrets/kratos-db-password}")"
export PGPASSWORD SELFRUN_APP_DB_PASSWORD SELFRUN_KRATOS_DB_PASSWORD

psql -v ON_ERROR_STOP=1 <<'SQL'
\getenv app_password SELFRUN_APP_DB_PASSWORD
\getenv kratos_password SELFRUN_KRATOS_DB_PASSWORD
-- CREATE DATABASE cannot run inside a transaction. Keep a session lock across
-- validation and creation, including concurrent or interrupted Compose runs.
SELECT pg_advisory_lock(hashtextextended('selfrun:database-initialize', 0));
DO $$
BEGIN
    IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'selfrun') <> 'selfrun' THEN
        RAISE EXCEPTION 'Existing selfrun database has an unexpected owner; refusing to alter it.';
    END IF;
    IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'kratos') <> 'kratos' THEN
        RAISE EXCEPTION 'Existing kratos database has an unexpected owner; refusing to alter it.';
    END IF;
    IF EXISTS (
        SELECT 1 FROM pg_roles
        WHERE rolname IN ('selfrun', 'kratos')
          AND (NOT rolcanlogin OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
    ) OR EXISTS (
        SELECT 1 FROM pg_auth_members
        WHERE member IN (SELECT oid FROM pg_roles WHERE rolname IN ('selfrun', 'kratos'))
    ) THEN
        RAISE EXCEPTION 'Existing service roles have unexpected privileges; refusing to alter them.';
    END IF;
END
$$;
SELECT format('CREATE ROLE selfrun LOGIN PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'selfrun') \gexec
SELECT format('CREATE ROLE kratos LOGIN PASSWORD %L', :'kratos_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'kratos') \gexec
SELECT 'CREATE DATABASE selfrun OWNER selfrun'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'selfrun') \gexec
SELECT 'CREATE DATABASE kratos OWNER kratos'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'kratos') \gexec
SQL
