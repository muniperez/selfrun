#!/usr/bin/env python3
"""Storage tiers (#202): prepare the local scratch tier and move caches off the retained volume.

Runs as root from minicloud-t3.service (ExecStartPre=-+...) before T3 starts and never blocks it:
it always exits 0 and logs one bounded JSON line to stderr (the journal).

Root only re-applies the scratch tmpfiles entry, checks the scratch layout and owns the migration
marker. Every /workspace/home operation runs in a child that has dropped to the coding user, opens
each path component relative to its parent with O_NOFOLLOW, and acts only on real directories owned
by the coding user on the workspace filesystem. Credentials and configuration are never touched:
only the exact cache directories below are moved, and only into a trash folder that is then purged.
"""
import json
import os
import re
import signal
import stat
import subprocess
import sys
import time

SCRATCH = "/var/lib/minicloud-scratch"
ENV_FILE = "/opt/minicloud-workspace/scratch.env"
TMPFILES = "/etc/tmpfiles.d/minicloud-scratch.conf"
WORKSPACE = "/workspace"
HOME = "home"
MARKER = "/var/lib/minicloud-workspace/storage-tiers-migrated.json"
TRASH = ".minicloud-cache-trash"
CODING_UID = 2000
CODING_GID = 2000
MIGRATION_VERSION = 1
PURGE_SECONDS = 40
CHILD_SECONDS = 60
MAX_DEPTH = 256
MAX_LOG_BYTES = 4096
DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
ENV_LINE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=(/[A-Za-z0-9._/-]+)")

# Retained-home cache directories removed once per VM. Credentials and configuration that live next
# to them (.npmrc, .nuget/NuGet/NuGet.Config, .cargo/{config.toml,credentials.toml,bin},
# .gradle/{gradle.properties,init.d}, .m2/settings.xml, go/bin, ...) are not listed and stay put.
MIGRATED = (".cache", ".npm", ".nuget/packages", ".local/share/NuGet/http-cache",
            ".local/share/NuGet/v3-cache", ".local/share/NuGet/plugins-cache", ".local/share/pnpm/store",
            ".yarn/berry/cache", "go/pkg/mod", ".cargo/registry", ".cargo/git", ".gradle/caches",
            ".gradle/wrapper/dists", ".m2/repository", ".local/share/docker")
# Some tools keep credentials inside a cache directory; such a directory is left in place.
GUARDED = {".cache": ("huggingface/token", "huggingface/stored_tokens")}

# Caches whose tools have no safe cache-only variable (their home directory also holds credentials,
# configuration or installed binaries) stay reachable at the usual path through a symlink into
# scratch. "" links the whole XDG cache directory for tools that ignore XDG_CACHE_HOME.
LINKS = ((".cache", ""), (".cargo/registry", "cargo/registry"), (".cargo/git", "cargo/git"),
         (".gradle/caches", "gradle/caches"), (".gradle/wrapper/dists", "gradle/wrapper-dists"),
         (".m2/repository", "maven/repository"), (".local/share/pnpm/store", "pnpm/store"),
         (".yarn/berry/cache", "yarn-berry/cache"))

SCRATCH_LAYOUT = (("", 0, 0o755), ("cache", CODING_UID, 0o700), ("tmp", CODING_UID, 0o700),
                  ("docker", 0, 0o711), ("repos", 0, 0o755))


class Skip(Exception):
    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def load_environment(path=ENV_FILE, scratch=SCRATCH):
    """Parses the shared KEY=/absolute/path file exactly as systemd and profile.sh read it."""
    values = {}
    with open(path, encoding="ascii") as stream:
        for line in stream.read().splitlines():
            if not line or line.startswith("#"):
                continue
            match = ENV_LINE.fullmatch(line)
            if not match or match.group(1) in values:
                raise ValueError("Invalid scratch environment line")
            key, value = match.groups()
            if os.path.normpath(value) != value or not value.startswith(scratch + "/"):
                raise ValueError("Scratch environment value outside the scratch tier")
            values[key] = value
    return values


def open_dir(parent, name, uid, device):
    """Opens one real directory without following links; refuses foreign or separately mounted ones."""
    try:
        fd = os.open(name, DIRECTORY, dir_fd=parent)
    except FileNotFoundError:
        raise Skip("absent") from None
    except OSError as error:
        if error.errno == 40 or isinstance(error, NotADirectoryError):
            # O_NOFOLLOW|O_DIRECTORY reports ELOOP or ENOTDIR for a symbolic link.
            info = os.stat(name, dir_fd=parent, follow_symlinks=False)
            raise Skip("symlink" if stat.S_ISLNK(info.st_mode) else "not_directory") from None
        raise
    info = os.fstat(fd)
    if info.st_dev != device or info.st_uid != uid:
        os.close(fd)
        raise Skip("separate_mount" if info.st_dev != device else "foreign_owner")
    return fd


def open_path(root, components, uid, device, create=False):
    fds = []
    try:
        for name in components:
            parent = fds[-1] if fds else root
            if create:
                try:
                    os.mkdir(name, 0o700, dir_fd=parent)
                except FileExistsError:
                    pass
            fds.append(open_dir(parent, name, uid, device))
        return fds
    except BaseException:
        for fd in fds:
            os.close(fd)
        raise


def open_home(workspace, uid):
    root = os.open(workspace, DIRECTORY)
    try:
        device = os.fstat(root).st_dev
        return open_dir(root, HOME, uid, device), device
    finally:
        os.close(root)


def contains_any(fd, relatives):
    """True when any listed entry exists below fd, reached without following links."""
    for relative in relatives:
        *parents, name = relative.split("/")
        opened = []
        try:
            for component in parents:
                opened.append(os.open(component, DIRECTORY, dir_fd=opened[-1] if opened else fd))
            os.stat(name, dir_fd=opened[-1] if opened else fd, follow_symlinks=False)
            return True
        except FileNotFoundError:
            continue
        except OSError:
            # Unknown is treated as present: never move a directory that may hold credentials.
            return True
        finally:
            for descriptor in opened:
                os.close(descriptor)
    return False


def migrate(home, uid, device, items=MIGRATED, guarded=GUARDED, clock=time.time_ns):
    """Moves real cache directories into the trash folder with one rename each."""
    result = {}
    trash = None
    try:
        for relative in items:
            *parents, name = relative.split("/")
            fds = []
            try:
                fds = open_path(home, parents, uid, device)
                parent = fds[-1] if fds else home
                fd = open_dir(parent, name, uid, device)
                try:
                    if contains_any(fd, guarded.get(relative, ())):
                        raise Skip("contains_credentials")
                    info = os.fstat(fd)
                    # Moving a directory to another parent rewrites its "..": it must be writable.
                    if not info.st_mode & stat.S_IWUSR:
                        os.fchmod(fd, stat.S_IMODE(info.st_mode) | stat.S_IRWXU)
                finally:
                    os.close(fd)
                if trash is None:
                    trash = open_path(home, [TRASH], uid, device, create=True)[0]
                os.rename(name, "%d-%s" % (clock(), relative.replace("/", "_")), src_dir_fd=parent,
                          dst_dir_fd=trash)
                result[relative] = "moved"
            except Skip as skip:
                result[relative] = skip.reason
            except OSError as error:
                result[relative] = "failed:" + type(error).__name__
            finally:
                for fd in fds:
                    os.close(fd)
    finally:
        if trash is not None:
            os.close(trash)
    return result


def migration_complete(result):
    return all(not outcome.startswith("failed") for outcome in result.values())


def ensure_links(home, uid, device, scratch_cache, links=LINKS):
    """Creates missing cache links. An existing directory or foreign link is the user's choice."""
    result = {}
    for relative, target in links:
        destination = os.path.join(scratch_cache, target) if target else scratch_cache
        *parents, name = relative.split("/")
        fds = []
        try:
            os.makedirs(destination, mode=0o700, exist_ok=True)
            fds = open_path(home, parents, uid, device, create=True)
            parent = fds[-1] if fds else home
            try:
                os.symlink(destination, name, dir_fd=parent)
                result[relative] = "created"
            except FileExistsError:
                info = os.stat(name, dir_fd=parent, follow_symlinks=False)
                result[relative] = ("present" if stat.S_ISLNK(info.st_mode) and
                                    os.readlink(name, dir_fd=parent) == destination else "kept_user_entry")
        except Skip as skip:
            result[relative] = "parent_" + skip.reason
        except OSError as error:
            result[relative] = "failed:" + type(error).__name__
        finally:
            for fd in fds:
                os.close(fd)
    return result


def _remove_tree(fd, uid, device, deadline, clock, counts, depth):
    """Empties one opened directory; returns True when it is empty. Never follows links."""
    if depth >= MAX_DEPTH:
        counts["errors"] += 1
        return False
    empty = True
    with os.scandir(fd) as entries:
        children = list(entries)
    for entry in children:
        if clock() >= deadline:
            return False
        try:
            info = entry.stat(follow_symlinks=False)
            if not stat.S_ISDIR(info.st_mode):
                os.unlink(entry.name, dir_fd=fd)
                counts["removed"] += 1
                continue
            if info.st_dev != device:
                counts["errors"] += 1
                empty = False
                continue
            child = os.open(entry.name, DIRECTORY, dir_fd=fd)
            try:
                opened = os.fstat(child)
                if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                    empty = False
                    continue
                # Module caches (Go) are read-only by design; the owner may restore write access.
                if opened.st_uid == uid and stat.S_IMODE(opened.st_mode) & stat.S_IRWXU != stat.S_IRWXU:
                    os.fchmod(child, stat.S_IMODE(opened.st_mode) | stat.S_IRWXU)
                done = _remove_tree(child, uid, device, deadline, clock, counts, depth + 1)
            finally:
                os.close(child)
            if not done:
                return False
            os.rmdir(entry.name, dir_fd=fd)
            counts["removed"] += 1
        except OSError:
            counts["errors"] += 1
            empty = False
    return empty


def purge(home, uid, device, seconds=PURGE_SECONDS, clock=time.monotonic):
    """Deletes the trash within a time budget; anything left is retried at the next T3 start."""
    counts = dict(removed=0, errors=0)
    try:
        trash = open_dir(home, TRASH, uid, device)
    except Skip as skip:
        return dict(state=skip.reason, **counts)
    try:
        info = os.fstat(trash)
        if stat.S_IMODE(info.st_mode) & stat.S_IRWXU != stat.S_IRWXU:
            os.fchmod(trash, stat.S_IMODE(info.st_mode) | stat.S_IRWXU)
        done = _remove_tree(trash, uid, device, clock() + seconds, clock, counts, 0)
    finally:
        os.close(trash)
    if done:
        try:
            os.rmdir(TRASH, dir_fd=home)
            return dict(state="purged", **counts)
        except OSError:
            counts["errors"] += 1
    return dict(state="pending", **counts)


def coding_work(workspace, scratch, uid, migrate_first):
    home, device = open_home(workspace, uid)
    try:
        result = {}
        if migrate_first:
            result["migration"] = migrate(home, uid, device)
        result["links"] = ensure_links(home, uid, device, os.path.join(scratch, "cache"))
        result["trash"] = purge(home, uid, device)
        return result
    finally:
        os.close(home)


def drop_privileges(uid, gid):
    os.setgroups([])
    os.setresgid(gid, gid, gid)
    os.setresuid(uid, uid, uid)
    os.umask(0o077)


def as_coding_user(function, uid=CODING_UID, gid=CODING_GID, drop=drop_privileges, seconds=CHILD_SECONDS):
    """Runs function in a forked child with the coding user's credentials; returns its JSON result."""
    read, write = os.pipe()
    pid = os.fork()
    if pid == 0:
        code = 1
        try:
            os.close(read)
            signal.alarm(seconds)
            drop(uid, gid)
            try:
                value = function()
            except Exception as error:  # Only the class name leaves the child.
                value = dict(error=type(error).__name__)
            with os.fdopen(write, "wb") as stream:
                stream.write(json.dumps(value).encode())
            code = 0
        except BaseException:
            pass
        finally:
            os._exit(code)
    os.close(write)
    with os.fdopen(read, "rb") as stream:
        data = stream.read()
    _, status = os.waitpid(pid, 0)
    if not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
        raise RuntimeError("coding_user_step_failed")
    value = json.loads(data)
    if not isinstance(value, dict) or "error" in value:
        raise RuntimeError(value.get("error", "invalid") if isinstance(value, dict) else "invalid")
    return value


def scratch_ready(scratch, layout=SCRATCH_LAYOUT, uid=CODING_UID, root_uid=0):
    problems = []
    for name, owner, mode in layout:
        try:
            info = os.lstat(os.path.join(scratch, name) if name else scratch)
        except OSError:
            problems.append(name or "root")
            continue
        expected = uid if owner else root_uid
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != expected or stat.S_IMODE(info.st_mode) != mode:
            problems.append(name or "root")
    return problems


def read_marker(path, root_uid=0):
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except OSError:
        return False
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != root_uid or info.st_mode & 0o077 or \
                info.st_size > 65536:
            return False
        with os.fdopen(os.dup(fd), "rb") as stream:
            value = json.loads(stream.read())
        return isinstance(value, dict) and value.get("version") == MIGRATION_VERSION
    except (OSError, ValueError):
        return False
    finally:
        os.close(fd)


def write_marker(path, value):
    directory = os.path.dirname(path)
    os.makedirs(directory, mode=0o700, exist_ok=True)
    temporary = os.path.join(directory, ".%s.%d.tmp" % (os.path.basename(path), os.getpid()))
    fd = os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(value, stream, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def prepare(workspace=WORKSPACE, scratch=SCRATCH, marker=MARKER, tmpfiles=TMPFILES, uid=CODING_UID,
            gid=CODING_GID, runner=subprocess.run, drop=drop_privileges, wall=time.time, root_uid=0):
    report = dict(type="StorageTiersPrepare")
    try:
        done = runner(["systemd-tmpfiles", "--create", tmpfiles], capture_output=True, timeout=30)
        if done.returncode:
            report["tmpfiles"] = "failed"
    except (OSError, subprocess.SubprocessError):
        report["tmpfiles"] = "unavailable"
    problems = scratch_ready(scratch, uid=uid, root_uid=root_uid)
    if problems:
        report.update(state="scratch_unavailable", scratch=problems)
        return report
    migrated = read_marker(marker, root_uid)
    try:
        result = as_coding_user(lambda: coding_work(workspace, scratch, uid, not migrated), uid, gid, drop)
    except (OSError, RuntimeError, ValueError) as error:
        report.update(state="coding_user_step_failed",
                      error=str(error) if isinstance(error, RuntimeError) else type(error).__name__)
        return report
    report.update(result)
    if "migration" in result:
        if migration_complete(result["migration"]):
            try:
                write_marker(marker, dict(version=MIGRATION_VERSION, completedAt=wall(), moved=sorted(
                    key for key, value in result["migration"].items() if value == "moved")))
                report["marker"] = "written"
            except OSError as error:
                report["marker"] = "write_failed:" + type(error).__name__
        else:
            report["marker"] = "retry_next_start"
    else:
        report["marker"] = "present"
    report["state"] = "ready"
    return report


def bounded(report):
    text = json.dumps(report, sort_keys=True, separators=(",", ":"))
    if len(text.encode()) > MAX_LOG_BYTES:
        text = json.dumps(dict(type=report.get("type"), state=report.get("state"), truncated=True))
    return text


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if argv != ["prepare"] or os.geteuid() != 0:
        print(json.dumps(dict(type="StorageTiersPrepare", state="refused")), file=sys.stderr)
        return 0
    try:
        report = prepare()
    except Exception as error:  # Never block T3; the reason class is enough to investigate.
        report = dict(type="StorageTiersPrepare", state="failed", error=type(error).__name__)
    print(bounded(report), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
