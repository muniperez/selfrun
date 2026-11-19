import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import copilot_provider as provider
from runtime import Blocked

ARTIFACTS = HERE.parents[1] / ".local/artifacts/issue147/tests"


class CopilotProviderTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.settings = self.root / "t3/userdata/settings.json"

    def write_settings(self, data):
        self.settings.parent.mkdir(parents=True, exist_ok=True)
        self.settings.write_text(json.dumps(data))

    def test_seed_and_repeat_preserve_codex_credentials_threads_and_user_settings(self):
        original = dict(providerInstances={"codex": dict(driver="codex", enabled=True,
                                                        config={"binaryPath": "custom-codex"})},
                        customSetting={"keep": True})
        self.write_settings(original)
        credentials = self.root / "home/.copilot/config.json"
        credentials.parent.mkdir(parents=True)
        credentials.write_bytes(b"synthetic-private-login")
        threads = self.settings.parent / "statev2.sqlite"
        threads.write_bytes(b"synthetic-existing-thread")
        first = provider.configure(self.settings)
        written = self.settings.read_bytes()
        second = provider.configure(self.settings)
        self.assertEqual(first["instanceId"], second["instanceId"])
        self.assertEqual(second["state"], "AlreadyConfigured")
        self.assertEqual(written, self.settings.read_bytes())
        settings = json.loads(written)
        self.assertEqual(settings["providerInstances"]["codex"], original["providerInstances"]["codex"])
        self.assertEqual(settings["customSetting"], original["customSetting"])
        self.assertEqual(credentials.read_bytes(), b"synthetic-private-login")
        self.assertEqual(threads.read_bytes(), b"synthetic-existing-thread")
        self.assertNotIn("synthetic-private-login", written.decode())
        self.assertEqual(os.stat(self.settings).st_mode & 0o777, 0o600)

    def test_preserves_existing_custom_id_disabled_provider_without_duplicate(self):
        self.write_settings({"providerInstances": {"my-copilot": dict(
            driver="acpRegistry", enabled=False, environment=[{"name": "HOME", "value": "/custom"}],
            config=dict(source="local", commandPath="copilot", commandArgs=["--acp"]))}})
        before = self.settings.read_bytes()
        self.assertEqual(provider.configure(self.settings)["instanceId"], "my-copilot")
        self.assertEqual(before, self.settings.read_bytes())

    def test_existing_stdio_argument_order_and_extra_options_do_not_duplicate(self):
        self.write_settings({"providerInstances": {"mine": dict(
            driver="acpRegistry", config=dict(source="local", commandPath="/custom/copilot",
                                              commandArgs=["--stdio", "--acp", "--model", "custom"]))}})
        self.assertEqual(provider.configure(self.settings)["instanceId"], "mine")
        self.assertEqual(len(json.loads(self.settings.read_text())["providerInstances"]), 1)

    def test_refuses_collision_invalid_settings_and_symlink_without_replacing(self):
        for content in ('{"providerInstances":{"minicloud-copilot":{"driver":"codex"}}}',
                        '{"providerInstances":[]}', '[]', '{invalid'):
            with self.subTest(content=content):
                self.settings.parent.mkdir(parents=True, exist_ok=True)
                self.settings.write_text(content)
                with self.assertRaises(Blocked):
                    provider.configure(self.settings)
                self.assertEqual(content, self.settings.read_text())
        link = self.root / "linked-settings"
        link.symlink_to(self.settings)
        with self.assertRaisesRegex(Blocked, "symlink"):
            provider.configure(link)

    def executable(self, response):
        executable = self.root / "copilot"
        executable.write_text("#!" + sys.executable + "\n"
                              "import sys, json\n"
                              "assert sys.argv[1:] == ['--acp', '--stdio']\n"
                              "request = json.loads(sys.stdin.readline())\n"
                              "assert request['method'] == 'initialize'\n"
                              "sys.stderr.write('synthetic-sensitive-error')\n" + response + "\n")
        executable.chmod(0o700)
        return str(executable)

    def test_real_stdio_initialize_requires_pinned_identity_and_does_not_claim_signin(self):
        executable = self.executable(
            "print(json.dumps({'id': 1, 'result': {'protocolVersion': 1, "
            "'agentInfo': {'name':'Copilot', 'version':'1.0.114'}}}), flush=True)")
        result = provider.probe(executable)
        self.assertEqual(result["state"], "Compatible")
        self.assertEqual(result["authentication"], "NotChecked")
        self.assertIn("copilot login --device-code", result["signIn"])

    def test_incompatible_missing_malformed_and_timeout_are_actionable_and_redacted(self):
        responses = [
            "print(json.dumps({'id':1,'result':{'protocolVersion':1,"
            "'agentInfo':{'name':'Copilot','version':'0.0.1'}}}), flush=True)",
            "print('not-json synthetic-sensitive-error', flush=True)",
            "import time; time.sleep(10)"]
        for response in responses:
            with self.subTest(response=response):
                with self.assertRaises(Blocked) as caught:
                    provider.probe(self.executable(response), timeout=0.15)
                self.assertNotIn("synthetic-sensitive-error", str(caught.exception))
                self.assertRegex(str(caught.exception), "rebuild")
        with self.assertRaisesRegex(Blocked, "rebuild"):
            provider.probe(str(self.root / "missing"))


if __name__ == "__main__":
    unittest.main()