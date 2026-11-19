#!/usr/bin/env python3
"""Loopback-only Connect enrollment. CLI approval output is memory-only, never terminal history."""
import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import re
import signal
import secrets
import subprocess
import threading
import urllib.request

from runtime import Git, Linux, Runtime, atomic_json, load_config, require, run

CONFIG = "/etc/minicloud-workspace/runtime.json"
STATE = "/var/lib/minicloud-workspace"
BASE = "/workspace/t3"
CLOUDFLARED = "/usr/local/bin/cloudflared"
CLOUDFLARED_SHA256 = "ae5e8add7884b0531d66bbca4570d1de491ff6da51e4ebe7eb160cdf241d9e42"
MANAGEMENT_UNIT = "minicloud-t3-connect.service"
MANAGEMENT_SCRIPT = "/opt/minicloud-workspace/t3_connect.py"


def command(action):
    require(action in ("link", "status", "logout"), "Unsupported Connect operation")
    return ["runuser", "-u", "coding", "--", "env", "-i", "HOME=/workspace/home",
            "CODEX_HOME=/workspace/home/.codex", "PATH=/usr/local/bin:/usr/bin:/bin",
            "t3", "connect", action, "--base-dir", BASE,
            *(["--headless"] if action == "link" else ["--json"] if action == "status" else [])]


def require_root_reserved_port():
    require(int(Path("/proc/sys/net/ipv4/ip_unprivileged_port_start").read_text()) > 774,
            "Root-reserved enrollment port required; refuse credential interception by coding processes")


def admitted(config, state_dir=STATE):
    require(config["kind"] == "primary" and config["workspace"] == "/workspace",
            "Connect is available only on the managed primary volume")
    require((Path(state_dir) / "state.json").is_file(), "Enrolled runtime required")
    runtime = Runtime(config, state_dir, git=Git())
    require(runtime.s["phase"] == "Ready", "Workspace admissions are fenced")
    return runtime


def require_trusted_listener(proc_root=Path("/proc")):
    properties = dict(line.split("=", 1) for line in run([
        "systemctl", "show", MANAGEMENT_UNIT, "-p", "ActiveState", "-p", "MainPID",
        "-p", "FragmentPath", "-p", "DropInPaths", "-p", "User", "-p", "ControlGroup"
    ]).splitlines() if "=" in line)
    unit_file = "/etc/systemd/system/" + MANAGEMENT_UNIT
    require(properties.get("ActiveState") == "active" and properties.get("User") == "root" and
            properties.get("FragmentPath") == unit_file and properties.get("DropInPaths") == "" and
            properties.get("ControlGroup") == "/system.slice/" + MANAGEMENT_UNIT,
            "Untrusted Connect management unit")
    pid = properties.get("MainPID", "")
    require(pid.isdecimal() and int(pid) > 0, "Connect management listener is missing")
    for path in (unit_file, MANAGEMENT_SCRIPT, "/opt/minicloud-workspace/runtime.py"):
        Linux.verify_unit_file(path)
    process = proc_root / pid
    status = dict(line.split(":", 1) for line in (process / "status").read_text().splitlines() if ":" in line)
    require(status.get("Uid", "").split() == ["0"] * 4 and
            (process / "cgroup").read_text().strip() == "0::/system.slice/" + MANAGEMENT_UNIT and
            (process / "cmdline").read_bytes() ==
            b"/usr/bin/python3\0-B\0" + os.fsencode(MANAGEMENT_SCRIPT) + b"\0serve\0",
            "Connect management process is not the root-owned helper")
    executable = process / "exe"
    Linux.verify_unit_file(str(executable.resolve(strict=True)))
    sockets = set()
    for descriptor in (process / "fd").iterdir():
        try:
            target = os.readlink(descriptor)
        except FileNotFoundError:
            continue
        if re.fullmatch(r"socket:\[[0-9]+\]", target):
            sockets.add(target[8:-1])
    listeners = []
    for table in ("tcp", "tcp6"):
        for line in (proc_root / "net" / table).read_text().splitlines()[1:]:
            fields = line.split()
            require(len(fields) >= 10, "Unrecognized kernel socket table")
            if fields[3] == "0A" and int(fields[1].split(":")[1], 16) == 774:
                listeners.append((table, fields[1], fields[7], fields[9]))
    require(len(listeners) == 1 and listeners[0][:3] == ("tcp", "0100007F:0306", "0") and
            listeners[0][3] in sockets, "Connect listener is not owned by the root management helper")
    require(run(["systemctl", "show", MANAGEMENT_UNIT, "-p", "MainPID", "--value"]) == pid,
            "Connect management process changed during admission")


def trusted_admission(config, state_dir=STATE):
    runtime = admitted(config, state_dir)
    require_root_reserved_port()
    require_trusted_listener()
    require_root_reserved_port()
    return runtime


def admission_proof(config, state_dir=STATE):
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        try:
            trusted_admission(config, state_dir)
            accepted = True
        except Exception:
            accepted = False
        return dict(type="ConnectManagementAdmission", admitted=accepted,
                    vmId=config["vmId"], generation=config["generation"])


def activate(config, state_dir=STATE):
    # This independent oneshot survives the management page's own PartOf restart.
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        admitted(config, state_dir)
        require(run(["systemctl", "is-active", "minicloud-t3.service"]) == "active",
                "Activation never starts stopped compute or an inactive T3 service")
        status = json.loads(run(command("status"), timeout=40, cwd="/workspace"))
        require(status.get("desired") is True and status.get("authenticated") is True,
                "Finish T3 account approval before activation")
        run(["systemctl", "restart", "minicloud-t3.service"], timeout=90)


def credential(config, state_dir=STATE):
    with (Path(state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        runtime = trusted_admission(config, state_dir)
        path = Path(state_dir) / "connect-credential.json"
        if path.exists():
            saved = json.loads(path.read_text())
            require(saved["identity"] == runtime.identity(), "Stale Connect credential")
            return saved["token"]
        token = secrets.token_urlsafe(48)
        atomic_json(path, dict(identity=runtime.identity(), token=token))
        return token


def public_status(value):
    require(isinstance(value, dict), "Invalid Connect status")
    reachability = value.get("reachability")
    return {
        "linked": value.get("linked") is True,
        "desired": value.get("desired") is True,
        "revoked": value.get("revoked") is True,
        "reachable": isinstance(reachability, dict) and reachability.get("status") == "reachable",
    }


class Enrollment:
    def __init__(self, config, state_dir=STATE):
        self.config, self.state_dir = config, state_dir
        self.lock = threading.RLock()
        self.process = None
        self.prompt = None
        self.result = "idle"
        self.remote_removal_confirmed = None

    @contextmanager
    def guard(self):
        with (Path(self.state_dir) / "control.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            admitted(self.config, self.state_dir)
            yield

    def clear_pending(self):
        with (Path(self.state_dir) / "control.lock").open("a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            (Path(self.state_dir) / "connect-operation.json").unlink(missing_ok=True)

    def status(self):
        with self.lock, self.guard():
            identity = {key: self.config[key] for key in ("vmId", "generation")}
            if self.process is not None:
                return dict(operation=self.result, approval=self.prompt, **identity)
            result = subprocess.run(command("status"), capture_output=True, text=True,
                                    timeout=40, cwd="/workspace")
            require(result.returncode == 0, "Connect status unavailable")
            with urllib.request.urlopen("http://127.0.0.1:3773/.well-known/t3/environment", timeout=3) as response:
                raw = response.read(65537)
            require(len(raw) <= 65536, "Oversized environment descriptor")
            descriptor = json.loads(raw)
            environment_id = descriptor.get("environmentId")
            require(descriptor.get("serverVersion") == "0.1.9" and
                    isinstance(environment_id, str) and
                    re.fullmatch(r"[a-zA-Z0-9._:-]{1,128}", environment_id),
                    "Unqualified environment descriptor")
            return dict(operation=self.result, environmentId=environment_id, **identity,
                        remoteRemovalConfirmed=self.remote_removal_confirmed,
                        **public_status(json.loads(result.stdout)))

    def start(self):
        with self.lock, self.guard():
            require(self.process is None, "A Connect operation is already pending")
            relay = Path(CLOUDFLARED)
            require(relay.is_file() and not relay.is_symlink() and relay.stat().st_uid == 0 and
                    not relay.stat().st_mode & 0o022, "Pinned root-owned relay client required")
            with relay.open("rb") as binary:
                require(hashlib.file_digest(binary, "sha256").hexdigest() == CLOUDFLARED_SHA256,
                        "Pinned relay client checksum mismatch")
            self.remote_removal_confirmed = None
            self.prompt, self.result = None, "awaiting_approval"
            marker = Path(self.state_dir) / "connect-operation.json"
            atomic_json(marker, {key: self.config[key] for key in ("vmId", "generation")})
            try:
                self.process = subprocess.Popen(command("link"), stdin=subprocess.DEVNULL,
                                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                                cwd="/workspace", start_new_session=True)
            except Exception:
                marker.unlink(missing_ok=True)
                raise
            threading.Thread(target=self.collect, args=(self.process,), daemon=True).start()
            timer = threading.Timer(900, self.expire, args=(self.process,))
            timer.daemon = True
            timer.start()

    def collect(self, process):
        url = None
        size = 0
        result = "approval_failed"
        timed_out = False
        try:
            while line := process.stdout.readline(4097):
                size += len(line)
                require(size <= 32768 and len(line) <= 4096, "Unexpected CLI output")
                text = line.decode("utf-8", errors="replace").strip()
                if text.endswith("Timed out waiting for T3 Connect authorization."):
                    timed_out = True
                if re.fullmatch(r"https://[a-zA-Z0-9.-]+/[^\s<>\"']*", text):
                    url = text
                match = re.fullmatch(r"Code: ([A-Za-z0-9-]{4,32})", text)
                if match and url:
                    with self.lock:
                        if self.process is process and self.result == "awaiting_approval":
                            self.prompt = dict(url=url, code=match[1])
            if process.wait() == 0:
                result = "approval_saved_activate_required"
            elif timed_out:
                result = "approval_expired"
        except Exception:
            self.terminate(process)
        finally:
            process.stdout.close()
            with self.lock:
                if self.process is process:
                    if self.result == "awaiting_approval":
                        self.result = result
                    self.process, self.prompt = None, None
                    self.clear_pending()

    @staticmethod
    def terminate(process):
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=5)

    def expire(self, process):
        with self.lock:
            if self.process is process:
                self.terminate(process)
                self.prompt = None
                self.result = "approval_expired"

    def unlink(self):
        with self.lock, self.guard():
            if self.process is not None:
                process, self.process = self.process, None
                self.terminate(process)
            self.prompt = None
            result = subprocess.run(command("logout"), capture_output=True, text=True,
                                    timeout=45, cwd="/workspace")
            (Path(self.state_dir) / "connect-operation.json").unlink(missing_ok=True)
            self.result = "unlinked" if result.returncode == 0 else "unlink_not_confirmed"
            # Upstream reports remote DELETE failures as warnings, sometimes with exit code zero.
            remote_confirmed = result.returncode == 0 and "Revoked the relay-side environment record." in result.stdout
            self.remote_removal_confirmed = remote_confirmed
            return dict(operation=self.result, remoteRemovalConfirmed=remote_confirmed)


class Handler(http.server.BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(10)

    def log_message(self, *_):
        pass

    def reply(self, status, value, content_type="application/json"):
        data = value if isinstance(value, bytes) else json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy",
                         "default-src 'self'; script-src 'self'; style-src 'self'; "
                         "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        try:
            if self.path == "/":
                self.reply(200, (Path(__file__).parent / "t3-connect.html").read_bytes(), "text/html; charset=utf-8")
            elif self.path == "/connect.js":
                self.reply(200, (Path(__file__).parent / "t3-connect.js").read_bytes(), "text/javascript; charset=utf-8")
            elif self.path == "/connect.css":
                self.reply(200, (Path(__file__).parent / "t3-connect.css").read_bytes(), "text/css; charset=utf-8")
            elif self.path == "/status":
                if self.authorized():
                    self.reply(200, self.server.enrollment.status())
            else:
                self.reply(404, {})
        except Exception:
            self.reply(409, dict(error="Connect unavailable; use a running, unfenced primary VM."))

    def do_POST(self):
        # Cross-origin forms cannot send this header, and this server never grants CORS access.
        if not self.authorized():
            return
        if self.headers.get("X-Minicloud-Connect") != "1" or self.headers.get("Content-Length", "0") != "0":
            self.reply(403, {})
            return
        try:
            manager = self.server.enrollment
            if self.path == "/link":
                manager.start()
                result = dict(operation="awaiting_approval")
            elif self.path == "/unlink":
                result = manager.unlink()
            elif self.path == "/activate":
                with manager.lock, manager.guard():
                    require(manager.process is None, "Finish account approval first")
                    run(["systemctl", "start", "--no-block", "minicloud-t3-connect-activate.service"])
                    result = dict(operation="restart_requested_check_status")
            else:
                self.reply(404, {})
                return
            self.reply(200, result)
        except Exception:
            self.reply(409, dict(error="Connect operation blocked or failed. No credentials were logged."))

    def authorized(self):
        supplied = self.headers.get("Authorization", "")
        if len(supplied) > 256 or not hmac.compare_digest(
                supplied.encode(), ("Bearer " + self.server.credential).encode()):
            self.reply(403, {})
            return False
        return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("serve", "activate", "credential", "admission"))
    parser.add_argument("--vm-id")
    parser.add_argument("--generation", type=int)
    args = parser.parse_args()
    require(os.geteuid() == 0, "Root-owned management service required")
    config = load_config(CONFIG)
    if args.action == "serve" and config["kind"] != "primary":
        return
    if args.action in ("credential", "admission"):
        require(config["vmId"] == args.vm_id and config["generation"] == args.generation,
                "Stale VM/generation")
        if args.action == "credential":
            require(not os.isatty(1), "Secret capture required")
            print(credential(config))
        else:
            print(json.dumps(admission_proof(config)))
    elif args.action == "activate":
        activate(config)
    else:
        admitted(config)
        require_root_reserved_port()
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 774), Handler)
        server.enrollment = Enrollment(config)
        server.credential = credential(config)
        # Binding succeeded, and systemd has killed the previous service's entire control group.
        server.enrollment.clear_pending()
        server.serve_forever()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        raise SystemExit("Connect management unavailable; no credentials logged")
