#!/usr/bin/env python3
"""Generic T3/environment kernel probes ONLY on an authorized disposable image VM."""
import argparse
import datetime as dt
import hashlib
import http.server
import importlib.util
import io
import json
import os
from pathlib import Path
import pwd
import re
import select
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
    "minicloud-t3.service": "6d191e239d85ab94792b57542cda24a39c139c616e2f9bfe81f1550e653ffcb9",
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
    "codex-empty-git-probes-save-and-reject-data",
    "copilot-stdio-provider-setup",
    "source-control-cli-service-terminal-and-discovery",
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
        exception_classes = {
            "FileNotFoundError", "ConnectionRefusedError", "ConnectionResetError",
            "PermissionError", "TimeoutError", "OSError", "ValueError",
        }
        exceptions = re.findall(r"^([A-Za-z]+Error):", result.stderr, re.MULTILINE)
        errnos = re.findall(r"\[Errno ([0-9]{1,3})\]", result.stderr)
        raise ProbeError("COMMAND_FAILED", {"tool": Path(argv[0]).name,
                                           "exitCode": result.returncode,
                                           "exceptionClass": next(
                                               (name for name in reversed(exceptions)
                                                if name in exception_classes), None),
                                           "errno": int(errnos[-1]) if errnos else None,
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


def initialize_codex_without_credentials(workspace, home):
    binaries = list(Path("/opt/minicloud-tools/node_modules/@openai").glob("codex*/vendor/*/bin/codex"))
    check(len(binaries) == 1, "NATIVE_CODEX_EXECUTABLE_AMBIGUOUS")
    binary = binaries[0]
    check(binary.is_file() and not binary.is_symlink() and binary.stat().st_uid == 0 and
          not binary.stat().st_mode & 0o022, "UNTRUSTED_NATIVE_CODEX_EXECUTABLE")
    process = subprocess.Popen(["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
        str(binary), "app-server", "--listen", "stdio://"], cwd=workspace, text=True,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        env={"PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin",
             "HOME": str(home), "CODEX_HOME": str(home / ".codex")})
    try:
        process.stdin.write(json.dumps(dict(id=1, method="initialize", params=dict(
            clientInfo=dict(name="minicloud-native-qualification", version="1.0.0"),
            capabilities=dict(experimentalApi=True)))) + "\n")
        process.stdin.flush()
        ready, _, _ = select.select([process.stdout], [], [], 15)
        check(bool(ready), "CODEX_NATIVE_INITIALIZE_TIMEOUT")
        response = json.loads(process.stdout.readline(65537))
        check(response.get("id") == 1 and "result" in response, "CODEX_NATIVE_INITIALIZE_REJECTED")
        process.stdin.close()
        process.wait(timeout=15)
        check(process.returncode == 0, "CODEX_NATIVE_INITIALIZE_EXIT_FAILED")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        if not process.stdin.closed:
            process.stdin.close()
        process.stdout.close()


def source_control_versions(path, lock):
    check(path == "/usr/local/bin:/usr/bin:/bin", "UNEXPECTED_T3_SERVICE_PATH")
    for name in ("gh", "glab"):
        output = command(["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
                          "env", "-i", "HOME=/workspace/home", "PATH=" + path,
                          "LC_ALL=C", name, "--version"], cwd="/workspace")
        check(re.search(r"(?:^|\s)v?" + re.escape(lock[name]["version"]) + r"(?:$|[\s,])", output),
              "SOURCE_CONTROL_CLI_VERSION_MISMATCH")
    return {"codingUid": 2000, "githubVersionMatched": True, "gitlabVersionMatched": True}


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
            "sourceControlUserSignInQualified": False,
            "nativeRuntimeScope": "isolated-loop-ext4-root-hooks-and-stop; NOT unused-terminal-opt-in/provider/browser",
            "probes": [], "cleanup": "not-started",
            "externalBlockers": ["REAL_MINICLOUD_GATEWAY_USEFUL_ACTIVITY_AND_ADMISSION_DRAIN_NOT_EXERCISED",
                                 "UNUSED_TERMINAL_ROOT_OPT_IN_FLOW_REQUIRES_REAL_GATEWAY_QUALIFICATION",
                                 "SOURCE_CONTROL_USER_SIGN_IN_AND_REPOSITORY_ACCESS_NOT_EXERCISED",
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
        self.git_fixture_cleanup = "not-started"
        self.restore_fixture_cleanup = "not-started"
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
                     "/etc/postgresql", "/var/log/postgresql",
                     "/etc/systemd/system/minicloud-postgres.service",
                     "/etc/systemd/system/minicloud-preview-canary.service"):
            path = Path(name)
            check(not path.exists() and not path.is_symlink(), "UNREQUESTED_DEFAULT_APP_PRESENT")
        try:
            pwd.getpwnam("postgres")
        except KeyError:
            pass
        else:
            raise ProbeError("UNREQUESTED_DATABASE_ACCOUNT_PRESENT")
        return {"defaultApplicationsAbsent": True, "postgresPackagesAbsent": True,
                "postgresAccountAbsent": True, "databaseDirectoriesAbsent": True,
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

    def codex_git(self):
        backing = self.directory / "codex-git-fixture.ext4"
        mountpoint = Path("/var/lib") / ("minicloud-native-git-" + self.run_id)
        device = None
        filesystem_uuid = None
        workspace = mountpoint / "workspace"
        self.git_fixture_cleanup = "pending"
        try:
            check(not mountpoint.exists(), "CODEX_GIT_MOUNTPOINT_ALREADY_EXISTS")
            mountpoint.mkdir(mode=0o755)
            with backing.open("xb") as stream:
                stream.truncate(256 * 1024**2)
            device = command(["losetup", "--find", "--show", "--nooverlap", str(backing)])
            guarded_loop(device, backing)
            command(["mkfs.ext4", "-q", "-F", device])
            filesystem_uuid = command(["blkid", "-s", "UUID", "-o", "value", device])
            command(["mount", "-t", "ext4", "-o", "nodev,nosuid", device, str(mountpoint)])
            mountpoint.chmod(0o755)
            origin, seed, verification = (mountpoint / name for name in ("origin.git", "seed", "verification"))
            for path in (origin, seed, verification):
                path.mkdir(mode=0o700)

            def git(path, *args, input=None):
                return command(["git", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false",
                    "-c", "protocol.file.allow=always", "-C", str(path), *args], input=input)

            git(origin, "init", "--bare", "--quiet", "--initial-branch=main")
            git(seed, "init", "--quiet", "--initial-branch=main")
            (seed / "README.txt").write_text("Explicit native Git fixture; no user credentials.\n")
            git(seed, "add", "README.txt")
            git(seed, "-c", "user.name=Native fixture", "-c", "user.email=native@example.invalid",
                "commit", "--quiet", "-m", "Native fixture")
            base = git(seed, "rev-parse", "HEAD")
            git(seed, "push", "--quiet", str(origin), "HEAD:refs/heads/main",
                "HEAD:refs/heads/native-codex-probe")
            registration = dict(path=str(workspace / "repos/project"), origin=str(origin), baseSha=base,
                branch="native-codex-probe", remoteRef="refs/heads/native-codex-probe",
                pushAuthorized=True, defaultBranches=["main"])
            config = dict(vmId=self.run_id + "-git", generation=1, kind="ephemeral", uid=2000, gid=2000,
                workspace=str(workspace), repositories=[registration], ignoredDataAcknowledged=True,
                startServices=False, infrastructureUnits=[], stopUnits=[])
            adapter = self.rt.Linux(config)
            local_git = self.rt.Git(allow_local=True, verification_dir=verification)
            runtime = self.rt.Runtime(config, self.directory / "git-runtime-state", adapter=adapter, git=local_git)

            def call(action, **extra):
                return runtime.handle(dict(vmId=config["vmId"], generation=1,
                    operationId="native-codex-git-save", action=action, **extra))

            check(call("bootstrap")["type"] == "BootstrapReady", "CODEX_GIT_FIXTURE_BOOTSTRAP_FAILED")
            home = workspace / "home"
            codex_home = home / ".codex"
            codex = subprocess.run(["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
                "env", "HOME=" + str(home), "CODEX_HOME=" + str(codex_home), "codex", "--version"],
                cwd=workspace, capture_output=True, text=True, timeout=30,
                env={"PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"})
            check(codex.returncode == 0 and "0.169.0" in codex.stdout and not codex.stderr.strip(),
                  "CODEX_GIT_PROBE_VERSION_FAILED")
            version_probe_count = len(list((codex_home / ".tmp").glob("git-*")))
            initialize_codex_without_credentials(Path(registration["path"]), home)
            probes = sorted((codex_home / ".tmp").glob("git-*"))
            check(len(probes) > version_probe_count and
                  all(path.is_dir() and not path.is_symlink() for path in probes),
                  "ACTUAL_CODEX_EMPTY_GIT_PROBES_NOT_CREATED")
            for probe in probes:
                check(set(path.name for path in probe.iterdir()) == {"HEAD", "objects", "refs"} and
                      (probe / "HEAD").read_bytes() == b"ref: refs/heads/main\n" and
                      not any((probe / "objects").iterdir()) and not any((probe / "refs").iterdir()),
                      "ACTUAL_CODEX_PROBE_FORMAT_CHANGED")

            def probe_tree():
                result = {}
                for probe in probes:
                    for path in (probe, *probe.rglob("*")):
                        info = path.lstat()
                        check(not path.is_symlink(), "ACTUAL_CODEX_PROBE_SYMLINK")
                        check(path.is_dir() or stat.S_ISREG(info.st_mode), "ACTUAL_CODEX_PROBE_SPECIAL_FILE")
                        result[str(path.relative_to(codex_home))] = (
                            info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode),
                            sha(path) if path.is_file() else None)
                return result

            original = probe_tree()
            evidence = local_git.inspect([registration], workspace)
            check(len(evidence) == 1 and call("git-check")["type"] == "GitSaveCandidate",
                  "ACTUAL_CODEX_PROBES_BLOCKED_REGISTERED_GIT")
            check(probe_tree() == original, "GIT_INSPECTION_MUTATED_CODEX_PROBES")
            rejected = []

            def reject(label):
                try:
                    call("git-check")
                except self.rt.Blocked:
                    rejected.append(label)
                    return
                raise ProbeError("CODEX_GIT_UNREGISTERED_DATA_ACCEPTED")

            unregistered = workspace / "artifacts/native-unregistered.git"
            unregistered.mkdir()
            try:
                git(unregistered, "init", "--bare", "--quiet")
                reject("genuine-unregistered-bare-outside-codex-cache")
            finally:
                shutil.rmtree(unregistered)
            check(local_git.inspect([registration], workspace) == evidence,
                  "GIT_BASELINE_NOT_RESTORED_AFTER_BARE_NEGATIVE")
            for kind in ("objects", "refs", "symlink"):
                candidate = codex_home / ".tmp" / ("git-" + uuid.uuid4().hex[:6])
                check(not candidate.exists(), "CODEX_GIT_NEGATIVE_ALREADY_EXISTS")
                shutil.copytree(probes[0], candidate, symlinks=True)
                try:
                    for target in (candidate, *candidate.rglob("*")):
                        info = (probes[0] / target.relative_to(candidate)).stat()
                        os.chown(target, info.st_uid, info.st_gid, follow_symlinks=False)
                    check(local_git.inspect([registration], workspace) == evidence,
                          "EMPTY_CODEX_PROBE_COPY_NOT_ACCEPTED")
                    if kind == "objects":
                        command(["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups", "env",
                            "HOME=" + str(home), "GIT_CONFIG_NOSYSTEM=1", "GIT_CONFIG_GLOBAL=/dev/null",
                            "git", "-c", "core.hooksPath=/dev/null", "--git-dir=" + str(candidate),
                            "hash-object", "-w", "--stdin"], input="Native real Git object\n")
                    elif kind == "refs":
                        ref = candidate / "refs/heads/native-data"
                        ref.parent.mkdir(parents=True, exist_ok=True)
                        ref.write_text(base + "\n")
                        os.chown(ref.parent, 2000, 2000)
                        os.chown(ref, 2000, 2000)
                    else:
                        objects = candidate / "objects"
                        check(objects.is_dir() and not objects.is_symlink() and not any(objects.iterdir()),
                              "UNEXPECTED_CODEX_PROBE_OBJECTS")
                        objects.rmdir()
                        objects.symlink_to(origin / "objects", target_is_directory=True)
                    reject("codex-shaped-probe-with-" + kind)
                finally:
                    shutil.rmtree(candidate)
                check(local_git.inspect([registration], workspace) == evidence,
                      "GIT_BASELINE_NOT_RESTORED_AFTER_PROBE_NEGATIVE")
            command(["systemctl", "start", "minicloudwork.slice"])
            saved = call("git-save", gatewayAdmissionsFenced=True, unobservableToolsAbsent=True,
                         t3SnapshotSequence=0)
            check(saved["type"] == "GitSaved" and saved["repositories"] == evidence,
                  "CODEX_PROBES_PREVENTED_ACTUAL_GIT_SAVE")
            check(call("receipt-validate", receiptId=saved["receiptId"])["type"] == "ReceiptValid",
                  "CODEX_GIT_SAVE_RECEIPT_INVALID")
            check(probe_tree() == original, "GIT_SAVE_MUTATED_CODEX_PROBES")
            return {"actualCodexVersionUid": 2000, "actualCodexVersion": "0.169.0",
                    "actualCodexEmptyProbeCount": len(probes), "registeredCheckoutInspected": True,
                    "codexVersionAloneProbeCount": version_probe_count,
                    "actualCodexCacheCreationTrigger": "credential-free-app-server-initialize",
                    "codexAppServerExitedCleanly": True,
                    "actualCodexExactThreeEntryEmptyProbeFormat": True,
                    "actualRuntimeGitSavedReceipt": True, "actualKernelFrozenReadonlyBind": True,
                    "probeFilesAndModesUnchanged": True, "rejected": rejected,
                    "remoteBareOutsideWorkspace": not origin.is_relative_to(workspace),
                    "allowLocalOnlyForNativeFixture": True, "gitFixtureCommandsRunAsRoot": True,
                    "ordinaryUserAuthenticatedPushQualified": False,
                    "scope": "loop-backed synthetic Git fixture; no T3 instance; separate cold T3 probes follow"}
        finally:
            try:
                events = Path("/sys/fs/cgroup/minicloudwork.slice/cgroup.events")
                if events.exists() and "frozen 1" in events.read_text():
                    command(["systemctl", "thaw", "minicloudwork.slice"])
                # ismount() misses a bind mount whose parent is on the same filesystem.
                bound = subprocess.run(["findmnt", "-rn", "-M", str(workspace), "-o", "UUID"],
                                       capture_output=True, text=True, timeout=30)
                check(bound.returncode in (0, 1), "CODEX_GIT_BIND_MOUNT_STATUS_UNKNOWN")
                if bound.returncode == 0:
                    check(bound.stdout.strip() == filesystem_uuid,
                          "CODEX_GIT_BIND_IDENTITY_CHANGED")
                    command(["mount", "-o", "remount,bind,rw", str(workspace)])
                    command(["umount", str(workspace)])
                if os.path.ismount(mountpoint):
                    check(command(["findmnt", "-rn", "-M", str(mountpoint), "-o", "UUID"]) == filesystem_uuid,
                          "CODEX_GIT_MOUNT_IDENTITY_CHANGED")
                    command(["umount", str(mountpoint)])
                if device:
                    guarded_loop(device, backing)
                    command(["losetup", "--detach", device])
                if backing.exists():
                    check(not command(["losetup", "--associated", str(backing)]),
                          "CODEX_GIT_LOOP_STILL_ATTACHED")
                    with backing.open("r+b") as output:
                        for _ in range(256):
                            output.write(bytes(1024**2))
                        output.flush()
                        os.fsync(output.fileno())
                    backing.unlink()
                if mountpoint.exists():
                    mountpoint.rmdir()
                self.git_fixture_cleanup = "complete"
            finally:
                self.report.value["codexGitFixtureCleanup"] = self.git_fixture_cleanup

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
        check(command(["systemctl", "show", "minicloud-workspace.service", "-p", "MainPID", "--value"]) == "0",
              "SUPERVISOR_ALREADY_RUNNING_BEFORE_IMAGE_BOOTSTRAP")
        check(not Path("/run/minicloud-workspace/control.sock").exists(),
              "SUPERVISOR_SOCKET_PRESENT_BEFORE_IMAGE_BOOTSTRAP")
        check(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]) == "0",
              "T3_STARTED_BEFORE_IMAGE_BOOTSTRAP")
        result = bootstrap_with_local_enrollment(self.config, self.directory)
        check(result["type"] == "BootstrapReady", "BOOTSTRAP_RECEIPT_REQUIRED")
        check(command(["findmnt", "-rn", "-M", "/workspace", "-o", "UUID"]) == fs_uuid,
              "MOUNT_UUID_MISMATCH")
        check(not Path("/workspace/postgres").exists() and not Path("/workspace/emulators").exists(),
              "UNREQUESTED_DEFAULT_WORKLOAD_DIRECTORY")
        codex_home = Path("/workspace/home/.codex")
        check(codex_home.is_dir() and not codex_home.is_symlink() and
              codex_home.stat().st_uid == 2000 and codex_home.stat().st_gid == 2000 and
              stat.S_IMODE(codex_home.stat().st_mode) == 0o700, "INVALID_CODING_CODEX_HOME")
        codex = subprocess.run(["setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
            "env", "HOME=/workspace/home", "CODEX_HOME=" + str(codex_home), "codex", "--version"],
            cwd="/workspace", capture_output=True, text=True, timeout=30,
            env={"PATH": "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"})
        check(codex.returncode == 0 and "0.169.0" in codex.stdout and not codex.stderr.strip(),
              "CODEX_VERSION_NOT_WARNING_FREE_AS_CODING_USER")
        for name, value in (("config/native-user.json", '{"userCreated":true}\n'),
                            ("transcripts/native-user.txt", "representative-user-data\n"),
                            ("config/lost+found/.minicloud-volume.json", "nested user metadata-like file\n"),
                            ("artifacts/lost+found", "nested user filename, not filesystem metadata\n"),
                            ("transcripts/.minicloud-volume.json", "nested transcript metadata-like file\n")):
            path = Path("/workspace") / name
            path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            os.chown(path.parent, 2000, 2000)
            path.write_text(value)
            os.chown(path, 2000, 2000)
        return {"type": result["type"], "filesystemUuid": fs_uuid,
                "formattedOnlyVerifiedNewLoopDevice": True, "providerPersistenceProof": False,
                "noApplicationInstalledOrRegistered": True,
                "actualImageBootstrapEntrypoint": True,
                "supervisorInitiallyInactive": True, "supervisorSocketInitiallyAbsent": True,
                "noManualSupervisorOrT3Start": True,
                "codexHomeExists": True, "codexHomeUid": 2000, "codexHomeGid": 2000,
                "codexHomeMode": "0700", "codexVersionAsCodingUid": "0.169.0",
                "codexVersionStderrEmpty": True, "codexLoginNotRequiredForVersionProof": True,
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

    def copilot_provider(self):
        settings_path = Path("/workspace/t3/userdata/settings.json")
        before = settings_path.read_bytes()
        settings = json.loads(before)
        provider = settings.get("providerInstances", {}).get("minicloud-copilot", {})
        check(provider.get("driver") == "acpRegistry" and provider.get("enabled") is True and
              provider.get("config") == {"source": "local", "commandPath": "/usr/local/bin/copilot",
                                         "commandArgs": ["--acp", "--stdio"]},
              "COPILOT_LOCAL_PROVIDER_NOT_SEEDED")
        for action in ("configure", "probe"):
            result = json.loads(command([
                "setpriv", "--reuid=2000", "--regid=2000", "--clear-groups",
                "env", "-i", "PATH=/usr/local/bin:/usr/bin:/bin", "HOME=/workspace/home",
                "python3", "-B", "/opt/minicloud-workspace/copilot_provider.py", action],
                cwd="/workspace"))
            check(result.get("state") == ("AlreadyConfigured" if action == "configure" else "Compatible"),
                  "COPILOT_CODING_USER_SETUP_FAILED")
            if action == "probe":
                check(result.get("version") == "1.0.114" and
                      result.get("authentication") == "NotChecked", "COPILOT_WRONG_ACP_VERSION")
        check(json.loads(settings_path.read_bytes()).get("providerInstances", {}) ==
              settings["providerInstances"], "COPILOT_REAPPLY_CHANGED_PROVIDER_INSTANCES")
        return {"stdioInitializeAsCodingUser": True, "version": "1.0.114",
                "providerSeededBeforeT3Start": True, "providerReapplyIdempotent": True,
                "authenticatedPromptAndFollowupQualified": False}

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
        result = self.client.call("init", tokenFiles=tokens, createProject=True)
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

    def source_control(self):
        main_pid = int(command(["systemctl", "show", "minicloud-t3.service", "-p", "MainPID", "--value"]))
        check(main_pid > 0, "SOURCE_CONTROL_T3_SERVICE_NOT_RUNNING")
        environment = dict(entry.split("=", 1) for entry in
                           Path("/proc", str(main_pid), "environ").read_text().split("\0") if "=" in entry)
        check(not any(environment.get(name) for name in
                      ("GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN",
                       "GLAB_TOKEN", "GITLAB_TOKEN", "OAUTH_TOKEN",
                       "GH_CONFIG_DIR", "GLAB_CONFIG_DIR")),
              "SOURCE_CONTROL_ENVIRONMENT_NOT_PRISTINE")
        check(environment.get("HOME") == "/workspace/home", "WRONG_SOURCE_CONTROL_SERVICE_HOME")
        for name in (".config/gh/hosts.yml", ".config/glab-cli/config.yml"):
            check(not (Path("/workspace/home") / name).exists(), "SOURCE_CONTROL_CONFIG_NOT_PRISTINE")
        lock = json.loads((HERE / "manifest.lock.json").read_text())
        versions = source_control_versions(environment.get("PATH"), lock)
        result = self.client.call("source-control", versions={name: lock[name]["version"]
                                                            for name in ("gh", "glab")})
        marker = Path("/workspace/artifacts/native-source-control-clis")
        self.wait(marker.exists)
        check(marker.read_text() == "source-control-clis-ok\n" and marker.stat().st_uid == 2000,
              "SOURCE_CONTROL_TERMINAL_BINARIES_UNAVAILABLE")
        return {**versions, **result, "servicePathChecked": True, "terminalVersionsExecuted": True,
                "providerCredentialsSupplied": False, "userSignInQualified": False}

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
        connection = sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True)
        try:
            metadata = connection.execute(
                "SELECT schema_version,last_sequence FROM orchestration_v2_projection_metadata "
                "WHERE projection_name='thread-projections'").fetchall()
            project_events = connection.execute(
                "SELECT COUNT(*) FROM orchestration_events WHERE aggregate_kind='project'").fetchone()[0]
            check(project_events > 0 and evidence["applicationSequence"] == self.snapshot and
                  evidence["threadSequence"] == 0 and metadata == [(1, 0)],
                  "ACTUAL_PROJECT_ONLY_FOUNDATION_STATE_NOT_PROVEN")
        finally:
            connection.close()
        return {"actualPinnedT3Database": True, "threadSequence": evidence["threadSequence"],
                "applicationSequence": evidence["applicationSequence"],
                "globalV2Sequence": evidence["globalV2Sequence"],
                "actualProjectEventCount": project_events,
                "projectOnlyFoundationMetadata": metadata,
                "httpCursorMatchesApplicationNotThreadCursor": True,
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
            blocked = []
            def expect_blocked(label, snapshot_sequence=None):
                try:
                    self.rt.t3_idle_database(target, snapshot_sequence)
                except self.rt.Blocked:
                    blocked.append(label)
                    return
                raise ProbeError("UNSAFE_SQLITE_COPY_ACCEPTED")
            next_sequence = copied.execute(
                "SELECT COALESCE(MAX(sequence),0)+1 FROM orchestration_events").fetchone()[0]
            synthetic_row(copied, "orchestration_events",
                          dict(sequence=next_sequence, application_event_version=1, aggregate_kind="project"))
            copied.commit()
            legacy_project = self.rt.t3_idle_database(target, next_sequence)
            check(legacy_project["applicationSequence"] == next_sequence and
                  legacy_project["threadSequence"] == base["threadSequence"] and
                  legacy_project["globalV2Sequence"] == base["globalV2Sequence"],
                  "LEGACY_PROJECT_APPLICATION_CURSOR_FAILED")
            next_sequence += 1
            synthetic_row(copied, "orchestration_events",
                          dict(sequence=next_sequence, application_event_version=2, aggregate_kind="project"))
            copied.commit()
            check(Path(str(target) + "-wal").stat().st_size > 32, "COMMITTED_WAL_NOT_PRESENT")
            current = self.rt.t3_idle_database(target, next_sequence)
            check(current["globalV2Sequence"] == next_sequence and
                  current["applicationSequence"] == next_sequence and
                  current["threadSequence"] == base["threadSequence"], "READONLY_WAL_OR_GLOBAL_CURSOR_FAILED")
            for kind in ("thread", "unknown-native-kind"):
                synthetic_row(copied, "orchestration_events",
                              dict(sequence=next_sequence + 1, application_event_version=1, aggregate_kind=kind))
                copied.commit()
                expect_blocked("foundation-nonproject-" + kind, next_sequence)
                copied.execute("DELETE FROM orchestration_events WHERE sequence=?", (next_sequence + 1,))
                copied.commit()
            next_sequence += 1
            synthetic_row(copied, "orchestration_events",
                          dict(sequence=next_sequence, application_event_version=2, aggregate_kind="thread"))
            copied.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=2,last_sequence=? "
                           "WHERE projection_name='thread-projections'", (next_sequence,))
            copied.commit()
            threaded = self.rt.t3_idle_database(target, next_sequence)
            check(threaded["applicationSequence"] == threaded["threadSequence"] ==
                  threaded["globalV2Sequence"] == next_sequence, "V2_THREAD_APPLICATION_CURSOR_FAILED")
            thread_sequence = next_sequence
            next_sequence += 1
            synthetic_row(copied, "orchestration_events",
                          dict(sequence=next_sequence, application_event_version=2, aggregate_kind="project"))
            copied.commit()
            after_project = self.rt.t3_idle_database(target, next_sequence)
            check(after_project["applicationSequence"] == after_project["globalV2Sequence"] == next_sequence
                  and after_project["threadSequence"] == thread_sequence,
                  "PROJECT_EVENT_INCORRECTLY_ADVANCED_THREAD_PROJECTION_REQUIREMENT")
            expect_blocked("stale-http-application-cursor", base["applicationSequence"])
            copied.execute("UPDATE orchestration_v2_projection_metadata SET last_sequence=? "
                           "WHERE projection_name='thread-projections'", (thread_sequence - 1,))
            copied.commit()
            expect_blocked("projection-lag")
            copied.execute("UPDATE orchestration_v2_projection_metadata SET last_sequence=?,schema_version=999 "
                           "WHERE projection_name='thread-projections'", (thread_sequence,))
            copied.commit()
            expect_blocked("unknown-schema-version")
            copied.execute("UPDATE orchestration_v2_projection_metadata SET schema_version=2 "
                           "WHERE projection_name='thread-projections'")
            synthetic_row(copied, "orchestration_v2_projection_runs",
                          dict(thread_id="synthetic-native-only", status="unknown-native-status"))
            copied.commit()
            expect_blocked("unknown-work-status")
            return {"basis": "SQLite backup of actual native T3 DB; corruption ONLY in private copy",
                    "committedWalObserved": True,
                    "applicationCursorContract": "all-project-events-or-v2-thread-events",
                    "legacyProjectAdvancesApplicationCursor": True,
                    "v2ProjectAndThreadAdvanceApplicationCursor": True,
                    "foundationRejectsEveryNonProjectEvent": True,
                    "v2MetadataComparedOnlyWithThreadCursor": True,
                    "projectionLagIndependentlyRejected": True,
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
        names = ("artifacts/native-input-seen", "config/native-user.json", "transcripts/native-user.txt",
                 "config/lost+found/.minicloud-volume.json", "artifacts/lost+found",
                 "transcripts/.minicloud-volume.json")
        mounted = self.directory / "restore-readback"
        restored = self.directory / "restore-target"
        backing = self.directory / "restore-target.ext4"
        key = self.directory / "restore.agekey"
        ciphertext = self.directory / "user-files.tar.age"
        target_device = None
        target_alias = None
        target_uuid = None
        self.restore_fixture_cleanup = "pending"
        mounted.mkdir(mode=0o700)
        restored.mkdir(mode=0o700)
        guarded_loop(self.device, self.directory / "fixture.ext4")
        command(["mount", "-t", "ext4", "-o", "ro,noload,nodev,nosuid", self.device, str(mounted)])
        try:
            check((mounted / "lost+found").is_dir() and not (mounted / "lost+found").is_symlink()
                  and not any((mounted / "lost+found").iterdir()),
                  "SOURCE_STANDARD_EXT4_LOST_FOUND_NOT_EMPTY")
            source_owner = (mounted / ".minicloud-volume.json").read_bytes()
            check(json.loads(source_owner) == self.config["volume"]["owner"],
                  "SOURCE_RESTORE_FIXTURE_OWNER_CHANGED")
            expected = {name: sha(mounted / name) for name in names}
            source_links = {str(path.relative_to(mounted)): os.readlink(path)
                            for path in mounted.rglob("*") if path.is_symlink()}
            codex_links = {name: target for name, target in source_links.items()
                           if name.startswith("home/.codex/tmp/arg0-linux/")}
            check({Path(name).name for name in codex_links} >=
                  {"codex-linux-sandbox", "apply_patch", "applypatch"} and
                  all(os.path.isabs(target) for target in codex_links.values()),
                  "ACTUAL_CODEX_ABSOLUTE_SYMLINK_FIXTURE_MISSING")
            self.rt.validate_backup_tree(mounted)
            command(["age-keygen", "--output", str(key)])
            recipient = command(["age-keygen", "-y", str(key)])
            check(recipient.startswith("age1"), "INVALID_SYNTHETIC_AGE_RECIPIENT")
            self.rt.encrypt_archive(mounted, ciphertext, recipient)
            check(ciphertext.is_file() and ciphertext.stat().st_size > 0,
                  "PRODUCTION_BACKUP_CIPHERTEXT_MISSING")
            inspect_archive = subprocess.Popen(["age", "--decrypt", "--identity", str(key), str(ciphertext)],
                                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            try:
                members = set()
                archived_links = {}
                with tarfile.open(fileobj=inspect_archive.stdout, mode="r|") as archive:
                    for member in archive:
                        check(len(members) < 10000 and len(member.name) <= 1024,
                              "NATIVE_ARCHIVE_UNEXPECTEDLY_LARGE")
                        members.add(member.name)
                        if member.issym():
                            relative = member.name.removeprefix("data/")
                            check(relative in source_links and member.linkname == source_links[relative]
                                  and member.size == 0, "SYMLINK_TARGET_CONTENT_ARCHIVED")
                            archived_links[relative] = member.linkname
                inspect_archive.stdout.read()
                check(inspect_archive.wait(timeout=10) == 0, "BACKUP_ARCHIVE_DECRYPTION_FAILED")
            finally:
                inspect_archive.stdout.close()
                if inspect_archive.poll() is None:
                    inspect_archive.terminate()
                    inspect_archive.wait(timeout=10)
            check(not {"data/lost+found", "data/.minicloud-volume.json"} & members,
                  "PRODUCTION_WRITER_INCLUDED_ROOT_FILESYSTEM_METADATA")
            check(all("data/" + name in members for name in names), "NESTED_USER_DATA_OMITTED")
            check(archived_links == source_links, "SOURCE_SYMLINK_METADATA_NOT_ARCHIVED")
            with backing.open("xb") as stream:
                stream.truncate(512 * 1024**2)
            target_device = command(["losetup", "--find", "--show", "--nooverlap", str(backing)])
            guarded_loop(target_device, backing)
            command(["mkfs.ext4", "-q", "-F", target_device])
            target_uuid = command(["blkid", "-s", "UUID", "-o", "value", target_device])
            check(target_uuid != self.config["volume"]["filesystemUuid"], "RESTORE_TARGET_UUID_REUSED")
            target_alias = Path("/dev/disk/by-id") / ("minicloud-native-restore-" + self.run_id)
            check(not target_alias.exists() and not target_alias.is_symlink(), "RESTORE_ALIAS_ALREADY_EXISTS")
            target_alias.symlink_to(target_device)
            target_volume_id = self.run_id + "-restore-target"
            target_claim = dict(device=str(target_alias), providerDeviceId=target_alias.name,
                providerVolumeId=target_volume_id, filesystemUuid=target_uuid, initializeOwner=True,
                owner={**self.config["volume"]["owner"], "providerVolumeId": target_volume_id})
            config = {**self.config, "vmId": self.run_id + "-restore", "workspace": str(restored),
                      "volume": target_claim}
            runtime = self.rt.Runtime(config, self.directory / "restore-runtime-state")
            runtime.adapter.mount(target_claim)
            target_marker = restored / ".minicloud-volume.json"
            target_owner = target_marker.read_bytes()
            check(target_owner != source_owner, "RESTORE_TARGET_OWNER_NOT_INDEPENDENT")
            lost = restored / "lost+found"
            check(lost.is_dir() and not lost.is_symlink() and not any(lost.iterdir()),
                  "TARGET_STANDARD_EXT4_LOST_FOUND_NOT_EMPTY")
            lost_identity = (lost.stat().st_ino, lost.stat().st_uid, lost.stat().st_gid,
                             stat.S_IMODE(lost.stat().st_mode))
            marker_identity = (target_marker.stat().st_ino, target_marker.stat().st_uid,
                               stat.S_IMODE(target_marker.stat().st_mode))
            rejected = []

            def reject_metadata(label):
                output = io.BytesIO()
                try:
                    self.rt.write_backup_archive(restored, output)
                except self.rt.Blocked:
                    check(output.getvalue() == b"", "UNSAFE_METADATA_PARTIALLY_ARCHIVED")
                    rejected.append(label)
                    return
                raise ProbeError("PRODUCTION_WRITER_ACCEPTED_UNSAFE_FILESYSTEM_METADATA")

            recovered = lost / "native-recovered-data"
            recovered.write_bytes(b"Do not silently discard recovered native fixture data\n")
            try:
                reject_metadata("nonempty-lost-found")
                check(recovered.read_bytes() == b"Do not silently discard recovered native fixture data\n",
                      "RECOVERED_DATA_MUTATED_DURING_REJECTION")
            finally:
                recovered.unlink()
            preserved_lost = restored / "native-preserved-lost-found"
            lost.rename(preserved_lost)
            try:
                lost.symlink_to(preserved_lost, target_is_directory=True)
                reject_metadata("aliased-lost-found")
            finally:
                lost.unlink()
                preserved_lost.rename(lost)
            # Only import is under test: no fabricated provider/off-host admission or durability claim.
            operation = "native-local-restore-import"
            runtime.s.update(phase="RestoreReady", restoreOperation=operation,
                restoreSource=dict(receiptId="native-local-import-fixture", representativeHashes=expected))
            runtime.save()
            decrypted = subprocess.Popen(["age", "--decrypt", "--identity", str(key), str(ciphertext)],
                                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            try:
                result = runtime.restore_import(operation, decrypted.stdout)
                decrypted.stdout.read()
                check(decrypted.wait(timeout=10) == 0, "SYNTHETIC_USER_FILE_DECRYPTION_FAILED")
            finally:
                decrypted.stdout.close()
                if decrypted.poll() is None:
                    decrypted.terminate()
                    decrypted.wait(timeout=10)
            check(result["type"] == "PrimaryRestored" and result["representativeHashesVerified"],
                  "PRODUCTION_RESTORE_IMPORT_RECEIPT_MISSING")
            check(all(sha(restored / name) == digest for name, digest in expected.items()),
                  "RESTORED_REPRESENTATIVE_USER_HASH_MISMATCH")
            check(all((restored / name).stat().st_uid == (restored / name).stat().st_gid == 2000
                      for name in expected), "RESTORED_USER_FILE_OWNERSHIP_MISMATCH")
            for name, target in source_links.items():
                link = restored / name
                check(link.is_symlink() and os.readlink(link) == target and
                      link.lstat().st_uid == link.lstat().st_gid == 2000,
                      "RESTORED_SYMLINK_METADATA_OR_OWNERSHIP_MISMATCH")
            check(target_marker.read_bytes() == target_owner and
                  (target_marker.stat().st_ino, target_marker.stat().st_uid,
                   stat.S_IMODE(target_marker.stat().st_mode)) == marker_identity,
                  "TARGET_OWNERSHIP_METADATA_REPLACED")
            check(not any(lost.iterdir()) and
                  (lost.stat().st_ino, lost.stat().st_uid, lost.stat().st_gid,
                   stat.S_IMODE(lost.stat().st_mode)) == lost_identity, "TARGET_LOST_FOUND_REPLACED")
            runtime.adapter.verify_owner(target_claim)
            return {"filesRestored": len(expected), "allSha256Matched": True,
                    "encryptedWithAge": True, "productionArchiveWriterUsed": True,
                    "actualRuntimeRestoreImport": True, "restoreReceiptType": result["type"],
                    "sourceAndTargetAreDistinctLoopExt4": True,
                    "rootFilesystemMetadataOmitted": True, "nestedMetadataNamesPreserved": True,
                    "targetOwnerAndLostFoundPreserved": True, "rejected": rejected,
                    "productionBackupTreeValidationUsed": True,
                    "allWorkspaceSymlinkMetadataRestored": True,
                    "actualCodexSymlinkCount": len(codex_links),
                    "actualCodexSymlinkMetadataRestored": True,
                    "symlinkTargetBytesNotArchived": True, "symlinkUidsAllCoding": True,
                    "representativeUserFilesAllCodingOwned": True,
                    "actualCodexSymlinkEvidence": [
                        {"relativePath": name, "target": target,
                         "targetTextSha256": hashlib.sha256(os.fsencode(target)).hexdigest(),
                         "archiveEntryType": "symlink", "archivedContentBytes": 0,
                         "restoredUid": 2000, "restoredGid": 2000}
                        for name, target in sorted(codex_links.items())],
                    "recoveryKeyOutsideSourceAndTarget": True, "recoveryKeyOffVm": False,
                    "plaintextArchivePersisted": False, "preAdmittedLocalImportFixture": True,
                    "restoreAdmissionQualified": False,
                    "scope": "production writer/age/import on separate loop-ext4; NOT provider/off-host restore",
                    "providerOrOffHostRestoreQualified": False}
        finally:
            try:
                if os.path.ismount(restored):
                    check(command(["findmnt", "-rn", "-M", str(restored), "-o", "UUID"]) == target_uuid,
                          "RESTORE_TARGET_MOUNT_IDENTITY_CHANGED")
                    command(["umount", str(restored)])
                if target_alias:
                    check(target_alias.is_symlink() and os.readlink(target_alias) == target_device,
                          "RESTORE_TARGET_ALIAS_CHANGED")
                    target_alias.unlink()
                if target_device:
                    guarded_loop(target_device, backing)
                    command(["losetup", "--detach", target_device])
                if backing.exists():
                    check(not command(["losetup", "--associated", str(backing)]),
                          "RESTORE_TARGET_LOOP_STILL_ATTACHED")
                    with backing.open("r+b") as output:
                        for _ in range(512):
                            output.write(bytes(1024**2))
                        output.flush()
                        os.fsync(output.fileno())
                    backing.unlink()
                command(["umount", str(mounted)])
                key.unlink(missing_ok=True)
                ciphertext.unlink(missing_ok=True)
                restored.rmdir()
                mounted.rmdir()
                self.restore_fixture_cleanup = "complete"
            finally:
                self.report.value["restoreFixtureCleanup"] = self.restore_fixture_cleanup

    def cleanup(self):
        check(self.git_fixture_cleanup in ("not-started", "complete"), "CODEX_GIT_FIXTURE_CLEANUP_INCOMPLETE")
        check(self.restore_fixture_cleanup in ("not-started", "complete"), "RESTORE_FIXTURE_CLEANUP_INCOMPLETE")
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
            if not self.report.probe("codex-empty-git-probes-save-and-reject-data", self.codex_git):
                return
            if not self.report.probe("loop-ext4-root-bootstrap", self.prepare):
                return
            if not self.report.probe("native-http-scopes-tickets-websocket", self.start_services):
                return
            if not self.report.probe("copilot-stdio-provider-setup", self.copilot_provider):
                return
            self.report.probe("authenticated-generation-task-and-activity-hooks", self.root_hooks)
            if not self.report.probe("real-terminal-input-and-passive-root-hook", self.terminals):
                return
            if not self.report.probe("source-control-cli-service-terminal-and-discovery", self.source_control):
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
