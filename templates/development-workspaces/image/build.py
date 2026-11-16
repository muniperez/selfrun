#!/usr/bin/env python3
"""Install ONLY inside an explicitly marked disposable Ubuntu image builder VM."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import urllib.request

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
from runtime import Blocked, atomic_json, require, run


def builder_guard(acknowledgement):
    require(acknowledgement == "DESTROY-DISPOSABLE-IMAGE-BUILDER",
            "Explicit disposable-image-builder acknowledgement required")
    marker = Path("/etc/minicloud-disposable-image-builder")
    require(marker.is_file() and marker.stat().st_uid == 0 and
            marker.read_text().strip() == acknowledgement, "Disposable builder marker missing")
    require(os.geteuid() == 0 and os.uname().machine == "x86_64", "Root linux/amd64 builder required")
    require('VERSION_ID="24.04"' in Path("/etc/os-release").read_text(), "Ubuntu 24.04 required")
    manifest = json.loads((HERE / "manifest.lock.json").read_text())
    build = {}
    for line in Path("/etc/cloud/build.info").read_text().splitlines():
        match = re.fullmatch(r"([a-zA-Z0-9_]+)\s*[:=]\s*([^#]+?)\s*", line)
        if match:
            build[match[1]] = match[2].strip("'\"")
    require(build.get("serial") == manifest["os"]["cloudImageSerial"],
            "Builder cloud-image serial does not match the pinned Ubuntu image")
    require(not Path("/etc/minicloud-workspace/runtime.json").exists(), "Already enrolled runtime")
    require(not os.path.ismount("/workspace") and
            not list(Path("/dev/disk/by-id").glob("*Volume*")), "Builder has workspace volume")
    require(run(["systemd-detect-virt", "--vm"]) not in ("", "none"), "A disposable VM is required")


def download(item, target):
    with urllib.request.urlopen(item["url"], timeout=120) as source, target.open("wb") as output:
        shutil.copyfileobj(source, output)
    algorithm = "sha512" if "sha512" in item else "sha256"
    checksum = hashlib.new(algorithm)
    with target.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(data)
    require(checksum.hexdigest() == item[algorithm], "Pinned artifact checksum mismatch")


def install_systemd_units(source_directory, destination_directory):
    for source in source_directory.iterdir():
        destination = destination_directory / source.name
        shutil.copy2(source, destination)
        os.chown(destination, 0, 0)
        destination.chmod(0o644)


def install(source_revision, builder):
    require(re.fullmatch(r"[a-f0-9]{40}", source_revision), "Exact source commit required")
    require(builder, "Builder provenance identity required")
    lock = json.loads((HERE / "manifest.lock.json").read_text())
    cache = Path.cwd() / ".local/artifacts/issue110/runtime/image-build"
    cache.mkdir(mode=0o700, parents=True, exist_ok=True)
    snapshot = lock["os"]["aptSnapshot"]
    sources = Path("/etc/apt/sources.list.d")
    for path in sources.iterdir():
        if path.suffix in (".sources", ".list"):
            path.unlink()
    Path("/etc/apt/sources.list").write_text(
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble main universe\n"
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble-updates main universe\n"
        "deb [check-valid-until=no] https://snapshot.ubuntu.com/ubuntu/" + snapshot +
        "/ noble-security main universe\n")
    env = dict(os.environ, DEBIAN_FRONTEND="noninteractive")
    run(["apt-get", "-qq", "update"], env=env, timeout=1800)
    packages = ["python3", "git", "curl", "ca-certificates", "openssh-server", "openssh-client",
                "age", "postgresql-16", "postgresql-client-16", "util-linux", "e2fsprogs",
                "libicu74", "libssl3t64", "xz-utils", "build-essential", "pkg-config",
                "unzip", "zip", "jq", "ripgrep", "sqlite3", *lock["browserLibraries"]]
    run(["apt-get", "-y", "-qq", "install", "--no-install-recommends", *packages], env=env, timeout=1800)
    for name in ("node", "dotnet", "t3"):
        artifact = cache / (name + ".tar")
        download(lock[name], artifact)
        destination = Path("/opt") / ("minicloud-" + name)
        destination.mkdir(parents=True, exist_ok=True)
        argv = ["tar", "--no-same-owner", "-xf", str(artifact), "-C", str(destination)]
        if name == "node":
            argv += ["--strip-components=1"]
        run(argv)
    for executable in ("node", "npm", "npx"):
        Path("/usr/local/bin", executable).symlink_to("/opt/minicloud-node/bin/" + executable)
    Path("/usr/local/bin/dotnet").symlink_to("/opt/minicloud-dotnet/dotnet")
    t3 = list(Path("/opt/minicloud-t3").rglob("t3"))
    require(len(t3) == 1 and t3[0].is_file(), "Unexpected T3 release archive layout")
    Path("/usr/local/bin/t3").symlink_to(t3[0])
    monitor = Path("/opt/minicloud-t3") / lock["t3"]["resourceMonitor"]["path"]
    with monitor.open("rb") as binary:
        require(hashlib.file_digest(binary, "sha256").hexdigest() ==
                lock["t3"]["resourceMonitor"]["sha256"], "Pinned T3 telemetry helper checksum mismatch")
    tools = Path("/opt/minicloud-tools")
    tools.mkdir(exist_ok=True)
    for name in ("package.json", "package-lock.json"):
        shutil.copy2(HERE / "tools" / name, tools / name)
    run(["npm", "ci", "--prefix", str(tools), "--ignore-scripts", "--omit=dev"],
        env=dict(os.environ, HOME=str(cache), npm_config_cache=str(cache / "npm")), timeout=600)
    Path("/usr/local/bin/codex").symlink_to(tools / "node_modules/.bin/codex")
    run(["groupadd", "--gid", "2000", "coding"])
    run(["useradd", "--uid", "2000", "--gid", "2000", "--home-dir", "/workspace/home",
         "--no-create-home", "--shell", "/usr/sbin/nologin", "coding"])
    runtime = Path("/opt/minicloud-workspace")
    runtime.mkdir(exist_ok=True)
    for name in ("runtime.py", "bootstrap.py", "issue-t3-session.py", "git-credential.py"):
        shutil.copy2(HERE.parent / name, runtime / name)
        (runtime / name).chmod(0o755)
    install_systemd_units(HERE.parent / "systemd", Path("/etc/systemd/system"))
    Path("/var/cache/minicloud-workspace/verification").mkdir(parents=True, exist_ok=True, mode=0o711)
    Path("/var/cache/minicloud-workspace").chmod(0o711)
    Path("/etc/minicloud-workspace").mkdir(mode=0o700, exist_ok=True)
    run(["systemctl", "mask", "postgresql.service", "postgresql@.service",
         "apt-daily.service", "apt-daily-upgrade.service", "apt-daily.timer",
         "apt-daily-upgrade.timer", "unattended-upgrades.service"])
    Path("/etc/ssh/sshd_config.d/00-minicloud-workspace.conf").write_text(
        "PasswordAuthentication no\nKbdInteractiveAuthentication no\n"
        "PermitRootLogin prohibit-password\nAllowUsers root\n"
        "AllowAgentForwarding no\nPermitTunnel no\nX11Forwarding no\n"
        "GatewayPorts no\nAllowTcpForwarding local\n")
    run(["systemctl", "enable", "minicloud-workspace.service"])
    require(run(["node", "--version"]) == "v" + lock["node"]["version"], "Wrong Node version")
    require(run(["dotnet", "--version"]) == lock["dotnet"]["version"], "Wrong .NET version")
    require(lock["codex"]["version"] in run(["codex", "--version"]), "Wrong Codex version")
    require(lock["t3"]["version"] in run(["t3", "--version"]), "Wrong T3 version")
    # Do not assume auth CLI options merely because an upstream source revision supports them.
    help_text = run(["t3", "auth", "session", "issue", "--help"])
    require(all(option in help_text for option in
                ("--token-only", "--ttl", "--subject", "--scope", "--base-dir")),
            "Installed T3 lacks required headless session issuance")
    lock["provenance"].update(sourceRevision=source_revision, builderIdentity=builder,
                              installedPackages=run(["dpkg-query", "-W", "-f=${Package}=${Version}\n"]),
                              toolArtifactsVerified=True, headlessAuthOptionsChecked=True)
    atomic_json("/opt/minicloud-workspace/image-manifest.json", lock)
    shutil.rmtree(cache)


def generalize():
    # Guard is applied by main before either install or this destructive operation.
    os.chdir("/")
    for unit in ("minicloud-workspace.service", "minicloud-t3.service",
                 "minicloud-postgres.service", "postgresql.service", "postgresql@16-main.service"):
        subprocess.run(["systemctl", "stop", unit], capture_output=True, timeout=120, check=False)
    require(not os.path.ismount("/workspace"), "Never erase an attached workspace")
    for path in ("/workspace", "/root/.codex", "/root/.t3", "/root/.config", "/root/.cache",
                 "/root/.npm", "/root/.ssh", "/home/ubuntu/.ssh", "/var/lib/minicloud-workspace",
                 "/var/cache/minicloud-workspace/verification", "/var/lib/postgresql",
                 "/etc/minicloud-workspace", "/var/log/journal"):
        target = Path(path)
        if target.is_dir() and not target.is_symlink():
            shutil.rmtree(target)
        else:
            target.unlink(missing_ok=True)
    for home in Path("/home").iterdir():
        if home.is_dir():
            shutil.rmtree(home)
    for path in Path("/root").iterdir():
        if path.is_dir() and not path.is_symlink():
            shutil.rmtree(path)
        else:
            path.unlink()
    staging = Path("/opt/minicloud-image-source")
    if staging.exists():
        shutil.rmtree(staging)
    for path in Path("/etc/ssh").glob("ssh_host_*"):
        path.unlink()
    for name in ("/root/.bash_history", "/root/.git-credentials", "/root/.gitconfig",
                 "/etc/ssh/ssh_host_ed25519_key", "/var/lib/dbus/machine-id",
                 "/var/lib/systemd/random-seed", "/var/lib/systemd/credential.secret"):
        Path(name).unlink(missing_ok=True)
    run(["cloud-init", "clean", "--logs", "--seed", "--machine-id"])
    Path("/etc/machine-id").write_text("")
    for path in Path("/var/log").rglob("*"):
        if path.is_file() and not path.is_symlink():
            with path.open("wb"):
                pass
    require(not list(Path("/etc/ssh").glob("ssh_host_*")), "SSH host keys remain")
    Path("/etc/minicloud-disposable-image-builder").unlink()
    os.sync()
    print(json.dumps({"type": "ImageGeneralized", "captureOnlyAfterPowerOff": True,
                      "providerVolumeEncryptionClaim": False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("install", "generalize"))
    parser.add_argument("--acknowledgement", required=True)
    parser.add_argument("--source-revision")
    parser.add_argument("--builder-identity")
    args = parser.parse_args()
    builder_guard(args.acknowledgement)
    if args.action == "install":
        install(args.source_revision or "", args.builder_identity)
    else:
        generalize()


if __name__ == "__main__":
    main()
