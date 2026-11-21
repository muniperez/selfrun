#!/usr/bin/env python3
"""Scratch bind-mount helper checks with synthetic trees, real Git and a fake kernel layer.

No root or real mounts: FakeSystem records mounts in a synthetic mountinfo, maps chown and the
scratch device, and supplies /proc and statvfs views."""
import errno
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import scratch_mounts as sm
import disk_usage as du
import runtime as rt

ARTIFACTS = HERE.parents[1] / ".local/artifacts/scratch-mounts"
ARTIFACTS.mkdir(parents=True, exist_ok=True)
SCRATCH_DEV = -7
OTHER_DEV = -9
XATTR = "user.minicloud-test-uid"
GIT_ENV = dict(os.environ, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_NOSYSTEM="1",
               GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@example.invalid",
               GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@example.invalid")


def key(info):
    return info.st_dev, info.st_ino


def escape(path):
    return "".join("\\%03o" % ord(char) if char in " \t\n\\" else char for char in path)


class FakeSystem(sm.System):
    def __init__(self, root, workspace, scratch, trusted):
        super().__init__()
        self.root = str(root)
        self.workspace, self.scratch = str(workspace), str(scratch)
        self.trusted = {key(os.stat(path)) for path in trusted}
        self.owners, self.devices, self.mount_keys = {}, {}, {}
        self.entries = [dict(id=1, parent=0, device="0:1", root="/", mountPoint="/"),
                        dict(id=2, parent=1, device="0:2", root="/", mountPoint=self.workspace)]
        self.next_id = 100
        self.binds, self.umounts, self.hardened = [], [], []
        self.busy = {}
        self.procs, self.unreadable = set(), 0
        self.ws_percent = lambda: 10.0

    def scratch_keys(self):
        keys = set()
        if os.path.isdir(self.scratch):
            keys.add(key(os.lstat(self.scratch)))
            for top, dirs, files in os.walk(self.scratch):
                for name in dirs + files:
                    keys.add(key(os.lstat(os.path.join(top, name))))
        return keys

    def dev(self, info):
        if key(info) in self.devices:
            return self.devices[key(info)]
        if key(info) in self.mount_keys.values() or key(info) in self.scratch_keys():
            return SCRATCH_DEV
        return info.st_dev

    def uid(self, info):
        # Ownership lives in an xattr on the inode itself, so reused inode numbers start clean.
        if key(info) in self.owners:
            return self.owners[key(info)]
        for top, dirs, files in os.walk(self.root):
            for name in [".", *dirs, *files]:
                path = os.path.join(top, name)
                if key(os.lstat(path)) == key(info) and not os.path.islink(path):
                    try:
                        return int(os.getxattr(path, XATTR, follow_symlinks=False))
                    except OSError:
                        return info.st_uid
        return info.st_uid

    def trusted_dir(self, info):
        return key(info) in self.trusted

    def fchown(self, fd, uid, gid):
        os.setxattr(fd, XATTR, str(uid).encode())

    def lchown(self, dir_fd, name, uid, gid):
        pass  # Symbolic link ownership is never checked.

    def own(self, path, uid=0):
        os.setxattr(path, XATTR, str(uid).encode(), follow_symlinks=False)

    def mnt_id(self, fd):
        target = key(os.fstat(fd))
        ids = [ident for ident, value in self.mount_keys.items() if value == target]
        return max(ids) if ids else 1

    def mountinfo(self):
        lines = ["%d %d %s %s %s rw - ext4 x rw" % (e["id"], e["parent"], e["device"], escape(e["root"]),
                                                     escape(e["mountPoint"])) for e in self.entries]
        return sm.parse_mountinfo(os.fsencode("\n".join(lines)))

    def bind(self, source_fd, target_fd):
        source = os.readlink("/proc/self/fd/%d" % source_fd)
        target = os.readlink("/proc/self/fd/%d" % target_fd)
        container = sm.containing_mount(self.entries, source)
        self.next_id += 1
        self.entries.append(dict(id=self.next_id, parent=sm.containing_mount(self.entries, target)["id"],
                                 device=container["device"], root=sm.fs_relative(self.entries, source),
                                 mountPoint=target))
        self.mount_keys[self.next_id] = key(os.fstat(target_fd))
        self.binds.append((source, target))

    def harden(self, fd):
        self.hardened.append(os.readlink("/proc/self/fd/%d" % fd))

    def umount(self, parent_fd, name, detach=False):
        path = os.path.join(os.readlink("/proc/self/fd/%d" % parent_fd), name)
        if not detach and self.busy.get(path, 0) > 0:
            self.busy[path] -= 1
            raise OSError(errno.EBUSY, "busy")
        matches = [entry for entry in self.entries if entry["mountPoint"] == path]
        if not matches:
            raise OSError(errno.EINVAL, "not mounted")
        self.entries.remove(matches[-1])
        self.mount_keys.pop(matches[-1]["id"], None)
        self.umounts.append((path, detach))

    def statvfs(self, fd):
        if key(os.fstat(fd)) in self.scratch_keys():
            return SimpleNamespace(f_blocks=1 << 40, f_bfree=1 << 40, f_bavail=1 << 40, f_frsize=1)
        used = self.ws_percent()
        return SimpleNamespace(f_blocks=1000, f_bfree=1000 - used * 10, f_bavail=1000 - used * 10,
                               f_frsize=1)

    def processes(self):
        return set(self.procs), self.unreadable


class ScratchMountTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="case-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.ws = self.root / "workspace"
        self.repos = self.ws / "repos"
        self.repos.mkdir(parents=True)
        self.outside = self.root / "outside"
        self.outside.mkdir()
        (self.root / "var").mkdir()
        self.scratch = self.root / "var/scratch"
        trusted = [Path("/"), *Path(self.root / "var").parents, self.root / "var"]
        self.sys = FakeSystem(self.root, self.ws, self.scratch, [p for p in trusted if p.exists()])
        self.events = []
        self.helper = self.make_helper()

    def make_helper(self, **kwargs):
        options = dict(workspace=str(self.ws), scratch=str(self.scratch), run_dir=str(self.root / "run"),
                       uid=os.getuid(), gid=os.getgid(), system=self.sys, quiet=0,
                       log=lambda event, **fields: self.events.append((event, fields)))
        options.update(kwargs)
        return sm.Helper(**options)

    def repo(self, name, ignore, files=(), tracked=()):
        path = self.repos / name
        path.mkdir(parents=True)
        subprocess.run(["git", "init", "-q", str(path)], check=True, env=GIT_ENV)
        (path / ".gitignore").write_text("".join(line + "\n" for line in ignore))
        for item in (*files, *tracked):
            (path / item).parent.mkdir(parents=True, exist_ok=True)
            (path / item).write_text("x")
        if tracked:
            subprocess.run(["git", "-C", str(path), "add", "-f", *tracked], check=True, env=GIT_ENV)
            subprocess.run(["git", "-C", str(path), "commit", "-qm", "init"], check=True, env=GIT_ENV)
        return path

    def apply(self, helper=None):
        status = (helper or self.helper).apply()
        self.assertEqual("active", status["state"], status)
        return status

    def mounted(self):
        return {e["mountPoint"]: e["root"] for e in self.sys.entries
                if e["mountPoint"].startswith(str(self.repos) + "/")}

    def leaf(self, path):
        return Path(self.mounted()[str(path)])

    def tree(self, path):
        result = {}
        for top, dirs, files in os.walk(path):
            for name in sorted(dirs + files):
                full = os.path.join(top, name)
                info = os.lstat(full)
                content = os.readlink(full) if os.path.islink(full) else \
                    Path(full).read_bytes() if os.path.isfile(full) else None
                result[os.path.relpath(full, path)] = (info.st_mode, info.st_mtime_ns, content)
        return result

    def deletion_request(self, path):
        info = path.stat()
        return dict(workspace=str(self.ws), path=["repos", *path.relative_to(self.repos).parts],
                    expect=dict(dev=info.st_dev, ino=info.st_ino), jobId="checkout-delete-1",
                    vmId="fixture-vm", generation=3)

    def test_confirmed_checkout_delete_unmounts_only_owned_target_and_serializes_remounts(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        other = self.repo("other", ["node_modules/"], files=["package.json"],
                          tracked=["src/keep.txt"])
        self.apply()
        other_mount = str(other / "node_modules")
        request = self.deletion_request(app)
        prepared = self.helper.prepare_delete(request)
        self.assertTrue(prepared["ready"], prepared)
        self.assertEqual(1, prepared["unmountedMounts"])
        self.assertEqual([(str(app / "node_modules"), False)], self.sys.umounts)
        self.apply(self.make_helper())
        self.assertNotIn(str(app / "node_modules"), self.mounted())
        self.assertIn(other_mount, self.mounted())
        self.assertFalse(self.helper.finish_delete(request)["released"])
        with patch.object(du, "CODING_UID", os.getuid()):
            result = du.delete(str(self.ws), request["path"], request["expect"], chain=prepared["chain"])
        self.assertEqual("succeeded", result["outcome"])
        self.assertTrue(self.helper.finish_delete(request)["released"])
        self.assertEqual("x", (other / "src/keep.txt").read_text())
        self.assertIn(other_mount, self.mounted())
        self.assertFalse(app.exists())

    def test_checkout_delete_accepts_empty_trusted_trash_after_normal_relocation(self):
        app = self.repo("app", ["node_modules/"], files=["package.json", "node_modules/cache.js"])
        self.apply()
        trash = self.ws / sm.TRASH
        trash.mkdir(mode=0o700, exist_ok=True)
        self.sys.own(trash)
        request = self.deletion_request(app)
        prepared = self.helper.prepare_delete(request)
        self.assertTrue(prepared["ready"], prepared)
        self.assertEqual([], list(trash.iterdir()))
        with patch.object(du, "CODING_UID", os.getuid()):
            result = du.delete(str(self.ws), request["path"], request["expect"], chain=prepared["chain"])
        self.assertEqual("succeeded", result["outcome"])
        self.assertTrue(self.helper.finish_delete(request)["released"])
        self.assertTrue(trash.is_dir())

    def test_checkout_delete_inspects_only_related_trusted_relocation_state(self):
        app = self.repo("app", [], files=["package.json"])
        other = self.repo("other", [], tracked=["keep.txt"])
        self.apply()
        trash = self.ws / sm.TRASH
        trash.mkdir(mode=0o700)
        self.sys.own(trash)
        token = trash / ("r-" + "c" * 32)
        token.mkdir(mode=0o700)
        self.sys.own(token)
        (token / "data").mkdir()
        (token / "data/keep").write_text("unrelated volume copy")
        journal = dict(phase="copying", repoId=sm.repo_id(("other",), other.stat().st_ino),
                       target=["other", "node_modules"], leaf=["node_modules"],
                       staging=".staging-" + "d" * 32)
        request = self.deletion_request(app)
        for phase in ("copying", "unsupported"):
            journal["phase"] = phase
            (token / "journal.json").write_text(json.dumps(journal))
            self.sys.own(token / "journal.json")
            before = self.tree(trash)
            self.assertTrue(self.helper.prepare_delete(request)["ready"])
            self.assertEqual(before, self.tree(trash))
        journal.update(phase="copying", repoId=sm.repo_id(("app",), app.stat().st_ino),
                       target=["app", "node_modules"])
        (token / "journal.json").write_text(json.dumps(journal))
        before = self.tree(trash)
        self.assertEqual("scratch_delete_unsupported", self.helper.prepare_delete(request)["refused"])
        self.assertEqual(before, self.tree(trash))
        self.assertTrue((other / "keep.txt").exists())
        self.assertTrue((app / "package.json").exists())

    def test_ambiguous_checkout_launch_reconciles_marker_before_reporting_success(self):
        for mismatched in (False, True):
            with self.subTest(mismatched=mismatched):
                app = self.repo("app-" + str(mismatched), ["node_modules/"], files=["package.json"])
                self.apply()
                request = self.deletion_request(app)
                request["jobId"] += "-" + str(mismatched)

                def launch(unit, payload, output):
                    prepared = self.helper.prepare_delete(payload)
                    self.assertTrue(prepared["ready"], prepared)
                    with patch.object(du, "CODING_UID", os.getuid()):
                        result = du.delete(str(self.ws), payload["path"], payload["expect"],
                                           chain=prepared["chain"])
                    Path(output).write_text(json.dumps(result))
                    raise rt.Blocked("Synthetic lost launch acknowledgement")

                def finish(record):
                    reconciliation = dict(request)
                    if mismatched:
                        reconciliation["jobId"] = "different-job"
                    return self.helper.finish_delete(reconciliation)

                adapter = SimpleNamespace(filesystem_jobs_supported=lambda: True,
                                          filesystem_launch=launch, filesystem_running=lambda unit: False,
                                          filesystem_finish=finish)
                config = dict(vmId=request["vmId"], generation=request["generation"], kind="ephemeral",
                              uid=2000, gid=2000, workspace=str(self.ws), ignoredDataAcknowledged=True,
                              repositories=[])
                runtime = rt.Runtime(config, self.root / ("state-" + str(mismatched)), adapter=adapter,
                                     git=rt.Git(allow_local=True, verification_dir=self.root / "verify"),
                                     resources=SimpleNamespace(invalidate=lambda: None))
                runtime.s["phase"] = "Ready"
                result = runtime.handle(dict(request, action="fs-job-start", mode="delete",
                                             operationId="fixture-delete"))["result"]
                record = json.loads((runtime.root / "fsjobs" / (request["jobId"] + ".json")).read_text())
                self.assertTrue(record["launchAmbiguous"])
                self.assertNotIn("preparation", record)
                self.assertEqual(mismatched, result["remountSuppressed"])
                self.assertEqual("partially_deleted" if mismatched else "succeeded", result["outcome"])
                self.assertEqual(mismatched, os.path.exists(self.helper.delete_marker(request)))
                if not mismatched:
                    fresh = self.repo(app.name, ["node_modules/"], files=["package.json"])
                    self.apply()
                    self.assertIn(str(fresh / "node_modules"), self.mounted())

    def test_checkout_delete_preflight_denies_active_unreadable_busy_and_foreign_mounts(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        self.apply()
        request = self.deletion_request(app)
        for unreadable, procs in ((0, {str(app / "package.json")}), (1, set())):
            self.sys.unreadable, self.sys.procs = unreadable, procs
            self.assertEqual("checkout_in_use", self.helper.prepare_delete(request)["refused"])
        self.sys.unreadable, self.sys.procs = 0, set()
        self.sys.busy[str(app / "node_modules")] = 9
        with patch.object(sm.time, "sleep"):
            self.assertEqual("mount_busy", self.helper.prepare_delete(request)["refused"])
        self.assertEqual([], self.sys.umounts)
        self.sys.busy[str(app / "node_modules")] = 0
        source = self.leaf(app / "node_modules")
        self.sys.own(source, os.getuid() + 1)
        self.assertEqual("scratch_delete_unsupported", self.helper.prepare_delete(request)["refused"])
        self.sys.own(source, os.getuid())
        self.sys.entries.append(dict(id=999, parent=2, device="0:2", root="/unrelated",
                                     mountPoint=str(app / "foreign")))
        self.assertEqual("separate_mount", self.helper.prepare_delete(request)["refused"])
        self.assertTrue((app / "package.json").exists())
        self.assertEqual([], self.sys.umounts)

    def test_checkout_delete_cancellation_before_and_after_unmount_fails_closed(self):
        app = self.repo("app", ["node_modules/", "dist/"], files=["package.json"])
        (app / "dist").mkdir()
        self.apply()
        request = self.deletion_request(app)
        stopping = self.make_helper(stop=lambda: True)
        self.assertEqual("cancelled", stopping.prepare_delete(request)["refused"])
        self.assertEqual([], self.sys.umounts)
        interrupted = self.make_helper(stop=lambda: bool(self.sys.umounts))
        result = interrupted.prepare_delete(request)
        self.assertEqual(("cancelled", 0, 1, True),
                         (result["outcome"], result["removedEntries"], result["unmountedMounts"],
                          result["remountSuppressed"]))
        count = len(self.sys.binds)
        self.apply(self.make_helper())
        self.assertEqual(count, len(self.sys.binds))
        self.assertTrue((app / "package.json").exists())
        self.assertFalse(self.helper.finish_delete(request)["released"])
        shutil.rmtree(app / ".git")  # A cancelled coding job may already have removed Git metadata.
        retry = dict(request, jobId="checkout-delete-retry")
        prepared = self.helper.prepare_delete(retry)
        self.assertTrue(prepared["ready"], prepared)
        with patch.object(du, "CODING_UID", os.getuid()):
            deleted = du.delete(str(self.ws), retry["path"], retry["expect"], chain=prepared["chain"])
        self.assertEqual("succeeded", deleted["outcome"])
        self.assertTrue(self.helper.finish_delete(retry)["released"])

    def test_checkout_delete_identity_changes_keep_inhibition_and_preserve_moved_tree(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        self.apply()
        request = self.deletion_request(app)
        wrong = dict(request, expect=dict(dev=request["expect"]["dev"], ino=0))
        self.assertEqual("target_changed", self.helper.prepare_delete(wrong)["refused"])
        original = self.sys.umount

        def move_after_unmount(*args, **kwargs):
            original(*args, **kwargs)
            app.rename(self.repos / "moved")
            app.mkdir()
            (app / "replacement").write_text("keep")

        with patch.object(self.sys, "umount", side_effect=move_after_unmount):
            result = self.helper.prepare_delete(request)
        self.assertEqual("failed", result["outcome"])
        self.assertEqual({"target_changed": 1}, result["errors"])
        self.assertEqual(1, result["unmountedMounts"])
        self.assertFalse(self.helper.finish_delete(request)["released"])
        self.assertTrue((self.repos / "moved/package.json").exists())
        self.assertEqual("keep", (app / "replacement").read_text())
        binds = len(self.sys.binds)
        self.apply(self.make_helper())
        self.assertEqual(binds, len(self.sys.binds))
        replacement = self.deletion_request(app)
        self.assertEqual("target_changed", self.helper.prepare_delete(replacement)["refused"])
        (app / "replacement").unlink()
        app.rmdir()
        (self.repos / "moved").rename(app)
        for stale in (dict(request, generation=4), dict(request, vmId="other-vm")):
            self.assertEqual("target_changed", self.helper.prepare_delete(stale)["refused"])
        self.assertTrue((app / "package.json").exists())

    def test_ignored_outputs_mount_from_scratch_and_other_dirs_stay_untouched(self):
        app = self.repo("my app", ["node_modules/", "bin/", "obj/", "dist", "build/", "logs/"],
                        files=["package.json", "src/App.csproj"], tracked=["build/keep.txt"])
        (app / "node_modules").mkdir()
        (app / "out").mkdir()  # Allowlisted but not ignored.
        (app / "logs").mkdir()  # Ignored but not allowlisted.
        (app / "dist").symlink_to(self.outside, target_is_directory=True)
        status = self.apply()

        mounted = self.mounted()
        expected = {app / "node_modules", app / "src/bin", app / "src/obj"}
        self.assertEqual({str(path) for path in expected}, set(mounted))
        for path in expected:
            leaf = self.leaf(path)
            self.assertTrue(sm.under(str(leaf), str(self.scratch / "repos")))
            self.assertTrue(leaf.is_dir() and path.is_dir())
            self.assertEqual(os.getuid(), self.sys.uid(leaf.stat()))
            self.assertEqual(0, self.sys.uid(leaf.parent.stat()))
        self.assertEqual("x", (app / "build/keep.txt").read_text())
        self.assertTrue((app / "dist").is_symlink())
        self.assertEqual([], os.listdir(self.outside))
        self.assertEqual({"mounted": 1, "created": 2}, status["counts"])
        self.assertEqual([str(self.scratch / "repos")], self.sys.hardened)

        # A second pass is idempotent.
        self.sys.binds.clear()
        self.apply()
        self.assertEqual([], self.sys.binds)

    def test_foreign_owner_other_device_and_unignored_projects_are_skipped(self):
        app = self.repo("app", ["/node_modules/", "target/", "packages/*/node_modules/"],
                        files=["package.json", "Cargo.toml", "packages/web/package.json",
                               "tools/cli/package.json"])
        (app / "node_modules").mkdir()
        self.sys.own(app / "node_modules")
        (app / "target").mkdir()
        self.sys.devices[key((app / "target").stat())] = OTHER_DEV
        status = self.apply()

        # The nested project gets its expected output; tools/cli/node_modules is not ignored.
        self.assertEqual({str(app / "packages/web/node_modules")}, set(self.mounted()))
        self.assertFalse((app / "tools/cli/node_modules").exists())
        skipped = {item["path"]: item["reason"] for item in status["skipped"]}
        self.assertEqual({"app/node_modules": "foreign_owner", "app/target": "separate_mount"}, skipped)

    def test_repository_ids_and_relative_paths_are_validated(self):
        first = sm.repo_id(("org", "app"), 11)
        self.assertEqual(first, sm.repo_id(("org", "app"), 11))
        self.assertNotEqual(first, sm.repo_id(("org", "app"), 12))
        self.assertNotEqual(first, sm.repo_id(("other", "app"), 11))
        for components in (("app",), (".hidden",), ("we ird/../",), ("x" * 200,)):
            self.assertTrue(sm.REPO_ID.fullmatch(sm.repo_id(components, 1)), components)
        for relative in ((), ("..",), (".",), ("a/b",), (".git",), ("src", ".git", "x"), ("",),
                         (".staging-0",), "node_modules", ["ok", "a\0b"]):
            self.assertFalse(sm.valid_relative(relative), relative)
        self.assertTrue(sm.valid_relative(("src", "node_modules")))
        self.helper.scratch_fd = os.open(self.root, sm.DIRECTORY)
        self.addCleanup(os.close, self.helper.scratch_fd)
        for identifier, relative in (("../x-0123456789abcdef", ("bin",)), ("app", ("bin",)),
                                     (first, ("..", "bin"))):
            with self.assertRaises(sm.Unsafe):
                self.helper.open_leaf(identifier, relative, parents=True, leaf=True)

    def test_manifests_map_to_expected_output_directories(self):
        cases = [
            ({"package.json"}, set(), False, {"node_modules"}),
            ({"App.csproj"}, set(), False, {"bin", "obj"}),
            ({"Lib.fsproj"}, set(), False, {"bin", "obj"}),
            ({"Cargo.toml"}, set(), False, {"target"}),
            ({"Cargo.toml"}, {"cargo"}, False, set()),
            ({"pyproject.toml"}, set(), False, {".venv"}),
            ({"requirements-dev.txt"}, set(), True, {".venv"}),
            ({"requirements.txt"}, set(), False, set()),
            ({"build.gradle.kts"}, set(), False, {"build", ".gradle"}),
            ({"build.gradle"}, {"gradle"}, False, {"build"}),
            ({"next.config.mjs", "package.json"}, set(), False, {"node_modules", ".next"}),
            ({"README.md"}, set(), True, set()),
        ]
        for files, inherited, root, expected in cases:
            self.assertEqual(expected, sm.expected_outputs(files, frozenset(inherited), root), files)

        app = self.repo("rust", ["target/"], files=["Cargo.toml", "crates/a/Cargo.toml"])
        self.apply()
        self.assertEqual({str(app / "target")}, set(self.mounted()))

    def test_existing_output_relocates_with_contents_links_and_times_preserved(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        modules = app / "node_modules"
        (modules / "pkg/lib").mkdir(parents=True)
        (modules / "pkg/lib/index.js").write_text("module.exports = 1\n")
        (modules / ".bin").mkdir()
        (modules / ".bin/tool").symlink_to("../pkg/lib/index.js")
        os.link(modules / "pkg/lib/index.js", modules / "pkg/alias.js")
        os.chmod(modules / "pkg/lib/index.js", 0o751)
        os.utime(modules / "pkg/lib", ns=(1, 1_000_000_000))
        os.utime(modules / ".bin/tool", ns=(1, 2_000_000_000), follow_symlinks=False)
        before = self.tree(modules)
        identity = key(modules.stat())
        status = self.apply()

        self.assertEqual([{"path": "app/node_modules"}], status["relocated"])
        leaf = self.leaf(modules)
        self.assertEqual(before, self.tree(leaf))
        self.assertEqual(os.stat(leaf / "pkg/lib/index.js").st_ino, os.stat(leaf / "pkg/alias.js").st_ino)
        self.assertEqual([], os.listdir(modules))  # The volume copy under the mount is gone.
        self.assertNotEqual(identity, key(modules.stat()))
        self.assertFalse((self.ws / sm.TRASH).exists())
        self.assertEqual([], [name for name in os.listdir(leaf.parent) if name.startswith(".staging-")])

    def test_relocation_waits_for_quiet_unused_trees(self):
        app = self.repo("app", ["node_modules/", "dist/"], files=["package.json"])
        (app / "node_modules").mkdir()
        (app / "node_modules/a.js").write_text("a")
        (app / "dist").mkdir()
        self.sys.procs = {str(app / "node_modules/a.js"), str(app / "dist")}
        status = self.apply()
        self.assertEqual({}, self.mounted())
        self.assertEqual({"app/node_modules": "in_use", "app/dist": "in_use"},
                         {item["path"]: item["reason"] for item in status["deferred"]})
        self.assertEqual("a", (app / "node_modules/a.js").read_text())

        self.sys.procs, self.sys.unreadable = set(), 1  # An unreadable process fails closed.
        self.apply()
        self.assertEqual({}, self.mounted())

        self.sys.unreadable = 0
        status = self.apply(self.make_helper(quiet=3600))
        self.assertEqual([{"path": "app/node_modules", "reason": "not_quiet"}], status["deferred"])
        self.assertEqual({str(app / "dist")}, set(self.mounted()))

        self.apply()
        self.assertEqual({str(app / "node_modules"), str(app / "dist")}, set(self.mounted()))
        self.assertEqual("a", (self.leaf(app / "node_modules") / "a.js").read_text())

    def test_changes_during_copy_roll_back_without_loss(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        (app / "node_modules").mkdir()
        (app / "node_modules/a.js").write_text("a")
        original = self.helper.copy_tree

        def racing_copy(*args):
            original(*args)
            (app / "node_modules/b.js").write_text("written during copy")
        self.helper.copy_tree = racing_copy
        status = self.apply()
        self.assertEqual([{"path": "app/node_modules", "reason": "changed"}], status["deferred"])
        self.assertEqual({}, self.mounted())
        self.assertEqual({"a.js", "b.js"}, set(os.listdir(app / "node_modules")))
        self.assertFalse((self.ws / sm.TRASH).exists())
        leaves = list((self.scratch / "repos").glob("*/node_modules")) + \
            list((self.scratch / "repos").glob("*/.staging-*"))
        self.assertEqual([], leaves)

    def crash_state(self, phase, data=None, staging=False, leaf=False):
        """An interrupted relocation journal for app/node_modules, as a crash would leave it."""
        app = self.repos / "app"
        repo = sm.Repo(("app",), app.stat().st_ino)
        trash = self.ws / sm.TRASH
        trash.mkdir(mode=0o700, exist_ok=True)
        self.sys.own(trash)
        token = trash / ("r-" + "a" * 32)
        token.mkdir()
        staging_name = ".staging-" + "b" * 32
        journal = dict(version=1, phase=phase, repoId=repo.id, leaf=["node_modules"],
                       target=["app", "node_modules"], staging=staging_name)
        (token / "journal.json").write_text(json.dumps(journal))
        self.sys.own(token / "journal.json")
        if data is not None:
            (token / "data").mkdir()
            (token / "data/a.js").write_text(data)
        base = self.scratch / "repos" / repo.id
        if staging or leaf:
            base.mkdir(parents=True)
            for path in (self.scratch, self.scratch / "repos", base):
                self.sys.own(path)
        if staging:
            (base / staging_name).mkdir()
            (base / staging_name / "partial").write_text("p")
        if leaf:
            (base / "node_modules").mkdir()
            (base / "node_modules/a.js").write_text("copy")
        return app, token

    def test_crash_recovery_restores_or_completes_each_phase(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        (app / "node_modules").mkdir()
        (app / "node_modules/a.js").write_text("original")
        self.crash_state("copying", staging=True)
        self.apply()
        self.assertEqual("original", (self.leaf(app / "node_modules") / "a.js").read_text())
        self.assertFalse((self.ws / sm.TRASH).exists())
        self.assertIn("relocation-rolled-back", [event for event, _ in self.events])

    def test_crash_after_swap_puts_the_original_back(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        self.crash_state("swapping", data="moved aside", leaf=True)
        self.apply(self.make_helper(quiet=3600))  # Keep the restored copy on the volume.
        self.assertEqual("moved aside", (app / "node_modules/a.js").read_text())
        self.assertEqual({}, self.mounted())
        self.assertFalse((self.ws / sm.TRASH).exists())
        self.assertEqual([], list((self.scratch / "repos").glob("*/node_modules")))

    def test_crash_recovery_never_overwrites_new_content(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        (app / "node_modules").mkdir()
        (app / "node_modules/new.js").write_text("new")
        _, token = self.crash_state("swapping", data="moved aside", leaf=True)
        status = self.apply(self.make_helper(quiet=3600))
        self.assertEqual("moved aside", (token / "data/a.js").read_text())
        self.assertEqual("new", (app / "node_modules/new.js").read_text())
        self.assertEqual("restore_conflict", status["conflict"][0]["reason"])

    def test_crash_after_mount_finishes_by_deleting_the_moved_copy(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        (app / "node_modules").mkdir()
        _, token = self.crash_state("swapping", data="moved aside", leaf=True)
        leaf = self.scratch / "repos" / sm.Repo(("app",), app.stat().st_ino).id
        self.sys.entries.append(dict(id=50, parent=2, device="0:1", root=str(leaf / "node_modules"),
                                     mountPoint=str(app / "node_modules")))
        self.sys.mount_keys[50] = key((app / "node_modules").stat())
        self.apply()
        self.assertFalse(token.exists())
        self.assertEqual("copy", (leaf / "node_modules/a.js").read_text())

    def test_unjournaled_trash_data_is_never_deleted(self):
        self.repo("app", ["node_modules/"], files=["package.json"])
        _, token = self.crash_state("swapping", data="keep")
        (token / "journal.json").unlink()
        status = self.apply()
        self.assertEqual("keep", (token / "data/a.js").read_text())
        self.assertEqual("journal_missing", status["conflict"][0]["reason"])

    def test_restored_user_owned_trash_is_set_aside_without_acting_on_its_journals(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        (app / "node_modules").mkdir()
        _, token = self.crash_state("mounted", data="restored backup copy")
        self.sys.own(token.parent, 2000)
        status = self.apply()
        aside = list(self.ws.glob(sm.TRASH + ".untrusted-*"))
        self.assertEqual(1, len(aside))
        self.assertEqual("restored backup copy", (aside[0] / token.name / "data/a.js").read_text())
        self.assertEqual("trash_owner", status["conflict"][0]["reason"])
        self.assertIn(str(app / "node_modules"), self.mounted())

    def test_removed_mount_point_is_recreated_as_a_fresh_mount(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        self.apply()
        leaf = self.leaf(app / "node_modules")
        (leaf / "stale.js").write_text("old build")
        # The mount disappeared (for example lazily detached) and the directory was removed.
        entry = next(e for e in self.sys.entries if e["mountPoint"] == str(app / "node_modules"))
        self.sys.entries.remove(entry)
        self.sys.mount_keys.pop(entry["id"])
        os.rmdir(app / "node_modules")
        status = self.apply()
        self.assertEqual([{"path": "app/node_modules"}], status["recreated"])
        self.assertEqual(str(leaf), self.mounted()[str(app / "node_modules")])
        self.assertEqual([], os.listdir(leaf))

    def test_deleted_checkout_releases_its_mounts_so_it_can_be_removed(self):
        gone = self.repo("gone", ["node_modules/"], files=["package.json"])
        kept = self.repo("kept", ["node_modules/"], files=["package.json"])
        self.apply()
        leaf = self.leaf(gone / "node_modules")
        # rm -rf removes everything except the busy mount point.
        shutil.rmtree(gone / ".git")
        for name in (".gitignore", "package.json"):
            (gone / name).unlink()
        self.sys.busy[str(gone / "node_modules")] = 5
        sm.time.sleep, sleep = (lambda seconds: None), sm.time.sleep
        try:
            status = self.apply()
            self.assertEqual({str(gone / "node_modules"), str(kept / "node_modules")}, set(self.mounted()))
            self.assertEqual([{"path": "gone/node_modules", "reason": "busy"}], status["deferred"])
            self.sys.busy[str(gone / "node_modules")] = 0
            status = self.apply()
        finally:
            sm.time.sleep = sleep
        self.assertEqual({str(kept / "node_modules")}, set(self.mounted()))
        self.assertEqual([{"path": "gone/node_modules", "reason": "repository_gone"}], status["unmounted"])
        self.assertFalse(leaf.exists())
        shutil.rmtree(gone)

    def test_unmount_all_is_deepest_first_with_lazy_fallback_and_suspends(self):
        app = self.repo("app", ["node_modules/", "packages/*/node_modules/"],
                        files=["package.json", "packages/a/package.json"])
        self.apply()
        self.assertEqual(2, len(self.mounted()))
        shallow = str(app / "node_modules")
        self.sys.busy[shallow] = 99
        sm.time.sleep, sleep = (lambda seconds: None), sm.time.sleep
        try:
            report = self.helper.unmount_all()
        finally:
            sm.time.sleep = sleep
        self.assertEqual([(str(app / "packages/a/node_modules"), False), (shallow, True)], self.sys.umounts)
        self.assertEqual(dict(unmounted=1, lazy=1, failed=0, lazyPaths=["app/node_modules"]),
                         {name: report[name] for name in ("unmounted", "lazy", "failed", "lazyPaths")})
        self.assertEqual({}, self.mounted())
        self.assertEqual("suspended", self.helper.apply()["state"])
        self.helper.set_suspended(False)
        self.apply()
        self.assertEqual(2, len(self.mounted()))

    def test_pressure_evicts_least_recent_ignored_outputs_of_idle_repositories_only(self):
        app = self.repo("app", ["dist/", "coverage/", "logs/", "build/"], tracked=["build/keep.txt"])
        busy = self.repo("busy", ["dist/"])
        for path, stamp in ((app / "dist", 1_000), (app / "coverage", 2_000), (app / "logs", 1),
                            (busy / "dist", 1)):
            path.mkdir()
            (path / "f").write_text("data")
            os.utime(path / "f", (stamp, stamp))
        self.sys.procs = {str(busy / "src")}
        self.sys.ws_percent = lambda: 73 + 6 * (app / "dist").exists() + 6 * (app / "coverage").exists()
        helper = self.make_helper(quiet=10 ** 9)  # Nothing relocates, so outputs stay on the volume.
        status = self.apply(helper)
        self.assertFalse((app / "dist").exists())
        for path in (app / "coverage/f", app / "logs/f", app / "build/keep.txt", busy / "dist/f"):
            self.assertTrue(path.exists(), path)
        self.assertEqual(dict(usagePercent=79.0, thresholdPercent=85, evicted=1), status["pressure"])
        self.assertEqual([{"path": "app/dist"}], status["evicted"])
        self.assertFalse((self.ws / sm.TRASH).exists())

    def test_same_device_scratch_is_a_no_op(self):
        app = self.repo("app", ["node_modules/"], files=["package.json"])
        self.sys.dev = lambda info: info.st_dev
        self.assertEqual("same-device", self.helper.apply()["state"])
        self.assertEqual({}, self.mounted())
        self.assertFalse((app / "node_modules").exists())

    def test_inotify_reports_output_and_manifest_names_only(self):
        try:
            notify = sm.Inotify(sm.System())
        except OSError as error:
            self.skipTest("inotify unavailable: %s" % error)
        self.addCleanup(os.close, notify.fd)
        fd = os.open(self.repos, sm.DIRECTORY)
        self.addCleanup(os.close, fd)
        notify.add(fd)
        (self.repos / "notes.txt").write_text("x")
        self.assertFalse(notify.read())
        (self.repos / "package.json").write_text("{}")
        self.assertTrue(notify.read())
        (self.repos / "newdir").mkdir()
        self.assertTrue(notify.read())


if __name__ == "__main__":
    unittest.main()
