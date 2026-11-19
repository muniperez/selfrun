#!/usr/bin/env python3
"""One-shot prepared-image bootstrap. Never installs tools; formats only blank new raw volumes."""
import argparse
import errno
import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import sys
import time
import urllib.error
import urllib.request

from runtime import Blocked, atomic_json, canonical_remote, load_config, require, run


GIT_CREDENTIAL_HELPER = "/opt/minicloud-workspace/git-credential.py"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise Blocked("Enrollment redirects are forbidden")


def call(request, socket_path="/run/minicloud-workspace/control.sock"):
    deadline = time.monotonic() + 30
    last_reason = "not attempted"
    while True:
        remaining = deadline - time.monotonic()
        require(remaining > 0,
                f"Runtime control socket readiness timed out after 30 seconds ({last_reason})")
        with socket.socket(socket.AF_UNIX) as client:
            client.settimeout(min(remaining, 1))
            try:
                client.connect(socket_path)
            except OSError as error:
                if error.errno not in (errno.ENOENT, errno.ECONNREFUSED):
                    code = error.errno if type(error.errno) is int and 0 < error.errno < 4096 else "unknown"
                    raise Blocked(f"Runtime control socket connection failed (errno {code}); not retried") from None
                last_reason = "ENOENT" if error.errno == errno.ENOENT else "ECONNREFUSED"
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise Blocked(
                        f"Runtime control socket readiness timed out after 30 seconds ({last_reason})") from None
                time.sleep(min(0.1, remaining))
                continue
            # Never reconnect once any request bytes may have been sent.
            client.settimeout(600)
            client.sendall(json.dumps(request).encode() + b"\n")
            data = b""
            while not data.endswith(b"\n"):
                part = client.recv(65536)
                require(part and len(data) + len(part) <= 1048576, "Invalid runtime response")
                data += part
            response = json.loads(data)
            require(response.get("ok"), response.get("reason", "Runtime blocked"))
            return response["result"]


def configure_user_git(config):
    if config["kind"] != "ephemeral" or not config.get("repositories"):
        return
    environment = dict(PATH="/usr/bin:/bin", HOME=str(Path(config["workspace"]) / "home"),
                       GIT_CONFIG_NOSYSTEM="1", GIT_TERMINAL_PROMPT="0")
    for repository in config.get("repositories", []):
        origin = canonical_remote(repository["origin"])
        if not origin.startswith("https://"):
            continue
        prefix = "credential." + origin
        for operation, key, value in (
                ("--replace-all", prefix + ".helper", ""),
                ("--add", prefix + ".helper", GIT_CREDENTIAL_HELPER),
                ("--replace-all", prefix + ".useHttpPath", "true")):
            run(["git", "config", "--global", operation, key, value], env=environment, cwd=environment["HOME"],
                user=config["uid"], group=config["gid"], extra_groups=[])


def bootstrap(config_path, enrollment_path, state_dir):
    c, enrollment = load_config(config_path), load_config(enrollment_path)
    require(enrollment["vmId"] == c["vmId"] and enrollment["generation"] == c["generation"],
            "Enrollment generation mismatch")
    require(enrollment["expiresAt"] > time.time(), "Enrollment expired")
    require(enrollment["url"].startswith("https://"), "Enrollment requires verified HTTPS")
    state = Path(state_dir)
    state.mkdir(parents=True, mode=0o700, exist_ok=True)
    intent = state / "enrollment-intent.json"
    require(not intent.exists(), "Enrollment already attempted; reconcile ambiguous result, do not replay")
    manifest_path = Path("/opt/minicloud-workspace/image-manifest.json")
    manifest = json.loads(manifest_path.read_text())
    require(manifest["architecture"] == "linux/amd64" and
            manifest["provenance"]["toolArtifactsVerified"], "Unqualified prepared image")
    request = dict(vmId=c["vmId"], generation=c["generation"],
                   operationId=enrollment["operationId"], action="bootstrap")
    run(["systemctl", "start", "minicloud-workspace.service"])
    ready = call(request)
    configure_user_git(c)
    if c.get("startServices", True):
        run(["systemctl", "start", "minicloud-t3.service"])
        descriptor = None
        for attempt in range(30):
            try:
                # Loopback only, no bearer or external network needed for the public descriptor.
                opener = urllib.request.build_opener(NoRedirect())
                with opener.open("http://127.0.0.1:3773/.well-known/t3/environment", timeout=2) as response:
                    raw = response.read(65537)
                    require(len(raw) <= 65536, "Oversized T3 descriptor")
                    descriptor = json.loads(raw)
                break
            except (OSError, ValueError):
                time.sleep(1)
        require(descriptor and descriptor.get("orchestrationProtocolVersion") == 2 and
                descriptor.get("serverVersion") == "0.1.9", "Pinned protocol-2 T3 is not ready")
        if c["kind"] == "primary":
            try:
                run(["systemctl", "start", "minicloud-t3-connect.service"], timeout=15)
            except Blocked:
                # Optional enrollment readiness is probed separately; never break routine Open T3.
                pass
    # Enrollment means runtime ready, not T3 gateway authenticated. Parent registers the gateway later.
    body = dict(schemaVersion=1, vmId=c["vmId"], generation=c["generation"], kind=c["kind"],
                operationId=enrollment["operationId"],
                imageManifestSha256=hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
                bootstrapReceipt=ready)
    atomic_json(intent, dict(vmId=c["vmId"], generation=c["generation"],
                             operationId=enrollment["operationId"], attemptedAt=time.time()))
    try:
        context = ssl.create_default_context(cafile=enrollment.get("caFile"))
        opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context))
        request = urllib.request.Request(enrollment["url"], data=json.dumps(body).encode(),
                                         headers={"Authorization": "Bearer " + enrollment["token"],
                                                  "Content-Type": "application/json"}, method="POST")
        with opener.open(request, timeout=30) as response:
            require(response.status == 200 and len(response.read(65537)) <= 65536,
                    "Enrollment was not explicitly accepted")
        atomic_json(state / "enrollment-accepted.json", dict(type="EnrollmentAccepted",
                    vmId=c["vmId"], generation=c["generation"],
                    operationId=enrollment["operationId"]))
    except Exception as error:
        raise Blocked("Enrollment result ambiguous or rejected; reconcile at control plane") from error
    finally:
        Path(enrollment_path).unlink(missing_ok=True)
    return dict(type="EnrollmentAccepted", vmId=c["vmId"], generation=c["generation"])


def probe(device, provider_device_id):
    # Read-only initial trusted attached-volume probe; parent persists UUID before admitting a writer.
    import stat
    path = Path(device)
    require(str(path).startswith("/dev/disk/by-id/") and path.name == provider_device_id,
            "Expected provider device identity required")
    require(stat.S_ISBLK(path.stat().st_mode), "Expected block device unavailable")
    require(run(["blkid", "-s", "TYPE", "-o", "value", device]) == "ext4", "Existing ext4 required")
    fs_uuid = run(["blkid", "-s", "UUID", "-o", "value", device])
    require(fs_uuid, "Filesystem UUID unavailable")
    return dict(schemaVersion=1, type="VolumeProbe", providerDeviceId=provider_device_id,
                device=device, filesystemUuid=fs_uuid, filesystem="ext4", formatted=False)


def block_signatures(device):
    """Return low-level signatures (filesystem, RAID, partition table); blkid exits 2 for none."""
    import subprocess
    try:
        found = subprocess.run(["blkid", "-p", "-o", "export", device], text=True, capture_output=True,
                               timeout=60, check=False)
    except (OSError, subprocess.SubprocessError) as error:
        raise Blocked("Tool unavailable or failed: blkid") from error
    if found.returncode == 2:
        return {}
    require(found.returncode == 0, "Cannot inspect the volume signature")
    return dict(line.split("=", 1) for line in found.stdout.splitlines() if "=" in line)


def format_volume(device, provider_device_id):
    """Create ext4 once on a blank volume the worker just created; idempotent on retry."""
    import stat
    path = Path(device)
    require(str(path).startswith("/dev/disk/by-id/") and path.name == provider_device_id,
            "Expected provider device identity required")
    require(stat.S_ISBLK(path.stat().st_mode), "Expected block device unavailable")
    resolved = str(path.resolve())
    with open("/proc/mounts") as mounts:
        sources = {line.split()[0] for line in mounts if line.strip()}
    require(device not in sources and resolved not in sources, "Refusing to format a mounted device")
    signatures = block_signatures(device)
    if signatures.get("TYPE") == "ext4":
        return probe(device, provider_device_id)
    require(not signatures, "Refusing to format a volume that already has a signature")
    # No -F: mkfs refuses mounted or partitioned devices on its own as well.
    run(["mkfs.ext4", "-q", "-L", "minicloud-ws", device], timeout=1800)
    return dict(probe(device, provider_device_id), formatted=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("bootstrap", "volume-probe", "volume-format"))
    parser.add_argument("--config", default="/etc/minicloud-workspace/runtime.json")
    parser.add_argument("--enrollment", default="/run/minicloud-workspace/enrollment.json")
    parser.add_argument("--state-dir", default="/var/lib/minicloud-workspace")
    parser.add_argument("--device")
    parser.add_argument("--provider-device-id")
    args = parser.parse_args()
    require(os.geteuid() == 0, "Bootstrap requires root over pinned management SSH")
    os.umask(0o077)
    try:
        if args.action == "volume-probe":
            result = probe(args.device, args.provider_device_id)
        elif args.action == "volume-format":
            result = format_volume(args.device, args.provider_device_id)
        else:
            result = bootstrap(args.config, args.enrollment, args.state_dir)
        print(json.dumps(dict(ok=True, result=result), sort_keys=True))
    except Blocked as error:
        print(json.dumps(dict(ok=False, state=error.state, reason=str(error))))
        sys.exit(1)


if __name__ == "__main__":
    main()
