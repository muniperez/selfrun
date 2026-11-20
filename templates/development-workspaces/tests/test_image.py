import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import sys
import unittest
import zipfile
from unittest.mock import Mock, patch


HERE = Path(__file__).resolve().parents[1]
ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, HERE / "image" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


builder = load("generic_image_builder", "build.py")
native = load("generic_native_qualification", "native.py")


class ImageTests(unittest.TestCase):
    def test_t3_restart_timer_is_distinct_from_a_start_job(self):
        probe = native.Native.__new__(native.Native)
        delay = dict(ActiveState="activating", SubState="auto-restart", Result="signal",
                     Job="", MainPID=0, ControlPID=0)
        with patch.object(probe, "wait_t3", return_value=delay):
            self.assertEqual(probe.t3_restart_delay("signal"), delay)
        for change in (dict(Job="42"), dict(ControlPID=123), dict(Result="oom-kill")):
            with self.subTest(change=change), patch.object(probe, "wait_t3", return_value=delay | change):
                with self.assertRaisesRegex(native.ProbeError, "T3_RESTART_DELAY_NOT_OBSERVED"):
                    probe.t3_restart_delay("signal")

    def test_t3_cancelled_restart_allows_retained_signal_result(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        probe.rt.Linux.t3_cgroup_empty.return_value = True
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        cleared = stopped | dict(Result="success")
        with patch.object(probe, "t3_unit", side_effect=[stopped, cleared]), \
                patch.object(probe, "check_t3_freezer") as freezer, \
                patch.object(native.time, "monotonic", side_effect=[0, 0, 6]), \
                patch.object(native.time, "sleep"):
            self.assertEqual(probe.t3_stays_stopped(frozen=True, after_crash=True), cleared)
        self.assertEqual([call.args for call in freezer.call_args_list], [(True,), (True,)])

    def test_t3_retained_signal_does_not_hide_unexpected_stop_state(self):
        probe = native.Native.__new__(native.Native)
        probe.rt = Mock()
        stopped = dict(ActiveState="inactive", SubState="dead", Result="signal",
                       Job="", MainPID=0, ControlPID=0)
        for state, empty, options in (
                (stopped, True, {}),
                (stopped | dict(Job="42"), True, {"after_crash": True}),
                (stopped, False, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed"), True, {"after_crash": True}),
                (stopped | dict(ActiveState="failed", SubState="failed", Result="start-limit-hit"),
                 True, {"failed": True})):
            with self.subTest(state=state, empty=empty, options=options):
                probe.rt.Linux.t3_cgroup_empty.return_value = empty
                with patch.object(probe, "t3_unit", return_value=state), \
                        self.assertRaisesRegex(native.ProbeError, "T3_DID_NOT_REMAIN_STOPPED"):
                    probe.t3_stays_stopped(**options)

    def test_copilot_install_verifies_pin_and_uses_credential_free_initialize(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(builder, "download") as download, \
                patch.object(builder, "run") as run, \
                patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "is_file", return_value=True), \
                patch.object(builder.Path, "write_text") as write, patch.object(builder.Path, "chmod"), \
                patch.object(builder, "probe_copilot", return_value={"version": "1.0.114"}) as probe:
            builder.install_copilot(lock, Path("/synthetic-build-cache"))
        download.assert_called_once_with(lock["copilot"], Path("/synthetic-build-cache/copilot.tar.gz"))
        run.assert_called_once_with(["tar", "--no-same-owner", "-xf",
                                     "/synthetic-build-cache/copilot.tar.gz", "-C", "/opt/minicloud-copilot"])
        self.assertIn('exec /opt/minicloud-copilot/copilot "$@"', write.call_args.args[0])
        self.assertIn("COPILOT_AUTO_UPDATE=false", write.call_args.args[0])
        probe.assert_called_once_with(expected_version=lock["copilot"]["version"],
                                      environment={"PATH": "/usr/local/bin:/usr/bin:/bin",
                                                   "HOME": "/synthetic-build-cache"})

    def test_native_source_control_versions_use_coding_uid_and_service_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        with patch.object(native, "command", side_effect=["gh version " + lock["gh"]["version"],
                                                         "glab version " + lock["glab"]["version"]]) as run:
            result = native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        self.assertTrue(result["githubVersionMatched"] and result["gitlabVersionMatched"])
        for call in run.call_args_list:
            self.assertEqual(call.args[0][:7],
                             ["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups", "env", "-i",
                              "HOME=/workspace/home"])
            self.assertIn("PATH=/usr/local/bin:/usr/bin:/bin", call.args[0])

    def test_native_source_control_rejects_missing_binaries_wrong_version_or_path(self):
        lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        for outputs in ([native.ProbeError("COMMAND_UNAVAILABLE_OR_TIMEOUT")],
                        ["gh version " + lock["gh"]["version"], native.ProbeError("COMMAND_FAILED")],
                        ["gh version 0.0.0"]):
            with self.subTest(outputs=len(outputs)), patch.object(native, "command", side_effect=outputs):
                with self.assertRaises(native.ProbeError):
                    native.source_control_versions("/usr/local/bin:/usr/bin:/bin", lock)
        with patch.object(native, "command") as run:
            with self.assertRaisesRegex(native.ProbeError, "UNEXPECTED_T3_SERVICE_PATH"):
                native.source_control_versions("/usr/bin:/bin", lock)
            run.assert_not_called()

    @unittest.skipUnless(shutil.which("node"), "Optional Node validates the native T3 controller")
    def test_pinned_t3_source_control_discovery_fails_closed_and_redacts_auth_output(self):
        module = (HERE / "image/source-control-probe.mjs").as_uri()
        script = """
import assert from "node:assert/strict";
import { sourceControlEvidence } from MODULE;
const versions = { gh: "2.83.1", glab: "1.74.0" };
const discovery = { sourceControlProviders: [
  { kind: "github", executable: "gh", status: "available", version: { _tag: "Some", value: "gh version 2.83.1" },
    auth: { status: "unauthenticated", detail: "sensitive-diagnostic", account: "private-account" } },
  { kind: "gitlab", executable: "glab", status: "available", version: { _tag: "Some", value: "glab 1.74.0 (build)" },
    auth: { status: "unauthenticated" } }
] };
const evidence = sourceControlEvidence(discovery, versions);
assert.equal(evidence.pinnedT3SourceControlDiscoveryChecked, true);
assert.equal(JSON.stringify(evidence).includes("sensitive"), false);
assert.equal(JSON.stringify(evidence).includes("private-account"), false);
for (const index of [0, 1]) {
  for (const change of [{ status: "missing" }, { auth: { status: "unknown" } },
    { auth: { status: "authenticated" } }, { version: { _tag: "None" } },
    { version: { _tag: "Some", value: "2.83.10" } }]) {
    const invalid = structuredClone(discovery);
    Object.assign(invalid.sourceControlProviders[index], change);
    assert.throws(() => sourceControlEvidence(invalid, versions), /SOURCE_CONTROL_DISCOVERY_FAILED/);
  }
}
assert.throws(() => sourceControlEvidence({ sourceControlProviders: [] }, versions));
assert.throws(() => sourceControlEvidence({ sourceControlProviders:
  [...discovery.sourceControlProviders, discovery.sourceControlProviders[0]] }, versions));
""".replace("MODULE", json.dumps(module))
        subprocess.run(["node", "--input-type=module", "-e", script], check=True, capture_output=True, text=True)

    def test_native_command_retains_safe_exception_identity_without_raw_stderr(self):
        with self.assertRaises(native.ProbeError) as caught:
            native.command([sys.executable, "-c",
                            "raise FileNotFoundError(2, 'synthetic-sensitive-error-value')"])
        evidence = caught.exception.evidence
        self.assertEqual(evidence["exceptionClass"], "FileNotFoundError")
        self.assertEqual(evidence["errno"], 2)
        self.assertNotIn("synthetic-sensitive-error-value", json.dumps(evidence))

    def test_install_does_not_request_a_workspace_database(self):
        class PackagesObserved(Exception):
            pass

        calls = []

        def run(argv, **kwargs):
            calls.append(argv)
            if argv[:2] == ["apt-get", "-y"]:
                raise PackagesObserved()
            return ""

        with patch.object(builder.Path, "mkdir"), patch.object(builder.Path, "iterdir", return_value=[]), \
                patch.object(builder.Path, "write_text"), patch.object(builder, "run", side_effect=run):
            with self.assertRaises(PackagesObserved):
                builder.install("a" * 40, "synthetic-disposable-builder")
        install = calls[-1]
        self.assertIn("age", install)
        self.assertIn("git", install)
        self.assertFalse(any(value.startswith(("postgresql", "libpq")) for value in install))


class NativeRestoreTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="native-restore-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def archive(self, files, link=False):
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w") as archive:
            for name, value in files.items():
                member = tarfile.TarInfo(name)
                if link:
                    member.type = tarfile.SYMTYPE
                    member.linkname = "../outside"
                    archive.addfile(member)
                else:
                    member.size = len(value)
                    archive.addfile(member, io.BytesIO(value))
        output.seek(0)
        return output

    def test_restores_only_selected_nested_user_files_with_verified_hashes(self):
        files = {"config/settings.json": b'{"user":true}', "transcripts/session.txt": b"user data"}
        expected = {name: hashlib.sha256(value).hexdigest() for name, value in files.items()}
        result = native.restore_selected_files(self.archive(files), expected, self.root)
        self.assertTrue(result["allSha256Matched"])
        for name, value in files.items():
            self.assertEqual((self.root / name).read_bytes(), value)

    def test_rejects_archive_links_instead_of_following_them(self):
        expected = {"config/settings.json": hashlib.sha256(b"user data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "UNSAFE_RESTORE_MEMBER"):
            native.restore_selected_files(self.archive({"config/settings.json": b""}, link=True),
                                          expected, self.root)
        self.assertFalse((self.root / "config").exists())

    def test_rejects_wrong_hash_and_missing_selected_file(self):
        expected = {"config/settings.json": hashlib.sha256(b"expected data").hexdigest()}
        with self.assertRaisesRegex(native.ProbeError, "RESTORED_USER_FILE_HASH_MISMATCH"):
            native.restore_selected_files(self.archive({"config/settings.json": b"changed"}), expected, self.root)
        with self.assertRaisesRegex(native.ProbeError, "USER_BACKUP_MEMBER_MISSING"):
            native.restore_selected_files(self.archive({}), expected, self.root)


class SourceControlInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="source-control-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())

    def download(self, item, target, *, kind="file"):
        with tarfile.open(target, "w:gz") as archive:
            member = tarfile.TarInfo(item["executable"] if kind != "missing" else "unexpected")
            if kind == "link":
                member.type = tarfile.SYMTYPE
                member.linkname = "/outside"
                archive.addfile(member)
            else:
                member.size = 6
                archive.addfile(member, io.BytesIO(b"binary"))
            # Unselected members must never be extracted.
            outside = tarfile.TarInfo("../../outside")
            outside.size = 6
            archive.addfile(outside, io.BytesIO(b"unsafe"))

    def install(self, download=None, versions=None):
        with patch.object(builder, "Path", return_value=self.bin), \
                patch.object(builder, "download", side_effect=download or self.download), \
                patch.object(builder.os, "chown"), \
                patch.object(builder, "run", side_effect=versions or [
                    "gh version " + self.lock["gh"]["version"],
                    "glab " + self.lock["glab"]["version"]]):
            builder.install_source_control_clis(self.lock, self.root)

    def test_installs_both_pinned_binaries_without_extracting_other_members(self):
        self.install()
        self.assertEqual((self.bin / "gh").read_bytes(), b"binary")
        self.assertEqual((self.bin / "glab").read_bytes(), b"binary")
        self.assertEqual(set(path.name for path in self.bin.iterdir()), {"gh", "glab"})
        self.assertFalse((self.root.parent / "outside").exists())

    def test_rejects_missing_or_linked_executable(self):
        for kind in ("missing", "link"):
            with self.subTest(kind=kind), self.assertRaisesRegex(builder.Blocked, "archive layout"):
                self.install(download=lambda item, target: self.download(item, target, kind=kind))
        self.assertEqual(list(self.bin.iterdir()), [])

    def test_rejects_wrong_installed_version(self):
        with self.assertRaisesRegex(builder.Blocked, "Wrong source-control CLI version"):
            self.install(versions=["gh version 2.83.10"])

    def test_checksum_mismatch_blocks_before_any_executable_is_installed(self):
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"not-the-release")):
            with self.assertRaisesRegex(builder.Blocked, "checksum mismatch"):
                builder.download(self.lock["gh"], self.root / "download")
        self.assertEqual(list(self.bin.iterdir()), [])


class AgentInstallTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="agent-image-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lock = json.loads((HERE / "image/manifest.lock.json").read_text())
        chown = patch.object(builder.os, "chown")
        chown.start()
        self.addCleanup(chown.stop)

    def test_agent_pins_are_consistent_across_lock_npm_lock_and_api_agent_list(self):
        package = json.loads((HERE / "image/tools/package.json").read_text())
        npm_lock = json.loads((HERE / "image/tools/package-lock.json").read_text())
        claude = npm_lock["packages"]["node_modules/@anthropic-ai/claude-code-linux-x64"]
        self.assertEqual(package["dependencies"]["@anthropic-ai/claude-code-linux-x64"], self.lock["claude"]["version"])
        self.assertEqual((claude["version"], claude["integrity"]),
                         (self.lock["claude"]["version"], self.lock["claude"]["integrity"]))
        self.assertEqual({agent["id"]: agent["version"] for agent in self.lock["agents"]},
                         {name: self.lock[name]["version"]
                          for name in ("codex", "copilot", "claude", "antigravity", "muse")})
        for name in ("antigravity", "muse"):
            self.assertRegex(self.lock[name]["sha256"], "^[0-9a-f]{64}$")
            self.assertTrue(self.lock[name]["url"].startswith("https://"))

    def test_claude_wrapper_disables_self_update_and_requires_the_pinned_version(self):
        binary = self.root / self.lock["claude"]["executable"]
        binary.parent.mkdir(parents=True)
        binary.write_bytes(b"binary")
        wrapper = self.root / "claude"
        with patch.object(builder, "run", return_value="2.1.328 (Claude Code)") as run:
            builder.install_claude(self.lock, self.root, wrapper)
        self.assertEqual(run.call_args.args[0], [str(wrapper), "--version"])
        self.assertIn("DISABLE_AUTOUPDATER=1", wrapper.read_text())
        self.assertIn("exec " + str(binary) + ' "$@"', wrapper.read_text())
        with patch.object(builder, "run", return_value="2.1.3289 (Claude Code)"), \
                self.assertRaisesRegex(builder.Blocked, "Wrong Claude Code version"):
            builder.install_claude(self.lock, self.root, wrapper)
        binary.unlink()
        with self.assertRaisesRegex(builder.Blocked, "package layout"):
            builder.install_claude(self.lock, self.root, wrapper)

    def antigravity(self, members, size_matches=True):
        lock = json.loads(json.dumps(self.lock))
        lock["antigravity"]["members"] = {"agy_acp_server.par": 3, "localharness_external": 4}

        def download(item, target):
            with zipfile.ZipFile(target, "w") as archive:
                for name, data in members.items():
                    archive.writestr(name, data)
            item["size"] = target.stat().st_size if size_matches else 1
        destination = self.root / "antigravity"
        with patch.object(builder, "download", side_effect=download):
            builder.install_antigravity(lock, self.root, destination)
        return destination

    def test_antigravity_copies_only_pinned_members(self):
        destination = self.antigravity({"agy_acp_server.par": b"par", "localharness_external": b"ext!",
                                        "../outside": b"unsafe"})
        self.assertEqual(sorted(path.name for path in destination.iterdir()),
                         ["agy_acp_server.par", "localharness_external"])
        self.assertEqual((destination / "agy_acp_server.par").read_bytes(), b"par")
        self.assertFalse((self.root.parent / "outside").exists())
        self.assertFalse((self.root / "antigravity.zip").exists())

    def test_antigravity_rejects_missing_resized_members_or_archive_size(self):
        for members, size_matches, message in (
                ({"agy_acp_server.par": b"par"}, True, "archive layout"),
                ({"agy_acp_server.par": b"parx", "localharness_external": b"ext!"}, True, "archive layout"),
                ({"agy_acp_server.par": b"par", "localharness_external": b"ext!"}, False, "size mismatch")):
            with self.subTest(message=message, members=sorted(members)):
                shutil.rmtree(self.root / "antigravity", ignore_errors=True)
                with self.assertRaisesRegex(builder.Blocked, message):
                    self.antigravity(members, size_matches)

    def test_muse_is_checksum_and_size_verified_and_wrapped_without_self_update(self):
        lock = json.loads(json.dumps(self.lock))
        lock["muse"].update(sha256=hashlib.sha256(b"muse").hexdigest(), size=4)
        destination, wrapper = self.root / "muse", self.root / "muse-wrapper"
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")):
            builder.install_muse(lock, self.root, destination, wrapper)
        self.assertEqual((destination / "muse").read_bytes(), b"muse")
        self.assertIn("MUSE_NO_AUTO_UPDATE=1", wrapper.read_text())
        lock["muse"]["size"] = 5
        shutil.rmtree(destination)
        with patch.object(builder.urllib.request, "urlopen", return_value=io.BytesIO(b"muse")), \
                self.assertRaisesRegex(builder.Blocked, "size mismatch"):
            builder.install_muse(lock, self.root, destination, wrapper)
