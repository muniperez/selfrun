# SPDX-License-Identifier: MPL-2.0
"""Bounded stock DigitalOcean package inventory; never execute vendor files."""
import pathlib
import re
import stat
import subprocess
import sys


def validate_provider_layout(provider, base=pathlib.Path("/"), run=subprocess.run):
    root = base / "opt/digitalocean"
    if not root.exists() and not root.is_symlink():
        return
    assert provider == "digitalocean"

    # Vendor units name /opt/digitalocean/bin/{do-agent,droplet-agent};
    # their update jobs name /opt/digitalocean/<package>/scripts/update.sh.
    # do-agent 3.18.17 ships some entries as 1000:nogroup, not root.
    packages = {
        "do-agent": {(0, 0), (1000, 65534)},
        "droplet-agent": {(0, 0)},
    }
    files = {
        "bin/do-agent": "do-agent",
        "do-agent/scripts/update.sh": "do-agent",
        "bin/droplet-agent": "droplet-agent",
        "droplet-agent/scripts/update.sh": "droplet-agent",
    }
    directories = {
        "": set(packages), "bin": set(packages),
        "do-agent": {"do-agent"}, "do-agent/scripts": {"do-agent"},
        "droplet-agent": {"droplet-agent"}, "droplet-agent/scripts": {"droplet-agent"},
    }

    def command(args):
        result = run(args, capture_output=True, timeout=20, check=False)
        assert result.returncode == 0 and not result.stderr.strip()
        return result.stdout.decode()

    present = {package for name, package in files.items()
               if (root / name).exists() or (root / name).is_symlink()}
    assert present
    expected_files = {name: package for name, package in files.items() if package in present}
    expected_dirs = {name: owners & present for name, owners in directories.items() if owners & present}
    for package in sorted(present):
        status = command(["dpkg-query", "-W", "-f=${Status}\n${Architecture}\n", package]).splitlines()
        assert len(status) == 2 and status[0] == "install ok installed" and status[1] in ("amd64", "arm64")
        # dpkg --verify cannot verify a file without a recorded checksum.
        # Ask dpkg for its control metadata; dpkg itself performs the hashing.
        checksums = command(["dpkg-query", "--control-show", package, "md5sums"]).splitlines()
        for name, authority in expected_files.items():
            if authority == package:
                assert any(re.fullmatch(r"[a-f0-9]{32}  opt/digitalocean/" + re.escape(name), line)
                           for line in checksums)
        assert not command(["dpkg", "--verify", package]).strip()

    # Check every entry (including directories) against dpkg's ownership authority.
    # Exact enumeration rejects additional data, links, sockets and package lookalikes.
    entries = {"": root}
    for directory in expected_dirs:
        p = root / directory
        info = p.lstat()
        assert stat.S_ISDIR(info.st_mode)
        for child in p.iterdir():
            entries[child.relative_to(root).as_posix()] = child
    assert set(entries) == set(expected_files) | set(expected_dirs)
    for name, p in entries.items():
        info = p.lstat()
        owners = expected_dirs[name] if name in expected_dirs else {expected_files[name]}
        assert (stat.S_ISDIR(info.st_mode) if name in expected_dirs else stat.S_ISREG(info.st_mode))
        assert stat.S_IMODE(info.st_mode) == 0o755
        assert (info.st_uid, info.st_gid) in set().union(*(packages[package] for package in owners))
        logical = "/opt/digitalocean" + ("/" + name if name else "")
        records = command(["dpkg-query", "-S", logical]).splitlines()
        claimed = set()
        for record in records:
            prefix, separator, target = record.partition(": ")
            assert separator and target == logical
            for package in prefix.split(", "):
                assert re.fullmatch(r"(do-agent|droplet-agent)(?::(?:amd64|arm64))?", package)
                claimed.add(package.split(":")[0])
        assert claimed and claimed <= owners
