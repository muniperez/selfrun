import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import t3_project_defaults as defaults
from runtime import Blocked

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue202/tests"


class ProjectDefaultsTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.settings = self.root / "t3/userdata/settings.json"

    def test_seeds_missing_or_empty_default_and_repeats_without_writing(self):
        for original in (None, {}, {"addProjectBaseDirectory": ""}, {"addProjectBaseDirectory": " \t"}):
            with self.subTest(original=original):
                if self.settings.exists():
                    self.settings.unlink()
                if original is not None:
                    self.settings.parent.mkdir(parents=True, exist_ok=True)
                    self.settings.write_text(json.dumps(original))
                self.assertEqual(defaults.configure(self.settings)["state"], "Configured")
                written = self.settings.read_bytes()
                self.assertEqual(json.loads(written)["addProjectBaseDirectory"], "/workspace/repos")
                self.assertEqual(self.settings.stat().st_mode & 0o777, 0o600)
                with patch.object(defaults, "atomic_json") as write:
                    self.assertEqual(defaults.configure(self.settings)["state"], "AlreadyConfigured")
                write.assert_not_called()
                self.assertEqual(written, self.settings.read_bytes())

    def test_keeps_explicit_defaults_and_existing_projects_and_settings(self):
        self.settings.parent.mkdir(parents=True)
        project = self.root / "repos/imported/README.md"
        project.parent.mkdir(parents=True)
        project.write_bytes(b"existing project")
        state = self.settings.parent / "statev2.sqlite"
        state.write_bytes(b"synthetic project and thread state")
        for base in ("", "/workspace/home/existing", "/workspace", "~/src"):
            with self.subTest(base=base):
                original = dict(addProjectBaseDirectory=base, ghqCloneLayout=True,
                                providerInstances={"custom": {"enabled": False}},
                                customSetting={"keep": True})
                self.settings.write_text(json.dumps(original))
                defaults.configure(self.settings)
                actual = json.loads(self.settings.read_text())
                self.assertEqual(actual, dict(original, addProjectBaseDirectory=base or "/workspace/repos"))
                self.assertEqual(project.read_bytes(), b"existing project")
                self.assertEqual(state.read_bytes(), b"synthetic project and thread state")

    def test_rejects_malformed_settings_and_symlinks_without_replacing(self):
        self.settings.parent.mkdir(parents=True)
        for content in ('[]', '{invalid', '{"addProjectBaseDirectory":null}',
                        '{"addProjectBaseDirectory":42}'):
            with self.subTest(content=content):
                self.settings.write_text(content)
                with self.assertRaises(Blocked):
                    defaults.configure(self.settings)
                self.assertEqual(self.settings.read_text(), content)
        link = self.root / "settings-link"
        link.symlink_to(self.settings)
        with self.assertRaisesRegex(Blocked, "symlink"):
            defaults.configure(link)
        self.assertTrue(link.is_symlink())

    def test_startup_reports_write_failure_without_sensitive_details(self):
        output = io.StringIO()
        with patch.object(defaults.os, "geteuid", return_value=2000), \
                patch.object(defaults.os, "umask"), \
                patch.object(defaults, "configure", side_effect=OSError("sensitive fixture")), \
                patch.object(sys, "stderr", output):
            self.assertEqual(defaults.main(), 1)
        result = json.loads(output.getvalue())
        self.assertEqual(result["state"], "SetupRequired")
        self.assertNotIn("sensitive fixture", output.getvalue())


if __name__ == "__main__":
    unittest.main()
