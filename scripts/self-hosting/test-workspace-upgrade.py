#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only
"""Rehearse the workspace extraction against a private, synthetic PostgreSQL cluster."""

from difflib import unified_diff
import http.client
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tarfile
import tempfile
import time


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
MANIFEST = json.loads((HERE / "workspace-upgrade.json").read_text())
API_PROJECT = Path("modules/api/Selfrun.Api.csproj")
HISTORY = 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId"'


def run(*args, **kwargs):
    return subprocess.run(args, check=True, text=True, timeout=300, **kwargs)


def loopback_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def build_api(source, output):
    run("dotnet", "build", source / API_PROJECT, "-c", "Release", "-o", output, "--nologo", "-v", "quiet")
    return output / "Selfrun.Api.dll"


def postgres16_bin():
    bin_dir = Path(os.environ.get("PG_BIN") or run("pg_config", "--bindir", capture_output=True).stdout.strip())
    for executable in ("postgres", "initdb", "pg_ctl", "psql", "pg_dump"):
        if not (bin_dir / executable).is_file() or not os.access(bin_dir / executable, os.X_OK):
            raise SystemExit(f"Missing executable {bin_dir / executable}. Install PostgreSQL 16 and set PG_BIN.")
    version = run(bin_dir / "postgres", "--version", capture_output=True).stdout.strip()
    if not version.startswith("postgres (PostgreSQL) 16."):
        raise SystemExit(f"Expected PostgreSQL 16 in {bin_dir}, got {version}. Set PG_BIN to its versioned bin directory.")
    print(f"Rehearsal server: {version}", flush=True)
    return bin_dir


class Postgres:
    def __init__(self, path, bin_dir):
        self.path = path
        self.bin = bin_dir
        self.port = loopback_port()
        self.password = secrets.token_hex(24)
        self.pg_env = {
            key: value for key, value in os.environ.items() if not key.startswith("PG")
        } | {"PGPASSWORD": self.password}

    def __enter__(self):
        with tempfile.NamedTemporaryFile(mode="w", dir=self.path) as password_file:
            password_file.write(self.password + "\n")
            password_file.flush()
            run(self.bin / "initdb", "-D", self.path / "pgdata", "-U", "synthetic",
                "--auth=scram-sha-256", f"--pwfile={password_file.name}", "--no-locale", "--encoding=UTF8",
                stdout=subprocess.DEVNULL, env=self.pg_env)
        run(self.bin / "pg_ctl", "-D", self.path / "pgdata", "-l", self.path / "postgres.log",
            "-o", f"-c listen_addresses=127.0.0.1 -c unix_socket_directories= -p {self.port}", "-w", "start",
            env=self.pg_env)
        return self

    def __exit__(self, *exc):
        run(self.bin / "pg_ctl", "-D", self.path / "pgdata", "-m", "fast", "-w", "stop", env=self.pg_env)

    def command(self, tool, database, *args):
        return run(self.bin / tool, "-h", "127.0.0.1", "-p", str(self.port), "-U", "synthetic",
                   "-d", database, *args, capture_output=True, env=self.pg_env).stdout

    def sql(self, database, query):
        return self.command("psql", database, "-XAt", "-v", "ON_ERROR_STOP=1", "-c", query).strip()

    def snapshot(self, database):
        dump = self.command("pg_dump", database, "--no-owner", "--no-privileges")
        return [line for line in dump.splitlines() if not line.startswith(("\\restrict ", "\\unrestrict "))]

    def catalog(self, database):
        tables = set(self.sql(database, """SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'""").splitlines())
        columns = {tuple(row.split("|")) for row in self.sql(database, """SELECT table_name, column_name
            FROM information_schema.columns WHERE table_schema = 'public'""").splitlines()}
        return tables, columns

    def without_additions(self, database, catalog, added_migrations):
        # Target migrations newer than the installed release may only add tables or columns.
        # Remove exactly those additions on a clone so everything pre-existing must still match.
        tables, columns = catalog
        clone = f"{database}_preexisting"
        self.sql("postgres", f"DROP DATABASE IF EXISTS {clone}")
        self.sql("postgres", f"CREATE DATABASE {clone} TEMPLATE {database}")
        now_tables, now_columns = self.catalog(clone)
        for table in sorted(now_tables - tables):
            self.sql(clone, f'DROP TABLE public."{table}" CASCADE')
        for table, column in sorted(c for c in now_columns - columns if c[0] in tables):
            self.sql(clone, f'ALTER TABLE public."{table}" DROP COLUMN "{column}"')
        ids = ", ".join(f"'{migration}'" for migration in added_migrations)
        self.sql(clone, f'DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" IN ({ids})')
        return clone

    def assert_preserved(self, database, before, message, catalog=None, added_migrations=()):
        if added_migrations:
            database = self.without_additions(database, catalog, added_migrations)
        after = self.snapshot(database)
        if after != before:
            diff = "\n".join(unified_diff(before, after, f"{database}/before.sql", f"{database}/after.sql",
                                          lineterm="")) + "\n"
            evidence = self.path.parent / "workspace-upgrade-diffs"
            evidence.mkdir(exist_ok=True)
            path = evidence / f"{self.path.name}-{database}.diff"
            path.write_text(diff, encoding="utf-8")
            print(diff, end="", flush=True)
            raise AssertionError(f"{message}; synthetic dump diff: {path}")

    def api_env(self, database):
        return {
            key: os.environ[key] for key in ("PATH", "DOTNET_ROOT", "LD_LIBRARY_PATH") if key in os.environ
        } | {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "Database__Provider": "Postgres",
            "Database__RunMigrationsOnStartup": "false",
            "ConnectionStrings__Selfrun":
                f"Host=127.0.0.1;Port={self.port};Database={database};Username=synthetic;Password={self.password}",
        }

    def migrate(self, api, database, command, error=None):
        argv = ["dotnet", api]
        env = self.api_env(database)
        if command is None:
            env |= {"Database__RunMigrationsOnStartup": "true", "ASPNETCORE_URLS": "http://127.0.0.1:0"}
        else:
            argv.append(command)
        result = subprocess.run(argv, cwd=api.parent, env=env, capture_output=True, text=True,
                                timeout=15 if error else 120)
        if error is None:
            assert result.returncode == 0, result.stdout + result.stderr
        else:
            assert result.returncode != 0 and error in result.stderr, result.stdout + result.stderr

    def check_api_on_clone(self, api, source):
        # Startup writes a maintenance participant row. Exercise that write without
        # letting it race the migration-only comparison on the source fixture.
        database = f"{source}_api"
        self.sql("postgres", f"CREATE DATABASE {database} TEMPLATE {source}")
        port = loopback_port()
        with tempfile.TemporaryFile(mode="w+", dir=self.path) as log:
            with subprocess.Popen(["dotnet", api], cwd=api.parent, stdout=log, stderr=log,
                                  env=self.api_env(database) | {"ASPNETCORE_URLS": f"http://127.0.0.1:{port}"}) as process:
                try:
                    deadline = time.monotonic() + 30
                    while process.poll() is None and time.monotonic() < deadline:
                        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=1)
                        try:
                            connection.request("GET", "/health")
                            if connection.getresponse().status == 200 and self.sql(database, """
                                SELECT EXISTS (SELECT 1 FROM maintenance_participants WHERE role = 'api')
                                """) == "t":
                                return
                        except OSError:
                            pass
                        finally:
                            connection.close()
                        time.sleep(0.1)
                    log.seek(0)
                    raise AssertionError(log.read())
                finally:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=10)
                        except subprocess.TimeoutExpired:
                            process.kill()


def rehearse(pg, legacy_api, target_api):
    target = MANIFEST["target"]
    command = target["migrationCommand"]
    latest = json.loads((ROOT / target["releaseDescriptor"]).read_text())["applicationMigration"]

    for db in ("upgraded", "fresh"):
        pg.sql("postgres", f"CREATE DATABASE {db}")

    pg.migrate(legacy_api, "upgraded", command)
    pg.command("psql", "upgraded", "-X", "-v", "ON_ERROR_STOP=1", "-f", HERE / "workspace-upgrade-fixture.sql")
    legacy_history = pg.sql("upgraded", HISTORY).splitlines()
    before = pg.snapshot("upgraded")
    catalog = pg.catalog("upgraded")

    pg.migrate(target_api, "upgraded", command)
    upgraded_history = pg.sql("upgraded", HISTORY).splitlines()
    added = [migration for migration in upgraded_history if migration not in legacy_history]
    assert upgraded_history == legacy_history + added and all(m > legacy_history[-1] for m in added), \
        f"Upgrade must only append migrations newer than the installed release: {added}"
    assert not added or added[-1] == latest, f"{target['migrationService']} did not apply {latest}"
    pg.assert_preserved("upgraded", before, "Upgrade changed schema, rows, sequences or migration history",
                        catalog, added)
    pg.check_api_on_clone(target_api, "upgraded")
    pg.migrate(target_api, "upgraded", command)
    pg.assert_preserved("upgraded", before, "Upgrade replay changed schema, rows, sequences or migration history",
                        catalog, added)
    print("PASS: retired-workspace upgrade, API health and registration on a clone, replay and full database preservation")

    pg.migrate(target_api, "fresh", command)
    before = pg.snapshot("fresh")
    pg.check_api_on_clone(target_api, "fresh")
    pg.migrate(target_api, "fresh", command)
    pg.assert_preserved("fresh", before, "Fresh replay changed schema, rows, sequences or migration history")
    fresh_history = pg.sql("fresh", HISTORY).splitlines()
    assert fresh_history[-1] == latest, f"{target['migrationService']} did not apply {latest}"
    assert pg.sql("fresh", """
        SELECT EXISTS (SELECT 1 FROM pg_class WHERE relname ~ '^development_workspace')
            OR EXISTS (SELECT 1 FROM pg_proc WHERE proname ~ '^development_workspace')
        """) == "f", "Fresh target created legacy workspace objects"
    assert len(set(legacy_history) - set(fresh_history)) == 7
    print(f"PASS: fresh app-only migration to {latest}, API health and registration on a clone, and replay")

    # Guard probes intentionally corrupt only the disposable fixture, never an existing installation.
    pg.sql("postgres", "CREATE DATABASE active TEMPLATE upgraded")
    pg.sql("active", "UPDATE development_workspaces SET volume_status = 'ready'")
    pg.sql("postgres", "CREATE DATABASE partial TEMPLATE upgraded")
    pg.sql("partial", """DELETE FROM "__EFMigrationsHistory"
        WHERE "MigrationId" = '20261120134056_DevelopmentWorkspaceVmResourceHistory'""")
    pg.sql("postgres", "CREATE DATABASE unrelated")
    pg.sql("unrelated", "CREATE TABLE foreign_installation (id integer)")
    for db in ("fresh", "upgraded"):
        pg.sql(db, """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('99991231000000_UnknownFutureMigration', '99.0.0')""")
    for db, error in (
        ("active", "workspaces that have not been retired"),
        ("partial", "incomplete workspace migration history"),
        ("unrelated", "unknown populated state"),
        ("fresh", "99991231000000_UnknownFutureMigration"),
        ("upgraded", "99991231000000_UnknownFutureMigration"),
    ):
        before = pg.snapshot(db)
        pg.migrate(target_api, db, command, error=error)
        if db == "upgraded":
            pg.migrate(target_api, db, None, error=error)
        pg.assert_preserved(db, before, f"Rejected database {db} was changed")
        print(f"PASS: rejected {db} guard probe without mutation ({error})")


def main():
    pg_bin = postgres16_bin()
    installed = MANIFEST["installed"]
    revision = installed["sourceRevision"]
    if subprocess.run(["git", "cat-file", "-e", f"{revision}^{{commit}}"], cwd=ROOT, capture_output=True).returncode:
        run("git", "fetch", "--no-tags", "origin", f"refs/tags/{installed['tag']}", cwd=ROOT)
    print(f"Installed source: {installed['tag']} @ {revision}", flush=True)
    print(f"Target checkout HEAD: {run('git', 'rev-parse', 'HEAD', cwd=ROOT, capture_output=True).stdout.strip()}",
          flush=True)
    artifacts = ROOT / ".local/artifacts"
    artifacts.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="workspace-upgrade-", dir=artifacts) as directory:
        path = Path(directory)
        archive = path / "legacy.tar"
        run("git", "archive", "-o", archive, revision, cwd=ROOT)
        with tarfile.open(archive) as source:
            source.extractall(path / "legacy", filter="data")
        legacy_api = build_api(path / "legacy", path / "legacy-api")
        target_api = build_api(ROOT, path / "target-api")
        with Postgres(path, pg_bin) as pg:
            rehearse(pg, legacy_api, target_api)


if __name__ == "__main__":
    main()
