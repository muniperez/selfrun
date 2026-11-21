import contextlib
import errno
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import bootstrap
import runtime

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue110/runtime"


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


builder = module("image_builder", HERE / "image/build.py")
issuer = module("session_issuer", HERE / "issue-t3-session.py")


class ControlSocketTests(unittest.TestCase):
    def client(self):
        client = MagicMock()
        client.__enter__.return_value = client
        client.recv.return_value = b'{"ok":true,"result":{"type":"BootstrapReady"}}\n'
        return client

    def test_initial_missing_and_refused_socket_connects_then_sends_exactly_once(self):
        clients = [self.client() for _ in range(3)]
        clients[0].connect.side_effect = FileNotFoundError(errno.ENOENT, "private path")
        clients[1].connect.side_effect = ConnectionRefusedError(errno.ECONNREFUSED, "private path")
        now = [0.0]
        def sleep(seconds):
            now[0] += seconds
        with patch.object(bootstrap.socket, "socket", side_effect=clients) as factory, \
                patch.object(bootstrap.time, "monotonic", side_effect=lambda: now[0]), \
                patch.object(bootstrap.time, "sleep", side_effect=sleep):
            result = bootstrap.call({"action": "bootstrap", "operationId": "fixture"})
        self.assertEqual("BootstrapReady", result["type"])
        self.assertEqual(3, factory.call_count)
        clients[0].sendall.assert_not_called()
        clients[1].sendall.assert_not_called()
        clients[2].sendall.assert_called_once_with(
            b'{"action": "bootstrap", "operationId": "fixture"}\n')
        self.assertGreater(now[0], 0)
        self.assertLess(now[0], 30)

    def test_other_connect_errors_fail_closed_without_retry_or_sensitive_reason(self):
        for error in (PermissionError(errno.EACCES, "private path"),
                      OSError(errno.EIO, "private credential"), TimeoutError("private timeout")):
            with self.subTest(error=type(error).__name__):
                client = self.client()
                client.connect.side_effect = error
                with patch.object(bootstrap.socket, "socket", return_value=client) as factory, \
                        patch.object(bootstrap.time, "sleep") as sleep:
                    with self.assertRaises(runtime.Blocked) as blocked:
                        bootstrap.call({"action": "bootstrap"})
                self.assertNotIn("private", str(blocked.exception))
                self.assertIn("not retried", str(blocked.exception))
                self.assertEqual(1, factory.call_count)
                client.sendall.assert_not_called()
                sleep.assert_not_called()

    def test_readiness_deadline_retains_only_safe_final_connect_reason(self):
        for number, name in ((errno.ENOENT, "ENOENT"), (errno.ECONNREFUSED, "ECONNREFUSED")):
            with self.subTest(errno=number):
                client = self.client()
                client.connect.side_effect = OSError(number, "private path")
                with patch.object(bootstrap.socket, "socket", return_value=client), \
                        patch.object(bootstrap.time, "monotonic", side_effect=[0, 0, 30]), \
                        patch.object(bootstrap.time, "sleep") as sleep:
                    with self.assertRaisesRegex(runtime.Blocked, "30 seconds.*" + name) as blocked:
                        bootstrap.call({"action": "bootstrap"})
                self.assertNotIn("private", str(blocked.exception))
                client.sendall.assert_not_called()
                sleep.assert_not_called()

    def test_failure_after_connection_never_reconnects_or_replays_request(self):
        for phase in ("sendall", "recv"):
            with self.subTest(phase=phase):
                client = self.client()
                getattr(client, phase).side_effect = ConnectionRefusedError(
                    errno.ECONNREFUSED, "ambiguous post-connect failure")
                with patch.object(bootstrap.socket, "socket", return_value=client) as factory, \
                        patch.object(bootstrap.time, "sleep") as sleep:
                    with self.assertRaises(ConnectionRefusedError):
                        bootstrap.call({"action": "bootstrap"})
                self.assertEqual(1, factory.call_count)
                client.connect.assert_called_once()
                client.sendall.assert_called_once()
                sleep.assert_not_called()


class BootstrapTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="bootstrap-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.enrollment = self.root / "enrollment.json"
        self.enrollment.write_text("synthetic disposable fixture")
        self.config = dict(vmId="vm", generation=2, kind="ephemeral", startServices=False)
        self.envelope = dict(vmId="vm", generation=2, operationId="start", expiresAt=1e12,
                             token="not-a-real-credential", url="https://example.invalid/enroll")
        self.manifest = dict(architecture="linux/amd64", provenance=dict(toolArtifactsVerified=True),
            t3=dict(version="1.2.3", environmentProtocol=2,
                    resourceMonitor=dict(path="t3-1.2.3-linux-x64/resource-monitor/linux-x64/t3-resource-monitor",
                                         sha256="a" * 64),
                    relayClient=dict(sha256="b" * 64)))
        self.manifest_bytes = (json.dumps(self.manifest, indent=2) + "\n").encode()

    def test_installed_systemd_units_are_root_owned_and_not_checkout_writable(self):
        source = self.root / "systemd-source"
        destination = self.root / "systemd-installed"
        source.mkdir()
        destination.mkdir()
        for name, mode in (("runtime.service", 0o664), ("work.slice", 0o775)):
            path = source / name
            path.write_text("[Unit]\nDescription=Disposable fixture\n")
            path.chmod(mode)
        with patch.object(builder.os, "chown") as chown:
            builder.install_systemd_units(source, destination)
        self.assertEqual(chown.call_count, 2)
        for path in source.iterdir():
            installed = destination / path.name
            self.assertEqual(installed.read_bytes(), path.read_bytes())
            self.assertEqual(installed.stat().st_mode & 0o777, 0o644)
            self.assertIn(unittest.mock.call(installed, 0, 0), chown.call_args_list)

    def test_user_git_uses_only_selected_repository_helper_without_operator_flags(self):
        home = self.root / "home"
        home.mkdir()
        helper = self.root / "selected-helper"
        helper.write_text("#!/bin/sh\nprintf 'username=synthetic\\npassword=fixture-only\\n\\n'\n")
        helper.chmod(0o700)
        origin = "https://github.com/example/selected.git"
        config = dict(kind="ephemeral", workspace=str(self.root), uid=2000, gid=2000,
                      repositories=[dict(origin=origin)])
        def execute(argv, **kwargs):
            self.assertEqual(kwargs["cwd"], str(home))
            self.assertEqual((kwargs.pop("user"), kwargs.pop("group"), kwargs.pop("extra_groups")),
                             (2000, 2000, []))
            subprocess.run(argv, check=True, capture_output=True, **kwargs)
        with patch.object(bootstrap, "GIT_CREDENTIAL_HELPER", str(helper)), \
                patch.object(bootstrap, "run", side_effect=execute):
            bootstrap.configure_user_git(config)
            bootstrap.configure_user_git(config)
        environment = dict(PATH="/usr/bin:/bin", HOME=str(home), GIT_CONFIG_NOSYSTEM="1",
                           GIT_TERMINAL_PROMPT="0")
        def credentials(url):
            return subprocess.run(["git", "credential", "fill"], input="url=" + url + "\n\n",
                                  env=environment, capture_output=True, text=True)
        selected = credentials(origin)
        self.assertEqual(selected.returncode, 0)
        self.assertIn("password=fixture-only", selected.stdout)
        self.assertIn("path=example/selected.git", selected.stdout)
        self.assertNotEqual(credentials("https://github.com/example/other.git").returncode, 0)
        self.assertNotEqual(credentials("https://elsewhere.invalid/example/selected.git").returncode, 0)
        self.assertNotIn("fixture-only", (home / ".gitconfig").read_text())

    def execute(self, opener, process_effect=None):
        def load(path):
            return self.envelope if Path(path) == self.enrollment else self.config
        with patch.object(bootstrap, "load_config", side_effect=load), \
             patch.object(bootstrap, "call", return_value={"type": "BootstrapReady"}), \
             patch.object(bootstrap, "run", side_effect=process_effect) as process, \
             patch.object(bootstrap, "read_image_manifest", return_value=(self.manifest, self.manifest_bytes)), \
             patch.object(bootstrap.urllib.request, "build_opener", return_value=opener):
            result = bootstrap.bootstrap(self.root / "config.json", self.enrollment, self.root / "state")
        if self.config.get("startServices") is False:
            self.assertTrue(all(call.args[0][0] == "systemctl" for call in process.call_args_list))
        return result

    def preview_startup(self, safe=True, version="1.2.3", protocol=2):
        self.config.update(workspace=str(self.root), uid=2000, gid=2000,
            infrastructureUnits=["minicloud-t3.service", "minicloud-preview-user-app.service"],
            stopUnits=["minicloud-t3.service", "minicloud-preview-user-app.service"])
        self.config.pop("startServices", None)
        started = []
        def process(argv, **kwargs):
            if argv[:2] == ["systemctl", "start"]:
                started.extend(argv[2:])
            if argv[:2] == ["systemctl", "show"]:
                return ("LoadState=loaded\nFragmentPath=/etc/systemd/system/minicloud-preview-user-app.service\n"
                        "DropInPaths=\nKillMode=" + ("process" if safe else "control-group") +
                        "\nSendSIGKILL=no\nSlice=minicloudwork.slice")
            return ""
        class Opener:
            def open(inner, request, timeout):
                body = (json.dumps(dict(serverVersion=version, orchestrationProtocolVersion=protocol)).encode()
                        if isinstance(request, str) else b"{}")
                return contextlib.nullcontext(SimpleNamespace(status=200, read=lambda n: body))
        return started, process, Opener()

    def test_fresh_bootstrap_starts_only_tooling_not_registered_user_applications(self):
        started, process, opener = self.preview_startup()
        with patch.object(runtime, "run", side_effect=process), \
                patch.object(runtime.Linux, "verify_unit_file") as unit_file:
            result = self.execute(opener, process)
        self.assertEqual("EnrollmentAccepted", result["type"])
        self.assertEqual(["minicloud-workspace.service", "minicloud-t3.service"], started)
        unit_file.assert_not_called()

    def test_other_t3_versions_and_protocols_fail_closed(self):
        for version, protocol in (("0.1.9", 2), ("1.2.3", 3), ("1.2.3", 2.0)):
            with self.subTest(version=version, protocol=protocol):
                _, process, opener = self.preview_startup(version=version, protocol=protocol)
                with self.assertRaisesRegex(runtime.Blocked, "version or protocol"):
                    self.execute(opener, process)

    def test_release_manifest_rejects_unsupported_protocol_and_unsafe_artifact_pins(self):
        for key, value in (("version", "1.2.3-rc.1"), ("environmentProtocol", 3),
                           ("resourceMonitor", dict(path="../helper", sha256="a" * 64)),
                           ("relayClient", dict(sha256="unverified"))):
            with self.subTest(key=key):
                manifest = dict(self.manifest, t3=dict(self.manifest["t3"], **{key: value}))
                with self.assertRaises(runtime.Blocked):
                    runtime.qualified_t3(manifest)

    def test_optional_connect_management_failure_preserves_routine_primary_enrollment(self):
        started, process, opener = self.preview_startup()
        self.config["kind"] = "primary"
        def commands(argv, **kwargs):
            if argv == ["systemctl", "start", "minicloud-t3-connect.service"]:
                raise runtime.Blocked("Connect management unavailable")
            return process(argv, **kwargs)
        result = self.execute(opener, commands)
        self.assertEqual("EnrollmentAccepted", result["type"])
        self.assertIn("minicloud-t3.service", started)

    def test_unstarted_user_service_is_not_implicitly_activated_or_needed_for_tooling(self):
        started, process, opener = self.preview_startup(safe=False)
        with patch.object(runtime, "run", side_effect=process), \
                patch.object(runtime.Linux, "verify_unit_file"):
            result = self.execute(opener, process)
        self.assertEqual("EnrollmentAccepted", result["type"])
        self.assertEqual(["minicloud-workspace.service", "minicloud-t3.service"], started)

    def test_enrollment_bound_once_with_no_toolchain_install_or_token_persistence(self):
        response = SimpleNamespace(status=200, read=lambda n: b"{}")
        class Opener:
            request = None
            def open(inner, request, timeout):
                inner.request = request
                return contextlib.nullcontext(response)
        opener = Opener()
        result = self.execute(opener)
        self.assertEqual("EnrollmentAccepted", result["type"])
        self.assertFalse(self.enrollment.exists())
        payload = json.loads(opener.request.data)
        self.assertNotIn("token", payload)
        self.assertEqual(("vm", 2), (payload["vmId"], payload["generation"]))
        for path in (self.root / "state").glob("*.json"):
            self.assertNotIn(self.envelope["token"], path.read_text())
        with self.assertRaisesRegex(runtime.Blocked, "already attempted"):
            self.execute(opener)

    def test_ambiguous_enrollment_is_not_replayed_and_secret_is_deleted(self):
        class Opener:
            count = 0
            def open(inner, *args, **kwargs):
                inner.count += 1
                raise OSError("ambiguous connection loss")
        opener = Opener()
        with self.assertRaisesRegex(runtime.Blocked, "ambiguous"):
            self.execute(opener)
        self.assertFalse(self.enrollment.exists())
        with self.assertRaisesRegex(runtime.Blocked, "already attempted"):
            self.execute(opener)
        self.assertEqual(1, opener.count)

    def test_enrollment_hashes_exact_manifest_bytes_not_reserialized_json(self):
        response = SimpleNamespace(status=200, read=lambda n: b"{}")
        class Opener:
            request = None
            def open(inner, request, timeout):
                inner.request = request
                return contextlib.nullcontext(response)
        opener = Opener()
        self.execute(opener)
        submitted = json.loads(opener.request.data)["imageManifestSha256"]
        self.assertEqual(hashlib.sha256(self.manifest_bytes).hexdigest(), submitted)
        self.assertNotEqual(runtime.digest(self.manifest), submitted)

    def test_wrong_generation_and_redirects_block(self):
        self.envelope["generation"] = 1
        with self.assertRaisesRegex(runtime.Blocked, "generation mismatch"):
            self.execute(None)
        with self.assertRaisesRegex(runtime.Blocked, "redirects"):
            bootstrap.NoRedirect().redirect_request(None)

    def test_builder_generalization_guard_has_no_shared_host_side_effect(self):
        with patch.object(builder, "run") as process:
            with self.assertRaisesRegex(runtime.Blocked, "acknowledgement"):
                builder.builder_guard("NOT-A-DISPOSABLE-IMAGE")
            process.assert_not_called()
        with patch.object(builder, "run") as process, patch.object(Path, "is_file", return_value=False):
            with self.assertRaisesRegex(runtime.Blocked, "marker missing"):
                builder.builder_guard("DESTROY-DISPOSABLE-IMAGE-BUILDER")
            process.assert_not_called()

    def test_builder_requires_actual_pinned_cloud_serial_before_any_install_command(self):
        original_read = Path.read_text
        build_info = "build_name: server\nserial: 20251002\n"
        def read(path, *args, **kwargs):
            if str(path) == "/etc/minicloud-disposable-image-builder":
                return "DESTROY-DISPOSABLE-IMAGE-BUILDER"
            if str(path) == "/etc/os-release":
                return 'VERSION_ID="24.04"\n'
            if str(path) == "/etc/cloud/build.info":
                return build_info
            return original_read(path, *args, **kwargs)
        with patch.object(builder.os, "geteuid", return_value=0), \
             patch.object(builder.os, "uname", return_value=SimpleNamespace(machine="x86_64")), \
             patch.object(builder.os.path, "ismount", return_value=False), \
             patch.object(Path, "is_file", return_value=True), \
             patch.object(Path, "stat", return_value=SimpleNamespace(st_uid=0)), \
             patch.object(Path, "exists", return_value=False), \
             patch.object(Path, "glob", return_value=[]), \
             patch.object(Path, "read_text", read), patch.object(builder, "run", return_value="kvm") as command:
            with self.assertRaisesRegex(runtime.Blocked, "serial does not match"):
                builder.builder_guard("DESTROY-DISPOSABLE-IMAGE-BUILDER")
            command.assert_not_called()
            for value in ("serial: 20251001\n", "serial=20251001\n"):
                build_info = value
                builder.builder_guard("DESTROY-DISPOSABLE-IMAGE-BUILDER")

    def test_headless_auth_uses_verified_singular_scope_duration_and_sensitive_stdout(self):
        self.config["workspace"] = str(self.root)
        scopes = ["terminal:operate", "orchestration:operate", "terminal:read", "orchestration:read"]
        arguments = ["issue-t3-session.py", "--vm-id", "vm", "--generation", "2",
                     "--operation-id", "session", "--subject", "minicloud-gateway", "--ttl", "120",
                     *[value for scope in scopes for value in ("--scope", scope)],
                     "--state-dir", str(self.root)]
        captured = io.StringIO()
        fake_runtime = SimpleNamespace(s={"phase": "Ready"}, validate_request=lambda request: None)
        with patch.dict(os.environ, {"GH_TOKEN": "synthetic-not-a-credential"}), \
             patch.object(sys, "argv", arguments), patch.object(sys, "stdout", captured), \
             patch.object(issuer.os, "geteuid", return_value=0), \
             patch.object(issuer, "load_config", return_value=self.config), \
             patch.object(issuer, "Runtime", return_value=fake_runtime), \
             patch.object(issuer, "run", return_value="--token-only --ttl --subject --scope --base-dir"), \
             patch.object(issuer.subprocess, "run",
                          return_value=SimpleNamespace(returncode=0, stdout="fixture-sensitive-output\n")) as command:
            issuer.main()
        argv = command.call_args.args[0]
        self.assertIn("120s", argv)
        self.assertEqual(scopes, [argv[index + 1] for index, value in enumerate(argv) if value == "--scope"])
        self.assertEqual(argv[:7], ["runuser", "-u", "coding", "--", "env", "-i", "HOME=/workspace/home"])
        self.assertNotIn("synthetic-not-a-credential", " ".join(argv))
        self.assertNotIn("--scopes", argv)
        self.assertNotIn("--dpop", argv)
        self.assertEqual(self.config["workspace"], command.call_args.kwargs["cwd"])
        self.assertEqual("fixture-sensitive-output\n", captured.getvalue())


class VolumeFormatTests(unittest.TestCase):
    device = "/dev/disk/by-id/virtio-ewr-0123456789ab"

    def patched(self, signatures, mounts="/dev/vda1 / ext4 rw 0 0\n"):
        stack = contextlib.ExitStack()
        original_stat = bootstrap.Path.stat
        original_resolve = bootstrap.Path.resolve
        device_path = Path(self.device)

        def fake_stat(path, *args, **kwargs):
            if path == device_path:
                return SimpleNamespace(st_mode=0o060600)
            return original_stat(path, *args, **kwargs)

        def fake_resolve(path, *args, **kwargs):
            if path == device_path:
                return Path("/dev/vdb")
            return original_resolve(path, *args, **kwargs)

        stack.enter_context(patch.object(bootstrap.Path, "stat", fake_stat))
        stack.enter_context(patch.object(bootstrap.Path, "resolve", fake_resolve))
        stack.enter_context(patch("builtins.open", unittest.mock.mock_open(read_data=mounts)))
        stack.enter_context(patch.object(bootstrap, "block_signatures", side_effect=signatures))
        lock_dir = stack.enter_context(tempfile.TemporaryDirectory())
        stack.enter_context(patch.object(bootstrap, "FORMAT_LOCK", os.path.join(lock_dir, "format.lock")))
        calls = []

        def fake_run(argv, **kwargs):
            calls.append(argv)
            if argv[:2] == ["blkid", "-s"]:
                return "ext4" if argv[2] == "TYPE" else "1b4e28ba-2fa1-11d2-883f-0016d3cca427"
            return ""
        stack.enter_context(patch.object(bootstrap, "run", side_effect=fake_run))
        return stack, calls

    def test_blank_volume_is_formatted_once_without_force(self):
        stack, calls = self.patched([{}])
        with stack:
            result = bootstrap.format_volume(self.device, "virtio-ewr-0123456789ab")
        self.assertTrue(result["formatted"])
        self.assertEqual("1b4e28ba-2fa1-11d2-883f-0016d3cca427", result["filesystemUuid"])
        mkfs = [argv for argv in calls if argv[0] == "mkfs.ext4"]
        self.assertEqual(1, len(mkfs))
        self.assertNotIn("-F", mkfs[0])
        self.assertIn("nodiscard,lazy_itable_init=1,lazy_journal_init=1", mkfs[0])

    def test_format_in_progress_blocks_retry_without_touching_device(self):
        import fcntl
        stack, calls = self.patched([{}])
        with stack:
            held = os.open(bootstrap.FORMAT_LOCK, os.O_RDWR | os.O_CREAT, 0o600)
            try:
                fcntl.flock(held, fcntl.LOCK_EX)
                with self.assertRaises(runtime.Blocked):
                    bootstrap.format_volume(self.device, "virtio-ewr-0123456789ab")
            finally:
                os.close(held)
        self.assertFalse([argv for argv in calls if argv[0] in ("mkfs.ext4", "blkid")])

    def test_retry_after_format_only_probes(self):
        stack, calls = self.patched([{"TYPE": "ext4", "UUID": "x"}])
        with stack:
            result = bootstrap.format_volume(self.device, "virtio-ewr-0123456789ab")
        self.assertFalse(result["formatted"])
        self.assertFalse([argv for argv in calls if argv[0] == "mkfs.ext4"])

    def test_foreign_signature_or_mounted_device_is_never_formatted(self):
        for signatures, mounts in (([{"TYPE": "xfs"}], ""), ([{"PTTYPE": "gpt"}], ""),
                                   ([{}], "/dev/vdb /workspace ext4 rw 0 0\n")):
            stack, calls = self.patched(signatures, mounts)
            with stack, self.assertRaises(runtime.Blocked):
                bootstrap.format_volume(self.device, "virtio-ewr-0123456789ab")
            self.assertFalse([argv for argv in calls if argv[0] == "mkfs.ext4"])

    def test_device_must_match_provider_identity(self):
        with self.assertRaises(runtime.Blocked):
            bootstrap.format_volume("/dev/vdb", "vdb")
        with self.assertRaises(runtime.Blocked):
            bootstrap.format_volume(self.device, "virtio-other")


if __name__ == "__main__":
    unittest.main()
