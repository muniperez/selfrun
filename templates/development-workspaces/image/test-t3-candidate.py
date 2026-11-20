import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import unittest

spec = importlib.util.spec_from_file_location("candidate", Path(__file__).with_name("t3-candidate.py"))
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)
VERSION = "0.1.11"
MONITOR = f"t3-{VERSION}-linux-x64/resource-monitor/linux-x64/t3-resource-monitor"


def archive(entries):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w:gz") as package:
        for member, content in entries:
            entry = tarfile.TarInfo(member)
            entry.size = len(content)
            package.addfile(entry, io.BytesIO(content))
    return output.getvalue()


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.original = candidate.LOCK.read_text()
        self.lock = json.loads(self.original)
        self.lock["t3"]["version"] = "0.1.9"
        self.data = archive([("./" + MONITOR, b"monitor")])
        self.release = {
            "tag_name": f"v{VERSION}", "draft": False, "prerelease": False,
            "assets": [{
                "name": f"t3-{VERSION}-linux-x64.tar.gz",
                "digest": "sha256:" + hashlib.sha256(self.data).hexdigest(),
                "size": len(self.data), "state": "uploaded",
                "browser_download_url": (
                    f"https://github.com/pingdotgg/t3code/releases/download/v{VERSION}/t3-{VERSION}-linux-x64.tar.gz"
                ),
            }],
        }

    def fetch(self, path):
        return {"/releases/latest": self.release, f"/commits/v{VERSION}": {"sha": "a" * 40}}[path]

    def download(self, url, *, timeout):
        return io.BytesIO(self.data)

    def propose(self):
        return candidate.propose(self.lock, self.fetch, self.download)

    def test_pins_new_stable_release_archive_and_monitor(self):
        result = self.propose()
        self.assertEqual(result["t3"]["version"], VERSION)
        self.assertEqual(result["t3"]["resourceMonitor"],
                         {"sha256": hashlib.sha256(b"monitor").hexdigest(), "path": MONITOR})
        self.assertEqual(result["t3"]["commit"], "a" * 40)
        self.assertEqual(result["t3"]["environmentProtocol"], self.lock["t3"]["environmentProtocol"])
        self.assertEqual(result["t3"]["relayClient"], self.lock["t3"]["relayClient"])
        self.assertEqual(json.loads(candidate.render(json.loads(self.original), result, self.original)), result)

    def test_current_or_newer_pin_skips_asset_lookup(self):
        for current in (VERSION, "0.2.0", "1.0.0"):
            self.lock["t3"]["version"] = current
            self.release["assets"] = []
            self.assertIsNone(self.propose())

    def test_prerelease_fails_closed(self):
        self.release["prerelease"] = True
        with self.assertRaisesRegex(ValueError, "not a stable"):
            self.propose()

    def test_requires_published_archive_digest_and_canonical_url(self):
        for key, value in (("digest", None), ("browser_download_url", "https://example.test/archive")):
            asset = self.release["assets"][0]
            before = asset[key]
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "approved GitHub asset metadata"):
                asset[key] = value
                self.propose()
            asset[key] = before

    def test_download_must_match_published_size_and_digest(self):
        self.release["assets"][0]["digest"] = "sha256:" + "f" * 64
        with self.assertRaisesRegex(ValueError, "published size or SHA256"):
            self.propose()

    def test_absent_or_duplicate_monitor_fails_closed(self):
        for entries in ([], [(MONITOR, b"one"), ("./" + MONITOR, b"two")]):
            self.data = archive(entries)
            self.release["assets"][0].update(size=len(self.data),
                digest="sha256:" + hashlib.sha256(self.data).hexdigest())
            with self.subTest(entries=entries), self.assertRaisesRegex(ValueError, "resource monitor"):
                self.propose()


if __name__ == "__main__":
    unittest.main()
