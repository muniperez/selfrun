#!/usr/bin/env python3
"""Bounded dev environment folder sizes and confirmed folder deletion.

Runs as the unprivileged coding user under a transient systemd unit with CPU, memory and
I/O limits. It never follows symbolic links, never crosses into another mount, walks with
directory file descriptors only, and prints exactly one bounded JSON document to stdout.
"""
import json
import os
import signal
import stat
import sys
import time

SCHEMA_VERSION = 1
POLICY_VERSION = 1
CODING_UID = 2000
DELETABLE_ROOTS = ("repos", "home", "artifacts")
SCAN_SECONDS = 30
DELETE_SECONDS = 60
MAX_ENTRIES = 250_000
MAX_DEPTH = 64
MAX_ROWS = 500
MAX_RESULT_BYTES = 256 * 1024
MAX_HARDLINKS = 100_000
MAX_COMPONENTS = 64
MAX_NAME_BYTES = 255
DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
ERROR_CATEGORIES = ("permission_denied", "vanished", "separate_mount", "io_error", "limit_reached",
                    "name_not_supported", "changed_during_scan")

_cancelled = False


class Refused(Exception):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def _cancel(signum, frame):
    global _cancelled
    _cancelled = True


def valid_component(name):
    if not isinstance(name, str) or name in ("", ".", "..") or "/" in name or "\0" in name:
        return False
    try:
        return len(name.encode("utf-8")) <= MAX_NAME_BYTES
    except UnicodeEncodeError:
        return False


def validate_path(components):
    if not isinstance(components, list) or len(components) > MAX_COMPONENTS or \
            not all(valid_component(name) for name in components):
        raise Refused("invalid_path")
    return components


def deletable_path(components):
    """Only strict descendants of the user data roots; never a root or tool-owned folder."""
    return len(components) >= 2 and components[0] in DELETABLE_ROOTS


def error_category(error):
    if isinstance(error, PermissionError):
        return "permission_denied"
    if isinstance(error, (FileNotFoundError, NotADirectoryError)):
        return "vanished"
    if isinstance(error, OSError) and error.errno == 40:  # ELOOP: replaced by a symbolic link
        return "changed_during_scan"
    return "io_error"


class Budget:
    def __init__(self, seconds, entries=MAX_ENTRIES, clock=time.monotonic):
        self.clock = clock
        self.deadline = clock() + seconds
        self.started = clock()
        self.entries = 0
        self.max_entries = entries
        self.limit = None

    def exhausted(self):
        if self.limit:
            return True
        if _cancelled:
            self.limit = "cancelled"
        elif self.clock() >= self.deadline:
            self.limit = "time"
        elif self.entries >= self.max_entries:
            self.limit = "entries"
        return self.limit is not None

    def elapsed(self):
        return round(self.clock() - self.started, 3)


class Walk:
    """Allocated-size accounting shared across one job; hard links count once."""
    def __init__(self, device, budget):
        self.device = device
        self.budget = budget
        self.links = set()
        self.links_approximate = False
        self.errors = {}

    def note(self, category):
        self.errors[category] = self.errors.get(category, 0) + 1

    def allocated(self, info):
        if not stat.S_ISDIR(info.st_mode) and info.st_nlink > 1:
            key = (info.st_dev, info.st_ino)
            if key in self.links:
                return 0
            if len(self.links) < MAX_HARDLINKS:
                self.links.add(key)
            else:
                self.links_approximate = True
        return info.st_blocks * 512

    def measure(self, fd, info):
        """Returns (bytes, complete, categories) for a directory already opened without following links."""
        total = self.allocated(info)
        complete = True
        categories = set()
        stack = [(fd, 0)]
        owned = []
        try:
            while stack:
                current, depth = stack.pop()
                try:
                    with os.scandir(current) as entries:
                        for entry in entries:
                            if self.budget.exhausted():
                                categories.add("limit_reached")
                                return total, False, categories
                            self.budget.entries += 1
                            try:
                                child = entry.stat(follow_symlinks=False)
                            except OSError as error:
                                categories.add(error_category(error))
                                complete = False
                                continue
                            if not stat.S_ISDIR(child.st_mode):
                                total += self.allocated(child)
                                continue
                            if child.st_dev != self.device:
                                categories.add("separate_mount")
                                complete = False
                                continue
                            if depth + 1 >= MAX_DEPTH:
                                total += self.allocated(child)
                                categories.add("limit_reached")
                                complete = False
                                continue
                            try:
                                opened = os.open(entry.name, DIRECTORY, dir_fd=current)
                            except OSError as error:
                                total += self.allocated(child)
                                categories.add(error_category(error))
                                complete = False
                                continue
                            check = os.fstat(opened)
                            if (check.st_dev, check.st_ino) != (child.st_dev, child.st_ino):
                                os.close(opened)
                                categories.add("changed_during_scan")
                                complete = False
                                continue
                            owned.append(opened)
                            total += self.allocated(check)
                            stack.append((opened, depth + 1))
                except OSError as error:
                    categories.add(error_category(error))
                    complete = False
        finally:
            for opened in owned:
                os.close(opened)
        return total, complete, categories


def open_root(workspace):
    fd = os.open(workspace, DIRECTORY)
    return fd, os.fstat(fd)


def open_chain(root_fd, device, components):
    """Opens each component relative to its parent with O_NOFOLLOW and refuses mount crossings."""
    fds = [root_fd]
    try:
        for name in components:
            try:
                fd = os.open(name, DIRECTORY, dir_fd=fds[-1])
            except NotADirectoryError:
                # O_NOFOLLOW|O_DIRECTORY reports ENOTDIR for a symbolic link on Linux.
                if stat.S_ISLNK(os.stat(name, dir_fd=fds[-1], follow_symlinks=False).st_mode):
                    raise Refused("symbolic_link")
                raise
            fds.append(fd)
            if os.fstat(fd).st_dev != device:
                raise Refused("separate_mount")
        return fds
    except OSError as error:
        for fd in fds[1:]:
            os.close(fd)
        if error.errno == 40:
            raise Refused("symbolic_link") from error
        if isinstance(error, NotADirectoryError):
            raise Refused("not_a_directory") from error
        if isinstance(error, FileNotFoundError):
            raise Refused("not_found") from error
        if isinstance(error, PermissionError):
            raise Refused("permission_denied") from error
        raise
    except Refused:
        for fd in fds[1:]:
            os.close(fd)
        raise


def scan(workspace, components, expect=None, budget=None):
    components = validate_path(components)
    budget = budget or Budget(SCAN_SECONDS)
    root_fd, root = open_root(workspace)
    fds = [root_fd]
    try:
        fds = open_chain(root_fd, root.st_dev, components)
        target = os.fstat(fds[-1])
        if expect and (target.st_dev, target.st_ino) != (expect.get("dev"), expect.get("ino")):
            raise Refused("target_changed")
        walk = Walk(root.st_dev, budget)
        files_here = walk.allocated(target)
        rows = []
        complete = True
        with os.scandir(fds[-1]) as entries:
            children = list(entries)
        for entry in children:
            if budget.exhausted():
                complete = False
                break
            budget.entries += 1
            try:
                info = entry.stat(follow_symlinks=False)
            except OSError as error:
                walk.note(error_category(error))
                complete = False
                continue
            if not stat.S_ISDIR(info.st_mode):
                files_here += walk.allocated(info)
                continue
            selectable = valid_component(entry.name)
            name = entry.name if selectable else entry.name.encode("utf-8", "surrogateescape").decode(
                "utf-8", "replace")
            row = dict(name=name, bytes=None, state="complete", errors=[], selectable=selectable,
                       deletable=False, uid=info.st_uid, dev=info.st_dev, ino=info.st_ino)
            rows.append(row)
            if not selectable:
                row["errors"].append("name_not_supported")
            if info.st_dev != root.st_dev:
                row.update(state="excluded", selectable=False)
                row["errors"].append("separate_mount")
                continue
            try:
                fd = os.open(entry.name, DIRECTORY, dir_fd=fds[-1])
            except OSError as error:
                row.update(state="unreadable", bytes=walk.allocated(info), selectable=False)
                row["errors"].append(error_category(error))
                complete = False
                continue
            try:
                check = os.fstat(fd)
                if (check.st_dev, check.st_ino) != (info.st_dev, info.st_ino):
                    row.update(state="unreadable", selectable=False)
                    row["errors"].append("changed_during_scan")
                    complete = False
                    continue
                size, finished, categories = walk.measure(fd, check)
            finally:
                os.close(fd)
            row["bytes"] = size
            row["errors"] = sorted(set(row["errors"]) | categories)
            if not finished:
                row["state"] = "partial"
                complete = False
            row["deletable"] = (selectable and deletable_path(components + [entry.name]) and
                                info.st_uid == CODING_UID)
        for row in rows:
            for category in row["errors"]:
                walk.note(category)
        # Unknown sizes sort after every measured folder instead of counting as zero.
        rows.sort(key=lambda row: (row["bytes"] is None, -(row["bytes"] or 0), row["name"]))
        known = [row["bytes"] for row in rows if row["bytes"] is not None]
        result = dict(schemaVersion=SCHEMA_VERSION, mode="scan", policyVersion=POLICY_VERSION,
                      path=components, target=dict(dev=target.st_dev, ino=target.st_ino, uid=target.st_uid),
                      complete=complete and budget.limit is None,
                      limitReached=budget.limit, totalBytes=files_here + sum(known),
                      filesHereBytes=files_here, entries=rows[:MAX_ROWS],
                      omittedEntries=max(0, len(rows) - MAX_ROWS),
                      omittedBytes=sum(row["bytes"] or 0 for row in rows[MAX_ROWS:]),
                      scannedEntries=budget.entries, hardLinksApproximate=walk.links_approximate,
                      errors=dict(sorted(walk.errors.items())), elapsedSeconds=budget.elapsed())
        return bound(result)
    finally:
        for fd in fds:
            os.close(fd)


def bound(result):
    while len(json.dumps(result, sort_keys=True).encode()) > MAX_RESULT_BYTES and result["entries"]:
        dropped = result["entries"].pop()
        result["omittedEntries"] += 1
        result["omittedBytes"] += dropped["bytes"] or 0
    return result


def delete(workspace, components, expect, budget=None):
    components = validate_path(components)
    if not deletable_path(components):
        raise Refused("protected_path")
    if not isinstance(expect, dict) or type(expect.get("dev")) is not int or type(expect.get("ino")) is not int:
        raise Refused("target_identity_required")
    budget = budget or Budget(DELETE_SECONDS, entries=sys.maxsize)
    root_fd, root = open_root(workspace)
    fds = [root_fd]
    removed = dict(entries=0, bytes=0)
    errors = {}
    try:
        fds = open_chain(root_fd, root.st_dev, components[:-1])
        parent, name = fds[-1], components[-1]
        try:
            info = os.stat(name, dir_fd=parent, follow_symlinks=False)
        except FileNotFoundError:
            raise Refused("not_found")
        if not stat.S_ISDIR(info.st_mode) or info.st_dev != root.st_dev or \
                (info.st_dev, info.st_ino) != (expect["dev"], expect["ino"]):
            raise Refused("target_changed")
        if info.st_uid != CODING_UID:
            raise Refused("not_owned_by_coding_user")
        try:
            target = os.open(name, DIRECTORY, dir_fd=parent)
        except OSError as error:
            raise Refused("target_changed" if error.errno == 40 else error_category(error)) from error
        try:
            opened = os.fstat(target)
            if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                raise Refused("target_changed")
            _remove_contents(target, root.st_dev, budget, removed, errors, 0)
        finally:
            os.close(target)
        gone = False
        if not errors and budget.limit is None:
            try:
                final = os.stat(name, dir_fd=parent, follow_symlinks=False)
                # rmdir only removes an empty directory; never one that replaced the target.
                if (final.st_dev, final.st_ino) == (info.st_dev, info.st_ino):
                    os.rmdir(name, dir_fd=parent)
                    removed["entries"] += 1
                    removed["bytes"] += info.st_blocks * 512
                    gone = True
                else:
                    errors["changed_during_scan"] = 1
            except OSError as error:
                category = error_category(error)
                errors[category] = errors.get(category, 0) + 1
        if gone:
            outcome = "succeeded"
        elif budget.limit == "cancelled":
            outcome = "cancelled"
        elif removed["entries"]:
            outcome = "partially_deleted"
        else:
            outcome = "failed"
        return dict(schemaVersion=SCHEMA_VERSION, mode="delete", policyVersion=POLICY_VERSION,
                    path=components, outcome=outcome, removedEntries=removed["entries"],
                    removedBytes=removed["bytes"], limitReached=budget.limit,
                    errors=dict(sorted(errors.items())), elapsedSeconds=budget.elapsed())
    finally:
        for fd in fds:
            os.close(fd)


def _remove_contents(fd, device, budget, removed, errors, depth):
    def note(category):
        errors[category] = errors.get(category, 0) + 1
    try:
        with os.scandir(fd) as entries:
            children = list(entries)
    except OSError as error:
        note(error_category(error))
        return
    for entry in children:
        if budget.exhausted():
            return
        budget.entries += 1
        try:
            info = entry.stat(follow_symlinks=False)
        except OSError as error:
            note(error_category(error))
            continue
        try:
            if stat.S_ISDIR(info.st_mode):
                if info.st_dev != device:
                    note("separate_mount")
                    continue
                if depth + 1 >= MAX_DEPTH:
                    note("limit_reached")
                    continue
                child = os.open(entry.name, DIRECTORY, dir_fd=fd)
                try:
                    opened = os.fstat(child)
                    if (opened.st_dev, opened.st_ino) != (info.st_dev, info.st_ino):
                        note("changed_during_scan")
                        continue
                    before = len(errors), sum(errors.values())
                    _remove_contents(child, device, budget, removed, errors, depth + 1)
                    if (len(errors), sum(errors.values())) != before or budget.limit:
                        continue
                finally:
                    os.close(child)
                os.rmdir(entry.name, dir_fd=fd)
            else:
                os.unlink(entry.name, dir_fd=fd)
            removed["entries"] += 1
            removed["bytes"] += info.st_blocks * 512
        except OSError as error:
            note(error_category(error))


def run(request):
    mode = request.get("mode")
    workspace = request.get("workspace")
    if not isinstance(workspace, str) or not workspace.startswith("/"):
        raise Refused("invalid_workspace")
    if mode == "scan":
        return scan(workspace, request.get("path"), request.get("expect"))
    if mode == "delete":
        return delete(workspace, request.get("path"), request.get("expect"))
    raise Refused("unknown_mode")


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    signal.signal(signal.SIGTERM, _cancel)
    signal.signal(signal.SIGINT, _cancel)
    try:
        if os.geteuid() == 0:
            raise Refused("refuse_root")
        if len(argv) != 1 or len(argv[0]) > 65536:
            raise Refused("invalid_request")
        result = run(json.loads(argv[0]))
    except Refused as error:
        result = dict(schemaVersion=SCHEMA_VERSION, refused=error.code)
    except (OSError, ValueError) as error:
        result = dict(schemaVersion=SCHEMA_VERSION, refused=error_category(error)
                      if isinstance(error, OSError) else "invalid_request")
    sys.stdout.write(json.dumps(result, sort_keys=True) + "\n")
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
