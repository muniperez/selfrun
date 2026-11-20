#!/usr/bin/env python3
"""Propose a pinned T3 archive for review and qualification."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import tarfile
import tempfile
from urllib.request import Request, urlopen

REPO = "pingdotgg/t3code"
API = f"https://api.github.com/repos/{REPO}"
LOCK = Path(__file__).with_name("manifest.lock.json")
VERSION = r"(?:0|[1-9][0-9]{0,8})(?:\.(?:0|[1-9][0-9]{0,8})){2}"
MAX_ARCHIVE_BYTES = 512 * 1024**2
MAX_MONITOR_BYTES = 32 * 1024**2


def github_json(path):
    req = Request(API + path, headers={"Accept": "application/vnd.github+json",
                                       "X-GitHub-Api-Version": "2022-11-28"})
    with urlopen(req, timeout=30) as response:
        return json.loads(response.read(2 * 1024**2))


def linux_asset(release, version):
    tag = f"v{version}"
    name = f"t3-{version}-linux-x64.tar.gz"
    url = f"https://github.com/{REPO}/releases/download/{tag}/{name}"
    assets = [asset for asset in release["assets"] if asset["name"] == name]
    if len(assets) != 1:
        raise ValueError("Expected exactly one Linux x64 T3 release archive")
    asset = assets[0]
    digest = re.fullmatch(r"sha256:([a-f0-9]{64})", asset.get("digest") or "")
    size = asset.get("size")
    if (not digest or type(size) is not int or not 0 < size <= MAX_ARCHIVE_BYTES
            or asset.get("state") != "uploaded" or asset.get("browser_download_url") != url):
        raise ValueError("Linux x64 archive lacks approved GitHub asset metadata")
    return url, digest[1], size


def monitor_digest(source, size, expected_sha256, member_path):
    digest = hashlib.sha256()
    received = 0
    with tempfile.TemporaryFile() as archive:
        for chunk in iter(lambda: source.read(1024**2), b""):
            received += len(chunk)
            if received > size:
                raise ValueError("T3 archive exceeds its declared size")
            digest.update(chunk)
            archive.write(chunk)
        if received != size or digest.hexdigest() != expected_sha256:
            raise ValueError("T3 archive differs from its published size or SHA256")
        archive.seek(0)
        found = None
        with tarfile.open(fileobj=archive, mode="r|gz") as package:
            for entry in package:
                if entry.name.removeprefix("./") != member_path:
                    continue
                if found is not None or not entry.isfile() or not 0 < entry.size <= MAX_MONITOR_BYTES:
                    raise ValueError("Unexpected T3 resource monitor archive entry")
                with package.extractfile(entry) as monitor:
                    found = hashlib.file_digest(monitor, "sha256").hexdigest()
        if found is None:
            raise ValueError("T3 archive has no resource monitor at the expected path")
        return found


def propose(lock, fetch=github_json, download=urlopen):
    release = fetch("/releases/latest")
    tag = release["tag_name"]
    if (not re.fullmatch("v" + VERSION, tag) or release.get("draft") is not False
            or release.get("prerelease") is not False):
        raise ValueError("Upstream latest release is not a stable vMAJOR.MINOR.PATCH release")
    version = tag[1:]
    current = lock["t3"]["version"]
    if not re.fullmatch(VERSION, current):
        raise ValueError("The committed T3 version is not stable")
    if tuple(map(int, version.split("."))) <= tuple(map(int, current.split("."))):
        return None

    url, digest, size = linux_asset(release, version)
    # The commits endpoint resolves lightweight and annotated tags to the commit.
    commit = fetch(f"/commits/{tag}")["sha"]
    if not re.fullmatch(r"[a-f0-9]{40}", commit):
        raise ValueError("T3 tag did not resolve to a commit SHA")
    member_path = f"t3-{version}-linux-x64/resource-monitor/linux-x64/t3-resource-monitor"
    with download(url, timeout=120) as source:
        resource_monitor = monitor_digest(source, size, digest, member_path)
    return {**lock, "t3": {**lock["t3"], "version": version, "commit": commit, "url": url,
                          "sha256": digest, "resourceMonitor": {"path": member_path, "sha256": resource_monitor}}}


def render(lock, candidate, original):
    # Replace only the T3 object, preserving the existing lockfile layout.
    start = original.index('  "t3":')
    start = original.index("{", start)
    old, end = json.JSONDecoder().raw_decode(original[start:])
    if old != lock["t3"]:
        raise ValueError("Unexpected T3 lockfile layout")
    replacement = json.dumps(candidate["t3"], indent=2).replace("\n", "\n  ")
    return original[:start] + replacement + original[start + end:]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True,
                        help="New file for the proposed lockfile. Existing files are refused.")
    args = parser.parse_args()
    try:
        if args.output.exists():
            raise ValueError("Choose a new candidate output file")
        original = LOCK.read_text()
        lock = json.loads(original)
        candidate = propose(lock)
        if candidate is None:
            print("The pinned T3 version is current or newer than the latest stable release.", file=sys.stderr)
            return
        with args.output.open("x") as output:
            output.write(render(lock, candidate, original))
        print("v" + candidate["t3"]["version"])
    except (KeyError, OSError, ValueError, tarfile.TarError) as error:
        parser.exit(1, f"T3 candidate discovery failed: {error}\n")


if __name__ == "__main__":
    main()
