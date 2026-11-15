import contextlib
import importlib.util
import io
import os
from pathlib import Path
import shlex
import subprocess
import sys
from types import SimpleNamespace
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HERE))
import runtime
spec = importlib.util.spec_from_file_location("credential_helper", HERE / "git-credential.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class CredentialTests(unittest.TestCase):
    def setUp(self):
        self.credential = dict(origin="https://github.com/fixture/project.git",
                               username="x-access-token", password="synthetic-fixture-not-a-token",
                               expiresAt=1600)
        self.fields = dict(protocol="https", host="github.com", path="fixture/project.git")

    def test_exact_selected_repository_and_valid_one_hour_window_only(self):
        self.assertIsNotNone(helper.selected_credential(self.fields, self.credential, 1000))
        for key, value in (("protocol", "http"), ("host", "other.invalid"),
                           ("host", "github.com.evil.invalid"), ("host", "github.com:443"),
                           ("path", "fixture/other.git"), ("path", "fixture/project.git/"),
                           ("path", "/fixture/project.git"), ("username", "other-user")):
            with self.subTest(key=key, value=value):
                self.assertIsNone(helper.selected_credential(dict(self.fields, **{key: value}),
                                                            self.credential, 1000))
        for expiry in (999, 1000, 4601, True):
            with self.subTest(expiry=expiry):
                self.assertIsNone(helper.selected_credential(
                    self.fields, dict(self.credential, expiresAt=expiry), 1000))
        self.assertIsNotNone(helper.selected_credential(
            self.fields, dict(self.credential, expiresAt=4600), 1000))

    def invoke(self, action, request, credential=None):
        output, error = io.StringIO(), io.StringIO()
        with patch.object(sys, "argv", ["git-credential.py", action]), \
             patch.object(sys, "stdin", io.StringIO(request)), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(error), \
             patch.object(helper, "load_credential", return_value=credential or self.credential) as load, \
             patch.object(helper.time, "time", return_value=1000):
            helper.main()
        return output.getvalue(), error.getvalue(), load.call_count

    def test_get_is_secret_only_while_wrong_requests_store_erase_and_errors_are_silent(self):
        request = "protocol=https\nhost=github.com\npath=fixture/project.git\nwwwauth[]=Basic realm=GitHub\n\n"
        output, error, count = self.invoke("get", request)
        self.assertTrue(output.startswith("username=x-access-token\npassword="))
        self.assertEqual(1, count)
        self.assertEqual("", error)
        for action in ("store", "erase", "unknown"):
            self.assertEqual(("", "", 0), self.invoke(action, request))
        for invalid in (request.replace("github.com", "other.invalid"),
                        request.replace("\n\n", "\nhost=other.invalid\n\n"),
                        request.replace("path=fixture/project.git\n", ""),
                        request.replace("project.git", "project.git\r"),
                        "protocol=https\nhost=" + "x" * 17000 + "\n\n"):
            with self.subTest(request_kind=len(invalid)):
                output, error, _ = self.invoke("get", invalid)
                self.assertEqual("", output)
                self.assertEqual("", error)
        bad = dict(self.credential, password="synthetic\ninjection")
        self.assertEqual(("", ""), self.invoke("get", request, bad)[:2])

    def test_missing_or_unreadable_secret_never_emits_diagnostics(self):
        output, error = io.StringIO(), io.StringIO()
        with patch.object(sys, "argv", ["git-credential.py", "get"]), \
             patch.object(sys, "stdin", io.StringIO("protocol=https\nhost=github.com\npath=x/y.git\n\n")), \
             contextlib.redirect_stdout(output), contextlib.redirect_stderr(error), \
             patch.object(helper, "load_credential", side_effect=OSError("sensitive fixture error")):
            helper.main()
        self.assertEqual("", output.getvalue())
        self.assertEqual("", error.getvalue())

    def test_complete_eof_request_is_accepted_but_truncated_field_is_rejected(self):
        request = "protocol=https\nhost=github.com\npath=fixture/project.git\n"
        self.assertEqual(self.invoke("get", request + "\n"), self.invoke("get", request))
        self.assertEqual(("", "", 0), self.invoke("get", request.rstrip("\n")))

    def test_actual_git_credential_fill_can_read_synthetic_credential(self):
        source = (
            "import importlib.util;"
            f"s=importlib.util.spec_from_file_location('helper',{str(HERE / 'git-credential.py')!r});"
            "h=importlib.util.module_from_spec(s);s.loader.exec_module(h);"
            f"h.load_credential=lambda:{self.credential!r};"
            "h.time.time=lambda:1000;h.main()"
        )
        command = "!" + shlex.join([sys.executable, "-B", "-c", source])
        result = subprocess.run(
            ["git", "-c", "credential.helper=", "-c", "credential.helper=" + command,
             "-c", "credential.useHttpPath=true", "credential", "fill"],
            input="protocol=https\nhost=github.com\npath=fixture/project.git\n\n",
            text=True, capture_output=True, timeout=10,
            env={"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": "/nonexistent",
                 "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": "/dev/null",
                 "GIT_TERMINAL_PROMPT": "0"})
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("username=x-access-token\n", result.stdout)
        self.assertIn("password=synthetic-fixture-not-a-token\n", result.stdout)
        self.assertEqual("", result.stderr)

    def test_runtime_git_selects_only_root_helper_and_runs_all_git_work_unprivileged(self):
        git = runtime.Git()
        with patch.object(Path, "exists", return_value=True), \
             patch.object(Path, "stat", return_value=SimpleNamespace(st_uid=0, st_mode=0o100755)), \
             patch.object(Path, "is_file", return_value=True), \
             patch.object(Path, "is_symlink", return_value=False), \
             patch.object(runtime, "run", return_value="") as command:
            git.command("/workspace/repos/fixture", "ls-remote")
            argv = command.call_args.args[0]
            helpers = [arg for arg in argv if arg.startswith("credential.helper=")]
            self.assertEqual(["credential.helper=",
                              "credential.helper=/opt/minicloud-workspace/git-credential.py"], helpers)
            self.assertEqual(2000, command.call_args.kwargs["user"])
            self.assertEqual([], command.call_args.kwargs["extra_groups"])
            self.assertIn("credential.useHttpPath=true", argv)
            self.assertIn("http.followRedirects=false", argv)
            self.assertEqual("0", command.call_args.kwargs["env"]["GIT_TERMINAL_PROMPT"])
            with patch.object(Path, "exists", return_value=False):
                git.command("/workspace/repos/fixture", "ls-remote")
            self.assertEqual(["credential.helper="],
                             [arg for arg in command.call_args.args[0] if arg.startswith("credential.helper=")])


if __name__ == "__main__":
    unittest.main()
