# SPDX-License-Identifier: MPL-2.0
import hashlib
import os
import re
import stat
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
INSTALL_SH = ROOT / "install.sh"
INSTALL_PS1 = ROOT / "install.ps1"
CUSTOMER_DEPLOY = ROOT / ".github/workflows/customer-deploy.yml"
SEED = ROOT / ".github/workflows/seed.yml"
ARTIFACTS = ROOT / ".local/artifacts"


class InstallerTests(unittest.TestCase):
    def test_shell_installer_rejects_invalid_mode_and_requires_an_exact_source_version(self):
        subprocess.run(["sh", "-n", str(INSTALL_SH)], check=True)
        for mode, version, message in (
            ("invalid", "", "SELFRUN_INSTALL_MODE must be binary or source"),
            ("source", "", "SELFRUN_VERSION must be a full lowercase commit SHA"),
            ("source", "main", "SELFRUN_VERSION must be a full lowercase commit SHA"),
        ):
            with self.subTest(mode=mode, version=version):
                env = {"PATH": os.defpath, "SELFRUN_INSTALL_MODE": mode, "SELFRUN_VERSION": version}
                result = subprocess.run(["sh", str(INSTALL_SH)], env=env, capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(message, result.stderr)

    def test_shell_installer_finalizes_one_executable_and_versioned_notices(self):
        match = re.search(r"FINALIZE = r'''\n(.*?)\n'''", INSTALL_SH.read_text(), re.DOTALL)
        self.assertIsNotNone(match, "FINALIZE snippet not found in install.sh")
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="selfrun-finalize-", dir=ARTIFACTS) as temporary:
            root = Path(temporary).resolve()
            for folder in root.parents:
                info = folder.lstat()
                if not stat.S_ISDIR(info.st_mode) or info.st_mode & 0o022 or info.st_uid not in (0, os.geteuid()):
                    self.skipTest(f"Untrusted ancestor for installer finalization: {folder}")
            source = root / "payload"
            source.mkdir()
            executable = "#!/bin/sh\necho selfrun\n"
            (source / "selfrun").write_text(executable)
            (source / "LICENSE.txt").write_text("MPL-2.0\n")
            destination = root / "bin"
            revision = "0" * 40
            subprocess.run(
                [sys.executable, "-c", match.group(1), str(source), str(destination), "v1.0.0", revision],
                check=True,
            )
            installed = destination / "selfrun"
            self.assertEqual({".selfrun", "selfrun"}, {path.name for path in destination.iterdir()})
            self.assertFalse(installed.is_symlink())
            self.assertEqual(executable, installed.read_text())
            self.assertEqual("selfrun\n", subprocess.check_output([installed], text=True))
            self.assertEqual("MPL-2.0\n", (destination / ".selfrun" / f"v1.0.0-{revision}" / "LICENSE.txt").read_text())

    def test_windows_installer_uses_the_selfrun_executable_and_public_release(self):
        text = INSTALL_PS1.read_text()
        self.assertIn('$Exe = Join-Path $Destination "selfrun.exe"', text)
        self.assertIn('https://github.com/muniperez/selfrun/releases/download/$Tag/', text)
        self.assertIn('Get-InstallSetting "SELFRUN_VERSION"', text)
        self.assertNotIn("selfrun.cmd", text)

    def test_seed_uses_the_reviewed_canonical_installer_bytes(self):
        text = SEED.read_text()
        blob = subprocess.check_output(["git", "hash-object", str(INSTALL_SH)], cwd=ROOT, text=True).strip()
        digest = hashlib.sha256(INSTALL_SH.read_bytes()).hexdigest()
        self.assertIn(f"installer_blob={blob}", text)
        self.assertIn(f"installer_sha256={digest}", text)

    def test_customer_deploy_sets_the_domain_for_each_supported_target(self):
        script = CUSTOMER_DEPLOY.read_text().split("      - name: Deploy with Selfrun CLI\n", 1)[1]
        script = textwrap.dedent(script.split("        run: |\n", 1)[1])
        stub = """selfrun() {
          printf 'dispatch=%s|%s\\n' "$SELFRUN_API_URL" "${SELFRUN_APP_DOMAIN-<discovery>}"
          printf 'arg=%s\\n' "$@"
        }
        """
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        for target, api in (
            ("prod", "https://api.cloud.muni.dev"),
            ("staging", "https://api.cloud-dev.muni.dev"),
            ("self-hosted", "https://api.instance.test"),
        ):
            for domain in ("apps.selfrun.test", ""):
                with self.subTest(target=target, domain=domain), tempfile.TemporaryDirectory(dir=ARTIFACTS) as temporary:
                    env = {
                        "PATH": os.defpath, "SELFRUN_API_KEY_SECRET": "synthetic-token",
                        "POSTGRES_PASSWORD": "", "SELFRUN_ENV_INPUT": target if target != "self-hosted" else "prod",
                        "SELFRUN_API_URL_INPUT": api if target == "self-hosted" else "",
                        "SELFRUN_APP_DOMAIN_INPUT": domain, "DATABASE": "none", "APP_ID": "app_synthetic",
                        "GITHUB_STEP_SUMMARY": str(Path(temporary) / "summary"),
                        "GITHUB_RUN_ID": "1", "GITHUB_SERVER_URL": "https://github.example.test",
                        "GITHUB_REPOSITORY": "synthetic/example",
                    }
                    result = subprocess.run(["bash", "-c", stub + script], env=env, capture_output=True, text=True)
                    if target == "self-hosted" and not domain:
                        self.assertNotEqual(result.returncode, 0)
                        self.assertIn("is required for a self-hosted instance", result.stderr)
                        self.assertNotIn("dispatch=", result.stdout)
                    else:
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assertIn(f"dispatch={api}|{domain or '<discovery>'}", result.stdout)
                        self.assertIn("arg=deploy\narg=--all\narg=--config\narg=selfrun.yml", result.stdout)


if __name__ == "__main__":
    unittest.main()
