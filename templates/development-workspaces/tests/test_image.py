import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import sys
import unittest
from unittest.mock import patch


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
