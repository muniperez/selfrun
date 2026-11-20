#!/usr/bin/env python3
"""minicloud-scratch-mounts: bind-mount git-ignored build outputs onto local scratch.

Runs as root over trees the coding user controls. Every path is opened component by
component relative to a verified directory descriptor with O_NOFOLLOW, mounts and unmounts
target descriptors or a pinned parent directory, and nothing outside the fixed allowlist of
git-ignored output directory names is ever moved, mounted or deleted. Standard library only.

Commands: apply, watch, suspend, resume, unmount-all, status. See README.md.
"""
import argparse
import ctypes
import errno
import fcntl
import hashlib
import json
import os
import re
import select
import signal
import stat
import struct
import subprocess
import sys
import time
import uuid

WORKSPACE = "/workspace"
SCRATCH = "/var/lib/minicloud-scratch"
RUN_DIR = "/run/minicloud-scratch-mounts"
TRASH = ".minicloud-scratch-trash"
CODING_UID = CODING_GID = 2000
ALLOWLIST = frozenset({
    "node_modules", "bin", "obj", "dist", "build", "out", "target", ".next", ".nuxt", ".svelte-kit",
    ".turbo", ".parcel-cache", ".angular", ".gradle", ".venv", "venv", "__pycache__", "coverage",
    ".pytest_cache", ".mypy_cache"})
SKIP_SCAN = frozenset({".git", ".hg", ".svn", ".jj", "vendor"})
MANIFEST_EVENTS = re.compile(r"package\.json|.*\.(cs|fs|vb)proj|.*\.sln|Cargo\.toml|pom\.xml|"
                             r"pyproject\.toml|setup\.py|requirements.*\.txt|(build|settings)\.gradle(\.kts)?|"
                             r"angular\.json|(next|nuxt|svelte)\.config\..*|turbo\.json|\.git|\.gitignore")
REPO_ID = re.compile(r"[A-Za-z0-9_-][A-Za-z0-9._-]{0,47}-[0-9a-f]{16}")
TOKEN = re.compile(r"[re]-[0-9a-f]{32}")
MAX_DEPTH = 12
MAX_DIRS = 100_000
MAX_ROUNDS = 6
MAX_MOUNTS = 512
MAX_TREE_DEPTH = 256
MAX_TREE_ENTRIES = 2_000_000
MAX_REPORTED = 50
QUIET_SECONDS = 60
SCRATCH_MARGIN = 1 << 30
PRESSURE_PERCENT = 85
PRESSURE_HYSTERESIS = 5
GIT_SECONDS = 60
LOCK_SECONDS = 60
DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
PATH_DIR = os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
MS_NOSUID, MS_NODEV, MS_REMOUNT, MS_BIND = 2, 4, 32, 4096
MNT_DETACH, UMOUNT_NOFOLLOW = 2, 8
IN_MOVED_FROM, IN_MOVED_TO, IN_CREATE, IN_DELETE = 0x40, 0x80, 0x100, 0x200
IN_DELETE_SELF, IN_MOVE_SELF, IN_Q_OVERFLOW, IN_IGNORED = 0x400, 0x800, 0x4000, 0x8000
IN_ONLYDIR, IN_ISDIR, IN_NONBLOCK, IN_CLOEXEC = 0x01000000, 0x40000000, 0o4000, 0o2000000
WATCH_MASK = (IN_CREATE | IN_DELETE | IN_MOVED_FROM | IN_MOVED_TO | IN_DELETE_SELF | IN_MOVE_SELF |
              IN_ONLYDIR)
MAX_WATCHES = 8192


class Unsafe(Exception):
    """A path changed, is a symbolic link, crosses a mount or has an unexpected owner."""
    def __init__(self, code):
        super().__init__(code)
        self.code = code


class Deferred(Exception):
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def valid_component(name):
    if not isinstance(name, str) or name in ("", ".", "..") or "/" in name or "\0" in name:
        return False
    try:
        return len(os.fsencode(name)) <= 255
    except UnicodeError:
        return False


def valid_relative(components):
    """A repository-relative path under scratch: plain components, never Git metadata or staging."""
    return (isinstance(components, (list, tuple)) and 0 < len(components) <= 64 and
            all(valid_component(name) and name != ".git" for name in components) and
            not components[0].startswith(".staging-"))


def repo_id(components, inode):
    """Stable for one checkout, distinct for another checkout at the same path."""
    base = re.sub(r"[^A-Za-z0-9._-]", "_", components[-1])[:48].lstrip(".") or "repo"
    digest = hashlib.sha256(os.fsencode("/".join(components)) + b"\0" + str(inode).encode())
    return base + "-" + digest.hexdigest()[:16]


def expected_outputs(files, inherited=frozenset(), repo_root=False):
    """Output directories the detected project manifests in one directory will create."""
    names = set()
    if "package.json" in files:
        names.add("node_modules")
    if any(name.endswith((".csproj", ".fsproj", ".vbproj")) for name in files):
        names.update(("bin", "obj"))
    if "Cargo.toml" in files and "cargo" not in inherited:
        names.add("target")  # A Cargo workspace shares the outermost target directory.
    if "pom.xml" in files:
        names.add("target")
    if "pyproject.toml" in files or "setup.py" in files or \
            repo_root and any(re.fullmatch(r"requirements.*\.txt", name) for name in files):
        names.add(".venv")
    gradle = any(re.fullmatch(r"build\.gradle(\.kts)?", name) for name in files)
    if gradle:
        names.add("build")
    if (gradle or any(re.fullmatch(r"settings\.gradle(\.kts)?", name) for name in files)) and \
            "gradle" not in inherited:
        names.add(".gradle")
    for manifest, output in (("angular.json", ".angular"), ("turbo.json", ".turbo")):
        if manifest in files:
            names.add(output)
    for prefix, output in (("next.config.", ".next"), ("nuxt.config.", ".nuxt"),
                           ("svelte.config.", ".svelte-kit")):
        if any(name.startswith(prefix) for name in files):
            names.add(output)
    return names


def manifest_flags(files, inherited):
    flags = set(inherited)
    if "Cargo.toml" in files:
        flags.add("cargo")
    if any(re.fullmatch(r"(build|settings)\.gradle(\.kts)?", name) for name in files):
        flags.add("gradle")
    return frozenset(flags)


def unescape_mountinfo(field):
    return os.fsdecode(re.sub(rb"\\([0-7]{3})", lambda match: bytes([int(match.group(1), 8)]), field))


def parse_mountinfo(data):
    entries = []
    for line in data.splitlines():
        fields = line.split(b" ")
        try:
            separator = fields.index(b"-", 6)
            entries.append(dict(id=int(fields[0]), parent=int(fields[1]), device=fields[2].decode(),
                                root=unescape_mountinfo(fields[3]),
                                mountPoint=unescape_mountinfo(fields[4]),
                                fstype=fields[separator + 1].decode(errors="replace")))
        except (ValueError, IndexError):
            continue
    return entries


def containing_mount(entries, path):
    best = None
    for entry in entries:
        point = entry["mountPoint"]
        if path == point or point == "/" or path.startswith(point + "/"):
            if best is None or len(point) >= len(best["mountPoint"]):
                best = entry
    return best


def fs_relative(entries, path):
    """The path relative to its filesystem root, as mountinfo reports bind mount roots."""
    entry = containing_mount(entries, path)
    if entry is None:
        return None
    remainder = path[len(entry["mountPoint"]):] if entry["mountPoint"] != "/" else path
    joined = entry["root"].rstrip("/") + "/" + remainder.strip("/")
    return joined.rstrip("/") or "/"


def under(path, prefix):
    return path == prefix or path.startswith(prefix.rstrip("/") + "/")


class System:
    """Kernel interface. Tests substitute mounts, ownership, devices and /proc views."""
    def __init__(self):
        self._libc = None

    def libc(self):
        if self._libc is None:
            libc = ctypes.CDLL(None, use_errno=True)
            libc.mount.argtypes = (ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_ulong,
                                   ctypes.c_void_p)
            libc.umount2.argtypes = (ctypes.c_char_p, ctypes.c_int)
            libc.inotify_init1.argtypes = (ctypes.c_int,)
            libc.inotify_add_watch.argtypes = (ctypes.c_int, ctypes.c_char_p, ctypes.c_uint32)
            self._libc = libc
        return self._libc

    def _check(self, result):
        if result < 0:
            code = ctypes.get_errno()
            raise OSError(code, os.strerror(code))
        return result

    def dev(self, info):
        return info.st_dev

    def uid(self, info):
        return info.st_uid

    def trusted_dir(self, info):
        """Only root may rename or replace anything along the path (sticky dirs excepted)."""
        return info.st_uid == 0 and not (info.st_mode & 0o022 and not info.st_mode & stat.S_ISVTX)

    def fchown(self, fd, uid, gid):
        os.fchown(fd, uid, gid)

    def lchown(self, dir_fd, name, uid, gid):
        os.chown(name, uid, gid, dir_fd=dir_fd, follow_symlinks=False)

    def mnt_id(self, fd):
        with open("/proc/self/fdinfo/%d" % fd) as stream:
            for line in stream:
                if line.startswith("mnt_id:"):
                    return int(line.split()[1])
        raise OSError(errno.ENOTSUP, "mnt_id unavailable")

    def mountinfo(self):
        with open("/proc/self/mountinfo", "rb") as stream:
            return parse_mountinfo(stream.read())

    def bind(self, source_fd, target_fd):
        # Magic links bind exactly the verified directories, never a re-resolved path.
        self._check(self.libc().mount(b"/proc/self/fd/%d" % source_fd, b"/proc/self/fd/%d" % target_fd,
                                      None, MS_BIND, None))

    def harden(self, fd):
        self._check(self.libc().mount(None, b"/proc/self/fd/%d" % fd, None,
                                      MS_BIND | MS_REMOUNT | MS_NOSUID | MS_NODEV, None))

    def umount(self, parent_fd, name, detach=False):
        # A mount point cannot be renamed while mounted, so resolving one final component
        # (no symlink) from the pinned parent directory targets exactly the verified mount.
        os.fchdir(parent_fd)
        try:
            self._check(self.libc().umount2(os.fsencode(name),
                                            UMOUNT_NOFOLLOW | (MNT_DETACH if detach else 0)))
        finally:
            os.chdir("/")

    def statvfs(self, fd):
        return os.fstatvfs(fd)

    def processes(self):
        """Paths every process uses (cwd, root, open files, mappings); fails closed if unreadable."""
        paths, unreadable, own = set(), 0, os.getpid()
        for name in os.listdir("/proc"):
            if not name.isdecimal() or int(name) == own:
                continue
            base = "/proc/" + name
            try:
                for link in ("cwd", "root"):
                    paths.add(os.readlink(base + "/" + link))
                for fd in os.listdir(base + "/fd"):
                    try:
                        paths.add(os.readlink(base + "/fd/" + fd))
                    except FileNotFoundError:
                        continue
                with open(base + "/maps", "rb") as stream:
                    for line in stream:
                        parts = line.rstrip(b"\n").split(None, 5)
                        if len(parts) == 6 and parts[5].startswith(b"/"):
                            paths.add(os.fsdecode(parts[5]).removesuffix(" (deleted)"))
            except (FileNotFoundError, ProcessLookupError):
                continue
            except OSError:
                unreadable += 1
        return {path.removesuffix(" (deleted)") for path in paths}, unreadable

    def check_ignore(self, repo_fd, repo_path, paths, uid, gid):
        """Ignored subset of repository-relative directory paths, or None when Git fails."""
        env = dict(PATH="/usr/bin:/bin", HOME="/nonexistent", LC_ALL="C", GIT_TERMINAL_PROMPT="0",
                   GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null", GIT_OPTIONAL_LOCKS="0",
                   GIT_ATTR_NOSYSTEM="1", GIT_NO_REPLACE_OBJECTS="1",
                   GIT_CEILING_DIRECTORIES=os.path.dirname(repo_path))
        privilege = dict(user=uid, group=gid, extra_groups=[]) if os.geteuid() == 0 else {}
        # Trailing slashes ask about directories, including ones that do not exist yet. Hooks,
        # fsmonitor and system/global configuration never run; Git runs as the coding user.
        base = ["git", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false",
                "-c", "core.untrackedCache=false", "-C", "/proc/self/fd/%d" % repo_fd]

        def git(arguments, data=None, **extra):
            try:
                result = subprocess.run(base + arguments, input=data, capture_output=True,
                                        env=dict(env, **extra),
                                        timeout=GIT_SECONDS, pass_fds=(repo_fd,), cwd="/", **privilege)
            except (OSError, subprocess.SubprocessError):
                return None
            return result

        result = git(["check-ignore", "--stdin", "-z"],
                     b"".join(os.fsencode(path) + b"/\0" for path in paths))
        if result is None or result.returncode not in (0, 1):
            return None
        ignored = {os.fsdecode(item).rstrip("/") for item in result.stdout.split(b"\0") if item}
        if not ignored:
            return ignored
        # Defense in depth: a directory holding any tracked file is never treated as ignored.
        tracked = git(["ls-files", "-z", "--cached", "--"] + sorted(ignored), GIT_LITERAL_PATHSPECS="1")
        if tracked is None or tracked.returncode != 0:
            return None
        for item in tracked.stdout.split(b"\0"):
            name = os.fsdecode(item)
            ignored = {path for path in ignored if not under(name, path)}
        return ignored


class Repo:
    def __init__(self, components, inode):
        self.components = components
        self.inode = inode
        self.id = repo_id(components, inode)
        self.dirs = set()
        self.candidates = {}
        self.expected = set()
        self.recreate = set()
        self.ignored = {}
        self.git_failed = False


class Candidate:
    def __init__(self, info, inherited, mounted):
        self.info = info
        self.inherited = inherited
        self.mounted = mounted
        self.walked = False


class Helper:
    def __init__(self, workspace=WORKSPACE, scratch=SCRATCH, run_dir=RUN_DIR, uid=CODING_UID,
                 gid=CODING_GID, system=None, clock=time.time, monotonic=time.monotonic,
                 stop=lambda: False, log=None, pressure=PRESSURE_PERCENT, quiet=QUIET_SECONDS):
        self.workspace = os.path.abspath(workspace)
        self.repos_path = os.path.join(self.workspace, "repos")
        self.scratch = os.path.abspath(scratch)
        self.scratch_repos = os.path.join(self.scratch, "repos")
        self.run_dir = os.path.abspath(run_dir)
        self.uid, self.gid = uid, gid
        self.sys = system or System()
        self.clock, self.monotonic, self.stop = clock, monotonic, stop
        self.log = log or (lambda event, **fields: print(json.dumps(dict(event=event, **fields)),
                                                         file=sys.stderr, flush=True))
        self.pressure = pressure
        self.quiet = quiet
        self.deadline = None
        self.on_dir = None

    # ----- run-state: lock, suspension and status (tmpfs, cleared on reboot) -----

    def ensure_run_dir(self):
        try:
            os.mkdir(self.run_dir, 0o700)
        except FileExistsError:
            pass
        info = os.lstat(self.run_dir)
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise Unsafe("run_dir")
        return self.run_dir

    def suspended(self):
        return os.path.lexists(os.path.join(self.run_dir, "suspended"))

    def set_suspended(self, value):
        path = os.path.join(self.ensure_run_dir(), "suspended")
        if value:
            os.close(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600))
        else:
            try:
                os.unlink(path)
            except FileNotFoundError:
                pass

    def lock(self, seconds=LOCK_SECONDS):
        fd = os.open(os.path.join(self.ensure_run_dir(), "lock"),
                     os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        deadline = self.monotonic() + seconds
        while True:
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                return fd
            except BlockingIOError:
                if self.monotonic() >= deadline or self.stop():
                    os.close(fd)
                    raise Deferred("locked")
                time.sleep(0.2)

    def write_status(self, status):
        path = os.path.join(self.ensure_run_dir(), "status.json")
        staging = path + ".pending"
        fd = os.open(staging, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
        with os.fdopen(fd, "w") as stream:
            json.dump(status, stream, sort_keys=True)
        os.replace(staging, path)

    # ----- descriptor helpers -----

    def open_dir(self, name, dir_fd, flags=DIRECTORY, dev=None):
        try:
            fd = os.open(name, flags, dir_fd=dir_fd)
        except OSError as error:
            if error.errno in (errno.ELOOP, errno.ENOTDIR):
                raise Unsafe("symlink_or_not_directory") from error
            raise
        if dev is not None and self.sys.dev(os.fstat(fd)) != dev:
            os.close(fd)
            raise Unsafe("separate_mount")
        return fd

    def open_path(self, root_fd, components, dev=None, flags=DIRECTORY):
        fd = os.dup(root_fd)
        try:
            for name in components:
                if not valid_component(name):
                    raise Unsafe("invalid_component")
                child = self.open_dir(name, fd, flags, dev)
                os.close(fd)
                fd = child
            return fd
        except BaseException:
            os.close(fd)
            raise

    def is_mount_point(self, parent_fd, child_fd):
        return self.sys.mnt_id(child_fd) != self.sys.mnt_id(parent_fd)

    def repo_path(self, components):
        return os.path.join(self.repos_path, *components) if components else self.repos_path

    def checkpoint(self):
        if self.stop():
            raise Deferred("stopping")
        if self.deadline is not None and self.monotonic() > self.deadline:
            raise Deferred("budget")

    # ----- scratch root -----

    def root_dir(self, parent, name, mode):
        """Opens (creating if needed) a root-owned directory, tightening group/other write bits."""
        created = False
        try:
            os.mkdir(name, mode, dir_fd=parent)
            created = True
        except FileExistsError:
            pass
        fd = self.open_dir(name, parent)
        try:
            if created:
                self.sys.fchown(fd, 0, 0)
            info = os.fstat(fd)
            if self.sys.uid(info) != 0:
                raise Unsafe("scratch_owner")
            if info.st_mode & ~mode & 0o777:
                os.fchmod(fd, info.st_mode & mode & 0o7777)
            return fd
        except BaseException:
            os.close(fd)
            raise

    def ensure_scratch(self, bind=True):
        """Root-owned scratch roots without symlinks; repos becomes a nosuid,nodev self-bind."""
        parts = self.scratch.strip("/").split("/")
        fd = os.open("/", PATH_DIR)
        try:
            for name in [None, *parts[:-1]]:
                if name is not None:
                    child = self.open_dir(name, fd, PATH_DIR)
                    os.close(fd)
                    fd = child
                if not self.sys.trusted_dir(os.fstat(fd)):
                    raise Unsafe("scratch_ancestor")
            base = self.root_dir(fd, parts[-1], 0o755)
        finally:
            os.close(fd)
        try:
            self.scratch_dev = self.sys.dev(os.fstat(base))
            repos = self.root_dir(base, "repos", 0o700)
            try:
                if bind and self.scratch_dev != getattr(self, "ws_dev", None) and \
                        not self.is_mount_point(base, repos):
                    self.sys.bind(repos, repos)
                    os.close(repos)
                    repos = -1
                    repos = self.open_dir("repos", base)
                    self.sys.harden(repos)
                    self.log("scratch-root-hardened", path=self.scratch_repos)
                return os.dup(repos)
            finally:
                if repos >= 0:
                    os.close(repos)
        finally:
            os.close(base)

    # ----- mountinfo views -----

    def mount_view(self):
        entries = self.sys.mountinfo()
        scratch_mount = containing_mount(entries, self.scratch_repos)
        scratch_root = fs_relative(entries, self.scratch_repos)
        ours, foreign = {}, set()
        for entry in entries:
            point = entry["mountPoint"]
            if not point.startswith(self.repos_path + "/"):
                continue
            root = entry["root"]
            if scratch_mount and entry["device"] == scratch_mount["device"] and \
                    root.startswith(scratch_root.rstrip("/") + "/"):
                leaf = tuple(root[len(scratch_root.rstrip("/")) + 1:].split("/"))
                ours[point] = dict(entry, leaf=leaf)
            else:
                foreign.add(point)
        self.mounts = ours
        self.foreign = foreign
        self.mounted_leaves = {entry["leaf"] for entry in ours.values()}
        self.entries = entries
        self.scratch_root = scratch_root
        return ours

    def leaf_mounted(self, leaf):
        """True when the leaf or anything below it is mounted anywhere."""
        return any(mounted[:len(leaf)] == leaf for mounted in self.mounted_leaves)

    # ----- discovery -----

    def scan(self, repos_fd):
        self.repos = {}
        self.dir_count = 0
        self.truncated = False
        queue = [((), None, frozenset())]
        for _ in range(MAX_ROUNDS):
            for components, repo, inherited in queue:
                try:
                    fd = self.open_path(repos_fd, components, self.ws_dev)
                except (OSError, Unsafe):
                    continue
                try:
                    self.walk(fd, components, repo, inherited)
                finally:
                    os.close(fd)
            self.classify(repos_fd)
            queue = []
            for repo in self.repos.values():
                for relative, candidate in repo.candidates.items():
                    # Tracked or non-ignored allowlisted directories may contain nested projects.
                    if repo.ignored.get(relative) is False and not candidate.walked and \
                            not candidate.mounted and not repo.git_failed:
                        candidate.walked = True
                        queue.append((repo.components + relative, repo, candidate.inherited))
            if not queue:
                break
        else:
            self.truncated = True
        self.registry(repos_fd)
        self.classify(repos_fd)

    def walk(self, fd, components, repo, inherited):
        if self.dir_count >= MAX_DIRS or len(components) > MAX_DEPTH:
            self.truncated = True
            return
        self.dir_count += 1
        if self.on_dir:
            self.on_dir(fd)
        infos = {}
        try:
            with os.scandir(fd) as entries:
                for entry in entries:
                    try:
                        infos[entry.name] = entry.stat(follow_symlinks=False)
                    except OSError:
                        continue
        except OSError:
            self.truncated = True
            return
        marker = infos.get(".git")
        if components and marker is not None and \
                (stat.S_ISDIR(marker.st_mode) or stat.S_ISREG(marker.st_mode)):
            repo = self.repos.setdefault(components, Repo(components, os.fstat(fd).st_ino))
            inherited = frozenset()
        relative = components[len(repo.components):] if repo else None
        if repo is not None:
            repo.dirs.add(relative)
            files = {name for name, info in infos.items() if stat.S_ISREG(info.st_mode)}
            for name in expected_outputs(files, inherited, repo_root=not relative):
                if name not in infos and valid_relative(relative + (name,)):
                    repo.expected.add(relative + (name,))
            inherited = manifest_flags(files, inherited)
        for name, info in sorted(infos.items()):
            if not stat.S_ISDIR(info.st_mode) or name in SKIP_SCAN or not valid_component(name):
                continue
            path = os.path.join(self.repo_path(components), name)
            mounted = path in self.mounts
            if repo is not None and name in ALLOWLIST and valid_relative(relative + (name,)):
                if path not in self.foreign:
                    repo.candidates[relative + (name,)] = Candidate(info, inherited, mounted)
                continue
            if mounted or path in self.foreign or self.sys.dev(info) != self.ws_dev:
                continue
            try:
                child = self.open_dir(name, fd, dev=self.ws_dev)
            except (OSError, Unsafe):
                continue
            try:
                if (os.fstat(child).st_ino) != info.st_ino:
                    continue
                self.walk(child, components + (name,), repo, inherited)
            finally:
                os.close(child)

    def classify(self, repos_fd):
        for repo in self.repos.values():
            pending = [relative for relative in sorted({*repo.candidates, *repo.expected})
                       if relative not in repo.ignored]
            if not pending:
                continue
            result = None
            try:
                fd = self.open_path(repos_fd, repo.components, self.ws_dev)
                try:
                    if os.fstat(fd).st_ino == repo.inode:
                        result = self.sys.check_ignore(fd, self.repo_path(repo.components),
                                                       ["/".join(item) for item in pending],
                                                       self.uid, self.gid)
                finally:
                    os.close(fd)
            except (OSError, Unsafe):
                result = None
            if result is None:
                repo.git_failed = True
                self.log("git-check-ignore-failed", repo=repo.id)
            for relative in pending:
                repo.ignored[relative] = result is not None and "/".join(relative) in result

    def registry(self, repos_fd):
        """Scratch leaves whose volume directory disappeared are recreated as empty mounts."""
        self.leaves = {}
        for repo in self.repos.values():
            leaves = self.list_leaves(repo.id)
            self.leaves[repo.id] = leaves
            for relative in leaves:
                if relative in repo.candidates or not valid_relative(relative):
                    continue
                if relative[:-1] in repo.dirs:
                    repo.expected.add(relative)
                    repo.recreate.add(relative)

    def list_leaves(self, identifier):
        """Coding-owned directories below root-owned scratch intermediates."""
        leaves = []
        try:
            fd = self.open_dir(identifier, self.scratch_fd)
        except (OSError, Unsafe):
            return leaves

        def visit(directory, prefix):
            if len(prefix) > MAX_DEPTH:
                return
            try:
                with os.scandir(directory) as entries:
                    children = [(entry.name, entry.stat(follow_symlinks=False)) for entry in entries]
            except OSError:
                return
            for name, info in children:
                if not stat.S_ISDIR(info.st_mode) or not prefix and name.startswith(".staging-"):
                    continue
                if self.sys.uid(info) != 0:
                    leaves.append(prefix + (name,))
                    continue
                try:
                    child = self.open_dir(name, directory)
                except (OSError, Unsafe):
                    continue
                try:
                    visit(child, prefix + (name,))
                finally:
                    os.close(child)
        try:
            visit(fd, ())
        finally:
            os.close(fd)
        return leaves

    # ----- scratch leaves -----

    def open_leaf(self, identifier, relative, parents=False, leaf=False, mode=0o755):
        """Returns (parent_fd, leaf_fd or None). Intermediates are root-owned, leaves coding-owned.

        Raises FileNotFoundError when an intermediate is missing and parents is false."""
        if not REPO_ID.fullmatch(identifier) or not valid_relative(relative):
            raise Unsafe("invalid_leaf")
        fd = os.dup(self.scratch_fd)
        try:
            for name in (identifier, *relative[:-1]):
                if parents:
                    try:
                        os.mkdir(name, 0o700, dir_fd=fd)
                        created = self.open_dir(name, fd)
                        try:
                            self.sys.fchown(created, 0, 0)
                        finally:
                            os.close(created)
                    except FileExistsError:
                        pass
                child = self.open_dir(name, fd, dev=self.scratch_dev)
                os.close(fd)
                fd = child
                if self.sys.uid(os.fstat(fd)) != 0:
                    raise Unsafe("leaf_conflict")
            name = relative[-1]
            try:
                result = self.open_dir(name, fd, dev=self.scratch_dev)
            except FileNotFoundError:
                if not leaf:
                    return fd, None
                os.mkdir(name, 0o700, dir_fd=fd)
                result = self.open_dir(name, fd, dev=self.scratch_dev)
                self.sys.fchown(result, self.uid, self.gid)
                os.fchmod(result, mode & 0o1777)
            if self.sys.uid(os.fstat(result)) != self.uid:
                os.close(result)
                raise Unsafe("leaf_conflict")
            return fd, result
        except BaseException:
            os.close(fd)
            raise

    def leaf_path(self, identifier, relative):
        return os.path.join(self.scratch_repos, identifier, *relative)

    def leaf_root(self, identifier, relative):
        return self.scratch_root.rstrip("/") + "/" + "/".join((identifier, *relative))

    def remove_leaf(self, identifier, relative):
        if self.leaf_mounted((identifier, *relative)):
            raise Deferred("leaf_mounted")
        parent, leaf = self.open_leaf(identifier, relative)
        try:
            if leaf is not None:
                os.close(leaf)
                self.rmtree(parent, relative[-1], self.scratch_dev)
        finally:
            os.close(parent)

    # ----- mounting -----

    def attach(self, repo, relative, parent_fd, target_fd, mode, fresh):
        """Binds the scratch leaf over an empty, verified volume directory."""
        name = relative[-1]
        leaf_parent, leaf = self.open_leaf(repo.id, relative, parents=True, leaf=True, mode=mode)
        try:
            if fresh:
                self.clear(leaf, self.scratch_dev)
            if os.listdir(target_fd):
                raise Deferred("not_empty")
            self.sys.bind(leaf, target_fd)
        finally:
            os.close(leaf)
            os.close(leaf_parent)
        self.mount_view()
        # Verify where the mount landed and that it hides nothing written meanwhile.
        expected = os.path.join(self.repo_path(repo.components), *relative)
        entry = self.mounts.get(expected)
        landed = entry is not None and entry["leaf"] == (repo.id, *relative)
        if landed:
            check = os.open(name, PATH_DIR, dir_fd=parent_fd)
            try:
                landed = self.sys.mnt_id(check) == entry["id"]
            finally:
                os.close(check)
        if not landed or os.listdir(target_fd):
            for point, mounted in list(self.mounts.items()):
                if mounted["leaf"] == (repo.id, *relative):
                    self.unmount_entry(mounted, lazy_ok=True)
            self.mount_view()
            raise Deferred("mount_unverified" if not landed else "changed")
        self.mount_count += 1
        return expected

    def unmount_entry(self, entry, lazy_ok):
        """Unmounts one verified mount from its pinned parent. Returns 'unmounted' or 'lazy'."""
        parts = entry["mountPoint"].strip("/").split("/")
        root = os.open("/", PATH_DIR)
        try:
            parent = self.open_path(root, parts[:-1], flags=PATH_DIR)
        finally:
            os.close(root)
        try:
            child = os.open(parts[-1], PATH_DIR, dir_fd=parent)
            try:
                if self.sys.mnt_id(child) != entry["id"]:
                    raise Unsafe("mount_changed")
            finally:
                os.close(child)
            for attempt in range(5):
                try:
                    self.sys.umount(parent, parts[-1])
                    return "unmounted"
                except OSError as error:
                    if error.errno != errno.EBUSY:
                        raise
                    time.sleep(0.2 * (attempt + 1))
            if not lazy_ok:
                raise Deferred("busy")
            self.sys.umount(parent, parts[-1], detach=True)
            return "lazy"
        finally:
            os.close(parent)

    # ----- trash and journals on the volume -----

    def trash(self, create):
        for _ in range(2):
            try:
                fd = self.open_trash(create)
            except Unsafe as error:
                if error.code != "symlink_or_not_directory":
                    raise
            else:
                if fd is None or self.sys.uid(os.fstat(fd)) == 0 and not os.fstat(fd).st_mode & 0o077:
                    return fd
                os.close(fd)
            # A restored backup hands every file to the coding user; such journals are no
            # longer trustworthy, so keep the content for the user and start a fresh trash.
            aside = TRASH + ".untrusted-" + os.urandom(8).hex()
            os.rename(TRASH, aside, src_dir_fd=self.ws_fd, dst_dir_fd=self.ws_fd)
            self.report("conflict", aside, "trash_owner")
        raise Unsafe("trash_owner")

    def open_trash(self, create):
        try:
            if create:
                try:
                    os.mkdir(TRASH, 0o700, dir_fd=self.ws_fd)
                    fd = self.open_dir(TRASH, self.ws_fd, dev=self.ws_dev)
                    self.sys.fchown(fd, 0, 0)
                    os.close(fd)
                except FileExistsError:
                    pass
            return self.open_dir(TRASH, self.ws_fd, dev=self.ws_dev)
        except FileNotFoundError:
            return None

    def write_journal(self, token_fd, journal):
        data = json.dumps(journal, sort_keys=True).encode()
        fd = os.open("journal.pending", os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=token_fd)
        try:
            self.sys.fchown(fd, 0, 0)
            os.write(fd, data)
            os.fsync(fd)
        finally:
            os.close(fd)
        os.replace("journal.pending", "journal.json", src_dir_fd=token_fd, dst_dir_fd=token_fd)
        os.fsync(token_fd)

    def read_journal(self, token_fd):
        fd = os.open("journal.json", os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=token_fd)
        try:
            info = os.fstat(fd)
            if self.sys.uid(info) != 0 or not stat.S_ISREG(info.st_mode) or info.st_size > 65536:
                raise Unsafe("journal_owner")
            journal = json.loads(os.read(fd, 65536))
        finally:
            os.close(fd)
        if not (journal.get("phase") in ("copying", "swapping", "mounted") and
                REPO_ID.fullmatch(str(journal.get("repoId"))) and
                valid_relative(journal.get("leaf")) and isinstance(journal.get("target"), list) and
                len(journal["target"]) >= 2 and all(valid_component(name) for name in journal["target"])
                and re.fullmatch(r"\.staging-[0-9a-f]{32}", str(journal.get("staging")))):
            raise Unsafe("journal_invalid")
        return journal

    def finish_token(self, trash_fd, token):
        fd = self.open_dir(token, trash_fd)
        try:
            for name in ("journal.json", "journal.pending"):
                try:
                    os.unlink(name, dir_fd=fd)
                except FileNotFoundError:
                    pass
            if "data" in os.listdir(fd):
                self.rmtree(fd, "data", self.ws_dev)
        finally:
            os.close(fd)
        os.rmdir(token, dir_fd=trash_fd)

    def recover(self):
        """Completes or rolls back interrupted relocations; data stays on the volume or is mounted."""
        trash = self.trash(create=False)
        if trash is None:
            return
        try:
            for token in sorted(os.listdir(trash)):
                try:
                    if token.startswith("e-") and TOKEN.fullmatch(token):
                        self.rmtree(trash, token, self.ws_dev)  # Interrupted eviction: intended deletion.
                    elif token.startswith("r-") and TOKEN.fullmatch(token):
                        self.recover_one(trash, token)
                    else:
                        self.report("conflict", TRASH + "/" + token, "unknown_trash_entry")
                except (OSError, Unsafe, Deferred) as error:
                    self.report("conflict", TRASH + "/" + token, getattr(error, "code", None) or
                                getattr(error, "reason", None) or "recovery_failed")
            if not os.listdir(trash):
                os.rmdir(TRASH, dir_fd=self.ws_fd)
        finally:
            os.close(trash)

    def recover_one(self, trash, token):
        token_fd = self.open_dir(token, trash)
        try:
            try:
                journal = self.read_journal(token_fd)
            except FileNotFoundError:
                journal = None
            data = "data" in os.listdir(token_fd)
        finally:
            os.close(token_fd)
        if journal is None:
            if data:
                raise Unsafe("journal_missing")  # Never delete unjournaled user data.
            self.finish_token(trash, token)
            return
        identifier, leaf, target = journal["repoId"], tuple(journal["leaf"]), tuple(journal["target"])
        try:
            fd = self.open_dir(identifier, self.scratch_fd, dev=self.scratch_dev)
            try:
                if journal["staging"] in os.listdir(fd):
                    self.rmtree(fd, journal["staging"], self.scratch_dev)
            finally:
                os.close(fd)
        except FileNotFoundError:
            pass
        point = self.repo_path(target)
        mounted = point in self.mounts and self.mounts[point]["leaf"] == (identifier, *leaf)
        if journal["phase"] == "mounted" or data and mounted:
            self.finish_token(trash, token)
            self.log("relocation-completed", path="/".join(target))
            return
        if journal["phase"] == "copying" or not data:
            if journal["phase"] == "swapping" and not self.leaf_mounted((identifier, *leaf)):
                try:
                    self.remove_leaf(identifier, leaf)
                except (FileNotFoundError, Unsafe):
                    pass
            self.finish_token(trash, token)
            self.log("relocation-rolled-back", path="/".join(target))
            return
        # Swapping with the original moved aside but not mounted: put it back.
        try:
            parent = self.open_path(self.repos_fd, target[:-1], self.ws_dev)
        except (FileNotFoundError, Unsafe):
            self.finish_token(trash, token)  # The checkout containing the output directory is gone.
            self.log("relocation-discarded", path="/".join(target), reason="parent_missing")
            return
        try:
            name = target[-1]
            try:
                existing = self.open_dir(name, parent, dev=self.ws_dev)
            except FileNotFoundError:
                existing = None
            if existing is not None:
                try:
                    if self.is_mount_point(parent, existing) or os.listdir(existing):
                        raise Deferred("restore_conflict")
                finally:
                    os.close(existing)
                os.rmdir(name, dir_fd=parent)
            token_fd = self.open_dir(token, trash)
            try:
                os.rename("data", name, src_dir_fd=token_fd, dst_dir_fd=parent)
            finally:
                os.close(token_fd)
        finally:
            os.close(parent)
        try:
            self.remove_leaf(identifier, leaf)
        except (FileNotFoundError, Unsafe, Deferred):
            pass
        self.finish_token(trash, token)
        self.log("relocation-rolled-back", path="/".join(target))

    # ----- tree utilities (descriptor based, never following links or crossing mounts) -----

    def rmtree(self, parent_fd, name, dev):
        fd = self.open_dir(name, parent_fd, dev=dev)
        try:
            self.clear(fd, dev)
        finally:
            os.close(fd)
        os.rmdir(name, dir_fd=parent_fd)

    def clear(self, fd, dev, depth=0):
        if depth > MAX_TREE_DEPTH:
            raise Unsafe("too_deep")
        with os.scandir(fd) as entries:
            children = [(entry.name, entry.stat(follow_symlinks=False)) for entry in entries]
        for name, info in children:
            if stat.S_ISDIR(info.st_mode):
                child = self.open_dir(name, fd, dev=dev)
                try:
                    if os.fstat(child).st_ino != info.st_ino:
                        raise Unsafe("changed")
                    self.clear(child, dev, depth + 1)
                finally:
                    os.close(child)
                os.rmdir(name, dir_fd=fd)
            else:
                os.unlink(name, dir_fd=fd)

    def manifest(self, fd, prefix=(), result=None, depth=0):
        """Metadata snapshot of a tree on the volume; refuses nested mounts and special files."""
        result = {} if result is None else result
        if depth > MAX_TREE_DEPTH or len(result) > MAX_TREE_ENTRIES:
            raise Deferred("too_large")
        with os.scandir(fd) as entries:
            children = sorted((entry.name, entry.stat(follow_symlinks=False)) for entry in entries)
        for name, info in children:
            path = prefix + (name,)
            if self.sys.dev(info) != self.ws_dev:
                raise Deferred("nested_mount")
            if stat.S_ISDIR(info.st_mode):
                child = self.open_dir(name, fd, dev=self.ws_dev)
                try:
                    if self.is_mount_point(fd, child):
                        raise Deferred("nested_mount")
                    self.manifest(child, path, result, depth + 1)
                finally:
                    os.close(child)
            elif not (stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode)):
                raise Deferred("unsupported_file_type")
            result[path] = (stat.S_IFMT(info.st_mode), info.st_mode & 0o7777, self.sys.uid(info),
                            info.st_gid, info.st_size if not stat.S_ISDIR(info.st_mode) else 0,
                            info.st_mtime_ns, info.st_ctime_ns, info.st_ino, info.st_nlink,
                            info.st_blocks)
        return result

    def copy_tree(self, source, target, root_target, prefix, links):
        for name, info in sorted((entry.name, entry.stat(follow_symlinks=False))
                                 for entry in os.scandir(source)):
            self.checkpoint()
            path = prefix + (name,)
            if self.sys.dev(info) != self.ws_dev:
                raise Deferred("nested_mount")
            owner = (self.sys.uid(info), info.st_gid)
            times = (info.st_atime_ns, info.st_mtime_ns)
            if stat.S_ISDIR(info.st_mode):
                os.mkdir(name, 0o700, dir_fd=target)
                src = self.open_dir(name, source, dev=self.ws_dev)
                try:
                    if os.fstat(src).st_ino != info.st_ino:
                        raise Deferred("changed")
                    dst = self.open_dir(name, target)
                    try:
                        self.copy_tree(src, dst, root_target, path, links)
                        self.sys.fchown(dst, *owner)
                        os.fchmod(dst, info.st_mode & 0o7777)
                        os.utime(dst, ns=times)
                    finally:
                        os.close(dst)
                finally:
                    os.close(src)
            elif stat.S_ISLNK(info.st_mode):
                os.symlink(os.readlink(name, dir_fd=source), name, dir_fd=target)
                self.sys.lchown(target, name, *owner)
                os.utime(name, ns=times, dir_fd=target, follow_symlinks=False)
            elif stat.S_ISREG(info.st_mode):
                key = (info.st_dev, info.st_ino)
                if info.st_nlink > 1 and key in links:
                    os.link(links[key], name, src_dir_fd=root_target, dst_dir_fd=target,
                            follow_symlinks=False)
                    continue
                src = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=source)
                try:
                    if os.fstat(src).st_ino != info.st_ino:
                        raise Deferred("changed")
                    dst = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                                  0o600, dir_fd=target)
                    try:
                        self.copy_data(src, dst)
                        self.sys.fchown(dst, *owner)
                        mode = info.st_mode & 0o7777
                        if owner[0] != self.uid:
                            mode &= ~(stat.S_ISUID | stat.S_ISGID)
                        os.fchmod(dst, mode)
                        os.utime(dst, ns=times)
                    finally:
                        os.close(dst)
                finally:
                    os.close(src)
                if info.st_nlink > 1:
                    links[key] = "/".join(path)
            else:
                raise Deferred("unsupported_file_type")

    def copy_data(self, src, dst):
        chunk = 64 << 20
        try:
            while os.copy_file_range(src, dst, chunk):
                self.checkpoint()
            return
        except OSError as error:
            if error.errno not in (errno.EXDEV, errno.ENOSYS, errno.EINVAL, errno.EOPNOTSUPP):
                raise
        os.lseek(src, 0, os.SEEK_SET)
        os.lseek(dst, 0, os.SEEK_SET)
        os.ftruncate(dst, 0)
        while True:
            data = os.read(src, 1 << 20)
            if not data:
                return
            view = memoryview(data)
            while view:
                view = view[os.write(dst, view):]
            self.checkpoint()

    # ----- processes -----

    def in_use(self, path):
        if self.processes is None:
            self.processes = self.sys.processes()
        paths, unreadable = self.processes
        return bool(unreadable) or any(under(item, path) for item in paths)

    # ----- relocation of an existing non-empty directory -----

    def relocate(self, repo, relative, parent_fd, target_fd, info):
        """Copy to scratch, verify, swap the volume copy into the trash, bind, then delete it.

        A root-only journal on the volume records each phase; recover() resumes or rolls back
        after a crash so the data is always either on the volume or mounted from scratch."""
        path = os.path.join(self.repo_path(repo.components), *relative)
        if self.deadline is not None and self.monotonic() > self.deadline - 5:
            raise Deferred("budget")
        before = self.manifest(target_fd)
        newest = max([info.st_ctime_ns, *(item[6] for item in before.values())])
        if self.clock() - newest / 1e9 < self.quiet:
            raise Deferred("not_quiet")
        if self.in_use(path):
            raise Deferred("in_use")
        size = sum({(item[7], item[0]): item[9] * 512 for item in before.values()}.values())
        space = self.sys.statvfs(self.scratch_fd)
        if space.f_bavail * space.f_frsize < size + SCRATCH_MARGIN:
            raise Deferred("scratch_full")
        if self.leaf_mounted((repo.id, *relative)):
            raise Deferred("leaf_mounted")
        try:
            self.remove_leaf(repo.id, relative)  # Stale scratch content: the volume copy wins.
        except FileNotFoundError:
            pass
        token = "r-" + uuid.uuid4().hex
        staging = ".staging-" + uuid.uuid4().hex
        journal = dict(version=1, phase="copying", repoId=repo.id, leaf=list(relative),
                       target=list(repo.components + relative), staging=staging)
        trash = self.trash(create=True)
        try:
            os.mkdir(token, 0o700, dir_fd=trash)
            token_fd = self.open_dir(token, trash)
            try:
                try:
                    self.write_journal(token_fd, journal)
                    self.stage_copy(repo, relative, target_fd, info, before, journal, staging, token_fd)
                except BaseException:
                    # Nothing has left the volume yet; dropping the journal is always safe.
                    self.finish_token(trash, token)
                    raise
                self.swap(repo, relative, parent_fd, info, before, journal, trash, token, token_fd)
            finally:
                os.close(token_fd)
        finally:
            os.close(trash)
        self.log("relocated", path=os.path.relpath(path, self.repos_path), bytes=size)
        return path

    def stage_copy(self, repo, relative, target_fd, info, before, journal, staging, token_fd):
        # The staging copy sits in the repository's scratch root so the final rename stays local.
        leaf_parent, existing = self.open_leaf(repo.id, relative, parents=True)
        try:
            if existing is not None:
                os.close(existing)
                raise Deferred("leaf_conflict")
            id_fd = self.open_dir(repo.id, self.scratch_fd, dev=self.scratch_dev)
            try:
                os.mkdir(staging, 0o700, dir_fd=id_fd)
                try:
                    stage = self.open_dir(staging, id_fd)
                    try:
                        self.copy_tree(target_fd, stage, stage, (), {})
                        self.sys.fchown(stage, self.sys.uid(info), info.st_gid)
                        os.fchmod(stage, info.st_mode & 0o7777)
                        os.utime(stage, ns=(info.st_atime_ns, info.st_mtime_ns))
                        os.fsync(stage)
                    finally:
                        os.close(stage)
                    if self.manifest(target_fd) != before or \
                            os.fstat(target_fd).st_mtime_ns != info.st_mtime_ns:
                        raise Deferred("changed")
                    self.processes = None
                    if self.in_use(os.path.join(self.repo_path(repo.components), *relative)):
                        raise Deferred("in_use")
                except BaseException:
                    self.rmtree(id_fd, staging, self.scratch_dev)
                    raise
                journal["phase"] = "swapping"
                self.write_journal(token_fd, journal)
                os.rename(staging, relative[-1], src_dir_fd=id_fd, dst_dir_fd=leaf_parent)
                os.fsync(leaf_parent)
            finally:
                os.close(id_fd)
        finally:
            os.close(leaf_parent)

    def swap(self, repo, relative, parent_fd, info, before, journal, trash, token, token_fd):
        name = relative[-1]
        # Move the volume copy into the root-only trash and verify it is the directory copied.
        try:
            os.rename(name, "data", src_dir_fd=parent_fd, dst_dir_fd=token_fd)
        except OSError:
            self.remove_leaf(repo.id, relative)
            self.finish_token(trash, token)
            raise Deferred("rename_failed")
        try:
            moved = self.open_dir("data", token_fd, dev=self.ws_dev)
            try:
                # Nothing may have changed or opened the tree since the copy was verified.
                self.processes = None
                if (os.fstat(moved).st_ino != info.st_ino or self.manifest(moved) != before or
                        self.in_use(os.path.join(self.workspace, TRASH, token))):
                    raise Deferred("changed")
            finally:
                os.close(moved)
        except BaseException:
            self.recover_one(trash, token)
            raise
        os.fsync(token_fd)
        os.fsync(parent_fd)
        try:
            try:
                os.mkdir(name, 0o700, dir_fd=parent_fd)
            except FileExistsError:
                pass
            fresh = self.open_dir(name, parent_fd, dev=self.ws_dev)
            try:
                if self.sys.uid(os.fstat(fresh)) not in (self.uid, os.geteuid()) or os.listdir(fresh):
                    raise Deferred("restore_conflict")
                self.sys.fchown(fresh, self.uid, self.gid)
                os.fchmod(fresh, info.st_mode & 0o1777)
                self.attach(repo, relative, parent_fd, fresh, info.st_mode, fresh=False)
            finally:
                os.close(fresh)
        except BaseException:
            # Journal still says "swapping": put the original back (or report a conflict).
            self.mount_view()
            self.recover_one(trash, token)
            raise
        journal["phase"] = "mounted"
        self.write_journal(token_fd, journal)
        if self.deadline is not None and self.monotonic() > self.deadline:
            return  # The next pass deletes the moved-aside volume copy.
        self.finish_token(trash, token)

    # ----- one pass -----

    def report(self, kind, path, reason=None):
        item = dict(path=path) if reason is None else dict(path=path, reason=reason)
        bucket = self.status.setdefault(kind, [])
        if len(bucket) < MAX_REPORTED:
            bucket.append(item)
        self.status.setdefault("counts", {})[kind] = self.status["counts"].get(kind, 0) + 1

    def apply(self, budget=None):
        self.status = dict(schemaVersion=1, updatedAt=int(self.clock()), state="active", counts={})
        self.deadline = None if budget is None else self.monotonic() + budget
        self.processes = None
        self.mount_count = 0
        if self.suspended():
            self.status["state"] = "suspended"
            return self.status
        try:
            self.ws_fd = os.open(self.workspace, DIRECTORY)
        except FileNotFoundError:
            self.status["state"] = "no-workspace"
            return self.status
        try:
            self.ws_dev = self.sys.dev(os.fstat(self.ws_fd))
            try:
                self.repos_fd = self.open_dir("repos", self.ws_fd, dev=self.ws_dev)
            except FileNotFoundError:
                self.status["state"] = "no-repos"
                return self.status
            try:
                self.scratch_fd = self.ensure_scratch()
                try:
                    if self.scratch_dev == self.ws_dev:
                        # Ephemeral or not-yet-mounted workspace: outputs are already local.
                        self.status["state"] = "same-device"
                        return self.status
                    self.mount_view()
                    self.recover()
                    self.mount_view()
                    self.scan(self.repos_fd)
                    self.status["truncated"] = self.truncated
                    self.mount_count = len(self.mounts)
                    for repo in sorted(self.repos.values(), key=lambda item: item.components):
                        self.apply_repo(repo)
                    self.release_orphans()
                    self.collect_garbage()
                    self.status["pressure"] = self.relieve_pressure()
                    self.status["mounts"] = len(self.mount_view())
                    try:
                        os.rmdir(TRASH, dir_fd=self.ws_fd)  # Only when empty.
                    except OSError:
                        pass
                finally:
                    os.close(self.scratch_fd)
            finally:
                os.close(self.repos_fd)
        finally:
            os.close(self.ws_fd)
        return self.status

    def apply_repo(self, repo):
        items = sorted({*repo.candidates, *repo.expected}, key=lambda item: (len(item), item))
        for relative in items:
            label = "/".join(repo.components + relative)
            candidate = repo.candidates.get(relative)
            if candidate is not None and candidate.mounted:
                continue
            if not repo.ignored.get(relative):
                continue
            if self.mount_count >= MAX_MOUNTS:
                self.report("deferred", label, "mount_limit")
                continue
            try:
                self.checkpoint()
                self.apply_one(repo, relative, candidate)
            except Deferred as error:
                if error.reason == "stopping":
                    raise
                self.report("deferred", label, error.reason)
            except Unsafe as error:
                self.report("skipped", label, error.code)
            except OSError as error:
                self.report("skipped", label, errno.errorcode.get(error.errno, "io_error"))

    def apply_one(self, repo, relative, candidate):
        parent = self.open_path(self.repos_fd, repo.components + relative[:-1], self.ws_dev)
        try:
            parent_info = os.fstat(parent)
            name = relative[-1]
            if candidate is None:
                if self.sys.uid(parent_info) != self.uid:
                    raise Unsafe("foreign_owner")
                try:
                    os.mkdir(name, 0o755, dir_fd=parent)
                except FileExistsError:
                    raise Deferred("appeared")
                created = True
            else:
                created = False
            target = self.open_dir(name, parent, dev=self.ws_dev)
            try:
                info = os.fstat(target)
                if created:
                    self.sys.fchown(target, self.uid, self.gid)
                    os.fchmod(target, 0o755)
                    info = os.fstat(target)
                elif (info.st_dev, info.st_ino) != (candidate.info.st_dev, candidate.info.st_ino):
                    raise Deferred("changed")
                if self.sys.uid(info) != self.uid and not created:
                    raise Unsafe("foreign_owner")
                if self.is_mount_point(parent, target):
                    raise Unsafe("mount_point")
                path = os.path.join(self.repo_path(repo.components), *relative)
                if os.listdir(target):
                    self.relocate(repo, relative, parent, target, info)
                    self.report("relocated", "/".join(repo.components + relative))
                    return
                if not created and self.in_use(path):
                    raise Deferred("in_use")
                fresh = created and relative in repo.recreate
                self.attach(repo, relative, parent, target, info.st_mode, fresh)
                self.report("recreated" if fresh else "created" if created else "mounted",
                            "/".join(repo.components + relative))
            except BaseException:
                if created:
                    try:
                        os.rmdir(name, dir_fd=parent)
                    except OSError:
                        pass
                raise
            finally:
                os.close(target)
        finally:
            os.close(parent)

    def release_orphans(self):
        """Unmounts our mounts left in deleted checkouts so the directory can be removed or re-cloned.

        Only after a complete scan, and only for mounts outside every detected repository; renamed
        checkouts keep their mounts (see README). Busy mounts are retried on a later pass.
        """
        if self.truncated or any(repo.git_failed for repo in self.repos.values()):
            return
        roots = [self.repo_path(repo.components) + "/" for repo in self.repos.values()]
        orphans = [point for point in self.mounts if not any(point.startswith(root) for root in roots)]
        for point in sorted(orphans, key=lambda item: (-item.count("/"), item)):
            label = os.path.relpath(point, self.repos_path)
            try:
                self.unmount_entry(self.mounts[point], lazy_ok=False)
                self.report("unmounted", label, "repository_gone")
            except Deferred as error:
                self.report("deferred", label, error.reason)
            except (OSError, Unsafe) as error:
                self.report("skipped", label, getattr(error, "code", None) or "io_error")
        if orphans:
            self.mount_view()

    def collect_garbage(self):
        """Removes scratch for vanished checkouts and stale staging; only after a complete scan."""
        if self.truncated or any(repo.git_failed for repo in self.repos.values()):
            return
        known = {repo.id: repo for repo in self.repos.values()}
        for identifier in sorted(os.listdir(self.scratch_fd)):
            if not REPO_ID.fullmatch(identifier):
                continue
            try:
                if identifier not in known:
                    if not self.leaf_mounted((identifier,)):
                        self.rmtree(self.scratch_fd, identifier, self.scratch_dev)
                        self.report("removed", identifier, "repository_gone")
                    continue
                repo = known[identifier]
                fd = self.open_dir(identifier, self.scratch_fd, dev=self.scratch_dev)
                try:
                    for name in os.listdir(fd):
                        if name.startswith(".staging-"):
                            self.rmtree(fd, name, self.scratch_dev)
                finally:
                    os.close(fd)
                for relative in self.leaves.get(identifier, []):
                    candidate = repo.candidates.get(relative)
                    if self.leaf_mounted((identifier, *relative)):
                        continue
                    stale = relative[:-1] not in repo.dirs or (
                        candidate is not None and repo.ignored.get(relative) is False)
                    if stale:
                        self.remove_leaf(identifier, relative)
                        self.report("removed", "/".join(repo.components + relative), "stale_scratch")
            except (OSError, Unsafe, Deferred):
                continue

    # ----- pressure safety net -----

    def usage(self):
        info = self.sys.statvfs(self.ws_fd)
        used = (info.f_blocks - info.f_bfree) * info.f_frsize
        total = used + info.f_bavail * info.f_frsize
        return 100.0 * used / total if total else 0.0

    def relieve_pressure(self):
        percent = self.usage()
        result = dict(usagePercent=round(percent, 1), thresholdPercent=self.pressure, evicted=0)
        if percent < self.pressure:
            return result
        self.log("volume-pressure", usagePercent=round(percent, 1), thresholdPercent=self.pressure)
        victims = []
        for repo in self.repos.values():
            if repo.git_failed or self.in_use(self.repo_path(repo.components)):
                continue
            for relative, candidate in repo.candidates.items():
                if candidate.mounted or repo.ignored.get(relative) is not True or \
                        relative[-1] not in ALLOWLIST or self.sys.uid(candidate.info) != self.uid:
                    continue
                try:
                    fd = self.open_path(self.repos_fd, repo.components + relative, self.ws_dev)
                except (OSError, Unsafe):
                    continue
                try:
                    if os.fstat(fd).st_ino != candidate.info.st_ino:
                        continue
                    tree = self.manifest(fd)
                except (OSError, Unsafe, Deferred):
                    continue
                finally:
                    os.close(fd)
                if tree:
                    newest = max(item[5] for item in tree.values())
                    victims.append((newest, repo, relative, candidate))
        victims.sort(key=lambda item: (item[0], item[1].components, item[2]))
        for _, repo, relative, candidate in victims:
            if percent < self.pressure - PRESSURE_HYSTERESIS:
                break
            label = "/".join(repo.components + relative)
            try:
                self.evict(repo, relative, candidate)
            except (OSError, Unsafe, Deferred) as error:
                self.report("deferred", label, getattr(error, "reason", None) or
                            getattr(error, "code", None) or "evict_failed")
                continue
            result["evicted"] += 1
            self.report("evicted", label)
            self.log("evicted", path=label)
            percent = self.usage()
        result["usagePercent"] = round(percent, 1)
        return result

    def evict(self, repo, relative, candidate):
        parent = self.open_path(self.repos_fd, repo.components + relative[:-1], self.ws_dev)
        try:
            target = self.open_dir(relative[-1], parent, dev=self.ws_dev)
            try:
                info = os.fstat(target)
                if (info.st_dev, info.st_ino) != (candidate.info.st_dev, candidate.info.st_ino) or \
                        self.is_mount_point(parent, target):
                    raise Deferred("changed")
            finally:
                os.close(target)
            self.processes = None
            if self.in_use(self.repo_path(repo.components)):
                raise Deferred("in_use")
            token = "e-" + uuid.uuid4().hex
            trash = self.trash(create=True)
            try:
                os.mkdir(token, 0o700, dir_fd=trash)
                token_fd = self.open_dir(token, trash)
                try:
                    os.rename(relative[-1], "data", src_dir_fd=parent, dst_dir_fd=token_fd)
                    moved = os.stat("data", dir_fd=token_fd, follow_symlinks=False)
                    if (moved.st_dev, moved.st_ino) != (info.st_dev, info.st_ino):
                        os.rename("data", relative[-1], src_dir_fd=token_fd, dst_dir_fd=parent)
                        raise Deferred("changed")
                finally:
                    os.close(token_fd)
                self.rmtree(trash, token, self.ws_dev)
            finally:
                os.close(trash)
        finally:
            os.close(parent)

    # ----- lifecycle -----

    def unmount_all(self):
        """Suspends the helper and removes every scratch bind mount below /workspace/repos."""
        self.set_suspended(True)
        self.status = dict(schemaVersion=1, counts={})
        report = dict(schemaVersion=1, type="ScratchUnmounted", unmounted=0, lazy=0, failed=0,
                      lazyPaths=[], recovered=True)
        try:
            lock = self.lock()
        except Deferred:
            report.update(failed=1, error="locked")
            return report
        try:
            self.mount_view()
            try:
                self.ws_fd = os.open(self.workspace, DIRECTORY)
            except FileNotFoundError:
                self.ws_fd = None
            if self.ws_fd is not None:
                try:
                    self.ws_dev = self.sys.dev(os.fstat(self.ws_fd))
                    self.repos_fd = self.open_dir("repos", self.ws_fd, dev=self.ws_dev)
                    self.scratch_fd = self.ensure_scratch()
                    try:
                        self.mount_view()
                        self.recover()
                        report["recovered"] = not self.status.get("conflict")
                    finally:
                        os.close(self.scratch_fd)
                        os.close(self.repos_fd)
                except (OSError, Unsafe):
                    report["recovered"] = False
                finally:
                    os.close(self.ws_fd)
            # Deepest first; repeat for stacked mounts (each round removes the top of each stack).
            for _ in range(4):
                ours = self.mount_view()
                if not ours:
                    break
                for point in sorted(ours, key=lambda item: (-item.count("/"), item)):
                    try:
                        outcome = self.unmount_entry(ours[point], lazy_ok=True)
                    except (OSError, Unsafe, Deferred) as error:
                        self.log("unmount-failed", path=point, reason=str(error))
                        continue
                    report[outcome] += 1
                    if outcome == "lazy":
                        report["lazyPaths"].append(os.path.relpath(point, self.repos_path))
                        self.log("lazy-unmount", path=point)
            report["failed"] = len(self.mount_view())
        finally:
            os.close(lock)
        self.write_status(dict(report, state="suspended", updatedAt=int(self.clock())))
        return report


class Inotify:
    def __init__(self, system):
        libc = system.libc()
        self.libc = libc
        self.fd = libc.inotify_init1(IN_NONBLOCK | IN_CLOEXEC)
        if self.fd < 0:
            code = ctypes.get_errno()
            raise OSError(code, os.strerror(code))
        self.watches = set()
        self.full = False

    def add(self, fd):
        if len(self.watches) >= MAX_WATCHES:
            self.full = True
            return
        wd = self.libc.inotify_add_watch(self.fd, b"/proc/self/fd/%d" % fd, WATCH_MASK)
        if wd < 0:
            self.full = True
        else:
            self.watches.add(wd)

    def read(self):
        """True when an event may change candidates or manifests."""
        relevant = False
        while True:
            try:
                data = os.read(self.fd, 1 << 16)
            except BlockingIOError:
                return relevant
            offset = 0
            while offset + 16 <= len(data):
                wd, mask, _, length = struct.unpack_from("iIII", data, offset)
                name = data[offset + 16:offset + 16 + length].rstrip(b"\0").decode(errors="replace")
                offset += 16 + length
                if mask & IN_IGNORED:
                    self.watches.discard(wd)
                if mask & (IN_Q_OVERFLOW | IN_DELETE_SELF | IN_MOVE_SELF | IN_ISDIR) or \
                        name in ALLOWLIST or MANIFEST_EVENTS.fullmatch(name):
                    relevant = True


STOP = False


def _stop(signum, frame):
    global STOP
    STOP = True


def watch(helper, interval=300, retry=60, debounce=2, settle=15, minimum=10):
    """Debounced passes on relevant inotify events, plus periodic passes for deferred work."""
    try:
        notify = Inotify(helper.sys)
        helper.on_dir = notify.add
    except (OSError, AttributeError):
        notify = None
        helper.log("inotify-unavailable", fallback="polling")
    last_pass, dirty_since, last_event = None, None, None
    pending = False
    while not helper.stop():
        now = helper.monotonic()
        due = last_pass is None or now - last_pass >= (retry if pending or notify is None or
                                                        notify.full else interval)
        if dirty_since is not None and now - last_pass >= minimum and \
                (now - last_event >= debounce or now - dirty_since >= settle):
            due = True
        if due:
            dirty_since = None
            pending = run_pass(helper, budget=None)
            last_pass = helper.monotonic()
            continue
        if notify is None:
            time.sleep(1)
            continue
        ready, _, _ = select.select([notify.fd], [], [], 1.0)
        if ready and notify.read():
            last_event = helper.monotonic()
            dirty_since = dirty_since or last_event


def run_pass(helper, budget):
    """One locked pass; returns True when work was deferred and should be retried soon."""
    try:
        lock = helper.lock(seconds=5 if budget is None else min(budget, LOCK_SECONDS))
    except Deferred:
        return True
    try:
        try:
            status = helper.apply(budget=budget)
        except Deferred as error:
            status = dict(getattr(helper, "status", {}), state="interrupted", reason=error.reason)
        except (OSError, Unsafe) as error:
            status = dict(schemaVersion=1, state="error",
                          reason=getattr(error, "code", None) or errno.errorcode.get(
                              getattr(error, "errno", 0), "io_error"))
            helper.log("pass-failed", reason=status["reason"])
        status["updatedAt"] = int(helper.clock())
        helper.write_status(status)
        counts = status.get("counts", {})
        if counts:
            helper.log("pass", state=status.get("state"), **counts)
        return bool(counts.get("deferred")) or status.get("state") in ("interrupted", "error")
    finally:
        os.close(lock)


def main(argv=None):
    parser = argparse.ArgumentParser(prog="minicloud-scratch-mounts")
    parser.add_argument("command", choices=("apply", "watch", "suspend", "resume", "unmount-all", "status"))
    parser.add_argument("--budget-seconds", type=int, default=None)
    parser.add_argument("--pressure-percent", type=int, default=PRESSURE_PERCENT)
    parser.add_argument("--quiet-seconds", type=int, default=QUIET_SECONDS)
    args = parser.parse_args(argv)
    if os.geteuid() != 0:
        print(json.dumps(dict(ok=False, error="root required")))
        return 2
    if not 50 <= args.pressure_percent <= 99:
        parser.error("--pressure-percent must be 50..99")
    if not 1 <= args.quiet_seconds <= 3600:
        parser.error("--quiet-seconds must be 1..3600")
    signal.signal(signal.SIGTERM, _stop)
    signal.signal(signal.SIGINT, _stop)
    os.umask(0o077)
    helper = Helper(stop=lambda: STOP, pressure=args.pressure_percent, quiet=args.quiet_seconds)
    if args.command == "apply":
        pending = run_pass(helper, budget=args.budget_seconds)
        print(json.dumps(dict(ok=True, deferred=pending)))
    elif args.command == "watch":
        watch(helper)
    elif args.command == "suspend":
        helper.set_suspended(True)
        print(json.dumps(dict(ok=True, suspended=True)))
    elif args.command == "resume":
        helper.set_suspended(False)
        print(json.dumps(dict(ok=True, suspended=False)))
    elif args.command == "unmount-all":
        print(json.dumps(helper.unmount_all()))
    else:
        try:
            with open(os.path.join(RUN_DIR, "status.json")) as stream:
                print(stream.read())
        except FileNotFoundError:
            print(json.dumps(dict(state="unknown")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
