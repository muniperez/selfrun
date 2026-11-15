#!/usr/bin/env python3
"""One-shot prepared-image bootstrap. Never installs tools or formats storage."""
import argparse
import json
import os
from pathlib import Path
import socket
import ssl
import sys
import time
import urllib.error
import urllib.request

from runtime import Blocked, atomic_json, digest, load_config, require, run


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise Blocked("Enrollment redirects are forbidden")


def call(request, socket_path="/run/minicloud-workspace/control.sock"):
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(600)
        client.connect(socket_path)
        client.sendall(json.dumps(request).encode() + b"\n")
        data = b""
        while not data.endswith(b"\n"):
            part = client.recv(65536)
            require(part and len(data) + len(part) <= 1048576, "Invalid runtime response")
            data += part
        response = json.loads(data)
        require(response.get("ok"), response.get("reason", "Runtime blocked"))
        return response["result"]


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
    if c.get("startServices", True):
        run(["systemctl", "start", "minicloud-postgres.service", "minicloud-t3.service"])
        database_ready = False
        for attempt in range(30):
            try:
                run(["pg_isready", "--host=/run/minicloud-postgres", "--dbname=postgres"])
                database_ready = True
                break
            except Blocked:
                time.sleep(1)
        require(database_ready, "Workspace PostgreSQL is not ready")
        # Peer-authenticated local role only: no passwords or superuser grant for coding tasks.
        sql = ("SELECT 'CREATE ROLE coding LOGIN CREATEDB' WHERE NOT EXISTS "
               "(SELECT FROM pg_roles WHERE rolname='coding')\\gexec\n"
               "SELECT 'CREATE DATABASE workspace OWNER coding' WHERE NOT EXISTS "
               "(SELECT FROM pg_database WHERE datname='workspace')\\gexec\n")
        run(["runuser", "-u", "postgres", "--", "psql", "--host=/run/minicloud-postgres",
             "--dbname=postgres", "--set=ON_ERROR_STOP=1"], input=sql)
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
    # Enrollment means runtime ready, not T3 gateway authenticated. Parent registers the gateway later.
    body = dict(schemaVersion=1, vmId=c["vmId"], generation=c["generation"], kind=c["kind"],
                operationId=enrollment["operationId"], imageManifestSha256=digest(manifest),
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("bootstrap", "volume-probe"))
    parser.add_argument("--config", default="/etc/minicloud-workspace/runtime.json")
    parser.add_argument("--enrollment", default="/run/minicloud-workspace/enrollment.json")
    parser.add_argument("--state-dir", default="/var/lib/minicloud-workspace")
    parser.add_argument("--device")
    parser.add_argument("--provider-device-id")
    args = parser.parse_args()
    require(os.geteuid() == 0, "Bootstrap requires root over pinned management SSH")
    os.umask(0o077)
    try:
        result = probe(args.device, args.provider_device_id) if args.action == "volume-probe" else \
            bootstrap(args.config, args.enrollment, args.state_dir)
        print(json.dumps(dict(ok=True, result=result), sort_keys=True))
    except Blocked as error:
        print(json.dumps(dict(ok=False, state=error.state, reason=str(error))))
        sys.exit(1)


if __name__ == "__main__":
    main()
