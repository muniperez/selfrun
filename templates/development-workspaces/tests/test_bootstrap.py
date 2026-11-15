import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

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
        self.manifest = dict(architecture="linux/amd64", provenance=dict(toolArtifactsVerified=True))
        self.manifest_bytes = (json.dumps(self.manifest, indent=2) + "\n").encode()

    def execute(self, opener):
        def load(path):
            return self.envelope if Path(path) == self.enrollment else self.config
        read_text = Path.read_text
        read_bytes = Path.read_bytes
        def read(path, *args, **kwargs):
            if str(path) == "/opt/minicloud-workspace/image-manifest.json":
                return self.manifest_bytes.decode()
            return read_text(path, *args, **kwargs)
        def read_raw(path):
            if str(path) == "/opt/minicloud-workspace/image-manifest.json":
                return self.manifest_bytes
            return read_bytes(path)
        with patch.object(bootstrap, "load_config", side_effect=load), \
             patch.object(bootstrap, "call", return_value={"type": "BootstrapReady"}), \
             patch.object(bootstrap, "run") as process, \
             patch.object(Path, "read_text", read), \
             patch.object(Path, "read_bytes", read_raw), \
             patch.object(bootstrap.urllib.request, "build_opener", return_value=opener):
            result = bootstrap.bootstrap(self.root / "config.json", self.enrollment, self.root / "state")
        self.assertTrue(all(call.args[0][0] == "systemctl" for call in process.call_args_list))
        return result

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
        arguments = ["issue-t3-session.py", "--vm-id", "vm", "--generation", "2",
                     "--operation-id", "session", "--subject", "authorized-user", "--ttl", "120",
                     "--scope", "fixture-scope-one", "--scope", "fixture-scope-two",
                     "--state-dir", str(self.root)]
        captured = io.StringIO()
        fake_runtime = SimpleNamespace(s={"phase": "Ready"}, validate_request=lambda request: None)
        with patch.object(sys, "argv", arguments), patch.object(sys, "stdout", captured), \
             patch.object(issuer.os, "geteuid", return_value=0), \
             patch.object(issuer, "load_config", return_value=self.config), \
             patch.object(issuer, "Runtime", return_value=fake_runtime), \
             patch.object(issuer, "run", return_value="--token-only --ttl --subject --scope --base-dir"), \
             patch.object(issuer.subprocess, "run",
                          return_value=SimpleNamespace(returncode=0, stdout="fixture-sensitive-output\n")) as command:
            issuer.main()
        argv = command.call_args.args[0]
        self.assertIn("120s", argv)
        self.assertEqual(2, argv.count("--scope"))
        self.assertNotIn("--scopes", argv)
        self.assertNotIn("--dpop", argv)
        self.assertEqual(self.config["workspace"], command.call_args.kwargs["cwd"])
        self.assertEqual("fixture-sensitive-output\n", captured.getvalue())


if __name__ == "__main__":
    unittest.main()
