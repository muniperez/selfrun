#!/usr/bin/env python3
"""Storage tiers (#202): shared scratch environment and the one-time cache migration. No root or systemd."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt
import storage_tiers as st

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue202/storage-tiers"
ARTIFACTS.mkdir(parents=True, exist_ok=True)


def load_image(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / "image" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class EnvironmentFileTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="env-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def login_environment(self, uid, env_file):
        """Runs the shipped profile.d script in sh with a fake id(1) and returns what it exported."""
        bin_dir = self.root / "bin"
        bin_dir.mkdir(exist_ok=True)
        fake_id = bin_dir / "id"
        fake_id.write_text("#!/bin/sh\necho %d\n" % uid)
        fake_id.chmod(0o755)
        script = self.root / "profile.sh"
        script.write_text((HERE / "storage/profile.sh").read_text().replace(st.ENV_FILE, str(env_file)))
        output = subprocess.run(["sh", "-c", '. "$1" && env', "sh", str(script)], capture_output=True, text=True,
                                env={"PATH": "%s:/usr/bin:/bin" % bin_dir}, check=True).stdout
        return dict(line.split("=", 1) for line in output.splitlines() if "=" in line)

    def test_one_file_feeds_the_service_runtime_tasks_and_coding_login_shells_only(self):
        source = HERE / "storage/scratch.env"
        values = st.load_environment(source)
        self.assertIn("XDG_CACHE_HOME", values)
        self.assertTrue(all(value.startswith(st.SCRATCH + "/") for value in values.values()))
        self.assertFalse({"CARGO_HOME", "GRADLE_USER_HOME", "CARGO_TARGET_DIR", "YARN_CACHE_FOLDER"} & set(values))
        unit = (HERE / "systemd/minicloud-t3.service").read_text().splitlines()
        self.assertIn("EnvironmentFile=-" + st.ENV_FILE, unit)
        self.assertIn("ReadWritePaths=/workspace -" + st.SCRATCH, unit)
        self.assertEqual((st.ENV_FILE, st.SCRATCH), (rt.SCRATCH_ENV, rt.SCRATCH_ROOT))
        coding = self.login_environment(2000, source)
        self.assertEqual(values, {key: coding.get(key) for key in values})
        self.assertFalse(set(values) & set(self.login_environment(0, source)))

    def test_profile_script_never_evaluates_file_content(self):
        hostile = self.root / "hostile.env"
        marker = self.root / "evaluated"
        hostile.write_text("XDG_CACHE_HOME=$(touch %s)\nTMPDIR=`touch %s`\n" % (marker, marker))
        exported = self.login_environment(2000, hostile)
        self.assertFalse(marker.exists())
        self.assertEqual("$(touch %s)" % marker, exported["XDG_CACHE_HOME"])
        with self.assertRaises(ValueError):
            st.load_environment(hostile)

    def test_unsafe_or_ambiguous_environment_files_are_rejected(self):
        for text in ("TMPDIR=/var/lib/minicloud-scratch/tmp\nTMPDIR=/var/lib/minicloud-scratch/x\n",
                     "TMPDIR=/workspace/home/tmp\n", "TMPDIR=/var/lib/minicloud-scratch/../x\n",
                     "TMPDIR=/var/lib/minicloud-scratch-other/tmp\n", "TMPDIR=relative\n",
                     "TMPDIR=/var/lib/minicloud-scratch/a b\n", "export TMPDIR=/var/lib/minicloud-scratch/t\n"):
            with self.subTest(text=text):
                path = self.root / "case.env"
                path.write_text(text)
                with self.assertRaises(ValueError):
                    st.load_environment(path)

    def test_boot_layout_matches_the_layout_prepare_and_qualification_check(self):
        entries = {}
        for line in (HERE / "storage/tmpfiles.conf").read_text().splitlines():
            if line and not line.startswith("#"):
                kind, path, mode, user, group, _age = line.split()
                self.assertEqual(user, group)
                entries[path] = (kind, int(mode, 8), user)
        expected = {os.path.join(st.SCRATCH, name).rstrip("/"): (mode, "coding" if owner else "root")
                    for name, owner, mode in st.SCRATCH_LAYOUT}
        self.assertEqual(expected, {path: value[1:] for path, value in entries.items()})
        self.assertEqual("D", entries[st.SCRATCH + "/tmp"][0])
        self.assertFalse(any(path.startswith("/workspace") for path in entries))

    def test_image_installs_storage_files_root_owned_and_unit_hashes_stay_pinned(self):
        builder = load_image("storage_tiers_image_builder", "build.py")
        native = load_image("storage_tiers_native", "native.py")
        root = self.root / "image-root"
        owners = []
        with patch.object(builder.os, "chown", side_effect=lambda path, uid, gid: owners.append((uid, gid))):
            builder.install_storage_tiers(HERE / "storage", "/opt/minicloud-workspace", root)
        for target in (st.ENV_FILE, st.TMPFILES, "/etc/profile.d/minicloud-scratch.sh"):
            installed = root / target.lstrip("/")
            self.assertEqual(0o644, stat.S_IMODE(installed.stat().st_mode))
        self.assertEqual([(0, 0)] * 3, owners)
        for unit, digest in native.UNIT_HASHES.items():
            self.assertEqual(digest, hashlib.sha256((HERE / "systemd" / unit).read_bytes()).hexdigest(), unit)


class MigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="tiers-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / "workspace"
        self.home = self.workspace / "home"
        self.home.mkdir(parents=True)
        self.scratch = self.root / "scratch"
        for name, _owner, mode in st.SCRATCH_LAYOUT:
            path = self.scratch / name
            path.mkdir(exist_ok=True)
            path.chmod(mode)
        self.marker = self.root / "state/storage-tiers-migrated.json"
        self.outside = self.root / "outside"
        self.outside.mkdir()
        (self.outside / "keep.txt").write_text("outside\n")
        self.addCleanup(self.make_writable)

    def make_writable(self):
        for directory, names, _ in os.walk(self.root):
            for name in names:
                path = os.path.join(directory, name)
                if not os.path.islink(path):
                    os.chmod(path, 0o700)

    def file(self, relative, data="regenerable\n"):
        path = self.home / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(data)
        return path

    def prepare(self, **overrides):
        options = dict(workspace=str(self.workspace), scratch=str(self.scratch), marker=str(self.marker),
                       tmpfiles="/nonexistent", uid=os.getuid(), gid=os.getgid(),
                       runner=lambda argv, **kwargs: SimpleNamespace(returncode=0),
                       drop=lambda uid, gid: None, wall=lambda: 1234.0, root_uid=os.getuid())
        options.update(overrides)
        return st.prepare(**options)

    def test_first_start_moves_caches_links_them_to_scratch_and_keeps_credentials(self):
        caches = (".npm/_cacache/index", ".cargo/registry/cache/crate", ".cargo/git/db/repo",
                  ".gradle/caches/modules-2/x", "go/pkg/mod/example.com/m@v1/go.mod", ".cache/pip/http/x",
                  ".nuget/packages/demo/1.0.0/demo.nupkg", ".m2/repository/x/y.jar")
        credentials = {".cargo/credentials.toml": "token\n", ".cargo/config.toml": "[net]\n",
                       ".gradle/gradle.properties": "secret=1\n", ".npmrc": "//r/:_authToken=x\n",
                       ".nuget/NuGet/NuGet.Config": "<configuration/>\n", ".m2/settings.xml": "<s/>\n"}
        for relative in caches:
            self.file(relative)
        for relative, data in credentials.items():
            self.file(relative, data)
        # Go module caches are read-only by design.
        (self.home / "go/pkg/mod/example.com/m@v1").chmod(0o500)
        (self.home / "go/pkg/mod").chmod(0o500)

        report = self.prepare()

        self.assertEqual(("ready", "written"), (report["state"], report["marker"]))
        self.assertEqual("purged", report["trash"]["state"])
        for relative in caches:
            self.assertFalse((self.home / relative).exists(), relative)
        for relative, data in credentials.items():
            self.assertFalse((self.home / relative).is_symlink())
            self.assertEqual(data, (self.home / relative).read_text())
        self.assertFalse((self.home / st.TRASH).exists())
        self.assertFalse((self.home / "go/pkg/mod").exists())
        cache = self.scratch / "cache"
        self.assertEqual(str(cache), os.readlink(self.home / ".cache"))
        self.assertEqual(str(cache / "cargo/registry"), os.readlink(self.home / ".cargo/registry"))
        self.assertEqual(str(cache / "gradle/caches"), os.readlink(self.home / ".gradle/caches"))
        self.assertTrue((cache / "cargo/registry").is_dir())
        self.assertEqual(0o600, stat.S_IMODE(self.marker.stat().st_mode))
        self.assertTrue(st.read_marker(str(self.marker), os.getuid()))

    def test_links_and_foreign_or_mounted_directories_are_never_followed_or_moved(self):
        (self.home / ".npm").symlink_to(self.outside, target_is_directory=True)
        (self.home / ".cargo").symlink_to(self.outside, target_is_directory=True)
        (self.outside / "registry").mkdir()
        (self.outside / "registry/keep.txt").write_text("outside\n")
        report = self.prepare()
        self.assertEqual("symlink", report["migration"][".npm"])
        self.assertEqual("symlink", report["migration"][".cargo/registry"])
        self.assertEqual("parent_symlink", report["links"][".cargo/registry"])
        self.assertEqual(["keep.txt", "registry"], sorted(os.listdir(self.outside)))
        self.assertEqual("outside\n", (self.outside / "registry/keep.txt").read_text())
        self.file(".gradle/caches/x")
        home = os.open(self.home, os.O_RDONLY | os.O_DIRECTORY)
        try:
            device = os.fstat(home).st_dev
            self.assertEqual("foreign_owner", st.migrate(home, os.getuid() + 1, device, [".gradle/caches"],
                                                         {})[".gradle/caches"])
            self.assertEqual("separate_mount", st.migrate(home, os.getuid(), device + 1, [".gradle/caches"],
                                                          {})[".gradle/caches"])
        finally:
            os.close(home)
        self.assertTrue((self.home / ".gradle/caches/x").is_file())

    def test_cache_holding_a_token_stays_in_place(self):
        self.file(".cache/huggingface/token", "hf_secret\n")
        report = self.prepare()
        self.assertEqual("contains_credentials", report["migration"][".cache"])
        self.assertEqual("kept_user_entry", report["links"][".cache"])
        self.assertEqual("hf_secret\n", (self.home / ".cache/huggingface/token").read_text())
        self.assertEqual("written", report["marker"])

    def test_marker_makes_migration_one_time_and_only_a_trusted_marker_counts(self):
        self.file(".npm/a")
        self.assertEqual("written", self.prepare()["marker"])
        self.file(".npm/b")
        second = self.prepare()
        self.assertEqual(("present", "present"), (second["marker"], second["links"][".cache"]))
        self.assertNotIn("migration", second)
        self.assertTrue((self.home / ".npm/b").exists())
        self.marker.chmod(0o644)
        third = self.prepare()
        self.assertEqual(("moved", "written"), (third["migration"][".npm"], third["marker"]))
        self.assertFalse((self.home / ".npm").exists())

    def test_failures_are_reported_without_a_marker_and_never_block(self):
        def broken(argv, **kwargs):
            raise OSError("missing")
        (self.home / ".npm").mkdir()
        (self.home / ".npm/x").write_text("x")
        with patch.object(st.os, "rename", side_effect=PermissionError):
            report = self.prepare(runner=broken)
        self.assertEqual(("unavailable", "failed:PermissionError", "retry_next_start"),
                         (report["tmpfiles"], report["migration"][".npm"], report["marker"]))
        self.assertFalse(self.marker.exists())
        (self.scratch / "cache").chmod(0o755)
        self.assertEqual(("scratch_unavailable", ["cache"]),
                         (self.prepare()["state"], self.prepare()["scratch"]))
        (self.scratch / "cache").chmod(0o700)
        shutil.rmtree(self.home)
        failed = self.prepare()
        self.assertEqual(("coding_user_step_failed", "Skip"), (failed["state"], failed["error"]))
        self.assertFalse(self.marker.exists())
        with patch.object(sys, "stderr") as stderr:
            self.assertEqual(0, st.main(["prepare", "--unexpected"]))
        self.assertIn('"refused"', "".join(call.args[0] for call in stderr.write.call_args_list))

    def test_purge_is_bounded_resumable_and_does_not_follow_links(self):
        trash = self.home / st.TRASH
        for index in range(5):
            self.file("%s/batch/%d" % (st.TRASH, index))
        (trash / "batch/escape").symlink_to(self.outside, target_is_directory=True)
        home = os.open(self.home, os.O_RDONLY | os.O_DIRECTORY)
        try:
            device = os.fstat(home).st_dev
            ticks = iter(range(100))
            first = st.purge(home, os.getuid(), device, seconds=3, clock=lambda: next(ticks))
            self.assertEqual("pending", first["state"])
            self.assertTrue(trash.exists())
            self.assertEqual("purged", st.purge(home, os.getuid(), device)["state"])
        finally:
            os.close(home)
        self.assertFalse(trash.exists())
        self.assertEqual(["keep.txt"], os.listdir(self.outside))


if __name__ == "__main__":
    unittest.main()
