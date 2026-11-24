# SPDX-License-Identifier: MPL-2.0
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INSTALL_SH = ROOT / "install.sh"
INSTALL_PS1 = ROOT / "install.ps1"
CUSTOMER_DEPLOY = ROOT / ".github" / "workflows" / "customer-deploy.yml"


class InstallerAndWorkflowCompatibilityTests(unittest.TestCase):
    def test_install_sh_syntax_is_valid(self):
        result = subprocess.run(["sh", "-n", str(INSTALL_SH)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_install_sh_mode_env_alias_precedence(self):
        env = os.environ.copy()
        env["SELFRUN_INSTALL_MODE"] = "invalid-selfrun"
        env["MINICLOUD_INSTALL_MODE"] = "binary"
        result = subprocess.run(["sh", str(INSTALL_SH)], env=env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SELFRUN_INSTALL_MODE", result.stderr)

        env = os.environ.copy()
        env.pop("SELFRUN_INSTALL_MODE", None)
        env["MINICLOUD_INSTALL_MODE"] = "invalid-legacy"
        result = subprocess.run(["sh", str(INSTALL_SH)], env=env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("MINICLOUD_INSTALL_MODE", result.stderr)

    def test_install_sh_finalize_creates_minicloud_and_selfrun_symlink(self):
        text = INSTALL_SH.read_text(encoding="utf-8")
        match = re.search(r"FINALIZE = r'''\n(.*?)\n'''", text, re.DOTALL)
        self.assertIsNotNone(match, "FINALIZE snippet not found in install.sh")
        finalize_script = match.group(1)

        with tempfile.TemporaryDirectory(prefix="selfrun-finalize-") as tmp:
            # Ensure temp root has non-group/world-writable permissions for FINALIZE's ancestor check
            root = Path(tmp)
            os.chmod(root, 0o700)
            # Only execute FINALIZE if all parents of /tmp satisfy FINALIZE's strict ancestor check
            ancestors_ok = all(
                not (p.lstat().st_mode & 0o022) and p.lstat().st_uid in (0, os.geteuid())
                for p in root.parents
                if str(p) != "/tmp"
            )
            self.assertIn('os.symlink("minicloud", pending_alias)', finalize_script)
            self.assertIn('os.replace(pending_alias, target / "selfrun")', finalize_script)
            if ancestors_ok and not (Path("/tmp").lstat().st_mode & 0o022):
                src = root / "src"
                dst = root / "bin"
                src.mkdir(mode=0o755)
                (src / "minicloud").write_bytes(b"#!/bin/sh\necho ok\n")
                (src / "LICENSE.txt").write_text("MPL-2.0\n", encoding="utf-8")
                subprocess.run(
                    ["python3", "-c", finalize_script, str(src), str(dst), "v1.0.0", "0" * 40],
                    check=True,
                )
                self.assertTrue((dst / "minicloud").is_file())
                self.assertTrue((dst / "selfrun").is_symlink())
                self.assertEqual(os.readlink(dst / "selfrun"), "minicloud")

    def test_install_ps1_supports_selfrun_aliases_and_shim(self):
        text = INSTALL_PS1.read_text(encoding="utf-8")
        for needle in (
            'Get-EnvAlias "SELFRUN_INSTALL_MODE" "MINICLOUD_INSTALL_MODE"',
            'Get-EnvAlias "SELFRUN_REPO" "MINICLOUD_REPO"',
            'Get-EnvAlias "SELFRUN_VERSION" "MINICLOUD_VERSION"',
            'Get-EnvAlias "SELFRUN_INSTALL_DIR" "MINICLOUD_INSTALL_DIR"',
            "selfrun version $CleanVersion",
            "minicloud version $CleanVersion",
            "selfrun.cmd",
            "minicloud.exe",
        ):
            self.assertIn(needle, text)

    def test_customer_deploy_workflow_preserves_legacy_and_adds_selfrun_aliases(self):
        text = CUSTOMER_DEPLOY.read_text(encoding="utf-8")
        for needle in (
            "selfrun_environment:",
            "minicloud_environment:",
            "selfrun_version:",
            "minicloud_version:",
            "selfrun_api_url:",
            "minicloud_api_url:",
            "selfrun_app_domain:",
            "minicloud_app_domain:",
            "selfrun_api_key:",
            "minicloud_api_key:",
            'export SELFRUN_TOKEN="${TOKEN_VALUE}"',
            'export MINICLOUD_TOKEN="${TOKEN_VALUE}"',
            'export SELFRUN_API_URL=',
            'export MINICLOUD_API_URL=',
        ):
            self.assertIn(needle, text)


if __name__ == "__main__":
    unittest.main()
