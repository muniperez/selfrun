#!/usr/bin/env python3
"""Generic T3/environment kernel probes ONLY on an authorized disposable image VM."""
import argparse
import datetime as dt
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import sqlite3
import ssl
import stat
import subprocess
import sys
import tarfile
import threading
import time
import traceback
import uuid

HERE = Path(__file__).resolve().parent
RUNTIME = Path("/opt/minicloud-workspace/runtime.py")
EXPECTED_RUNTIME_SHA256 = None
ACK = "RUN-NATIVE-QUALIFICATION-ON-DISPOSABLE-IMAGE-BUILDER"
SCOPES = ["orchestration:read", "orchestration:operate", "terminal:read", "terminal:operate"]
UNITS = ("minicloud-t3.service", "minicloud-workspace.service")
UNIT_HASHES = {
    "minicloud-t3.service": "b2d02c7eff28545dc6a15f0960fe2e9aab347cb5b1bd5150623b64ea86b29029",
    "minicloud-workspace.service": "a16e51b25b6cccd0c0314b106cf371880a43b52387c444070418c2e539276198",
    "minicloudwork.slice": "e1de2748256aed52d74784f73c1c640eb25ec50ac7a3788b09c84599b5e66618",
}
REQUIRED_PROBES = {
    "target-and-toolchain-guard", "pristine-tools-only-no-default-apps", "loop-ext4-root-bootstrap",
    "native-http-scopes-tickets-websocket", "authenticated-generation-task-and-activity-hooks",
    "real-terminal-input-and-passive-root-hook", "actual-pinned-t3-sqlite-idle",
    "actual-schema-copy-wal-and-fail-closed", "actual-kernel-frozen-readonly-t3-sqlite",
    "real-active-foreground-and-unexpected-child", "unfrozen-real-pty-blocked-stdin-proof",
    "frozen-real-pty-blocked-stdin-proof", "root-quiesce-controlled-thaw-stop-refreeze-unmount",
    "loop-readback-user-files", "encrypted-user-file-restore",
}


class ProbeError(Exception):
    """Only predefined codes, never subprocess/HTTP/SQL exception text, enter reports."""
    def __init__(self, code, evidence=None):
        super().__init__(code)
        self.evidence = evidence


def check(condition, code):
    if not condition:
        raise ProbeError(code)


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(data)
    return digest.hexdigest()


def command(argv, *, input=None, timeout=120, cwd=None):
    try:
        result = subprocess.run(
            argv, input=input, text=True, capture_output=True, timeout=timeout, cwd=cwd,
            env={"PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin", "HOME": str(HERE), "LC_ALL": "C",
                 "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1"})
    except (OSError, subprocess.SubprocessError):
        raise ProbeError("COMMAND_UNAVAILABLE_OR_TIMEOUT") from None
    if result.returncode:
        raise ProbeError("COMMAND_FAILED", {"tool": Path(argv[0]).name,
                                           "exitCode": result.returncode,
                                           "errorCategories": [word for word in (
                                               "permission denied", "eacces", "enoent", "sqlite",
                                               "database is locked", "duration", "ttl", "scope",
                                               "subject", "label", "invalid", "parse", "unsupported",
                                               "not found", "readonly", "connection", "minimum",
                                               "maximum", "expired", "already", "required",
                                               "error", "failed", "busy", "token", "session",
                                               "auth", "read", "write", "root")
                                               if word in (result.stdout + result.stderr).lower()],
                                           "stdoutBytes": len(result.stdout),
                                           "stderrBytes": len(result.stderr)})
    return result.stdout.strip()


def write_json(path, value):
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "w") as stream:
        json.dump(value, stream, sort_keys=True, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def validate_authorization(value, now, machine_hash):
    check(value.get("acknowledgement") == ACK, "EXPLICIT_AUTHORIZATION_REQUIRED")
    check(re.fullmatch(r"native-[a-f0-9]{32}", value.get("runId", "")), "BAD_RUN_ID")
    check(value.get("machineIdSha256") == machine_hash and
          re.fullmatch(r"[a-f0-9]{64}", machine_hash), "WRONG_TARGET_MACHINE")
    check(value.get("runtimeSha256") == EXPECTED_RUNTIME_SHA256, "WRONG_AUTHORIZED_SOURCE")
    check(value.get("clockSource") in ("native-vm-utc", "provider-https-date"),
          "TRUSTED_AUTHORIZATION_CLOCK_REQUIRED")
    try:
        issued = dt.datetime.fromisoformat(value["issuedAt"].replace("Z", "+00:00"))
        expiry = dt.datetime.fromisoformat(value["expiresAt"].replace("Z", "+00:00"))
        check(issued.tzinfo is not None and expiry.tzinfo is not None,
              "AUTHORIZATION_TIMEZONE_REQUIRED")
        check(issued <= now and now - issued < dt.timedelta(minutes=5),
              "AUTHORIZATION_NOT_FRESH")
        check(now < expiry, "EXPIRED_AUTHORIZATION")
        check(dt.timedelta() < expiry - issued <= dt.timedelta(minutes=5),
              "AUTHORIZATION_FIVE_MINUTE_TTL_REQUIRED")
    except (ValueError, KeyError, TypeError, AttributeError):
        raise ProbeError("BAD_AUTHORIZATION_EXPIRY") from None
    check(value.get("loopBackedExt4Only") is True, "LOOP_ONLY_AUTHORIZATION_REQUIRED")
    check(value.get("syntheticWorkspaceMayBeErased") is True, "SYNTHETIC_DATA_ACK_REQUIRED")


def guarded_loop(device, backing):
    check(re.fullmatch(r"/dev/loop[0-9]+", device), "REFUSE_NON_LOOP_DEVICE")
    actual = Path(command(["losetup", "-n", "-O", "BACK-FILE", device])).resolve()
    check(actual == backing.resolve() and backing.is_file() and not backing.is_symlink(),
          "LOOP_BACKING_IDENTITY_MISMATCH")
    check(stat.S_ISBLK(Path(device).stat().st_mode) and os.major(Path(device).stat().st_rdev) == 7,
          "NOT_A_LOOP_BLOCK_DEVICE")


def restore_selected_files(source, expected, destination):
    for name in expected:
        path = Path(name)
        check(not path.is_absolute() and ".." not in path.parts and "." not in path.parts
              and str(path) == name and name, "UNSAFE_SELECTED_FILE")
    seen = set()
    with tarfile.open(fileobj=source, mode="r|") as archive:
        for member in archive:
            check(member.name in expected and member.name not in seen and member.isfile()
                  and 0 <= member.size <= 1024 * 1024, "UNSAFE_RESTORE_MEMBER")
            data = archive.extractfile(member).read(1024 * 1024 + 1)
            check(hashlib.sha256(data).hexdigest() == expected[member.name],
                  "RESTORED_USER_FILE_HASH_MISMATCH")
            path = destination / member.name
            path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            with path.open("xb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            seen.add(member.name)
    check(seen == set(expected), "USER_BACKUP_MEMBER_MISSING")
    return {"filesRestored": len(seen), "allSha256Matched": True}


def bootstrap_with_local_enrollment(config, directory):
    """Run the real image entrypoint; only its external enrollment peer is synthetic."""
    certificate, key = directory / "enrollment.crt", directory / "enrollment.key"
    command(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
             "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1",
             "-keyout", str(key), "-out", str(certificate)])
    key.chmod(0o600)
    token = uuid.uuid4().hex
    received = []

    class Enrollment(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            if self.path != "/enroll" or not 0 < length <= 65536 or \
                    self.headers.get("Authorization") != "Bearer " + token:
                self.send_error(403)
                return
            body = json.loads(self.rfile.read(length))
            if body.get("vmId") != config["vmId"] or body.get("generation") != config["generation"] or \
                    body.get("bootstrapReceipt", {}).get("type") != "BootstrapReady":
                self.send_error(400)
                return
            received.append(body)
            self.send_response(200)
            self.send_header("Content-Length", "2")
            self.end_headers()
            self.wfile.write(b"{}")

    server = http.server.HTTPServer(("127.0.0.1", 0), Enrollment)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    enrollment = directory / "enrollment.json"
    write_json(enrollment, dict(vmId=config["vmId"], generation=config["generation"],
               operationId="native-fresh-bootstrap", expiresAt=time.time() + 180,
               url="https://127.0.0.1:" + str(server.server_port) + "/enroll",
               caFile=str(certificate), token=token))
    thread.start()
    try:
        result = json.loads(command(["python3", "-B", str(RUNTIME.parent / "bootstrap.py"),
            "bootstrap", "--enrollment", str(enrollment),
            "--state-dir", str(directory / "enrollment-state")], timeout=120))
        check(result.get("ok") is True and len(received) == 1, "FRESH_BOOTSTRAP_ENROLLMENT_FAILED")
        return received[0]["bootstrapReceipt"]
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
        key.unlink(missing_ok=True)
        enrollment.unlink(missing_ok=True)


class Report:
    def __init__(self, path, run_id, authorize=None):
        self.path = path
        self.authorize = authorize
        self.value = {
            "schemaVersion": 1, "type": "NativeWorkspaceQualification", "runId": run_id,
            "runtimeSha256": EXPECTED_RUNTIME_SHA256, "startedAt": None,
            "clockSource": "pending-native-target-guard", "operatorWallClockTrusted": False,
            "storageMedium": "temporary-loop-backed-ext4-NOT-provider-persistence",
            "providerPersistenceQualified": False, "encryptedOffHostBackupQualified": False,
            "runtimeQualified": False, "observedTerminalInputsConfigWritten": False,
            "runtimeQualifiedNative": False, "terminalInputObservedNative": False,
            "terminalIdleKernelQualifiedNative": False,
            "productionGatewayUsefulActivityQualified": False, "browserQualified": False,
            "nativeRuntimeScope": "isolated-loop-ext4-root-hooks-and-stop; NOT unused-terminal-opt-in/provider/browser",
            "probes": [], "cleanup": "not-started",
            "externalBlockers": ["REAL_MINICLOUD_GATEWAY_USEFUL_ACTIVITY_AND_ADMISSION_DRAIN_NOT_EXERCISED",
                                 "UNUSED_TERMINAL_ROOT_OPT_IN_FLOW_REQUIRES_REAL_GATEWAY_QUALIFICATION",
                                 "PROVIDER_VOLUME_CYCLES_AND_ENCRYPTED_OFFHOST_RESTORE_SEPARATE_E2E"],
        }

    def save(self):
        staging = self.path.with_suffix(".pending")
        if staging.exists():
            staging.unlink()
        write_json(staging, self.value)
        os.replace(staging, self.path)

    def probe(self, name, function, *, required=True):
        entry = {"name": name, "requiredForNativeCandidate": required}
        try:
            if self.authorize is not None:
                self.authorize()
            evidence = function()
            entry.update(state="passed", evidence=evidence if evidence is not None else {})
            passed = True
        except Exception as error:
            # Never serialize str(error), tracebacks, command lines, WS payloads or DB rows.
            entry.update(state="blocked", failureCode=str(error) if isinstance(error, ProbeError)
                         and re.fullmatch(r"[A-Z0-9_]+", str(error)) else "PROBE_FAILED_REDACTED",
                         exceptionType=type(error).__name__)
            frame = traceback.extract_tb(error.__traceback__)[-1]
            entry["failureFrame"] = {"file": Path(frame.filename).name, "line": frame.lineno}
            entry["failureFrames"] = [{"file": Path(frame.filename).name, "line": frame.lineno}
                                     for frame in traceback.extract_tb(error.__traceback__)[-8:]]
            if isinstance(error, ProbeError) and error.evidence is not None:
                entry["evidence"] = error.evidence
            passed = False
        self.value["probes"].append(entry)
        self.save()
        return passed

    def finish(self):
        self.value["probeCount"] = len(self.value["probes"])
        self.value["finishedAt"] = (dt.datetime.now(dt.timezone.utc).isoformat()
                                    if self.value["startedAt"] is not None else None)
        self.value["nativeCandidatePassed"] = (
            {item["name"] for item in self.value["probes"]} == REQUIRED_PROBES and
            all(item["state"] == "passed" for item in self.value["probes"]
                if item["requiredForNativeCandidate"]) and self.value["cleanup"] == "complete")
        passed = {item["name"] for item in self.value["probes"] if item["state"] == "passed"}
        pty = {"unfrozen-real-pty-blocked-stdin-proof", "frozen-real-pty-blocked-stdin-proof"}
        self.value["runtimeQualifiedNative"] = (
            REQUIRED_PROBES - pty <= passed and self.value["cleanup"] == "complete")
        self.value["terminalInputObservedNative"] = "real-terminal-input-and-passive-root-hook" in passed
        self.value["terminalIdleKernelQualifiedNative"] = pty <= passed
        self.save()


class T3Client:
    def __init__(self, directory):
        self.child = subprocess.Popen(
            ["node", str(HERE / "native-t3-probe.mjs")], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, env={"PATH": "/usr/local/bin:/usr/bin:/bin",
                                                     "HOME": str(directory)})
        self.counter = 0

    def call(self, operation, **payload):
        import select
        self.counter += 1
        self.child.stdin.write(json.dumps(dict(id=self.counter, operation=operation, **payload)) + "\n")
        self.child.stdin.flush()
        check(select.select([self.child.stdout], [], [], 40)[0], "T3_CLIENT_RESPONSE_TIMEOUT")
        response = json.loads(self.child.stdout.readline())
        check(response.get("id") == self.counter and response.get("ok") is True,
              "T3_CLIENT_PROBE_FAILED")
        return response["result"]

    def close(self):
        if self.child.poll() is None:
            self.child.stdin.close()
            try:
                self.child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.child.terminate()  # Exact owned child, never a process-name/global kill.
                self.child.wait(timeout=5)


class Native:
    def __init__(self, authorization):
        self.authorization = authorization
        self.run_id = authorization["runId"]
        self.directory = HERE / "runs" / self.run_id
        self.directory.mkdir(mode=0o700, parents=True, exist_ok=False)
        self.report = Report(self.directory / "report.json", self.run_id, self.fresh_authorization)
        self.device = None
        self.alias = None
        self.client = None
        self.config = None
        self.owns_runtime = False
        self.shell_pid = None
        self.snapshot = None
        self.clean_workspace = False
        self.frozen_by_harness = False
        self.original_workspace_exists = Path("/workspace").exists()
        self.expires = dt.datetime.fromisoformat(authorization["expiresAt"].replace("Z", "+00:00"))
        self.rt = None

    def fresh_authorization(self):
        validate_authorization(self.authorization, dt.datetime.now(dt.timezone.utc), sha("/etc/machine-id"))

    def guard(self):
        check(os.geteuid() == 0 and os.uname().machine == "x86_64", "ROOT_NATIVE_AMD64_ONLY")
        validate_authorization(self.authorization, dt.datetime.now(dt.timezone.utc),
                               sha("/etc/machine-id"))
        check('VERSION_ID="24.04"' in Path("/etc/os-release").read_text(), "UBUNTU_2404_ONLY")
        marker = Path("/etc/minicloud-disposable-image-builder")
        check(marker.is_file() and marker.stat().st_uid == 0 and
              not marker.stat().st_mode & 0o022 and
              marker.read_text().strip() == "DESTROY-DISPOSABLE-IMAGE-BUILDER", "BUILDER_MARKER_REQUIRED")
        check(command(["systemd-detect-virt", "--vm"]) not in ("", "none"), "DISPOSABLE_VM_ONLY")
        check(Path("/sys/fs/cgroup/cgroup.controllers").is_file(), "CGROUP_V2_REQUIRED")
        check(sha(RUNTIME) == EXPECTED_RUNTIME_SHA256, "INSTALLED_RUNTIME_SOURCE_MISMATCH")
        for path in (HERE, *HERE.parents, RUNTIME, RUNTIME.parent):
            info = path.stat()
            check(info.st_uid == 0 and not info.st_mode & 0o022, "ROOT_OWNED_STAGING_REQUIRED")
        check(not Path("/etc/minicloud-workspace/runtime.json").exists(), "EXISTING_RUNTIME_CONFIG")
        check(not Path("/var/lib/minicloud-workspace").exists() or
              not any(Path("/var/lib/minicloud-workspace").iterdir()), "EXISTING_RUNTIME_STATE")
        check(not os.path.ismount("/workspace") and
              (not Path("/workspace").exists() or not any(Path("/workspace").iterdir())),
              "NONEMPTY_OR_MOUNTED_WORKSPACE")
        check(not list(Path("/dev/disk/by-id").glob("*Volume*")), "PROVIDER_VOLUME_ATTACHED")
        check(pwd.getpwnam("coding").pw_uid == 2000, "WRONG_CODING_UID")
        for unit in UNITS:
            state = command(["systemctl", "show", unit, "-p", "ActiveState", "--value"])
            check(state in ("inactive", "failed"), "PREEXISTING_ACTIVE_WORKSPACE_SERVICE")
        for unit, expected in UNIT_HASHES.items():
            path = Path("/etc/systemd/system") / unit
            check(sha(path) == expected and path.stat().st_uid == 0 and
                  not path.stat().st_mode & 0o022 and not path.is_symlink(),
                  "PINNED_UNIT_FILE_MISMATCH")
            check(command(["systemctl", "show", unit, "-p", "DropInPaths", "--value"]) == "",
                  "UNQUALIFIED_UNIT_DROPINS")
        check(shutil.disk_usage(HERE).free >= 3 * 1024**3, "THREE_GIB_FREE_DISK_REQUIRED")
        for port in (3773,):
            with socket.socket() as test:
                check(test.connect_ex(("127.0.0.1", port)) != 0,                 "PREEXISTING_T3_LISTENER")
        manifest = json.loads(Path("/opt/minicloud-workspace/image-manifest.json").read_text())
        check(manifest["os"]["cloudImageSerial"] == "20251001", "WRONG_MANIFEST_SERIAL")
        inputs = manifest.get("provenance", {}).get("installedImmutableInputs", {}).get("files")
        check(isinstance(inputs, dict) and bool(inputs), "EXPLICIT_IMMUTABLE_INPUTS_REQUIRED")
        for name, digest in inputs.items():
            path = Path(name)
            check(path.is_absolute() and path.resolve() == path and path.is_file()
                  and not path.is_symlink() and path.stat().st_uid == 0
                  and not path.stat().st_mode & 0o022 and sha(path) == digest,
                  "INSTALLED_IMMUTABLE_INPUT_MISMATCH")
        serials = re.findall(r"^serial\s*[:=]\s*['\"]?([0-9]+)", Path("/etc/cloud/build.info").read_text(),
                             re.MULTILINE)
        check(serials == ["20251001"], "WRONG_ACTUAL_CLOUD_IMAGE_SERIAL")
        for argv, expected in ((["node", "--version"], "v24.11.1"),
                               (["dotnet", "--version"], "10.0.100"),
                               (["codex", "--version"], "0.169.0"), (["t3", "--version"], "0.1.9")):
            output = command(argv)
            check(output == expected if argv[0] in ("node", "dotnet") else
                  re.search(r"(?:^|\s)v?" + re.escape(expected) + r"(?:$|\s)", output),
                  "PINNED_TOOL_VERSION_MISMATCH")
        spec = importlib.util.spec_from_file_location("native_pinned_runtime", RUNTIME)
        self.rt = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.rt)
        check(not any(2000 in process["uids"] for process in self.rt.Procfs().inventory().values()),
              "PREEXISTING_CODING_PROCESSES")
        self.report.value.update(clockSource="native-vm-utc",
                                 startedAt=dt.datetime.now(dt.timezone.utc).isoformat(),
                                 installedImmutableInputs=inputs,
                                 installedImageManifestSha256=sha("/opt/minicloud-workspace/image-manifest.json"))
        return {"architecture": "x86_64", "ubuntuSerial": "20251001",
                "machineIdSha256": sha("/etc/machine-id"), "toolVersionsMatched": True,
                "codingUid": 2000}

    def pristine(self):
        packages = command(["dpkg-query", "-W", "-f=${binary:Package} ${db:Status-Status}\n"])
        installed = [line.split()[0].split(":")[0] for line in packages.splitlines()
                     if len(line.split()) == 2 and line.split()[1] == "installed"]
        check(not any(name.startswith(("postgresql", "libpq")) for name in installed),
              "UNREQUESTED_DATABASE_PACKAGES_INSTALLED")
        for name in ("/opt/minicloud-preview", "/var/lib/postgresql",
                     "/etc/systemd/system/minicloud-postgres.service",
                     "/etc/systemd/system/minicloud-preview-canary.service"):
            path = Path(name)
            check(not path.exists() and not path.is_symlink(), "UNREQUESTED_DEFAULT_APP_PRESENT")
        return {"defaultApplicationsAbsent": True, "postgresPackagesAbsent": True,
                "defaultWorkload": "T3-and-coding-environment-only"}

    def control(self, action, operation="native-control", expect_ok=True, **extra):
        self.fresh_authorization()
        request = dict(vmId=self.run_id, generation=1, operationId=operation, action=action)
        request.update(extra)
        response = json.loads(command(["python3", "-B", str(RUNTIME), "call"],
                                      input=json.dumps(request) + "\n", timeout=600))
        if response.get("ok") is not expect_ok:
            match = re.fullmatch(r"Tool unavailable or failed: ([a-zA-Z0-9._-]{1,64})",
                                 response.get("reason", ""))
            evidence = {"rootHookAction": action,
                        "runtimeFailureTool": match[1] if match else "redacted"}
            raise ProbeError("UNEXPECTED_ROOT_HOOK_RESULT", evidence)
        return response.get("result") if expect_ok else response

    def prepare(self):
        backing = self.directory / "fixture.ext4"
        with backing.open("xb") as file:
            file.truncate(2 * 1024**3)
        self.device = command(["losetup", "--find", "--show", "--nooverlap", str(backing)])
        guarded_loop(self.device, backing)
        command(["mkfs.ext4", "-q", "-F", "-L", "MC_NATIVE_ONLY", self.device])
        fs_uuid = command(["blkid", "-s", "UUID", "-o", "value", self.device])
        self.alias = Path("/dev/disk/by-id") / ("minicloud-native-" + self.run_id)
        self.alias.parent.mkdir(parents=True, exist_ok=True)
        check(not self.alias.exists() and not self.alias.is_symlink(), "LOOP_ALIAS_ALREADY_EXISTS")
        self.alias.symlink_to(self.device)
        owner = dict(organizationId="synthetic-native-only", workspaceId=self.run_id,
                     providerAccountId="NO-PROVIDER-LOOP-FIXTURE", location="native-disposable-builder",
                     providerVolumeId=self.run_id)
        self.config = dict(vmId=self.run_id, generation=1, kind="primary", uid=2000, gid=2000,
                           workspace="/workspace", repositories=[], requireFreshBackup=False,
                           startServices=True, observedTerminalInputs=False,
                           infrastructureUnits=["minicloud-t3.service"],
                           stopUnits=["minicloud-t3.service"],
                           volume=dict(device=str(self.alias), providerDeviceId=self.alias.name,
                                       providerVolumeId=self.run_id, filesystemUuid=fs_uuid,
                                       initializeOwner=True, owner=owner))
        write_json("/etc/minicloud-workspace/runtime.json", self.config)
        self.owns_runtime = True
        command(["systemctl", "start", "minicloud-workspace.service"])
        self.wait(lambda: Path("/run/minicloud-workspace/control.sock").exists())
        check(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]) == "0",
              "T3_STARTED_BEFORE_IMAGE_BOOTSTRAP")
        result = bootstrap_with_local_enrollment(self.config, self.directory)
        check(result["type"] == "BootstrapReady", "BOOTSTRAP_RECEIPT_REQUIRED")
        check(command(["findmnt", "-rn", "-M", "/workspace", "-o", "UUID"]) == fs_uuid,
              "MOUNT_UUID_MISMATCH")
        check(not Path("/workspace/postgres").exists() and not Path("/workspace/emulators").exists(),
              "UNREQUESTED_DEFAULT_WORKLOAD_DIRECTORY")
        for name, value in (("config/native-user.json", '{"userCreated":true}\n'),
                            ("transcripts/native-user.txt", "representative-user-data\n")):
            path = Path("/workspace") / name
            path.write_text(value)
            os.chown(path, 2000, 2000)
        return {"type": result["type"], "filesystemUuid": fs_uuid,
                "formattedOnlyVerifiedNewLoopDevice": True, "providerPersistenceProof": False,
                "noApplicationInstalledOrRegistered": True,
                "actualImageBootstrapEntrypoint": True,
                "enrollmentPeer": "synthetic-loopback-verified-TLS-only"}

    def wait(self, predicate, seconds=30):
        expires = time.monotonic() + seconds
        while time.monotonic() < expires:
            check(dt.datetime.now(dt.timezone.utc) < self.expires, "AUTHORIZATION_EXPIRED")
            try:
                if predicate():
                    return
            except (OSError, ProbeError):
                pass
            time.sleep(0.2)
        raise ProbeError("EXPECTED_NATIVE_STATE_TIMEOUT")

    def start_services(self):
        check(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]) != "0",
              "FRESH_BOOTSTRAP_DID_NOT_START_T3")
        self.wait(lambda: (Path("/workspace/t3/userdata/statev2.sqlite").exists()))
        for prop, expected in (("KillMode", "process"), ("SendSIGKILL", "no")):
            check(command(["systemctl", "show", "minicloud-t3.service", "-p", prop, "--value"]) == expected,
                  "UNSAFE_T3_STOP_POLICY")
        tokens = {}
        help_text = command(["t3", "auth", "session", "issue", "--help"])
        check(all(flag in help_text for flag in ("--scope", "--ttl", "--token-only", "--base-dir")),
              "REAL_HEADLESS_CLI_FLAGS_MISSING")
        for name, scopes in (("operate", SCOPES), ("readonly", ["orchestration:read"])):
            path = self.directory / (name + ".secret")
            argv = ["python3", "-B", "/opt/minicloud-workspace/issue-t3-session.py",
                    "--vm-id", self.run_id, "--generation", "1",
                    "--operation-id", "native-token-" + name, "--ttl", "900",
                    "--subject", self.run_id]
            for scope in scopes:
                argv.extend(("--scope", scope))
            token = command(argv, cwd="/workspace")
            check(token and not re.search(r"\s", token), "HEADLESS_TOKEN_BAD_SHAPE")
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "w") as secret:
                secret.write(token)
            tokens[name] = str(path)
        self.client = T3Client(self.directory)
        result = self.client.call("init", tokenFiles=tokens)
        self.snapshot = result["snapshotSequence"]
        return {**result, "tokenFilesRootOnly": True, "freshBootstrapStartedT3WithoutManualStart": True}

    def root_hooks(self):
        before = self.control("status")["activityAt"]
        self.control("activity", expect_ok=False, source="keepalive")
        self.control("activity", expect_ok=False, source="task-progress", taskId="not-admitted")
        self.control("status", operation="stale-generation", expect_ok=False, generation=2)
        check(self.control("status")["activityAt"] == before, "NON_INPUT_RENEWED_ACTIVITY")
        task = self.control("task-start", taskId="native-sleep", argv=["/usr/bin/sleep", "5"], leaseSeconds=1)
        self.control("task-start", expect_ok=False, taskId="native-sleep",
                     argv=["/usr/bin/touch", "/workspace/artifacts/REPLAY-MUST-NOT-HAPPEN"], leaseSeconds=1)
        time.sleep(1.2)
        inventory = self.control("status", includeWorkInventory=True)["workInventory"]
        check(inventory["state"] == "BusyOrUnknown", "EXPIRED_RUNNING_TASK_NOT_BUSY")
        self.wait(lambda: not self.rt.Linux(self.config).task_running(task), seconds=15)
        self.control("task-complete", taskId="native-sleep")
        check(not Path("/workspace/artifacts/REPLAY-MUST-NOT-HAPPEN").exists(), "TASK_REPLAY_OCCURRED")
        return {"generationRejected": True, "keepaliveRejected": True, "unknownProgressRejected": True,
                "expiredTaskStayedBusy": True, "ambiguousCommandNotReplayed": True}

    def terminals(self):
        result = self.client.call("open")
        self.shell_pid = result["pid"]
        check(isinstance(self.shell_pid, int), "REAL_TERMINAL_PID_REQUIRED")
        activity = self.control("status")["activityAt"]
        passive = self.client.call("passive")
        check(self.control("status")["activityAt"] == activity, "PASSIVE_RENEWED_ROOT_ACTIVITY")
        self.client.call("input", kind="marker")
        marker = Path("/workspace/artifacts/native-input-seen")
        self.wait(marker.exists)
        check(marker.read_text() == "native-input\n" and marker.stat().st_uid == 2000,
              "TERMINAL_INPUT_DID_NOT_EXECUTE")
        # This tests the authenticated root hook, NOT the production gateway observer.
        self.control("activity", source="user-input")
        check(self.control("status")["activityAt"] != activity, "AUTHENTICATED_INPUT_NOT_ACTIVITY")
        return {"realTerminalPid": self.shell_pid, "nativeTerminalWriteEffectObserved": True,
                "passiveObserveResizePingRoundTrips": passive["roundTrips"],
                "rootHookActivityAdvancedOnlyAfterExplicitInputCall": True,
                "productionGatewayObserverExercised": False}

    def shell_proof(self):
        check(Path("/workspace/artifacts/native-input-seen").is_file(), "GENUINE_INPUT_NOT_YET_OBSERVED")
        processes = self.rt.Procfs()
        inventory = processes.inventory()
        main_pid = int(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]))
        check(self.shell_pid in inventory and main_pid in inventory, "NATIVE_SHELL_OR_T3_MISSING")
        process = inventory[self.shell_pid]
        raw = Path("/proc", str(self.shell_pid), "syscall").read_text().split()
        channel = Path("/proc", str(self.shell_pid), "wchan").read_text().strip()
        evidence = {"pid": self.shell_pid, "t3MainPid": main_pid,
                "syscallNumber": int(raw[0], 0) if raw and re.fullmatch(r"-?[0-9]+", raw[0]) else None,
                "waitChannel": channel if re.fullmatch(r"[A-Za-z0-9_]{1,64}", channel) else "unavailable",
                "foregroundProcessGroup": process["foreground"], "pidStartTicks": process["start"],
                "processState": process["state"], "runtimeConfigOptInWritten": False}
        if len(raw) == 9 and raw[0] == "270":
            evidence["pselect"] = {"nfdsIsOne": int(raw[1], 0) == 1,
                                   "infiniteTimeout": int(raw[5], 0) == 0}
            with Path("/proc", str(self.shell_pid), "mem").open("rb", buffering=0) as memory:
                for index, name, expected in ((2, "readSetOnlyStdin", 1),
                                              (3, "writeSetEmpty", 0), (4, "exceptSetEmpty", 0)):
                    address = int(raw[index], 0)
                    mask = os.pread(memory.fileno(), 8, address) if address else bytes(8)
                    evidence["pselect"][name] = len(mask) == 8 and int.from_bytes(mask, "little") == expected
        try:
            processes.idle_shell(process, inventory, {main_pid})
        except self.rt.Blocked:
            raise ProbeError("NATIVE_PTY_KERNEL_PROOF_BLOCKED", evidence) from None
        return {**evidence, "blockedReadZeroProved": evidence["syscallNumber"] in (0, 63),
                "stdinOnlyPselectProved": evidence["syscallNumber"] == 270}

    def active_work(self):
        for kind in ("foreground", "background"):
            self.client.call("input", kind=kind)
            # Real subprocesses, not CPU/idle-time inference or simulated process records.
            self.wait(lambda: any(p["ppid"] == self.shell_pid
                                  for p in self.rt.Procfs().inventory().values()))
            result = self.control("status", includeWorkInventory=True)["workInventory"]
            check(result["state"] == "BusyOrUnknown", "ACTIVE_TERMINAL_PROCESS_NOT_BUSY")
            processes = self.rt.Procfs()
            inventory = processes.inventory()
            check(any(p["ppid"] == self.shell_pid for p in inventory.values()),
                  "ACTIVE_TERMINAL_FIXTURE_EXITED_BEFORE_CLASSIFIER_PROBE")
            main_pid = int(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]))
            try:
                processes.idle_shell(inventory[self.shell_pid], inventory, {main_pid})
            except self.rt.Blocked:
                pass
            else:
                raise ProbeError("ACTIVE_PTY_WAS_CLASSIFIED_IDLE")
            self.wait(lambda: not any(p["ppid"] == self.shell_pid
                                      for p in self.rt.Procfs().inventory().values()), seconds=15)
        return {"foregroundBusy": True, "unexpectedBackgroundChildBusy": True,
                "actualClassifierRejectedBothLiveWorkCases": True,
                "allFixtureJobsExitedNaturally": True}

    def freeze_shell(self):
        self.rt.Linux(self.config).freeze()
        self.frozen_by_harness = True
        try:
            return self.shell_proof()
        finally:
            self.rt.Linux(self.config).thaw()
            self.frozen_by_harness = False

    def sqlite(self):
        snapshot = self.client.call("snapshot")
        path = Path("/workspace/t3/userdata/statev2.sqlite")
        try:
            evidence = self.rt.t3_idle_database(path, snapshot["snapshotSequence"])
        except self.rt.Blocked:
            c = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
            try:
                c.execute("PRAGMA query_only=ON")
                metadata = c.execute("SELECT schema_version,last_sequence FROM "
                                     "orchestration_v2_projection_metadata "
                                     "WHERE projection_name='thread-projections'").fetchall()
                cursors = c.execute("SELECT COUNT(*),COALESCE(MAX(sequence),0) "
                                    "FROM orchestration_events").fetchone()
                raise ProbeError("ACTUAL_T3_PROJECTION_METADATA_BLOCKED",
                                 {"threadProjectionVersionAndCursor": metadata,
                                  "eventCountAndGlobalCursor": cursors,
                                  "httpThreadCursor": snapshot["snapshotSequence"]})
            finally:
                c.close()
        self.snapshot = snapshot["snapshotSequence"]
        check(evidence["type"] == "T3CommittedIdle", "ACTUAL_T3_DATABASE_NOT_IDLE")
        return {"actualPinnedT3Database": True, "threadSequence": evidence["threadSequence"],
                "globalV2Sequence": evidence["globalV2Sequence"],
                "rowsChecked": evidence["rowsChecked"]}

    def sqlite_negatives(self):
        target = self.directory / "synthetic-corruption-copy.sqlite"
        source = sqlite3.connect("file:/workspace/t3/userdata/statev2.sqlite?mode=ro", uri=True)
        copied = sqlite3.connect(target)
        try:
            source.backup(copied)
            copied.execute("PRAGMA journal_mode=WAL")
            copied.execute("PRAGMA wal_autocheckpoint=0")
            copied.execute("PRAGMA foreign_keys=OFF")
            copied.execute("PRAGMA ignore_check_constraints=ON")
            base = self.rt.t3_idle_database(target, None)
            next_sequence = copied.execute(
                "SELECT COALESCE(MAX(sequence),0)+1 FROM orchestration_events").fetchone()[0]
            synthetic_row(copied, "orchestration_events",
                          dict(sequence=next_sequence, application_event_version=2, aggregate_kind="project"))
            copied.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=2,last_sequence=? "
                           "WHERE projection_name='thread-projections'", (next_sequence,))
            copied.commit()
            check(Path(str(target) + "-wal").stat().st_size > 32, "COMMITTED_WAL_NOT_PRESENT")
            current = self.rt.t3_idle_database(target, base["threadSequence"])
            check(current["globalV2Sequence"] == next_sequence and
                  current["threadSequence"] == base["threadSequence"], "READONLY_WAL_OR_GLOBAL_CURSOR_FAILED")
            blocked = []
            def expect_blocked(label):
                try:
                    self.rt.t3_idle_database(target, None)
                except self.rt.Blocked:
                    blocked.append(label)
                    return
                raise ProbeError("UNSAFE_SQLITE_COPY_ACCEPTED")
            copied.execute("UPDATE orchestration_v2_projection_metadata SET last_sequence=? "
                           "WHERE projection_name='thread-projections'", (next_sequence - 1,))
            copied.commit()
            expect_blocked("projection-lag")
            copied.execute("UPDATE orchestration_v2_projection_metadata SET last_sequence=?,schema_version=999 "
                           "WHERE projection_name='thread-projections'", (next_sequence,))
            copied.commit()
            expect_blocked("unknown-schema-version")
            copied.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=2 "
                           "WHERE projection_name='thread-projections'")
            synthetic_row(copied, "orchestration_v2_projection_runs",
                          dict(thread_id="synthetic-native-only", status="unknown-native-status"))
            copied.commit()
            expect_blocked("unknown-work-status")
            return {"basis": "SQLite backup of actual native T3 DB; corruption ONLY in private copy",
                    "committedWalObserved": True, "projectGlobalCursorNotThreadCursor": True,
                    "blockersRejected": blocked, "actualT3DatabaseMutated": False}
        finally:
            copied.close()
            source.close()
            for path in (target, Path(str(target) + "-wal"), Path(str(target) + "-shm")):
                path.unlink(missing_ok=True)

    def frozen_sqlite(self):
        self.snapshot = self.client.call("snapshot")["snapshotSequence"]
        adapter = self.rt.Linux(self.config)
        path = Path("/workspace/t3/userdata/statev2.sqlite")
        adapter.freeze()
        try:
            files = [file for file in (path, Path(str(path) + "-wal")) if file.exists()]
            before = {file.name: sha(file) for file in files}
            result = self.rt.t3_idle_database(path, self.snapshot)
            check(before == {file.name: sha(file) for file in files},
                  "FROZEN_READONLY_DB_CONTENT_CHANGED")
            return {"actualKernelFrozen": True, "actualT3Database": True,
                    "threadSequence": result["threadSequence"], "mainAndWalUnchanged": True}
        finally:
            adapter.thaw()

    def quiesce(self):
        self.wait(lambda: not any(p["ppid"] == self.shell_pid
                                  for p in self.rt.Procfs().inventory().values()), seconds=15)
        main_pid = int(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]))
        processes = self.rt.Procfs()
        inventory = processes.inventory()
        unknown = [p for p in inventory.values()
                   if 2000 in p["uids"] and p["pid"] not in (main_pid, self.shell_pid) and
                   not processes.t3_resource_monitor(p, inventory, {main_pid})]
        if unknown:
            details = []
            for p in unknown:
                item = {key: p[key] for key in ("pid", "ppid", "pgrp", "session", "tty",
                                                "state", "uids", "cgroup", "start", "threads")}
                executable = self.rt.Procfs().executable(p["pid"])
                approved = {"/usr/bin/bash", "/usr/bin/dash", "/usr/bin/node",
                            str(Path("/usr/local/bin/t3").resolve()),
                            str(Path("/usr/local/bin/node").resolve())}
                item["executable"] = (executable["path"] if executable["path"] in approved or
                                      executable["path"].startswith("/opt/minicloud-") else "other")
                item["executableRootOwned"] = executable["uid"] == 0
                item["executableWritableByCoding"] = bool(executable["mode"] & 0o022) or executable["uid"] == 2000
                args = Path("/proc", str(p["pid"]), "cmdline").read_bytes().split(b"\0")[:-1]
                item["argumentCount"] = len(args)
                item["hasCommandStringFlag"] = any(a in (b"-c", b"-lc", b"-ilc", b"-lic") for a in args)
                item["argvHasInteractiveFlag"] = any(a in (b"-i", b"-il", b"-li") for a in args)
                details.append(item)
            raise ProbeError("UNKNOWN_PROCESS_BEFORE_SYNTHETIC_TERMINAL_CLOSE",
                             {"processes": details})
        self.client.call("close")
        self.client.close()
        self.client = None
        self.wait(lambda: self.shell_pid not in self.rt.Procfs().inventory())
        inventory = self.control("status", includeWorkInventory=True)["workInventory"]
        check(inventory["state"] == "IdleCandidate", "NO_TERMINAL_RUNTIME_NOT_IDLE")
        # No production gateway is running on this fixture. Close ALL owned clients; this
        # isolated attestation must never be reused for a live gateway/application.
        time.sleep(75)
        snapshot = self.http_snapshot_without_retaining_token()
        result = self.control("quiesce", operation="native-quiesce", gatewayAdmissionsFenced=True,
                              unobservableToolsAbsent=True, t3SnapshotSequence=snapshot)
        check(result["type"] == "PrimaryQuiesced", "NO_REAL_QUIESCENCE_RECEIPT")
        check(not os.path.ismount("/workspace"), "WORKSPACE_NOT_UNMOUNTED")
        self.clean_workspace = True
        self.control("receipt-validate", operation="native-quiesce", receiptId=result["receiptId"])
        for unit in ("minicloud-t3.service",):
            check(command(["systemctl", "show", unit, "-p", "ActiveState", "--value"]) == "inactive",
                  "SERVICE_DID_NOT_STOP")
        check("frozen 1" in Path("/sys/fs/cgroup/minicloudwork.slice/cgroup.events").read_text(),
              "POSTSTOP_CGROUP_NOT_FROZEN")
        return {"receiptType": result["type"], "receiptId": result["receiptId"],
                "receiptBinding": {key: result[key] for key in ("vmId", "generation", "operationId", "revision")},
                "actualStopAndUnmount": True, "postStopFrozen": True,
                "productionGatewayAdmissionProof": False}

    def http_snapshot_without_retaining_token(self):
        client = T3Client(self.directory)
        try:
            client.call("init", tokenFiles={"operate": str(self.directory / "operate.secret"),
                                            "readonly": str(self.directory / "readonly.secret")})
            result = client.call("snapshot")
            return result["snapshotSequence"]
        finally:
            client.close()

    def readback(self):
        directory = self.directory / "readback"
        directory.mkdir(mode=0o700)
        guarded_loop(self.device, self.directory / "fixture.ext4")
        command(["mount", "-t", "ext4", "-o", "ro,noload,nodev,nosuid", self.device, str(directory)])
        try:
            check((directory / "artifacts/native-input-seen").read_text() == "native-input\n",
                  "REPRESENTATIVE_LOOP_READBACK_MISMATCH")
            check((directory / "config/native-user.json").read_text() == '{"userCreated":true}\n',
                  "USER_CONFIG_READBACK_MISMATCH")
            check((directory / "transcripts/native-user.txt").read_text() == "representative-user-data\n",
                  "USER_TRANSCRIPT_READBACK_MISMATCH")
            return {"loopReadback": True, "representativeUserFilesPreserved": True,
                    "providerVolumePersistenceQualified": False}
        finally:
            command(["umount", str(directory)])

    def encrypted_restore(self):
        names = ("artifacts/native-input-seen", "config/native-user.json", "transcripts/native-user.txt")
        mounted = self.directory / "restore-readback"
        restored = self.directory / "restored-user-files"
        key = self.directory / "restore.agekey"
        ciphertext = self.directory / "user-files.tar.age"
        mounted.mkdir(mode=0o700)
        restored.mkdir(mode=0o700)
        guarded_loop(self.device, self.directory / "fixture.ext4")
        command(["mount", "-t", "ext4", "-o", "ro,noload,nodev,nosuid", self.device, str(mounted)])
        try:
            expected = {name: sha(mounted / name) for name in names}
            command(["age-keygen", "--output", str(key)])
            recipient = command(["age-keygen", "-y", str(key)])
            check(recipient.startswith("age1"), "INVALID_SYNTHETIC_AGE_RECIPIENT")
            archive = subprocess.Popen(["tar", "-C", str(mounted), "-cf", "-", *names],
                                       stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            try:
                encrypted = subprocess.run(["age", "--recipient", recipient, "--output", str(ciphertext)],
                                           stdin=archive.stdout, capture_output=True, timeout=60)
            finally:
                archive.stdout.close()
            check(encrypted.returncode == 0 and archive.wait(timeout=10) == 0,
                  "SYNTHETIC_USER_FILE_ENCRYPTION_FAILED")
            decrypted = subprocess.Popen(["age", "--decrypt", "--identity", str(key), str(ciphertext)],
                                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            try:
                result = restore_selected_files(decrypted.stdout, expected, restored)
                decrypted.stdout.read()
            finally:
                decrypted.stdout.close()
            check(decrypted.wait(timeout=10) == 0, "SYNTHETIC_USER_FILE_DECRYPTION_FAILED")
            return {**result, "encryptedWithAge": True, "plaintextArchivePersisted": False,
                    "scope": "representative user files only; local loop fixture",
                    "providerOrOffHostRestoreQualified": False}
        finally:
            command(["umount", str(mounted)])
            key.unlink(missing_ok=True)
            ciphertext.unlink(missing_ok=True)
            shutil.rmtree(restored)

    def cleanup(self):
        if self.client:
            try:
                if self.shell_pid:
                    self.wait(lambda: not any(p["ppid"] == self.shell_pid
                                              for p in self.rt.Procfs().inventory().values()), seconds=15)
                self.client.call("close")
            except Exception:
                pass
            self.client.close()
            self.client = None
        for name in ("operate.secret", "readonly.secret"):
            (self.directory / name).unlink(missing_ok=True)
        if self.owns_runtime:
            if self.rt:
                self.rt.Linux(self.config).thaw()
            # Exact units were inactive at guard entry and belong to this isolated fixture.
            for unit in UNITS[:1]:
                command(["systemctl", "stop", unit], timeout=180)
            check(not any(2000 in p["uids"] for p in self.rt.Procfs().inventory().values()),
                  "LEFTOVER_CODING_PROCESSES_KEEP_MOUNT_AND_LOOP_FOR_INSPECTION")
            command(["systemctl", "stop", UNITS[1]])
            if os.path.ismount("/workspace"):
                check(command(["findmnt", "-rn", "-M", "/workspace", "-o", "UUID"]) ==
                      self.config["volume"]["filesystemUuid"], "CLEANUP_MOUNT_CHANGED")
                command(["umount", "/workspace"])
            check(not os.path.ismount(self.directory / "readback"), "READBACK_STILL_MOUNTED")
            config = Path("/etc/minicloud-workspace/runtime.json")
            check(json.loads(config.read_text())["vmId"] == self.run_id, "CLEANUP_CONFIG_CHANGED")
            config.unlink()
            state = Path("/var/lib/minicloud-workspace")
            if state.exists():
                if (state / "state.json").exists():
                    check(json.loads((state / "state.json").read_text())["identity"]["vmId"] == self.run_id,
                          "CLEANUP_STATE_CHANGED")
                    shutil.rmtree(state)
                else:
                    check(not any(state.iterdir()), "UNKNOWN_PARTIAL_RUNTIME_STATE")
                    state.rmdir()
            if not self.original_workspace_exists and Path("/workspace").exists():
                Path("/workspace").rmdir()
        if self.alias:
            check(self.alias.is_symlink() and os.readlink(self.alias) == self.device, "CLEANUP_ALIAS_CHANGED")
            self.alias.unlink()
        if self.device:
            guarded_loop(self.device, self.directory / "fixture.ext4")
            command(["losetup", "--detach", self.device])
            backing = self.directory / "fixture.ext4"
            # Do not export the synthetic DB/auth material or leave a sparse credential image.
            with backing.open("r+b") as output:
                zeros = bytes(1024 * 1024)
                for _ in range(2048):
                    output.write(zeros)
                output.flush()
                os.fsync(output.fileno())
            backing.unlink()
        elif (self.directory / "fixture.ext4").exists():
            check(command(["losetup", "--associated", str(self.directory / "fixture.ext4")]) == "",
                  "AMBIGUOUS_LOOP_ATTACHMENT_KEEP_FOR_INSPECTION")
            (self.directory / "fixture.ext4").unlink()
        self.report.value["cleanup"] = "complete"

    def run(self):
        guarded = self.report.probe("target-and-toolchain-guard", self.guard)
        try:
            if not guarded:
                return
            if not self.report.probe("pristine-tools-only-no-default-apps", self.pristine):
                return
            if not self.report.probe("loop-ext4-root-bootstrap", self.prepare):
                return
            if not self.report.probe("native-http-scopes-tickets-websocket", self.start_services):
                return
            self.report.probe("authenticated-generation-task-and-activity-hooks", self.root_hooks)
            if not self.report.probe("real-terminal-input-and-passive-root-hook", self.terminals):
                return
            self.report.probe("actual-pinned-t3-sqlite-idle", self.sqlite)
            self.report.probe("actual-schema-copy-wal-and-fail-closed", self.sqlite_negatives)
            self.report.probe("actual-kernel-frozen-readonly-t3-sqlite", self.frozen_sqlite)
            self.report.probe("real-active-foreground-and-unexpected-child", self.active_work)
            self.report.probe("unfrozen-real-pty-blocked-stdin-proof", self.shell_proof)
            self.report.probe("frozen-real-pty-blocked-stdin-proof", self.freeze_shell)
            if self.report.probe("root-quiesce-controlled-thaw-stop-refreeze-unmount", self.quiesce):
                self.report.probe("loop-readback-user-files", self.readback)
                self.report.probe("encrypted-user-file-restore", self.encrypted_restore)
        finally:
            if guarded:
                try:
                    self.cleanup()
                except Exception:
                    self.report.value["cleanup"] = "blocked-preserve-loop-and-data-inspect-before-generalize"
            else:
                self.report.value["cleanup"] = "not-needed-no-native-mutation"
            self.report.finish()


def synthetic_row(connection, table, overrides):
    check(re.fullmatch(r"[a-z0-9_]+", table), "INVALID_SYNTHETIC_TABLE")
    values = {}
    for _, name, kind, not_null, default, primary in connection.execute("PRAGMA table_info(" + table + ")"):
        check(re.fullmatch(r"[a-z0-9_]+", name), "INVALID_SYNTHETIC_COLUMN")
        if name in overrides:
            values[name] = overrides[name]
        elif default is None and (not_null or primary):
            values[name] = 0 if "INT" in kind.upper() else (
                b"native-only" if "BLOB" in kind.upper() else "native-" + uuid.uuid4().hex)
    check(set(overrides) <= set(values), "ACTUAL_SCHEMA_MISSING_EXPECTED_COLUMNS")
    columns = ",".join(values)
    connection.execute("INSERT INTO " + table + "(" + columns + ") VALUES(" +
                       ",".join("?" for _ in values) + ")", tuple(values.values()))


def main():
    global EXPECTED_RUNTIME_SHA256
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("run", "plan"))
    parser.add_argument("--authorization", type=Path)
    parser.add_argument("--expected-runtime-sha256", required=True)
    args = parser.parse_args()
    check(re.fullmatch(r"[a-f0-9]{64}", args.expected_runtime_sha256), "EXACT_RUNTIME_SHA256_REQUIRED")
    EXPECTED_RUNTIME_SHA256 = args.expected_runtime_sha256
    if args.action == "plan":
        print(json.dumps({"type": "NativeHarnessPlan", "nativeExecutionPerformed": False,
                          "expectedRuntimeSha256": EXPECTED_RUNTIME_SHA256,
                          "requiredScopes": SCOPES, "requiredProbes": sorted(REQUIRED_PROBES),
                          "authorizationMaxTtlSeconds": 300,
                          "authorizationClockSources": ["native-vm-utc", "provider-https-date"],
                          "operatorWallClockTrusted": False,
                          "defaultApplicationsInstalled": False,
                          "runtimeQualified": False, "authorizationRequired": ACK}, indent=2))
        return
    check(args.authorization is not None, "AUTHORIZATION_FILE_REQUIRED")
    info = args.authorization.stat()
    check(info.st_uid == 0 and not info.st_mode & 0o077 and not args.authorization.is_symlink(),
          "AUTHORIZATION_MUST_BE_ROOT_0600")
    authorization = json.loads(args.authorization.read_text())
    validate_authorization(authorization, dt.datetime.now(dt.timezone.utc), sha("/etc/machine-id"))
    os.umask(0o077)
    instance = Native(authorization)
    instance.run()
    print(json.dumps({"report": str(instance.report.path),
                      "nativeCandidatePassed": instance.report.value["nativeCandidatePassed"],
                      "runtimeQualified": False}))
    raise SystemExit(0 if instance.report.value["nativeCandidatePassed"] else 1)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print(json.dumps({"ok": False, "failureCode": "HARNESS_FAILED_REDACTED"}))
        raise SystemExit(1) from None
