from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime as rt
import t3_connect as connect

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue146/tests"


class ConnectTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(dir=ARTIFACTS)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.config = dict(vmId="primary", generation=3, kind="primary", uid=2000, gid=2000,
                           workspace="/workspace", volume={"id": "retained"})
        self.runtime = rt.Runtime(self.config, self.root, git=rt.Git())
        self.runtime.s["phase"] = "Ready"
        self.runtime.save()

    def test_credential_is_root_state_only_stable_and_generation_bound(self):
        with patch.object(connect, "require_trusted_listener"), patch.object(connect, "require_root_reserved_port"):
            token = connect.credential(self.config, self.root)
            self.assertEqual(len(token), 64)
            self.assertEqual(connect.credential(self.config, self.root), token)
            self.assertEqual((self.root / "connect-credential.json").stat().st_mode & 0o777, 0o600)
            changed = dict(self.config, generation=4)
            with self.assertRaisesRegex(rt.Blocked, "different generation"):
                connect.credential(changed, self.root)
            self.runtime.s["phase"] = "Frozen"
            self.runtime.save()
            with self.assertRaisesRegex(rt.Blocked, "fenced"):
                connect.credential(self.config, self.root)

    def test_credential_export_and_cached_proof_fail_closed_when_policy_is_unsafe(self):
        with patch.object(connect, "require_trusted_listener"), patch.object(connect, "require_root_reserved_port"):
            token = connect.credential(self.config, self.root)
        original_read = Path.read_text
        def read(path, *args, **kwargs):
            if str(path) == "/proc/sys/net/ipv4/ip_unprivileged_port_start":
                return "0"
            return original_read(path, *args, **kwargs)
        with patch.object(connect.Path, "read_text", read), patch.object(connect, "require_trusted_listener") as listener:
            with self.assertRaisesRegex(rt.Blocked, "Root-reserved"):
                connect.credential(self.config, self.root)
            proof = connect.admission_proof(self.config, self.root)
            self.assertFalse(proof["admitted"])
            self.assertNotIn(token, json.dumps(proof))
            listener.assert_not_called()
            original_credential = connect.credential
            output = io.StringIO()
            with patch.object(connect, "load_config", return_value=self.config), \
                    patch.object(connect.os, "geteuid", return_value=0), \
                    patch.object(connect.os, "isatty", return_value=False), \
                    patch.object(connect, "credential", side_effect=lambda config: original_credential(config, self.root)), \
                    patch.object(sys, "argv", ["t3_connect.py", "credential", "--vm-id", "primary", "--generation", "3"]), \
                    redirect_stdout(output), self.assertRaises(rt.Blocked):
                connect.main()
            self.assertEqual(output.getvalue(), "")
        self.assertEqual(json.loads((self.root / "connect-credential.json").read_text())["token"], token)

    def listener_fixture(self):
        proc = self.root / "proc"
        process = proc / "123"
        (process / "fd").mkdir(parents=True)
        (proc / "net").mkdir()
        (process / "status").write_text("Uid:\t0\t0\t0\t0\n")
        (process / "cgroup").write_text("0::/system.slice/minicloud-t3-connect.service\n")
        (process / "cmdline").write_bytes(b"/usr/bin/python3\0-B\0" +
                                         os.fsencode(connect.MANAGEMENT_SCRIPT) + b"\0serve\0")
        (process / "exe").symlink_to(sys.executable)
        (process / "fd" / "3").symlink_to("socket:[456]")
        (proc / "net" / "tcp").write_text("header\n 0: 0100007F:0306 00000000:0000 0A 0:0 00:0 0 0 0 456\n")
        (proc / "net" / "tcp6").write_text("header\n")
        properties = "\n".join(("ActiveState=active", "MainPID=123", "User=root",
            "FragmentPath=/etc/systemd/system/minicloud-t3-connect.service", "DropInPaths=",
            "ControlGroup=/system.slice/minicloud-t3-connect.service"))
        return proc, properties

    def test_listener_admission_requires_root_service_process_and_its_actual_listening_socket(self):
        proc, properties = self.listener_fixture()
        with patch.object(connect, "run", side_effect=lambda argv: "123" if "--value" in argv else properties), \
                patch.object(connect.Linux, "verify_unit_file"):
            connect.require_trusted_listener(proc)
            for path, replacement in (
                    (proc / "123/status", "Uid:\t2000\t2000\t2000\t2000\n"),
                    (proc / "123/cgroup", "0::/minicloudwork.slice/minicloud-t3.service\n"),
                    (proc / "123/cmdline", "/usr/bin/python3\0-B\0/workspace/spoof.py\0serve\0"),
                    (proc / "net/tcp", "header\n"),
                    (proc / "net/tcp", "header\n 0: 00000000:0306 00000000:0000 0A 0:0 00:0 0 0 0 456\n"),
                    (proc / "net/tcp", "header\n 0: 0100007F:0306 00000000:0000 0A 0:0 00:0 0 2000 0 456\n"),
                    (proc / "net/tcp", "header\n 0: 0100007F:0306 00000000:0000 0A 0:0 00:0 0 0 0 999\n")):
                before = path.read_text()
                with self.subTest(path=path, replacement=replacement):
                    path.write_text(replacement)
                    with self.assertRaises(rt.Blocked):
                        connect.require_trusted_listener(proc)
                    path.write_text(before)

    def test_missing_service_or_listener_blocks_credential_export_and_cached_admission(self):
        with patch.object(connect, "run", return_value="ActiveState=inactive\nMainPID=0"), \
                patch.object(connect, "require_root_reserved_port"):
            with self.assertRaisesRegex(rt.Blocked, "Untrusted"):
                connect.credential(self.config, self.root)
            self.assertFalse(connect.admission_proof(self.config, self.root)["admitted"])
        self.assertFalse((self.root / "connect-credential.json").exists())

    def test_management_listener_refuses_a_kernel_policy_that_allows_coding_to_bind_its_port(self):
        for value in ("0", "774", "1024"):
            with self.subTest(value=value), patch.object(connect.Path, "read_text", return_value=value):
                if value == "1024":
                    connect.require_root_reserved_port()
                else:
                    with self.assertRaisesRegex(rt.Blocked, "Root-reserved"):
                        connect.require_root_reserved_port()

    def test_ephemeral_unenrolled_and_fenced_activation_never_touch_systemd(self):
        with patch.object(connect, "run") as run:
            with self.assertRaises(rt.Blocked):
                connect.activate(dict(self.config, kind="ephemeral"), self.root)
            self.runtime.s["phase"] = "Frozen"
            self.runtime.save()
            with self.assertRaises(rt.Blocked):
                connect.activate(self.config, self.root)
            run.assert_not_called()

    def test_pending_approval_fences_shutdown_even_before_cli_child_has_started(self):
        rt.atomic_json(self.root / "connect-operation.json", dict(vmId="primary", generation=3))
        request = dict(vmId="primary", generation=3, operationId="stop",
                       action="quiesce", gatewayAdmissionsFenced=True, unobservableToolsAbsent=True)
        with self.assertRaisesRegex(rt.Blocked, "approval is pending"):
            self.runtime.handle(request)
        status = self.runtime.handle(dict(request, action="status", includeWorkInventory=True))
        self.assertEqual(status["workInventory"]["state"], "BusyOrUnknown")
        self.assertEqual(self.runtime.s["phase"], "Ready")

    def test_supported_enrollment_never_installs_or_replaces_a_t3_service(self):
        argv = connect.command("link")
        self.assertEqual(argv[-6:], ["t3", "connect", "link", "--base-dir", "/workspace/t3", "--headless"])
        self.assertIn("HOME=/workspace/home", argv)
        self.assertNotIn("up", argv)
        with patch.object(connect, "run", side_effect=[
                "active", '{"desired":true,"authenticated":true}', ""]) as run:
            connect.activate(self.config, self.root)
        self.assertEqual(run.call_args_list[-1].args[0],
                         ["systemctl", "restart", "minicloud-t3.service"])
        with patch.object(connect, "run", return_value="inactive") as run:
            with self.assertRaises(rt.Blocked):
                connect.activate(self.config, self.root)
            self.assertEqual(run.call_count, 1)

    def test_public_status_drops_identity_tokens_and_raw_errors(self):
        status = connect.public_status(dict(linked=True, desired=True, revoked=False,
                                            reachability=dict(status="reachable", token="secret"),
                                            cloudUserId="private", accessToken="secret"))
        self.assertEqual(status, dict(linked=True, desired=True, revoked=False, reachable=True))
        self.assertNotIn("private", json.dumps(status))
        self.assertNotIn("secret", json.dumps(status))

    def test_approval_is_memory_only_and_cleared_on_completion(self):
        manager = connect.Enrollment(self.config, self.root)
        gate = threading.Event()

        class Output:
            lines = iter([b"Sign in to T3 Connect. Open on any device:\n",
                          b"  https://example.invalid/activate?code=ABCD-EFGH\n",
                          b"Code: ABCD-EFGH\n"])

            def readline(self, limit):
                try:
                    return next(self.lines)
                except StopIteration:
                    gate.wait(2)
                    return b""

            def close(self):
                pass

        process = Mock(stdout=Output())
        process.wait.return_value = 0
        manager.process = process
        manager.result = "awaiting_approval"
        worker = threading.Thread(target=manager.collect, args=(process,))
        worker.start()
        for _ in range(100):
            with manager.lock:
                if manager.prompt:
                    break
            threading.Event().wait(0.005)
        try:
            self.assertEqual(manager.prompt["code"], "ABCD-EFGH")
            for path in self.root.iterdir():
                self.assertNotIn("ABCD-EFGH", path.read_text())
        finally:
            gate.set()
            worker.join(3)
        self.assertIsNone(manager.prompt)
        self.assertIsNone(manager.process)
        self.assertEqual(manager.result, "approval_saved_activate_required")

    def test_expiry_clears_approval_and_survives_cli_cleanup(self):
        manager = connect.Enrollment(self.config, self.root)
        marker = self.root / "connect-operation.json"
        marker.touch()
        process = Mock(stdout=io.BytesIO(b"https://example.invalid/activate\nCode: TEST-CODE\n"))
        process.wait.return_value = -15
        manager.process = process
        manager.result = "awaiting_approval"
        manager.prompt = dict(code="OLD-CODE", url="https://example.invalid/activate")
        with patch.object(manager, "terminate"):
            manager.expire(process)
        self.assertIsNone(manager.prompt)
        manager.collect(process)
        self.assertIsNone(manager.prompt)
        self.assertIsNone(manager.process)
        self.assertEqual(manager.result, "approval_expired")
        self.assertFalse(marker.exists())

    def test_upstream_approval_timeout_is_reported_as_expired(self):
        manager = connect.Enrollment(self.config, self.root)
        process = Mock(stdout=io.BytesIO(
            b"CloudCliAuthorizationTimeoutError: Timed out waiting for T3 Connect authorization.\n"))
        process.wait.return_value = 1
        manager.process = process
        manager.result = "awaiting_approval"
        manager.collect(process)
        self.assertEqual(manager.result, "approval_expired")
        self.assertIsNone(manager.prompt)

    def test_page_stylesheet_is_served_under_the_existing_csp(self):
        handler = object.__new__(connect.Handler)
        handler.path = "/connect.css"
        handler.send_response = Mock()
        handler.send_header = Mock()
        handler.end_headers = Mock()
        handler.wfile = io.BytesIO()
        handler.do_GET()
        handler.send_response.assert_called_once_with(200)
        handler.send_header.assert_any_call("Content-Type", "text/css; charset=utf-8")
        handler.send_header.assert_any_call("Cache-Control", "no-store")
        handler.send_header.assert_any_call("Content-Security-Policy",
                                           "default-src 'self'; script-src 'self'; style-src 'self'; "
                                           "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
        self.assertEqual(handler.wfile.getvalue(), (HERE / "t3-connect.css").read_bytes())

    def test_routine_t3_token_and_missing_management_credential_are_rejected(self):
        handler = object.__new__(connect.Handler)
        handler.server = SimpleNamespace(credential="owner-only-secret")
        handler.reply = Mock()
        for token in ("", "Bearer routine-terminal-orchestration-token"):
            handler.headers = {"Authorization": token}
            self.assertFalse(handler.authorized())
            handler.reply.assert_called_with(403, {})
        handler.headers = {"Authorization": "Bearer owner-only-secret"}
        self.assertTrue(handler.authorized())

    def test_post_rejects_anonymous_and_cross_origin_forms_before_starting_any_cli(self):
        manager = Mock()
        handler = object.__new__(connect.Handler)
        handler.server = SimpleNamespace(credential="owner-only-secret", enrollment=manager)
        handler.path = "/link"
        handler.reply = Mock()
        for headers in ({}, {"Authorization": "Bearer owner-only-secret"},
                        {"Authorization": "Bearer owner-only-secret", "X-Minicloud-Connect": "1",
                         "Content-Length": "10"}):
            handler.headers = headers
            handler.do_POST()
            handler.reply.assert_called_with(403, {})
        manager.start.assert_not_called()

    def test_unlink_warns_when_upstream_has_not_confirmed_remote_removal(self):
        manager = connect.Enrollment(self.config, self.root)
        for output, confirmed in (
                ("Disconnected locally. Remove from T3 Connect account page.", False),
                ("Revoked the relay-side environment record.", True)):
            with patch.object(connect.subprocess, "run",
                              return_value=SimpleNamespace(returncode=0, stdout=output)) as run:
                result = manager.unlink()
                self.assertEqual(result["remoteRemovalConfirmed"], confirmed)
                self.assertEqual(run.call_args.args[0], connect.command("logout"))


if __name__ == "__main__":
    unittest.main()
