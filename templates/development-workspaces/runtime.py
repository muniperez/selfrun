#!/usr/bin/env python3
"""Root-owned, local-only development VM control protocol. No T3 mutation API."""
import argparse
import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import socket
import sqlite3
import stat
import struct
import subprocess
import sys
import tarfile
import time
import uuid


class Blocked(Exception):
    def __init__(self, reason, state="Blocked"):
        super().__init__(reason)
        self.state = state


def require(condition, reason):
    if not condition:
        raise Blocked(reason)


def run(argv, **kwargs):
    try:
        timeout = kwargs.pop("timeout", 120)
        return subprocess.run(argv, check=True, text=True, capture_output=True,
                              timeout=timeout, **kwargs).stdout.strip()
    except (OSError, subprocess.SubprocessError) as error:
        # Never include subprocess output: Git/SSH/service errors can contain credentials.
        raise Blocked("Tool unavailable or failed: " + Path(argv[0]).name) from error


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    staging = path.with_suffix(".pending")
    fd = os.open(staging, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(value, stream, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(staging, path)
    fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def under(path, root):
    return Path(path).resolve().is_relative_to(Path(root).resolve())


def canonical_remote(value):
    # Deliberately exclude URL passwords, local paths, helpers, alternate protocols and scp syntax.
    require(isinstance(value, str) and re.fullmatch(
        r"(https://[a-zA-Z0-9.-]+(?::[0-9]+)?/[a-zA-Z0-9._/-]+|"
        r"ssh://git@[a-zA-Z0-9.-]+(?::[0-9]+)?/[a-zA-Z0-9._/-]+)", value),
        "Repository origin must be a canonical credential-free HTTPS or ssh://git@ URL")
    require("/../" not in value and not value.endswith("/.."), "Invalid repository origin")
    return value


class Git:
    def __init__(self, allow_local=False, verification_dir=None):
        self.allow_local = allow_local  # Only the injected test adapter enables this.
        self.verification_dir = verification_dir

    def command(self, repo, *args):
        env = dict(PATH="/usr/bin:/bin", HOME="/nonexistent", GIT_TERMINAL_PROMPT="0",
                   GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null", GIT_OPTIONAL_LOCKS="0",
                   GIT_NO_REPLACE_OBJECTS="1", GIT_GRAFT_FILE="/dev/null",
                   GIT_SSH_COMMAND="ssh -F /dev/null -oBatchMode=yes -oStrictHostKeyChecking=yes "
                   "-oUserKnownHostsFile=/etc/minicloud-git-known-hosts "
                   "-oIdentitiesOnly=yes -i /run/minicloud-git/key")
        privilege = {} if self.allow_local else dict(user=2000, group=2000, extra_groups=[])
        helper = []
        if not self.allow_local and Path("/run/minicloud-git/credential.json").exists():
            path = Path("/opt/minicloud-workspace/git-credential.py")
            info = path.stat()
            require(info.st_uid == 0 and not info.st_mode & 0o022 and path.is_file() and
                    not path.is_symlink(), "Selected HTTPS credential helper is not root-owned")
            helper = ["-c", "credential.helper=" + str(path)]
        # Disable hooks and external filters; never execute checkout-owned programs.
        return run(["git", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false",
                    "-c", "credential.helper=", *helper, "-c", "credential.useHttpPath=true",
                    "-c", "http.followRedirects=false", "-c", "core.gitProxy=",
                    "-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=" +
                    ("always" if self.allow_local else "never"), "-C", str(repo), *args],
                   env=env, **privilege)

    def prepare(self, registrations, workspace):
        require(registrations, "Ephemeral launch needs explicit repository registrations")
        for item in registrations:
            path = Path(item["path"])
            require(under(path, Path(workspace) / "repos") and
                    path.parent.resolve() == (Path(workspace) / "repos").resolve(),
                    "Initial clones must be direct children of /workspace/repos")
            canonical_remote(item["origin"]) if not self.allow_local else None
            require(re.fullmatch(r"[a-f0-9]{40}", item["baseSha"]), "Exact base SHA required")
            require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._/-]*", item["branch"]) and
                    ".." not in item["branch"] and item["branch"] not in
                    {"main", "master", *item.get("defaultBranches", [])},
                    "Unique non-default work branch required")
            if path.exists():
                require((path / ".git").exists(), "Partial clone requires explicit reconciliation")
                continue  # Resume bootstrap never destroys/re-clones existing root data.
            self.command(path.parent, "clone", "--no-checkout", "--origin", "origin", "--",
                         item["origin"], str(path))
            self.command(path, "checkout", "--no-track", "-b", item["branch"], item["baseSha"])

    def inspect(self, registrations, workspace):
        require(registrations, "No registered checkouts")
        discovered = set()
        # Detect unregistered nested checkouts, linked worktrees and bare Git directories.
        for root, dirs, files in os.walk(workspace, followlinks=False):
            if ".git" in dirs or ".git" in files:
                discovered.add(str(Path(root).resolve()))
                if ".git" in dirs:
                    dirs.remove(".git")
            require(not ("HEAD" in files and "objects" in dirs and "refs" in dirs),
                    "Unregistered bare repository")
        expected = {str(Path(item["path"]).resolve()) for item in registrations}
        require(discovered == expected, "Registered checkout inventory differs from filesystem")
        evidence = []
        for item in registrations:
            path = Path(item["path"]).resolve()
            require(under(path, workspace), "Checkout outside workspace")
            require(not any(p.is_symlink() for p in (path, *path.parents) if under(p, workspace)),
                    "Symlink checkout is unsupported")
            origin = item["origin"] if self.allow_local else canonical_remote(item["origin"])
            require(item.get("pushAuthorized") is True, "Explicit push authority is required")
            ref = item["remoteRef"]
            require(re.fullmatch(r"refs/heads/[a-zA-Z0-9][a-zA-Z0-9._/-]*", ref)
                    and ".." not in ref and not ref.endswith("/"), "Invalid exact remote ref")
            branch = item["branch"]
            require(ref == "refs/heads/" + branch and
                    branch not in {"main", "master", *item.get("defaultBranches", [])},
                    "Only the authorized non-default work branch is allowed")
            base = item["baseSha"]
            require(re.fullmatch(r"[a-f0-9]{40}", base), "An exact base SHA is required")
            require(self.command(path, "rev-parse", "--show-toplevel") == str(path),
                    "Checkout root changed")
            for name in ("--git-common-dir", "--git-dir"):
                require(under(self.command(path, "rev-parse", "--path-format=absolute", name), workspace),
                        "Git metadata outside workspace")
            require(self.command(path, "remote", "get-url", "--all", "origin") == origin,
                    "Repository origin changed")
            require(self.command(path, "symbolic-ref", "--short", "HEAD") == branch,
                    "Work branch changed")
            require(not self.has_config(path, r"^(filter\.|remote\..*\.pushurl$)"),
                    "External filters or alternate push URLs are unsupported")
            require(not (path / ".gitmodules").exists(), "Submodules require separate verified support")
            tracked = self.command(path, "ls-files", "--stage")
            require(not any(line.startswith("160000 ") for line in tracked.splitlines()),
                    "Submodules require separate verified support")
            # LFS is intentionally unsupported, including pointer files without attributes.
            for name in self.command(path, "ls-files", "-z").split("\0"):
                if not name:
                    continue
                file = path / name
                if file.is_file() and not file.is_symlink():
                    with file.open("rb") as stream:
                        prefix = stream.read(1024)
                    require(b"version https://git-lfs.github.com/spec/" not in prefix,
                            "Git LFS content is unsupported")
                if file.name == ".gitattributes" and file.is_file():
                    require(b"filter=lfs" not in file.read_bytes(), "Git LFS is unsupported")
            require(not self.command(path, "status", "--porcelain=v1", "--untracked-files=all"),
                    "Resolve tracked modifications and untracked files")
            head = self.command(path, "rev-parse", "HEAD")
            self.command(path, "merge-base", "--is-ancestor", base, head)
            worktrees = self.command(path, "worktree", "list", "--porcelain")
            for line in worktrees.splitlines():
                if line.startswith("worktree "):
                    require(str(Path(line[9:]).resolve()) in expected, "Unregistered Git worktree")
            require(not self.has_config(path, r"^remote\..*\.promisor$") and
                    self.command(path, "rev-parse", "--is-shallow-repository") == "false",
                    "Partial/shallow clones are unsupported")
            # ls-remote provides fresh exact ref evidence. Fetch does not update a local work ref.
            require(self.verification_dir is not None, "Independent Git verification directory required")
            verifier = Path(self.verification_dir) / uuid.uuid4().hex
            verifier.mkdir(mode=0o700, parents=True)
            try:
                if not self.allow_local:
                    os.chown(verifier, 2000, 2000)
                self.command(verifier, "init", "--bare", "--quiet")
                remote = self.command(verifier, "ls-remote", "--refs", origin, ref)
                default = self.command(verifier, "ls-remote", "--symref", origin, "HEAD")
                default_refs = [line.split()[1] for line in default.splitlines() if line.startswith("ref: ")]
                require(len(default_refs) == 1 and default_refs[0] != ref,
                        "Default-branch save or unavailable remote default identity is forbidden")
                parts = remote.split()
                require(len(parts) == 2 and parts[1] == ref, "Authorized remote ref unavailable")
                remote_sha = parts[0]
                self.command(verifier, "-c", "fetch.writeCommitGraph=false", "fetch", "--no-tags",
                             "--no-write-fetch-head", origin, ref)
                self.command(verifier, "merge-base", "--is-ancestor", head, remote_sha)
                require(self.command(verifier, "ls-remote", "--refs", origin, ref) == remote,
                        "Remote ref changed during verification; retry")
            finally:
                shutil.rmtree(verifier)
            evidence.append(dict(path=str(path), origin=origin, baseSha=base, branch=branch,
                                 headSha=head, remoteRef=ref, remoteSha=remote_sha))
        return evidence

    def has_config(self, path, expression):
        # git config exit 1 means missing, all other failures fail closed.
        result = subprocess.run(["git", "-C", str(path), "config", "--get-regexp", expression],
                                capture_output=True, text=True, timeout=30,
                                env=dict(PATH="/usr/bin:/bin", HOME="/nonexistent",
                                         GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_NOSYSTEM="1"),
                                **({} if self.allow_local else dict(user=2000, group=2000, extra_groups=[])))
        require(result.returncode in (0, 1), "Cannot inspect Git configuration")
        return result.returncode == 0


class Procfs:
    """Read-only process evidence; unsupported kernels and incoherent reads fail closed."""
    def __init__(self, root="/proc", machine=None):
        self.root = Path(root)
        self.machine = machine or os.uname().machine

    def process(self, pid):
        directory = self.root / str(pid)
        raw = (directory / "stat").read_text()
        fields = raw[raw.rfind(")") + 2:].split()
        require(len(fields) >= 20 and raw.startswith(str(pid) + " ("),
                "Unparseable process identity")
        status = {}
        for line in (directory / "status").read_text().splitlines():
            if ":" in line:
                key, value = line.split(":", 1)
                status[key] = value.strip()
        uids = tuple(map(int, status.get("Uid", "").split()))
        require(len(uids) == 4, "Process ownership unavailable")
        return dict(pid=pid, state=fields[0], ppid=int(fields[1]), pgrp=int(fields[2]),
                    session=int(fields[3]), tty=int(fields[4]), foreground=int(fields[5]),
                    start=int(fields[19]), uids=uids, threads=int(status.get("Threads", "0")),
                    cgroup=(directory / "cgroup").read_text().strip())

    def inventory(self):
        processes = {}
        try:
            for directory in self.root.iterdir():
                if directory.name.isdecimal():
                    try:
                        pid = int(directory.name)
                        process = self.process(pid)
                        require(self.process(pid)["start"] == process["start"], "PID changed during inventory")
                        processes[pid] = process
                    except FileNotFoundError:
                        continue
        except (OSError, ValueError) as error:
            raise Blocked("Cannot enumerate all live work") from error
        return processes

    def executable(self, pid):
        executable = self.root / str(pid) / "exe"
        path = os.readlink(executable)
        info = executable.stat()
        return dict(path=path, uid=info.st_uid, mode=info.st_mode)

    def tty(self, pid):
        directory = self.root / str(pid) / "fd"
        result = []
        for number in ("0", "2"):
            path = directory / number
            name = os.readlink(path)
            require(re.fullmatch(r"/dev/pts/[0-9]+", name), "Shell descriptor is not a PTY slave")
            info = path.stat()
            require(stat.S_ISCHR(info.st_mode), "Shell descriptor is not a character device")
            result.append((name, info.st_dev, info.st_ino, info.st_rdev))
        require(result[0] == result[1], "Shell stdin/stderr PTY identity differs")
        return result[0]

    def idle_shell(self, process, inventory, t3_pids):
        pid = process["pid"]
        directory = self.root / str(pid)
        try:
            require(process["uids"] == (2000,) * 4 and process["threads"] == 1,
                    "Idle terminal requires a single-threaded coding-owned shell")
            require(process["state"] == "S" and pid == process["session"] == process["pgrp"] and
                    process["tty"] != 0 and process["foreground"] == process["pgrp"],
                    "Shell is not sleeping in its foreground controlling terminal")
            parent, seen = process["ppid"], {pid}
            while parent not in t3_pids:
                require(parent in inventory and parent not in seen and
                        inventory[parent]["uids"] == (2000,) * 4, "Shell is not a T3 descendant")
                seen.add(parent)
                parent = inventory[parent]["ppid"]
            require(not any(item["ppid"] == pid for item in inventory.values()),
                    "Interactive shell has children or stopped/background jobs")
            require(not (directory / "task" / str(pid) / "children").read_text().strip(),
                    "Interactive shell has children or stopped/background jobs")
            executable = self.executable(pid)
            aliases = {"/usr/bin/bash": {"bash", "sh"}, "/usr/bin/dash": {"dash", "sh"}}
            require(executable["path"] in aliases and executable["uid"] == 0 and
                    stat.S_ISREG(executable["mode"]) and not executable["mode"] & 0o022,
                    "Shell executable is not an approved root-owned image binary")
            argv = (directory / "cmdline").read_bytes().split(b"\0")
            require(argv[-1] == b"", "Incomplete shell command line")
            argv = [arg.decode("utf-8", errors="strict") for arg in argv[:-1]]
            options = {"-i", "-l", "-il", "-li", "--interactive", "--login",
                       "--noprofile", "--norc", "--noediting", "--posix"}
            require(argv and Path(argv[0]).name.removeprefix("-") in aliases[executable["path"]] and
                    all(arg in options for arg in argv[1:]),
                    "Shell scripts, -c commands and unknown invocation flags remain active work")
            syscall = (directory / "syscall").read_text().strip()
            parts = syscall.split()
            read_number = {"x86_64": 0, "aarch64": 63}.get(self.machine)
            require(read_number is not None and len(parts) == 9,
                    "Native terminal adapter required: no proven blocking stdin syscall")
            channel = (directory / "wchan").read_text().strip()
            polling = self.machine == "x86_64" and int(parts[0], 0) == 270
            if polling:
                require(int(parts[1], 0) == 1 and int(parts[2], 0) > 0 and
                        all(int(parts[index], 0) == 0 for index in (3, 4, 5)),
                        "Native terminal adapter required: pselect is not an infinite stdin-only wait")
                require(self.stdin_fd_set(pid, int(parts[2], 0)) == 1 and
                        (channel == "do_select" or channel == "get_signal" and self.frozen(process)),
                        "Native terminal adapter required: no proven sleeping stdin-only pselect")
            else:
                require(int(parts[0], 0) == read_number and int(parts[1], 0) == 0 and int(parts[3], 0) > 0,
                        "Native terminal adapter required: no proven blocking read(0) syscall")
                require(channel in ("n_tty_read", "wait_woken"),
                        "Native terminal adapter required: no proven sleeping PTY read")
            tty = self.tty(pid)
            device = process["tty"] & 0xffffffff
            require((os.major(tty[3]), os.minor(tty[3])) ==
                    ((device >> 8) & 0xfff, (device & 0xff) | ((device >> 12) & 0xfff00)),
                    "PTY device/session/foreground group changed")
            require(self.process(pid) == process and self.executable(pid) == executable and
                    (directory / "syscall").read_text().strip() == syscall and self.tty(pid) == tty and
                    not (directory / "task" / str(pid) / "children").read_text().strip(),
                    "Terminal evidence changed during inspection")
            if polling:
                require(self.stdin_fd_set(pid, int(parts[2], 0)) == 1 and
                        (directory / "wchan").read_text().strip() == channel and
                        (channel != "get_signal" or self.frozen(process)),
                        "Terminal stdin wait evidence changed during inspection")
        except (OSError, ValueError, UnicodeError) as error:
            raise Blocked("Cannot prove idle terminal from kernel evidence") from error

    def stdin_fd_set(self, pid, address):
        with (self.root / str(pid) / "mem").open("rb", buffering=0) as memory:
            mask = os.pread(memory.fileno(), 8, address)
        require(len(mask) == 8, "Native terminal adapter required: unreadable stdin fd set")
        return int.from_bytes(mask, "little")

    def frozen(self, process):
        group = process["cgroup"]
        require(group.startswith("0::/minicloudwork.slice/") and
                ".." not in Path(group.removeprefix("0::")).parts,
                "Unknown terminal freezer cgroup")
        return "frozen 1" in (Path("/sys/fs/cgroup" + group.removeprefix("0::")) /
                              "cgroup.events").read_text().splitlines()


def t3_idle_database(path, expected_sequence):
    """Pinned v0.1.9 committed-state proof, never an invented T3 drain endpoint."""
    require(expected_sequence is None or type(expected_sequence) is int and expected_sequence >= 0,
            "Actual authenticated T3 snapshot sequence is required")
    path = Path(path)
    require(path.is_file() and not path.is_symlink(), "Pinned T3 state database is unavailable")
    terminal = {
        "runs": {"completed", "interrupted", "failed", "cancelled", "rolled_back"},
        "run_attempts": {"completed", "interrupted", "failed", "cancelled", "superseded"},
        "nodes": {"idle", "completed", "interrupted", "failed", "cancelled", "rolled_back"},
        "runtime_requests": {"resolved", "expired", "cancelled"},
        "provider_sessions": {"ready", "stopped", "error"},
        "provider_threads": {"not_loaded", "idle", "archived", "closed", "error"},
        "provider_turns": {"completed", "interrupted", "failed", "cancelled"},
        "turn_items": {"idle", "completed", "interrupted", "failed", "cancelled"},
        "subagents": {"idle", "completed", "interrupted", "failed", "cancelled"},
    }
    tables = {"orchestration_v2_projection_" + name: statuses for name, statuses in terminal.items()}
    tables["orchestration_v2_effect_outbox"] = {"succeeded", "failed", "cancelled"}
    connection = None
    try:
        connection = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True,
                                     isolation_level=None, timeout=1)
        connection.execute("PRAGMA query_only=ON")
        connection.execute("PRAGMA trusted_schema=OFF")
        connection.execute("BEGIN")
        required = {**{name: {"thread_id": "TEXT", "status": "TEXT"} for name in tables},
                    "orchestration_events": {"sequence": "INTEGER", "application_event_version": "INTEGER",
                                             "aggregate_kind": "TEXT"},
                    "orchestration_v2_projection_metadata": {"projection_name": "TEXT",
                                                              "schema_version": "INTEGER",
                                                              "last_sequence": "INTEGER"}}
        for name, columns in required.items():
            require(connection.execute("SELECT type FROM sqlite_master WHERE name=?", (name,)).fetchone()
                    == ("table",), "Unknown pinned T3 database schema")
            actual = {row[1]: row[2].upper() for row in connection.execute("PRAGMA table_info(" + name + ")")}
            require(all(actual.get(key) == value for key, value in columns.items()),
                    "Unknown pinned T3 database columns/types")
        thread_sequence, global_sequence = connection.execute(
            "SELECT COALESCE(MAX(CASE WHEN aggregate_kind='thread' THEN sequence END),0),"
            "COALESCE(MAX(sequence),0) FROM orchestration_events WHERE application_event_version=2"
        ).fetchone()
        require(expected_sequence is None or thread_sequence == expected_sequence,
                "T3 thread state changed after the HTTP idle snapshot")
        metadata = connection.execute(
            "SELECT schema_version,last_sequence FROM orchestration_v2_projection_metadata "
            "WHERE projection_name='thread-projections'").fetchall()
        empty_bootstrap = False
        if metadata == [(1, 0)] and global_sequence == 0:
            # The pinned Foundation migration seeds version 1 before any V2 event exists.
            projections = [name for name, in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table' "
                "AND name GLOB 'orchestration_v2_projection_*'") if
                name != "orchestration_v2_projection_metadata"]
            require(all(re.fullmatch(r"orchestration_v2_projection_[a-z_]+", name)
                        for name in projections), "Unknown pinned T3 projection table")
            empty_bootstrap = (
                connection.execute("SELECT COUNT(*) FROM orchestration_events").fetchone() == (0,) and
                connection.execute("SELECT COUNT(*) FROM orchestration_v2_effect_outbox").fetchone() == (0,) and
                all(connection.execute("SELECT COUNT(*) FROM " + name).fetchone() == (0,)
                    for name in projections))
        require(metadata == [(2, global_sequence)] or empty_bootstrap,
                "Pinned T3 thread projection is unknown or not caught up to the global V2 cursor")
        counts = {}
        for table, allowed in tables.items():
            count = 0
            for thread_id, status in connection.execute("SELECT thread_id,status FROM " + table):
                require(status in allowed, "T3 has active/pending work or an unknown status in " + table)
                count += 1
            counts[table] = count
        return dict(type="T3CommittedIdle", sourceCommit="24e77473ecf41bc1f7142529c7911c1c1aae1d76",
                    schemaVersion=metadata[0][0], emptyBootstrap=empty_bootstrap,
                    threadSequence=thread_sequence, globalV2Sequence=global_sequence,
                    projectionName="thread-projections", rowsChecked=counts)
    except sqlite3.Error as error:
        raise Blocked("Pinned T3 read-only state inspection failed; keep compute running") from error
    finally:
        if connection:
            connection.close()


class Linux:
    """Privileged adapter. Tests inject a fake; no default test can mount/kill anything."""
    def __init__(self, config, processes=None):
        self.config = config
        self.workspace = Path(config["workspace"])
        self.processes = processes or Procfs()

    def probe_volume(self, claim):
        device = Path(claim["device"])
        require(str(device).startswith("/dev/disk/by-id/"), "Stable provider device path required")
        require(device.name == claim["providerDeviceId"], "Provider volume/device identity mismatch")
        require(stat.S_ISBLK(device.stat().st_mode), "Expected block device unavailable")
        require(run(["blkid", "-s", "TYPE", "-o", "value", str(device)]) == "ext4",
                "Only existing ext4 is accepted; runtime never formats")
        require(run(["blkid", "-s", "UUID", "-o", "value", str(device)]) == claim["filesystemUuid"],
                "Filesystem UUID mismatch")
        return str(device.resolve())

    def mount(self, claim):
        device = self.probe_volume(claim)
        self.workspace.mkdir(mode=0o755, parents=True, exist_ok=True)
        result = subprocess.run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "SOURCE,FSTYPE"],
                                capture_output=True, text=True, timeout=30)
        require(result.returncode in (0, 1), "Cannot inspect mount")
        if result.returncode == 0:
            source, fs = result.stdout.split()
            require(str(Path(source).resolve()) == device and fs == "ext4", "Unexpected mount")
        else:
            require(not any(self.workspace.iterdir()), "Refuse local workspace fallback")
            run(["mount", "-t", "ext4", "-o", "nodev,nosuid", device, str(self.workspace)])
        marker = self.workspace / ".minicloud-volume.json"
        if not marker.exists() and claim.get("initializeOwner") is True:
            require(set(p.name for p in self.workspace.iterdir()) <= {"lost+found"} and
                    (not (self.workspace / "lost+found").exists() or
                     not any((self.workspace / "lost+found").iterdir())),
                    "Ownership initialization requires an empty, separately provisioned volume")
            atomic_json(marker, claim["owner"])
        self.verify_owner(claim)

    def verify_owner(self, claim):
        require(all(claim.get("owner", {}).get(key) for key in
                    ("organizationId", "workspaceId", "providerAccountId", "location", "providerVolumeId"))
                and claim["owner"]["providerVolumeId"] == claim["providerVolumeId"],
                "Incomplete or inconsistent volume ownership claim")
        marker = self.workspace / ".minicloud-volume.json"
        require(marker.is_file() and not marker.is_symlink(), "Missing volume ownership claim")
        info = marker.stat()
        require(info.st_uid == 0 and not info.st_mode & 0o022, "Unsafe volume ownership claim")
        require(json.loads(marker.read_text()) == claim["owner"], "Unexpected volume owner claim")
        require(run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "UUID"]) ==
                claim["filesystemUuid"], "Mount is not the expected filesystem")

    def ephemeral(self):
        require(not os.path.ismount(self.workspace), "Ephemeral cannot mount a primary volume")
        require(not any("volume" in p.name.lower() for p in Path("/dev/disk/by-id").iterdir())
                if Path("/dev/disk/by-id").exists() else True, "Ephemeral has attached volume")
        filesystems = run(["findmnt", "-rn", "-o", "FSTYPE"]).splitlines()
        require(not any(fs in ("nfs", "nfs4", "cifs", "9p", "virtiofs", "fuse.sshfs")
                        for fs in filesystems), "Ephemeral filesystem export side-channel")
        for forbidden in ("/mnt/primary", "/mnt/backups", "/var/run/docker.sock",
                          "/var/run/podman/podman.sock", *self.config.get("forbiddenPaths", [])):
            require(not Path(forbidden).exists(), "Ephemeral storage side-channel present")
        self.workspace.mkdir(mode=0o755, parents=True, exist_ok=True)
        require(not (self.workspace / ".minicloud-volume.json").exists(), "Primary data on ephemeral")

    def layout(self):
        require(self.workspace.is_dir() and not self.workspace.is_symlink(), "Workspace layout symlink")
        self.workspace.chmod(0o755)
        for relative in ("repos", "t3", "transcripts", "postgres", "postgres/16", "emulators", "emulators/exports",
                         "emulators/config", "artifacts", "config", "home"):
            directory = self.workspace / relative
            directory.mkdir(parents=True, exist_ok=True, mode=0o700)
            require(not directory.is_symlink(), "Workspace layout symlink")
            uid = self.config["uid"]
            gid = self.config["gid"]
            if relative.startswith("postgres"):
                import pwd
                uid = pwd.getpwnam("postgres").pw_uid
                gid = pwd.getpwnam("postgres").pw_gid
            os.chown(directory, uid, gid)
        postgres = self.workspace / "postgres/16"
        if not (postgres / "PG_VERSION").exists():
            require(not any(postgres.iterdir()), "Unknown PostgreSQL data format")
            run(["runuser", "-u", "postgres", "--", "/usr/lib/postgresql/16/bin/initdb",
                 "-D", str(postgres), "--auth-local=peer", "--auth-host=scram-sha-256",
                 "--encoding=UTF8", "--no-locale"])
        require((postgres / "PG_VERSION").read_text().strip() == "16",
                "PostgreSQL major mismatch; explicit migration required")
        # Dedicated subdirectories retain ignored/untracked files. Root home is disposable.

    def restore_ownership(self):
        import pwd
        postgres = pwd.getpwnam("postgres")
        for path in self.workspace.rglob("*"):
            if path.name in (".minicloud-volume.json", "lost+found"):
                continue
            is_postgres = under(path, self.workspace / "postgres")
            os.chown(path, postgres.pw_uid if is_postgres else self.config["uid"],
                     postgres.pw_gid if is_postgres else self.config["gid"], follow_symlinks=False)

    def launch(self, task_id, argv):
        unit = "minicloud-task-" + hashlib.sha256(task_id.encode()).hexdigest()[:24]
        run(["systemd-run", "--quiet", "--unit=" + unit,
             "--uid=" + str(self.config["uid"]), "--gid=" + str(self.config["gid"]),
             "--slice=minicloudwork.slice", "--working-directory=" + str(self.workspace),
             "--property=KillMode=control-group", "--property=NoNewPrivileges=yes",
             "--property=ProtectSystem=strict", "--property=ReadWritePaths=" + str(self.workspace),
             "--property=PrivateTmp=yes", "--setenv=HOME=" + str(self.workspace / "home"),
             "--", *argv])
        return unit

    def task_running(self, task):
        unit = task.get("unit")
        require(unit is not None, "Ambiguous task launch requires explicit reconciliation")
        result = subprocess.run(["systemctl", "show", unit, "-p", "ActiveState", "-p", "LoadState"],
                                capture_output=True, text=True, timeout=30)
        properties = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        require(result.returncode == 0 or properties.get("LoadState") == "not-found",
                "Unreachable task service manager")
        active = properties.get("ActiveState")
        require(active in ("active", "activating", "inactive", "failed", "deactivating"),
                "Unknown task service state")
        return active in ("active", "activating", "deactivating")

    def freeze(self):
        run(["systemctl", "freeze", "minicloudwork.slice"])
        require("frozen 1" in Path("/sys/fs/cgroup/minicloudwork.slice/cgroup.events").read_text(),
                "Work cgroup did not freeze")

    def thaw(self):
        run(["systemctl", "thaw", "minicloudwork.slice"])

    def writable(self):
        if self.config["kind"] == "primary":
            self.probe_volume(self.config["volume"])
            self.verify_owner(self.config["volume"])
        run(["mount", "-o", "remount,bind,rw" if self.config["kind"] == "ephemeral" else "remount,rw",
             str(self.workspace)])
        require("rw" in run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "OPTIONS"]).split(","),
                "Cannot restore writable workspace")

    def start_services(self):
        if self.config.get("startServices", True):
            previews = [unit for unit in self.infrastructure_units()
                        if unit != "minicloud-t3.service"]
            for unit in previews:
                self.verify_preview_unit(unit)
            run(["systemctl", "start", "minicloud-postgres.service", "minicloud-t3.service", *previews])

    def infrastructure_units(self):
        units = self.config.get("infrastructureUnits", ["minicloud-t3.service"])
        require(isinstance(units, list) and all(isinstance(unit, str) and
                (unit == "minicloud-t3.service" or
                 re.fullmatch(r"minicloud-preview-[a-z0-9-]{1,24}\.service", unit))
                for unit in units), "Unsupported infrastructure allowlist")
        require(len(set(units)) == len(units), "Duplicate infrastructure registration")
        stops = self.config.get("stopUnits", ["minicloud-t3.service", "minicloud-postgres.service"])
        require(isinstance(stops, list) and all(isinstance(unit, str) and
                (unit in ("minicloud-t3.service", "minicloud-postgres.service") or unit in units)
                for unit in stops), "Stop service is not explicitly registered")
        require(all(unit in stops for unit in units),
                "Registered infrastructure must have a designated graceful stop")
        return units

    @staticmethod
    def verify_unit_file(filename):
        require(isinstance(filename, str) and re.fullmatch(r"/[A-Za-z0-9_./@:-]+", filename),
                "Unknown preview unit file")
        path = Path(filename)
        resolved = path.resolve(strict=True)
        require(stat.S_ISREG(resolved.stat().st_mode), "Preview unit file is not regular")
        for component in {resolved, *resolved.parents, *path.parents}:
            info = component.stat()
            require(info.st_uid == 0 and not info.st_mode & 0o022,
                    "Preview unit must be root/operator-owned and not group/other writable")

    def verify_preview_unit(self, unit):
        require(unit in self.infrastructure_units() and unit != "minicloud-t3.service",
                "Preview unit is not explicitly registered")
        properties = run(["systemctl", "show", unit, "-p", "LoadState", "-p", "FragmentPath",
                          "-p", "DropInPaths", "-p", "KillMode", "-p", "SendSIGKILL", "-p", "Slice"])
        values = dict(line.split("=", 1) for line in properties.splitlines() if "=" in line)
        require(values.get("LoadState") == "loaded" and values.get("KillMode") == "process" and
                values.get("SendSIGKILL") == "no" and values.get("Slice") == "minicloudwork.slice",
                "Preview unit requires loaded owned slice, KillMode=process and SendSIGKILL=no")
        require(values.get("FragmentPath") and "DropInPaths" in values,
                "Preview unit needs known root/operator-owned unit files")
        for filename in [values["FragmentPath"], *values["DropInPaths"].split()]:
            self.verify_unit_file(filename)

    def verify_fence(self, unmounted=False):
        require("frozen 1" in Path("/sys/fs/cgroup/minicloudwork.slice/cgroup.events").read_text(),
                "Work cgroup is no longer frozen")
        result = subprocess.run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "OPTIONS"],
                                capture_output=True, text=True, timeout=30)
        if unmounted:
            require(result.returncode == 1, "Primary remounted after quiescence")
        else:
            require(result.returncode == 0 and "ro" in result.stdout.strip().split(","),
                    "Workspace no longer read-only")

    def idle(self, tasks):
        for task in tasks.values():
            require(not self.task_running(task), "Registered task still running; lease expiry is not completion")
        allowed, t3_pids, preview_pids, preview_groups = {}, set(), set(), set()
        for unit in self.infrastructure_units():
            if unit != "minicloud-t3.service":
                self.verify_preview_unit(unit)
                preview_groups.add("0::/minicloudwork.slice/" + unit)
            pid = run(["systemctl", "show", unit, "-p", "MainPID", "--value"])
            require(pid.isdecimal(), "Unreachable tool")
            if pid != "0":
                require(int(pid) not in allowed, "Conflicting infrastructure MainPID claims")
                allowed[int(pid)] = unit
                (t3_pids if unit == "minicloud-t3.service" else preview_pids).add(int(pid))
        inventory = self.processes.inventory()
        for pid, unit in allowed.items():
            require(pid in inventory and inventory[pid]["uids"] == (self.config["uid"],) * 4 and
                    inventory[pid]["cgroup"] == "0::/minicloudwork.slice/" + unit,
                    ("T3" if unit == "minicloud-t3.service" else "Preview") +
                    " service identity is unknown or outside its owned cgroup")
        def verify_preview_children(values):
            require(not any(p["ppid"] in preview_pids or
                            (p["pid"] not in preview_pids and any(
                                p["cgroup"] == group or p["cgroup"].startswith(group + "/")
                                for group in preview_groups)) for p in values.values()),
                    "Preview service has unregistered children or leftover cgroup processes")
        verify_preview_children(inventory)
        for process in inventory.values():
            if self.config["uid"] not in process["uids"]:
                continue
            require("/minicloudwork.slice/" in process["cgroup"],
                    "Coding process escaped supervised work slice")
            if process["pid"] not in allowed:
                require(self.config.get("observedTerminalInputs") is True,
                        "Unregistered live coding process; terminal input observation is not qualified")
                self.processes.idle_shell(process, inventory, t3_pids)
        after = self.processes.inventory()
        verify_preview_children(after)
        identity = lambda values: {pid: (p["start"], p["ppid"], p["pgrp"], p["session"], p["uids"], p["cgroup"])
                                   for pid, p in values.items() if self.config["uid"] in p["uids"]}
        require(identity(inventory) == identity(after), "Coding process inventory changed during inspection")
        for process in inventory.values():
            if process["pid"] not in allowed and self.config["uid"] in process["uids"]:
                require(not any(p["ppid"] == process["pid"] for p in after.values()),
                        "Shell acquired children during idle inspection")

    def verify_t3_snapshot(self, expected_sequence):
        path = self.workspace / "t3/userdata/statev2.sqlite"
        if self.config.get("startServices") is False and not path.exists():
            require(run(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]) == "0",
                    "Unexpected T3 process without a state snapshot")
            return None
        require(under(path, self.workspace), "T3 state database escapes the workspace")
        return t3_idle_database(path, expected_sequence)

    def stop_services(self):
        self.infrastructure_units()
        for unit in self.config.get("stopUnits", ["minicloud-t3.service", "minicloud-postgres.service"]):
            if unit not in ("minicloud-t3.service", "minicloud-postgres.service"):
                self.verify_preview_unit(unit)
        for argv in self.config.get("emulatorExportCommands", []):
            require(isinstance(argv, list) and argv and all(isinstance(a, str) for a in argv),
                    "Invalid emulator export command")
            run(argv)
        for unit in self.config.get("stopUnits", ["minicloud-t3.service", "minicloud-postgres.service"]):
            require(re.fullmatch(r"[A-Za-z0-9@_.-]+\.service", unit), "Invalid service unit")
            if unit != "minicloud-postgres.service":
                if unit != "minicloud-t3.service":
                    self.verify_preview_unit(unit)
                # Queue TERM while frozen, then allow the known infrastructure main process
                # to flush. Its unit never kills child tasks; the post-stop inventory blocks them.
                run(["systemctl", "stop", "--no-block", unit])
                try:
                    self.thaw()
                    run(["systemctl", "stop", unit])
                finally:
                    self.freeze()
            else:
                run(["systemctl", "stop", unit])
            state = run(["systemctl", "show", unit, "-p", "ActiveState", "--value"])
            require(state == "inactive", "Designated service did not stop cleanly")
        # pg_ctlcluster --foreground postgres service uses fast (checkpointed) stop, never immediate.
        if (self.workspace / "postgres/16/postmaster.pid").exists():
            raise Blocked("PostgreSQL still running; refuse quiescence")

    def readonly(self):
        if self.config["kind"] == "primary":
            expected = self.probe_volume(self.config["volume"])
            self.verify_owner(self.config["volume"])
            source = run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "SOURCE"])
            require(str(Path(source).resolve()) == expected, "Primary mounted device changed")
        os.sync()
        if self.config["kind"] == "ephemeral":
            # Separate bind mount prevents accidental continued writes through /workspace.
            if not os.path.ismount(self.workspace):
                run(["mount", "--bind", str(self.workspace), str(self.workspace)])
            run(["mount", "-o", "remount,bind,ro", str(self.workspace)])
        else:
            run(["mount", "-o", "remount,ro", str(self.workspace)])
        options = run(["findmnt", "-rn", "-M", str(self.workspace), "-o", "OPTIONS"]).split(",")
        require("ro" in options, "Workspace did not become read-only")

    def unmount(self):
        run(["umount", str(self.workspace)])
        result = subprocess.run(["findmnt", "-rn", "-M", str(self.workspace)],
                                capture_output=True, timeout=30)
        require(result.returncode == 1, "Primary filesystem is still mounted")


class Runtime:
    def __init__(self, config, state_dir, adapter=None, git=None, clock=time.time):
        self.c = config
        self.root = Path(state_dir)
        self.root.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.adapter = adapter or Linux(config)
        verification = Path("/var/cache/minicloud-workspace/verification")
        if git is None:
            for directory in (verification.parent, verification):
                directory.mkdir(mode=0o711, parents=True, exist_ok=True)
                require(directory.stat().st_uid == 0 and not directory.stat().st_mode & 0o022,
                        "Unsafe disposable Git verification cache")
                directory.chmod(0o711)
        # The verifier runs unprivileged and never writes to the frozen checkout.
        self.git = git or Git(verification_dir=verification)
        self.clock = clock
        allowed = {"vmId", "generation", "kind", "uid", "gid", "workspace", "volume", "backup",
                   "restore", "requireFreshBackup", "ignoredDataAcknowledged", "repositories",
                   "infrastructureUnits", "stopUnits", "emulatorExportCommands", "forbiddenPaths",
                   "startServices", "observedTerminalInputs"}
        require(not set(config) - allowed, "Unknown runtime configuration field; reject implicit credentials")
        require(config["kind"] in ("primary", "ephemeral"), "Invalid VM kind")
        require(config["uid"] == 2000 and config["gid"] == 2000, "Coding UID/GID must be 2000")
        require(config["generation"] > 0 and config["vmId"], "Invalid VM identity")
        require(Path(config["workspace"]).is_absolute(), "Absolute workspace path required")
        if config["kind"] == "ephemeral":
            require(not any(config.get(key) for key in
                            ("volume", "backup", "restore", "emulatorExportCommands")),
                    "Ephemeral has forbidden storage/backup/export configuration")
            require(config.get("ignoredDataAcknowledged") is True,
                    "Acknowledge loss of ignored files and all non-Git root data at launch")
        else:
            require(config.get("volume"), "Primary volume claim required")
            for policy in (config.get("backup", {}), config.get("restore", {})):
                require(not any(policy.get(key) for key in ("identityFile", "privateKey", "decryptionKey")),
                        "Recovery private keys must remain off the VM")
        self.state_file = self.root / "state.json"
        if self.state_file.exists():
            self.s = json.loads(self.state_file.read_text())
            require(self.s["identity"] == self.identity(), "State belongs to a different generation")
            require(self.s["configDigest"] == digest(config), "Runtime configuration changed")
        else:
            self.s = dict(identity=self.identity(), configDigest=digest(config), phase="New",
                          tasks={}, receipts={}, revision=0, activityAt=None)
            self.save()

    def identity(self):
        return {key: self.c[key] for key in ("vmId", "generation", "kind")}

    def save(self):
        self.s["revision"] += 1
        atomic_json(self.state_file, self.s)

    def receipt(self, receipt_type, operation, **extra):
        result = dict(schemaVersion=1, type=receipt_type, **self.identity(), operationId=operation,
                      revision=self.s["revision"] + 1, issuedAt=self.clock(), **extra)
        result["receiptId"] = digest(result)
        self.s["receipts"][operation] = result
        self.save()
        atomic_json(self.root / "receipts" / (result["receiptId"] + ".json"), result)
        return result

    def validate_request(self, request):
        require(request.get("vmId") == self.c["vmId"] and
                request.get("generation") == self.c["generation"], "Stale VM/generation")
        require(re.fullmatch(r"[A-Za-z0-9_-]{1,128}", request.get("operationId", "")),
                "Operation ID required")

    def handle(self, request):
        self.validate_request(request)
        action, operation = request["action"], request["operationId"]
        if action == "status":
            result = dict(schemaVersion=1, type="RuntimeStatus", **self.identity(),
                          phase=self.s["phase"], tasks=self.s["tasks"], revision=self.s["revision"],
                          activityAt=self.s["activityAt"], backup=self.backup_status(),
                          freezeCompleted=self.s.get("freezeCompleted", False),
                          filesystemReadOnly=self.s.get("filesystemReadOnly", False))
            if request.get("includeWorkInventory") is True:
                try:
                    self.adapter.idle(self.s["tasks"])
                    result["workInventory"] = dict(state="IdleCandidate", authoritative=False)
                except Blocked as error:
                    result["workInventory"] = dict(state="BusyOrUnknown", authoritative=False,
                                                   reason=str(error))
                except (OSError, ValueError, subprocess.SubprocessError):
                    result["workInventory"] = dict(state="BusyOrUnknown", authoritative=False,
                                                   reason="Cannot enumerate all live work")
            return result
        if action == "git-check":
            require(self.c["kind"] == "ephemeral" and self.s["phase"] == "Ready",
                    "Git preflight requires an admitting ephemeral runtime")
            try:
                evidence = self.git.inspect(self.c.get("repositories", []), self.c["workspace"])
            except Blocked as error:
                raise Blocked(str(error), "AwaitingGitSave") from error
            return dict(schemaVersion=1, type="GitSaveCandidate", **self.identity(),
                        operationId=operation, repositories=evidence, deletionEligible=False)
        if action == "bootstrap":
            require(self.s["phase"] in ("New", "Ready"), "Cannot bootstrap frozen runtime")
            if self.c["kind"] == "primary":
                self.adapter.mount(self.c["volume"])
            else:
                self.adapter.ephemeral()
            self.adapter.layout()
            if self.c["kind"] == "ephemeral":
                self.git.prepare(self.c.get("repositories", []), self.c["workspace"])
            self.s["phase"] = "Ready"
            self.save()
            return self.receipt("BootstrapReady", operation, toolchainReinstalled=False)
        if action == "task-start":
            require(self.s["phase"] == "Ready", "Admissions fenced")
            task_id = request["taskId"]
            require(re.fullmatch(r"[A-Za-z0-9_-]{1,128}", task_id), "Invalid task ID")
            require(task_id not in self.s["tasks"],
                    "Task ID already admitted; never replay ambiguous commands")
            argv = request.get("argv")
            require(isinstance(argv, list) and argv and all(isinstance(a, str) and "\0" not in a for a in argv),
                    "Task requires argv, not shell text")
            lease = request.get("leaseSeconds", 300)
            require(isinstance(lease, int) and 1 <= lease <= 3600, "Lease must be 1..3600 seconds")
            self.s["tasks"][task_id] = dict(state="LaunchIntent", expiresAt=self.clock() + lease)
            self.save()  # Persist before side effects: an ambiguous launch cannot be replayed.
            unit = self.adapter.launch(task_id, argv)
            self.s["tasks"][task_id].update(state="Running", unit=unit)
            self.s["activityAt"] = self.clock()
            self.save()
            return dict(type="TaskAdmitted", taskId=task_id, unit=unit, **self.identity())
        if action in ("task-renew", "task-complete"):
            require(self.s["phase"] == "Ready", "Admissions and task mutations fenced")
            task = self.s["tasks"].get(request.get("taskId"))
            require(task is not None, "Unknown task")
            if action == "task-complete":
                require(not self.adapter.task_running(task), "Task still running")
                task["state"] = "Completed"
            else:
                lease = request.get("leaseSeconds", 300)
                require(isinstance(lease, int) and 1 <= lease <= 3600, "Invalid lease")
                require(task["state"] == "Running", "Task not renewable")
                task["expiresAt"] = self.clock() + lease
            self.save()
            return dict(type="TaskUpdated", **self.identity())
        if action == "activity":
            require(self.s["phase"] == "Ready", "Runtime fenced")
            require(request.get("source") in ("user-input", "task-progress"),
                    "Keepalive, passive sockets, probes and logs are not useful activity")
            if request["source"] == "task-progress":
                task = self.s["tasks"].get(request.get("taskId"))
                require(task and task["state"] == "Running" and self.adapter.task_running(task),
                        "Useful task progress requires a registered live task")
            self.s["activityAt"] = self.clock()
            self.save()
            return dict(type="UsefulActivity", **self.identity())
        if action == "cancel-drain":
            require(self.s["phase"] == "Fenced" and self.s.get("fenceOperation") == operation,
                    "Only an unfrozen pending drain can be cancelled")
            self.adapter.thaw()
            self.s["phase"] = "Ready"
            self.s.pop("fenceOperation", None)
            self.save()
            return dict(type="DrainCancelled", **self.identity())
        if action == "resume":
            require(self.s["phase"] == "Frozen" and self.s.get("fenceOperation") == operation and
                    request.get("deletionNotCommitted") is True,
                    "Only an explicitly cancelled, not-delete-committed frozen VM can resume")
            self.s["phase"] = "Resuming"
            self.s["receipts"] = {}
            self.s.pop("fenceOperation", None)
            self.save()  # Revoke all deletion receipts durably before allowing a single write.
            if self.s.get("filesystemReadOnly", True):
                self.adapter.writable()
            self.adapter.thaw()
            self.adapter.start_services()
            self.s["phase"] = "Ready"
            self.s["filesystemReadOnly"] = False
            self.s["freezeCompleted"] = False
            self.s.pop("frozenT3State", None)
            self.save()
            return self.receipt("RuntimeResumed", operation)
        if action in ("quiesce", "git-save", "discard"):
            require(request.get("gatewayAdmissionsFenced") is True and
                    request.get("unobservableToolsAbsent") is True,
                    "Fence gateway/in-flight mutations and verify no unobservable tools before shutdown")
            if self.c["kind"] == "ephemeral":
                require(action in ("git-save", "discard"), "Ephemeral requires Git save or explicit discard")
            else:
                require(action == "quiesce", "Primary requires retained-volume quiescence")
            if self.s.get("fenceOperation") not in (None, operation):
                raise Blocked("Another operation owns the freeze; stale operation")
            existing = self.s["receipts"].get(operation)
            if existing and existing["type"] in ("GitSaved", "DiscardAuthorized", "PrimaryQuiesced"):
                require(existing["type"] == {"git-save": "GitSaved", "discard": "DiscardAuthorized",
                                             "quiesce": "PrimaryQuiesced"}[action],
                        "Operation ID already has another receipt type")
                self.handle(dict(request, action="receipt-validate", receiptId=existing["receiptId"]))
                return existing
            sequence = request.get("t3SnapshotSequence")
            require(type(sequence) is int and sequence >= 0, "Authenticated T3 snapshot sequence required")
            if self.s.get("freezeCompleted"):
                require(sequence == self.s["freezeHttpSequence"], "Stale freeze snapshot sequence")
                self.adapter.idle(self.s["tasks"])
                self.adapter.verify_fence()
                previous = self.s["frozenT3State"]
                expected = previous["threadSequence"] if previous else None
                require(self.adapter.verify_t3_snapshot(expected) == previous,
                        "Frozen T3 state changed after quiescence")
            else:
                self.s["fenceOperation"] = operation
                self.s["phase"] = "Fenced"
                self.save()
                # Check both before and after the kernel freeze to close the task-start race.
                self.adapter.idle(self.s["tasks"])
                self.adapter.freeze()
                self.s.update(phase="Frozen", filesystemReadOnly=False, freezeHttpSequence=sequence)
                self.save()
                self.adapter.idle(self.s["tasks"])
                self.adapter.verify_t3_snapshot(sequence)
                self.adapter.stop_services()
                self.adapter.idle(self.s["tasks"])
                final_t3 = self.adapter.verify_t3_snapshot(None)
                self.s["filesystemReadOnly"] = True
                self.save()
                self.adapter.readonly()
                self.adapter.verify_fence()
                self.s.update(freezeCompleted=True, frozenT3State=final_t3)
                self.save()
            if action == "git-save":
                try:
                    evidence = self.git.inspect(self.c.get("repositories", []), self.c["workspace"])
                    # A second full inspection catches mutations during remote evidence collection.
                    require(evidence == self.git.inspect(self.c["repositories"], self.c["workspace"]),
                            "Checkout changed during verification")
                except Blocked as error:
                    raise Blocked(str(error), "AwaitingGitSave") from error
                return self.receipt("GitSaved", operation, repositories=evidence,
                                    ignoredDataAcknowledged=True, ignoredDataPreserved=False,
                                    t3State=self.s["frozenT3State"],
                                    t3SnapshotSequence=self.s["freezeHttpSequence"])
            if action == "discard":
                require(request.get("acknowledgeLostIgnoredAndRootData") is True and
                        request.get("authorizedDiscard") is True,
                        "Explicit authorized discard and ignored/root-data acknowledgement required")
                return self.receipt("DiscardAuthorized", operation, ignoredDataPreserved=False,
                                    t3State=self.s["frozenT3State"],
                                    t3SnapshotSequence=self.s["freezeHttpSequence"])
            backup = None
            if request.get("backupRequired") or self.c.get("requireFreshBackup"):
                backup = self.backup(operation)
                require(backup["type"] == "PrimaryBackup",
                        "Encrypted backup awaits durable off-host acknowledgement; export then backup-ack")
            self.adapter.unmount()
            self.s["phase"] = "Quiesced"
            self.save()
            return self.receipt("PrimaryQuiesced", operation, filesystemUuid=
                                self.c["volume"]["filesystemUuid"], unmounted=True, backup=backup,
                                t3State=self.s["frozenT3State"],
                                t3SnapshotSequence=self.s["freezeHttpSequence"])
        if action == "receipt-validate":
            receipt = self.s["receipts"].get(operation)
            require(receipt and receipt["type"] in ("GitSaved", "DiscardAuthorized", "PrimaryQuiesced")
                    and receipt["receiptId"] == request.get("receiptId"), "Stale receipt")
            require(self.s.get("fenceOperation") == operation, "Freeze operation changed")
            self.adapter.idle(self.s["tasks"])
            self.adapter.verify_fence(unmounted=receipt["type"] == "PrimaryQuiesced")
            if self.c["kind"] == "ephemeral" and receipt["type"] == "GitSaved":
                require(self.git.inspect(self.c["repositories"], self.c["workspace"]) ==
                        receipt["repositories"], "Checkout or remote changed since save receipt")
            return dict(type="ReceiptValid", receiptId=receipt["receiptId"], **self.identity(),
                        operationId=operation)
        if action == "backup":
            require(self.s["phase"] == "Frozen", "Backup requires a frozen, stopped, read-only primary")
            return self.backup(operation)
        if action == "backup-ack":
            require(self.c["kind"] == "primary" and self.s["phase"] == "Frozen",
                    "Only a frozen primary accepts an off-host backup acknowledgement")
            prepared = self.s.get("preparedBackup")
            require(prepared and prepared["operationId"] == operation and
                    request.get("preparedReceiptId") == prepared["receiptId"] and
                    request.get("ciphertextSha256") == prepared["ciphertextSha256"],
                    "Stale backup acknowledgement or ciphertext mismatch")
            require(request.get("durablyStoredOffHost") is True and
                    request.get("destinationIdentity") == prepared["destinationIdentity"] and
                    request.get("independentRecoveryKeyConfirmed") is True,
                    "Authenticated off-host durability and independent recovery key confirmation required")
            result = self.receipt("PrimaryBackup", operation, **{
                key: value for key, value in prepared.items() if key not in {
                    "schemaVersion", "type", "vmId", "generation", "kind", "operationId", "revision",
                    "issuedAt", "receiptId", "freshness", "verifiedAt"}},
                freshness="VerifiedOffHost", verifiedAt=self.clock())
            self.s["lastBackup"] = result
            self.s.pop("preparedBackup", None)
            self.save()
            (self.root / prepared["objectId"]).unlink(missing_ok=True)
            return result
        if action == "restore":
            require(self.c["kind"] == "primary" and self.s["phase"] == "New",
                    "Restore only into a new, separate primary target")
            return self.restore(request)
        raise Blocked("Unknown action")

    def backup_status(self):
        if self.c["kind"] == "ephemeral":
            return dict(state="Forbidden")
        last = self.s.get("lastBackup")
        result = dict(state="NotConfigured" if not self.c.get("backup") else "NeverBackedUp")
        if last:
            maximum_age = self.c.get("backup", {}).get("maxAgeSeconds", 86400)
            result.update(state="Fresh" if self.clock() - last["verifiedAt"] <= maximum_age
                          and self.clock() <= last["retentionUntil"] else "Stale",
                          verifiedAt=last["verifiedAt"], receiptId=last["receiptId"],
                          retentionUntil=last["retentionUntil"])
        if self.s.get("preparedBackup"):
            result["state"] = "AwaitingOffHostAcknowledgement"
        elif self.s.get("backupFreshness") == "Failed":
            result["state"] = "Failed"
        return result

    def backup(self, operation):
        require(self.c["kind"] == "primary", "No ephemeral backup capability")
        require(self.s.get("freezeCompleted"), "Backup requires completed service/data quiescence")
        policy = self.c.get("backup")
        require(policy and policy.get("recipient") and policy.get("keyRecoveryId"),
                "Independent age recipient and recoverable external key reference required")
        require(policy.get("retentionDays", 0) > 0, "Explicit retention policy required")
        last = self.s.get("lastBackup")
        if last and last["operationId"] == operation and last["freshness"] in (
                "VerifiedOffHost", "VerifiedUpload"):
            return last
        prepared = self.s.get("preparedBackup")
        if prepared:
            require(prepared["operationId"] == operation, "Another backup awaits off-host acknowledgement")
            return prepared
        streaming = policy.get("transport") == "management-ssh-stream"
        require(policy.get("destinationIdentity") not in (None, "", self.c["vmId"]),
                "Independent off-host destination required")
        transport = None if streaming else SshArchive(policy, self.root, self.identity())
        source = Path(self.c["workspace"])
        validate_backup_tree(source)
        hashes = representative_hashes(source)
        require(hashes, "No representative files available for backup verification")
        object_id = str(uuid.uuid4()) + ".tar.age"
        encrypted = self.root / object_id
        try:
            # Only encrypted bytes leave the VM. Never put private encryption keys in the VM image.
            encrypt_archive(source, encrypted, policy["recipient"])
            sha = file_hash(encrypted)
            if transport:
                transport.upload(encrypted, object_id, sha)
            now = self.clock()
            receipt = self.receipt("PrimaryBackupPrepared" if streaming else "PrimaryBackup",
                                  operation, objectId=object_id,
                                  ciphertextSha256=sha, representativeHashes=hashes,
                                  destinationIdentity=policy["destinationIdentity"],
                                  sourceVolumeId=self.c["volume"]["providerVolumeId"],
                                  sourceFilesystemUuid=self.c["volume"]["filesystemUuid"],
                                  volumeOwner=self.c["volume"]["owner"],
                                  keyRecoveryId=policy["keyRecoveryId"],
                                  retentionUntil=now + policy["retentionDays"] * 86400,
                                  freshness="AwaitingOffHostAcknowledgement" if streaming else "VerifiedUpload",
                                  verifiedAt=None if streaming else now,
                                  providerVolumeEncryptionClaim=False)
            self.s["preparedBackup" if streaming else "lastBackup"] = receipt
            self.s.pop("backupFreshness", None)
            self.save()
            return receipt
        except Blocked:
            self.s["backupFreshness"] = "Failed"
            self.save()
            raise
        finally:
            if not self.s.get("preparedBackup"):
                encrypted.unlink(missing_ok=True)

    def restore(self, request):
        source = request.get("backupReceipt", {})
        require(request.get("authorizedRestore") is True, "Explicit restore authorization required")
        require(source.get("type") == "PrimaryBackup" and source.get("kind") == "primary",
                "A primary backup receipt is required")
        target = self.c["volume"]
        require(source.get("sourceVolumeId") != target["providerVolumeId"],
                "Restore target must be a separate volume")
        require(source.get("sourceFilesystemUuid") != target["filesystemUuid"],
                "Restore target must have its own filesystem UUID")
        require({k: v for k, v in source.get("volumeOwner", {}).items() if k != "providerVolumeId"} ==
                {k: v for k, v in target["owner"].items() if k != "providerVolumeId"},
                "Backup source and restore target ownership differ")
        require(source.get("receiptId") == digest({k: v for k, v in source.items() if k != "receiptId"}),
                "Backup receipt corrupt")
        require(source.get("freshness") in ("VerifiedOffHost", "VerifiedUpload") and
                source.get("representativeHashes"), "A verified off-host backup with hashes is required")
        require(request.get("independentRecoveryKeyConfirmed") is True,
                "Restore decryption must occur off VM with the independent recovery key")
        self.adapter.mount(target)
        workspace = Path(self.c["workspace"])
        require(set(p.name for p in workspace.iterdir()) <= {".minicloud-volume.json", "lost+found"},
                "Restore target is not empty")
        lost = workspace / "lost+found"
        require(not lost.exists() or not any(lost.iterdir()), "Restore target lost+found is not empty")
        self.s["restoreSource"] = source
        self.s["restoreOperation"] = request["operationId"]
        self.s["phase"] = "RestoreReady"
        self.save()
        return self.receipt("RestoreImportReady", request["operationId"],
                            sourceBackupReceiptId=source["receiptId"],
                            targetVolumeId=target["providerVolumeId"])

    def restore_import(self, operation, stream):
        require(self.c["kind"] == "primary" and self.s["phase"] == "RestoreReady" and
                self.s["restoreOperation"] == operation, "No authenticated restore admission")
        source = self.s["restoreSource"]
        target = self.c["volume"]
        workspace = Path(self.c["workspace"])
        self.s["phase"] = "Restoring"
        self.save()  # A partial restore is never automatically replayed over nonempty data.
        try:
            with tarfile.open(fileobj=stream, mode="r|") as archive:
                for member in archive:
                    require(member.name.startswith("data/") or member.name == "data",
                            "Unexpected archive member")
                    member.name = member.name.removeprefix("data").lstrip("/")
                    if not member.name or member.name == ".minicloud-volume.json":
                        continue
                    require(member.name != "lost+found", "Archive contains filesystem metadata")
                    if member.islnk():
                        require(member.linkname.startswith("data/"), "Unexpected hardlink target")
                        member.linkname = member.linkname.removeprefix("data/")
                        require(member.linkname not in (".minicloud-volume.json", "lost+found"),
                                "Archive hardlink targets volume ownership metadata")
                    archive.extract(member, workspace, filter="data")
            for relative, expected in source["representativeHashes"].items():
                file = workspace / relative
                require(under(file, workspace) and not file.is_symlink() and
                        file_hash(file) == expected, "Restored representative file hash mismatch")
            self.adapter.restore_ownership()
            self.adapter.layout()
            os.sync()
            self.s["phase"] = "New"
            self.save()
            return self.receipt("PrimaryRestored", operation,
                                sourceBackupReceiptId=source["receiptId"],
                                targetVolumeId=target["providerVolumeId"],
                                representativeHashesVerified=True)
        except Exception:
            self.s["phase"] = "RestoreFailed"
            self.save()
            raise

    def export_backup(self, receipt_id, stream):
        require(self.c["kind"] == "primary" and self.s["phase"] == "Frozen",
                "Backup export only available on a frozen primary")
        receipt = self.s.get("preparedBackup")
        require(receipt and receipt["receiptId"] == receipt_id, "Stale backup export receipt")
        self.adapter.verify_fence()
        encrypted = self.root / receipt["objectId"]
        require(file_hash(encrypted) == receipt["ciphertextSha256"], "Prepared backup corrupted")
        with encrypted.open("rb") as source:
            shutil.copyfileobj(source, stream)


def file_hash(path):
    result = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def encrypt_archive(source, output, recipient):
    process = None
    try:
        process = subprocess.Popen(["age", "--encrypt", "--recipient", recipient,
                                    "--output", str(output)], stdin=subprocess.PIPE,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        with tarfile.open(fileobj=process.stdin, mode="w|") as archive:
            archive.add(source, arcname="data", recursive=True)
        process.stdin.close()
        require(process.wait(timeout=600) == 0, "Age encryption failed")
    except (OSError, subprocess.SubprocessError) as error:
        raise Blocked("Age encryption unavailable or failed") from error
    finally:
        if process and process.poll() is None:
            process.kill()  # Only the exact encryption helper, never a user task.
            process.wait()


def validate_backup_tree(source):
    for path in Path(source).rglob("*"):
        mode = path.lstat().st_mode
        require(stat.S_ISREG(mode) or stat.S_ISDIR(mode) or stat.S_ISLNK(mode),
                "Backup contains unsupported live socket/device/FIFO")
        if path.is_symlink():
            require(under(path, source), "Backup symlink escapes the durable workspace")


def representative_hashes(root):
    files = [p for p in sorted(Path(root).rglob("*"))
             if p.is_file() and not p.is_symlink() and under(p, root)
             and p.name != ".minicloud-volume.json"]
    first_per_category = {}
    for path in files:
        first_per_category.setdefault(path.relative_to(root).parts[0], path)
    representatives = list(dict.fromkeys([*first_per_category.values(), *files]))[:64]
    return {str(p.relative_to(root)): file_hash(p) for p in representatives}


class SshArchive:
    """Concrete pinned-SSH off-host ciphertext transport; no operator endpoints in source."""
    def __init__(self, policy, state_dir, identity):
        self.policy = policy
        require(policy.get("destinationIdentity") not in
                (None, "", identity["vmId"]), "Independent off-host destination required")
        host = policy.get("host", "")
        require(re.fullmatch(r"[a-zA-Z0-9.-]+", host), "Invalid backup host")
        try:
            addresses = {entry[4][0] for entry in socket.getaddrinfo(host, 22)}
            local = {"127.0.0.1", "::1", *socket.gethostbyname_ex(socket.gethostname())[2]}
        except OSError as error:
            raise Blocked("Cannot resolve independent backup host") from error
        require(addresses and not addresses & local and
                all(not ipaddress.ip_address(a).is_loopback and not ipaddress.ip_address(a).is_unspecified
                    for a in addresses), "Backup destination must be off-host")
        require(re.fullmatch(r"[a-z_][a-z0-9_-]*", policy.get("user", "")), "Invalid SSH backup user")
        require(re.fullmatch(r"/[A-Za-z0-9_./-]+", policy.get("directory", "")) and
                ".." not in policy["directory"], "Absolute backup directory required")
        for name in ("knownHostsFile", "sshIdentityFile"):
            path = Path(policy.get(name, ""))
            require(path.is_file() and path.stat().st_uid == 0 and not path.stat().st_mode & 0o022,
                    "Root-owned pinned SSH files required")
        self.ssh = ["ssh", "-oBatchMode=yes", "-oStrictHostKeyChecking=yes",
                    "-oIdentitiesOnly=yes", "-oUserKnownHostsFile=" + policy["knownHostsFile"],
                    "-i", policy["sshIdentityFile"], policy["user"] + "@" + host]

    def remote_path(self, object_id):
        require(re.fullmatch(r"[a-f0-9-]{36}\.tar\.age", object_id), "Invalid backup object ID")
        return self.policy["directory"] + "/" + object_id

    def upload(self, path, object_id, sha):
        import shlex
        remote = self.remote_path(object_id)
        # Exclusive create, fsync, and server-side checksum. Remote account owns retention policy.
        program = ("import os,sys,hashlib; p=sys.argv[1]; "
                   "f=open(p,'xb'); h=hashlib.sha256(); "
                   "exec('for b in iter(lambda:sys.stdin.buffer.read(1048576),b\\\"\\\"):\\n"
                   " f.write(b); h.update(b)'); f.flush(); os.fsync(f.fileno()); f.close(); "
                   "d=os.open(os.path.dirname(p),os.O_DIRECTORY); os.fsync(d); os.close(d); "
                   "print(h.hexdigest())")
        command = "umask 077; python3 -c " + shlex.quote(program) + " " + shlex.quote(remote)
        with Path(path).open("rb") as stream:
            result = run([*self.ssh, command], stdin=stream)
        require(result == sha, "Off-host encrypted upload checksum mismatch")

    def download(self, object_id, path):
        import shlex
        command = "cat -- " + shlex.quote(self.remote_path(object_id))
        try:
            with Path(path).open("xb") as stream:
                subprocess.run([*self.ssh, command], stdout=stream, stderr=subprocess.DEVNULL,
                               check=True, timeout=600)
        except (OSError, subprocess.SubprocessError) as error:
            raise Blocked("Cannot retrieve encrypted off-host backup") from error


def load_config(path):
    path = Path(path)
    info = path.stat()
    require(info.st_uid == 0 and not info.st_mode & 0o077 and not path.is_symlink(),
            "Configuration must be a root-owned 0600 regular file")
    require(path.is_file(), "Invalid configuration")
    return json.loads(path.read_text())


def serve(config, state_dir, socket_path):
    require(os.geteuid() == 0, "Agent requires root")
    sock = Path(socket_path)
    require(not sock.exists(), "Control socket already exists; investigate stale agent")
    sock.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with socket.socket(socket.AF_UNIX) as server:
        server.bind(str(sock))
        os.chmod(sock, 0o600)
        server.listen(8)
        try:
            while True:
                connection, _ = server.accept()
                with connection:
                    connection.settimeout(10)
                    _, uid, _ = struct.unpack("3i", connection.getsockopt(socket.SOL_SOCKET,
                                                                         socket.SO_PEERCRED, 12))
                    if uid != 0:
                        continue
                    try:
                        data = b""
                        while not data.endswith(b"\n"):
                            part = connection.recv(65536)
                            require(part and len(data) + len(part) <= 262144, "Invalid request size")
                            data += part
                        with (Path(state_dir) / "control.lock").open("a") as lock:
                            fcntl.flock(lock, fcntl.LOCK_EX)
                            runtime = Runtime(config, state_dir)
                            response = dict(ok=True, result=runtime.handle(json.loads(data)))
                    except Blocked as error:
                        response = dict(ok=False, state=error.state, reason=str(error))
                    except Exception:
                        response = dict(ok=False, state="Blocked", reason="Invalid request or adapter failure")
                    connection.sendall(json.dumps(response, sort_keys=True).encode() + b"\n")
        finally:
            sock.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("serve", "call", "export-backup", "restore-import"))
    parser.add_argument("--config", default="/etc/minicloud-workspace/runtime.json")
    parser.add_argument("--state-dir", default="/var/lib/minicloud-workspace")
    parser.add_argument("--socket", default="/run/minicloud-workspace/control.sock")
    parser.add_argument("--receipt-id")
    parser.add_argument("--operation-id")
    args = parser.parse_args()
    os.umask(0o077)
    if args.mode == "serve":
        Path(args.state_dir).mkdir(mode=0o700, parents=True, exist_ok=True)
        serve(load_config(args.config), args.state_dir, args.socket)
    elif args.mode == "call":
        require(os.geteuid() == 0, "Control client requires root (use pinned restricted SSH)")
        data = sys.stdin.buffer.readline(262145)
        require(data.endswith(b"\n") and len(data) <= 262144, "One bounded JSON request line required")
        with socket.socket(socket.AF_UNIX) as client:
            client.settimeout(600)
            client.connect(args.socket)
            client.sendall(data)
            result = b""
            while not result.endswith(b"\n"):
                part = client.recv(65536)
                require(part and len(result) + len(part) <= 1048576, "Invalid response")
                result += part
            sys.stdout.buffer.write(result)
    else:
        require(os.geteuid() == 0, "Archive transport requires authenticated root management SSH")
        with (Path(args.state_dir) / "control.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            runtime = Runtime(load_config(args.config), args.state_dir)
            if args.mode == "export-backup":
                runtime.export_backup(args.receipt_id, sys.stdout.buffer)
            else:
                result = runtime.restore_import(args.operation_id, sys.stdin.buffer)
                print(json.dumps(dict(ok=True, result=result), sort_keys=True))


if __name__ == "__main__":
    main()
