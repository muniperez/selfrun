#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Linux black-box seed rehearsal. Pass an already-published standalone executable."""

import errno
import hashlib
import hmac
import http.server
import json
import os
from pathlib import Path
import pty
import re
import select
import shutil
import socket
import socketserver
import ssl
import subprocess
import sys
import tarfile
import tempfile
import termios
import threading
import time


TOKEN = "fixture-token-not-for-any-real-provider"
CHANGED_TOKEN = "different-fixture-token-not-for-any-real-provider"
TOKENS = (TOKEN, CHANGED_TOKEN)
ADDRESS = "127.0.0.1"
DOMAIN = "cloud.example.com"
APP_DOMAIN = "apps.example.com"
PROVIDER = "api.digitalocean.com"
HETZNER = "api.hetzner.cloud"


class Fixture:
    def __init__(self):
        self.requests = []
        self.posts = []
        self.ssh_keys = []
        self.droplet = None
        self.visible = False
        self.lose_response = False
        self.user_data = None

    def api(self, path, body):
        if body is not None:
            self.posts.append(path)
            if path == "/v2/account/keys":
                key = {"id": 7, **body}
                self.ssh_keys.append(key)
                return {"ssh_key": key}
            assert path == "/v2/droplets" and self.droplet is None
            assert body["ssh_keys"] == [7] and body["vpc_uuid"] == "existing-network"
            self.user_data = body["user_data"]
            self.droplet = {"id": 17, "tags": body["tags"], "region": {"slug": "fixture"},
                            "size_slug": "fixture-size", "image": {"id": 42}, "networks": {
                                "v4": [{"type": "public", "ip_address": ADDRESS}]}}
            return None if self.lose_response else {"droplet": self.droplet}
        return {
            "/v2/account": {"account": {"uuid": "fixture", "status": "active", "email_verified": True, "droplet_limit": 5}},
            "/v2/droplets": {"droplets": [self.droplet] if self.visible and self.droplet else []},
            "/v2/account/keys": {"ssh_keys": self.ssh_keys},
            "/v2/regions": {"regions": [{"slug": "fixture", "available": True}]},
            "/v2/sizes": {"sizes": [{"slug": "fixture-size", "available": True, "regions": ["fixture"], "disk": 40,
                                    "vcpus": 2, "memory": 2048, "transfer": 1, "price_monthly": 12, "price_hourly": .02}]},
            "/v2/vpcs": {"vpcs": [{"id": "existing-network", "region": "fixture", "default": True}]},
            "/v2/images/ubuntu-24-04-x64": {"image": {"id": 42, "distribution": "Ubuntu", "slug": "ubuntu-24-04-x64",
                                                     "public": True, "regions": ["fixture"], "min_disk_size": 10}},
        }[path]


class HetznerFixture:
    def __init__(self):
        self.requests = []

    def api(self, path, body):
        if body is not None:
            if path == "/v1/ssh_keys":
                return {"ssh_key": {"id": 7}}
            assert path == "/v1/servers" and body["ssh_keys"] == [7]
            return None  # The create request arrived, but the client never receives a response.
        price = {"location": "fixture", "price_monthly": {"net": "11"}, "price_hourly": {"net": "0.02"},
                 "included_traffic": 1000000000, "price_per_tb_traffic": {"net": "1"}}
        return {
            "/v1/servers": {"servers": []},
            "/v1/ssh_keys": {"ssh_keys": []},
            "/v1/locations": {"locations": [{"id": 1, "name": "fixture"}]},
            "/v1/server_types": {"server_types": [{"id": 1, "name": "fixture-size", "architecture": "x86",
                                                    "cores": 2, "memory": 2, "disk": 40,
                                                    "locations": [{"name": "fixture", "available": True}]}]},
            "/v1/images": {"images": [{"id": 42, "os_flavor": "ubuntu", "os_version": "24.04", "architecture": "x86",
                                       "type": "system", "status": "available", "disk_size": 10}]},
            "/v1/pricing": {"pricing": {"currency": "USD", "server_types": [{"id": 1, "prices": [price]}],
                                        "primary_ips": [{"type": "ipv4", "prices": [price]}]}},
        }[path]


def proxy(root, fixtures):
    cert, key = root / "ca.pem", root / "tls-key.pem"
    origins = ("github.com", *fixtures)
    names = ",".join(f"DNS:{origin}" for origin in origins)
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                    "-subj", "/CN=seed-only-fixture", "-addext", f"subjectAltName={names}",
                    "-keyout", key, "-out", cert], check=True, capture_output=True)
    tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls.load_cert_chain(cert, key)

    class Http(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            if self.headers["Host"] == "github.com":
                assert self.path.startswith("/muniperez/selfrun/releases/download/server-v0.0.0-unavailable/")
                assert self.headers["Authorization"] is None
                self.send_error(404)
            else:
                self.respond(None)

        def do_POST(self):
            if self.headers.get("Transfer-Encoding") == "chunked":
                chunks = []
                while (size := int(self.rfile.readline(), 16)) > 0:
                    chunks.append(self.rfile.read(size))
                    assert self.rfile.read(2) == b"\r\n"
                raw = b"".join(chunks)
            else:
                raw = self.rfile.read(int(self.headers["Content-Length"]))
            assert all(token.encode() not in raw for token in TOKENS)
            self.respond(json.loads(raw))

        def respond(self, body):
            fixture = fixtures[self.headers["Host"]]
            assert self.headers["Authorization"] in {f"Bearer {token}" for token in TOKENS}
            path = self.path.split("?")[0]
            fixture.requests.append((self.command, path))
            value = fixture.api(path, body)
            if value is not None:
                data = json.dumps(value).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

    class Connect(socketserver.StreamRequestHandler):
        def handle(self):
            method, authority, _ = self.rfile.readline().decode().split()
            assert method == "CONNECT" and authority in {origin + ":443" for origin in origins}
            while line := self.rfile.readline():
                if line == b"\r\n":
                    break
            self.wfile.write(b"HTTP/1.1 200 Connection Established\r\n\r\n")
            with tls.wrap_socket(self.request, server_side=True) as tunnel:
                Http(tunnel, self.client_address, self.server)

    return socketserver.ThreadingTCPServer(("127.0.0.1", 0), Connect)


def dialogue(cli, root, env, state, turns, code):
    master, slave = pty.openpty()
    with subprocess.Popen([cli, "seed", "--state-dir", state], cwd=root, env=env,
                          stdin=slave, stdout=slave, stderr=slave) as process:
        output = bytearray()
        deadline = time.monotonic() + 40

        def read():
            while not select.select([master], [], [], .2)[0]:
                if process.poll() is not None:
                    return False
                assert time.monotonic() < deadline, output.decode(errors="replace")
            try:
                chunk = os.read(master, 16384)
            except OSError as error:
                if error.errno == errno.EIO:
                    return False
                raise
            output.extend(chunk)
            return bool(chunk)

        try:
            pos = 0
            for prompt, answer in turns:
                marker = prompt.encode()
                while (index := output.find(marker, pos)) == -1:
                    assert read(), output.decode(errors="replace")
                pos = index + len(marker)
                if answer in TOKENS or answer == "\x1b":
                    while termios.tcgetattr(slave)[3] & termios.ECHO:
                        assert time.monotonic() < deadline, "Credential prompt did not hide input."
                        time.sleep(.01)
                os.write(master, (answer if answer == "\x1b" else answer + "\n").encode())
            os.close(slave)
            slave = None
            while read():
                pass
            text = output.decode(errors="replace")
            assert process.wait(timeout=5) == code, text
            assert all(token not in text for token in TOKENS), "Fixture credential echoed to terminal."
            return text
        finally:
            os.close(master)
            if slave is not None:
                os.close(slave)
            if process.poll() is None:
                process.kill()


def check_hetzner_resume(cli, root, env, fixture):
    state = root / "hetzner"
    approval = "type 'create'"
    credential = ("credential (hidden): ", TOKEN)
    changed = ("credential (hidden): ", CHANGED_TOKEN)
    dialogue(cli, root, env, state, [
        ("Server source:", "local"), ("Provider (", "hetzner"), ("Region ID", "fixture"),
        ("Server size ID", "fixture-size"), ("Dashboard hostname", DOMAIN), ("Separate application DNS suffix", APP_DOMAIN),
        ("Server architecture", "linux/amd64"), ("Hetzner project identifier", "fixture-project"), credential, (approval, ""),
    ], 10)
    session_file = state / "session.json"
    session = json.loads(session_file.read_text())
    scope = session["hetznerCredentialScope"]
    seed_id = session["config"]["seedId"]
    assert session["keyPath"] == str(state / f"operator-{seed_id}_ed25519")
    assert scope == hashlib.sha256(f"selfrun-seed-scope:{seed_id}:{TOKEN}".encode()).hexdigest()
    report_file = state / "reports" / "seed-report.json"
    assert scope not in report_file.read_text()
    mismatch = "Hetzner credential differs from the original guided seed"
    before_pause = fixture.requests[:]
    assert before_pause and all(method == "GET" for method, _ in before_pause)
    assert mismatch in dialogue(cli, root, env, state, [changed], 12)
    assert fixture.requests == before_pause and not (state / "create-dispatched.json").exists()
    dialogue(cli, root, env, state, [credential, (approval, "create")], 12)
    assert [path for method, path in fixture.requests if method == "POST"] == ["/v1/ssh_keys", "/v1/servers"]
    before_recovery = fixture.requests[:]
    assert (state / "create-dispatched.json").exists()
    assert mismatch in dialogue(cli, root, env, state, [changed], 2)
    assert fixture.requests == before_recovery
    result = dialogue(cli, root, env, state, [credential], 10)
    assert "inventory absence never authorizes creation" in result
    assert fixture.requests[len(before_recovery):] == [("GET", "/v1/servers"), ("GET", "/v1/servers")]
    assert all(token not in file.read_text() for token in TOKENS for file in state.glob("**/*.json"))


def cloud_init_host(root, port):
    """Run the actual cloud-init handlers without accessing the host's SSH configuration."""
    from types import SimpleNamespace
    from cloudinit.config import cc_scripts_user, cc_ssh
    from cloudinit.handlers.shell_script import ShellScriptPartHandler

    subprocess.run(["mount", "--make-rprivate", "/"], check=True)
    ssh = root / "isolated-ssh"
    for source, target in ((ssh, "/etc/ssh"), (root / "isolated-run", "/run"), (root / "isolated-root", "/root")):
        subprocess.run(["mount", "--bind", source, target], check=True)
    instance = root / "cloud-instance"
    cloud = SimpleNamespace(distro=SimpleNamespace(osfamily="debian", get_default_user=lambda: {}),
                            get_ipath_cur=lambda: str(instance))
    cc_ssh.handle("ssh", {"ssh_genkeytypes": ["ed25519"], "ssh_quiet_keygen": True,
                         "allow_public_ssh_keys": False, "ssh_publish_hostkeys": {"enabled": False}, "users": []}, cloud, [])
    handler = ShellScriptPartHandler(SimpleNamespace(get_ipath_cur=lambda name: str(instance / name)))
    handler.handle_part(None, "text/x-shellscript", "seed", (root / "user-data").read_text(), None)
    assert (instance / "scripts" / "seed").stat().st_mode & 0o777 == 0o700
    cc_scripts_user.handle("scripts-user", {}, cloud, [])
    config = ["-f", "/etc/ssh/sshd_config", "-p", port, "-o", "ListenAddress=127.0.0.1"]
    # A single-ID user namespace cannot switch to sshd's privilege-separation account.
    # Run the test daemon non-root; this fixture never offers an SSH credential.
    os.execvp("unshare", ["unshare", "--map-user=1000", "--map-group=1000", "/usr/sbin/sshd", "-D", "-e", *config])


def boot_fixture(root, user_data):
    ssh = root / "isolated-ssh"
    (ssh / "sshd_config.d").mkdir(parents=True)
    (root / "isolated-run").mkdir()
    (root / "isolated-root").mkdir()
    (root / "user-data").write_text(user_data)
    (ssh / "sshd_config").write_text("Include /etc/ssh/sshd_config.d/*.conf\n"
                                    "HostKey /etc/ssh/ssh_host_ed25519_key\nUsePAM no\n")
    vendor = ssh / "sshd_config.d" / "50-provider.conf"
    vendor.write_text("PasswordAuthentication no\nPermitRootLogin prohibit-password\n")
    systemctl = root / "bin" / "systemctl"
    systemctl.write_text('#!/bin/bash\n[[ "$*" == "reload-or-restart ssh.service" ]] && /usr/sbin/sshd -t\n')
    systemctl.chmod(0o700)
    with socket.socket() as sock:
        sock.bind((ADDRESS, 0))
        port = sock.getsockname()[1]
    env = dict(os.environ, PATH=str(root / "bin") + ":" + os.environ["PATH"])
    process = subprocess.Popen(
        ["unshare", "-Ur", "-m", "/usr/bin/python3", "-B", __file__, "--cloud-init-host", root, str(port)],
        env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        deadline = time.monotonic() + 10
        while True:
            if process.poll() is not None:
                _, error = process.communicate(timeout=5)
                detail = re.sub(r"(?i)[0-9a-f]{64}", "[redacted]", error.decode(errors="replace"))
                raise AssertionError(f"Isolated cloud-init/sshd failed to start:\n{detail}")
            try:
                with socket.create_connection((ADDRESS, port), timeout=.2):
                    break
            except OSError:
                assert time.monotonic() < deadline, "Isolated SSH listener did not become ready."
                time.sleep(.05)
        assert vendor.read_text() == "PasswordAuthentication no\nPermitRootLogin prohibit-password\n"
        pin = subprocess.check_output(["ssh-keygen", "-lf", ssh / "ssh_host_ed25519_key.pub"], text=True).split()[1]
        assert (ssh / "ssh_host_ed25519_key").stat().st_mode & 0o777 == 0o600
        (root / "ssh-port").write_text(f"{port}\n")
        return process, pin
    except BaseException:
        if process.poll() is None:
            process.terminate()
        process.communicate(timeout=5)
        raise


def run(cli, root, lose_create_response, real_ssh):
    path = root / "bin"
    path.mkdir()
    for tool in ("stat", "id", "sync", "ssh-keygen", "cat", "sha256sum", "touch"):
        (path / tool).symlink_to(shutil.which(tool))
    for key in ("host-key", "wrong-host-key"):
        subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", root / key], check=True)
    public = (root / "host-key.pub").read_text().split()[1]
    pin = subprocess.check_output(["ssh-keygen", "-lf", root / "host-key.pub"], text=True).split()[1]
    (root / "scan").write_text(f"{ADDRESS} ssh-ed25519 {public}\n")
    (path / "ssh-keyscan").write_text("""#!/bin/bash
set -eu
[[ "$1" != -h ]] || exit 1
[[ "${@: -1}" == 127.0.0.1 ]]
if [[ -f "$HOME/ssh-port" ]]; then
    read -r port < "$HOME/ssh-port"
    /usr/bin/ssh-keyscan -T 2 -t ed25519 -p "$port" 127.0.0.1 | while read -r address type blob; do
        [[ "$address" != '#' ]] || continue
        [[ "$address" == "[127.0.0.1]:$port" ]]
        printf '127.0.0.1 %s %s\\n' "$type" "$blob"
    done
else
    cat "$HOME/scan"
fi
""")
    (path / "ssh").write_text("""#!/bin/bash
set -eu
[[ "$1" != -V ]] || exit 0
[[ " $* " == *' StrictHostKeyChecking=yes '* && " $* " == *' ForwardAgent=no '* ]]
if [[ " $* " == *' PreferredAuthentications=none '* ]]; then
    [[ " $* " == *' -N '* && " $* " != *' -i '* && -z "${SSH_AUTH_SOCK:-}" ]]
    for option in IdentityAgent=none IdentityFile=none PubkeyAuthentication=no PasswordAuthentication=no \
        KbdInteractiveAuthentication=no HostbasedAuthentication=no GSSAPIAuthentication=no ClearAllForwardings=yes; do
        [[ " $* " == *" $option "* ]]
    done
    if [[ -f "$HOME/ssh-port" ]]; then
        read -r port < "$HOME/ssh-port"
        exec /usr/bin/ssh -p "$port" -o HostKeyAlias=127.0.0.1 "$@"
    fi
    if [[ -f "$HOME/wrong-proofs" ]]; then
        { read -r wrong_seed; read -r wrong_key; } < "$HOME/wrong-proofs"
        if [[ -f "$HOME/wrong-key-next" ]]; then line=$wrong_key; else line=$wrong_seed; fi
        touch "$HOME/wrong-key-next"
        printf '%s\\n' "$line" >&2
    elif [[ -f "$HOME/proof" ]]; then
        cat "$HOME/proof" >&2
    fi
    exit 255
fi
[[ -f "$HOME/local/target.json" ]]
[[ ! -e "$HOME/local/ssh-enrollment.secret" ]]
printf '%s\\n' ssh >> "$HOME/ssh-calls"
case "${@: -1}" in
    *'bash -s -- '*) cat >/dev/null; touch "$HOME/prepared";;
    *'local-source.py install '*) printf '%s\\n' install >> "$HOME/mutations"
        touch "$HOME/installed"; printf '%s\\n' '{"state":"awaiting_dns_tls"}';;
    *'sys.stdin.buffer.read()'*'source.tar.gz'*) sha256sum > "$HOME/uploaded-source";;
    *'sys.stdin.buffer.read()'*) cat >/dev/null;;
    *'p.mkdir(mode=0o700)'*) ;;
    *'binding=pathlib.Path('*)
        if [[ -f "$HOME/installed" ]]; then cat "$HOME/metadata.json"; else echo null; fi;;
    *'base64.b64encode(p.read_bytes())'*)
        printf '%s\\n' cHJpdmF0ZS1maXh0dXJl;;
    *)
        if [[ -f "$HOME/https-ready" ]]; then cat "$HOME/ready.json"
        elif [[ -f "$HOME/installed" ]]; then cat "$HOME/installed.json"
        elif [[ -f "$HOME/prepared" ]]; then echo '{"state":"preparing","progress":"prepared","prepared":true}'
        else echo '{"state":"empty","progress":"unknown"}'; fi;;
esac
""")
    for script in ("ssh", "ssh-keyscan"):
        (path / script).chmod(0o700)

    fixture = Fixture()
    hetzner = HetznerFixture()
    with proxy(root, {PROVIDER: fixture, HETZNER: hetzner}) as server:
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            env = {"HOME": str(root), "PATH": str(path), "TERM": "dumb", "LC_ALL": "C",
                   "SSL_CERT_FILE": str(root / "ca.pem"), "DOTNET_BUNDLE_EXTRACT_BASE_DIR": str(root / "native"),
                   "SSH_AUTH_SOCK": str(root / "never-use-agent"),
                   "HTTPS_PROXY": f"http://127.0.0.1:{server.server_address[1]}"}
            info = subprocess.check_output([cli, "--version"], cwd=root, env=env, text=True)
            revision = info.strip().splitlines()[-1].removeprefix("source ")
            assert len(revision) == 40, info
            help_text = subprocess.check_output([cli, "help", "seed"], cwd=root, env=env, text=True)
            assert f"Embedded TEST source: server-v0.0.1-local.{revision}" in help_text.splitlines()

            choices = [("Provider (", "digitalocean"), ("Region ID", "fixture"), ("Server size ID", "fixture-size"),
                       ("Dashboard hostname", DOMAIN), ("Separate application DNS suffix", APP_DOMAIN),
                       ("Server architecture", "linux/amd64"), ("credential (hidden): ", TOKEN)]

            signed = root / "signed"
            output = dialogue(cli, root, env, signed, [("Server source:", "signed"),
                ("Exact published compatible server tag", "server-v0.0.0-unavailable"), *choices], 12)
            assert "Exact release asset is absent" in output and not (signed / "source.tar.gz").exists()
            assert fixture.posts == []

            state = root / "local"
            state.mkdir()
            abandoned = [state / (name + ".pending") for name in
                         ("session.json", "create-dispatched.json", "resource.json", "target.json")]
            abandoned.append(state / "create-dispatched.json.pending-00000000000000000000000000000000")
            for path in abandoned:
                path.write_text('{"interrupted":')
            report = state / "reports" / "seed-report.json"
            approval = "type 'create'"
            credential = ("credential (hidden): ", TOKEN)
            local_choices = [("Server source:", "local"), *choices[:-1]]
            archive = state / "source.tar.gz"
            session_file = state / "session.json"
            requests = list(fixture.requests)
            output = dialogue(cli, root, env, state, [*local_choices, (credential[0], "\x1b")], 2)
            assert "Credential entry canceled" in output and not session_file.exists()
            assert fixture.requests == requests
            original = archive.read_bytes()
            tampered = original + b"unexpected bytes"
            archive.write_bytes(tampered)
            output = dialogue(cli, root, env, state, local_choices, 2)
            assert "does not match this executable's embedded TEST source" in output
            assert credential[0] not in output and not session_file.exists() and fixture.requests == requests
            assert archive.read_bytes() == tampered
            archive.write_bytes(original)
            archive_inode = archive.stat().st_ino
            output = dialogue(cli, root, env, state, [*local_choices, credential, (approval, "")], 10)
            sha = hashlib.sha256(original).hexdigest()
            assert archive.stat().st_ino == archive_inode and archive.read_bytes() == original
            with tarfile.open(archive) as source:
                descriptor = json.load(source.extractfile("selfrun-local-source.json"))
                assert descriptor["sourceRevision"] == revision and descriptor["cliCompatibility"]["sourceRevisions"] == [revision]
                assert source.getmember("modules/api/Dockerfile").isfile()
            assert sha in output and revision in output and fixture.posts == []
            assert json.loads(report.read_text())["plan"]["releaseDigest"] == sha
            assert not (state / "ssh-enrollment.secret").exists()

            session_bytes = session_file.read_bytes()
            session = json.loads(session_bytes)
            identity_path = state / f"operator-{session['config']['seedId']}_ed25519"
            assert session["keyPath"] == str(identity_path) and not session["useAgent"]
            original_identity = identity_path.read_bytes()
            assert identity_path.stat().st_mode & 0o777 == 0o600
            session["sourceSha256"] = hashlib.sha256(tampered).hexdigest()
            session_file.write_text(json.dumps(session))
            archive.write_bytes(tampered)
            requests = list(fixture.requests)
            output = dialogue(cli, root, env, state, [], 2)
            assert "does not match this executable's embedded TEST source" in output
            assert credential[0] not in output and fixture.requests == requests
            archive.write_bytes(original)
            session_file.write_bytes(session_bytes)

            if lose_create_response:
                fixture.lose_response = True
                dialogue(cli, root, env, state, [credential, (approval, "create")], 12)
                output = dialogue(cli, root, env, state, [credential], 10)
                assert "inventory absence never authorizes creation" in output
                fixture.visible = True
                public_ipv4 = fixture.droplet["networks"]["v4"]
                fixture.droplet["networks"]["v4"] = []
                output = dialogue(cli, root, env, state, [credential], 10)
                assert "public IPv4 is not ready" in output
                assert json.loads((state / "resource.json").read_text())["id"] == "17"
                fixture.droplet["networks"]["v4"] = public_ipv4
                fixture.droplet["id"] = 18
                output = dialogue(cli, root, env, state, [credential], 2)
                assert "Provider resource does not match" in output and not (root / "ssh-calls").exists()
                fixture.droplet["id"] = 17
                # Even under the original secret, a different seed or host key cannot enroll.
                secret = (state / "ssh-enrollment.secret").read_bytes()
                wrong_public = (root / "wrong-host-key.pub").read_text().split()[1]
                proofs = [hmac.new(secret, f"selfrun-seed-host-v1\n{seed}\nssh-ed25519 {key}".encode(),
                                   hashlib.sha256).hexdigest()
                          for seed, key in (("0" * 32, public), (session["config"]["seedId"], wrong_public))]
                (root / "wrong-proofs").write_text("".join(f"selfrun-seed-host-v1 {mac}\n" for mac in proofs))
                output = dialogue(cli, root, env, state, [credential], 10)
                (root / "wrong-proofs").unlink()
            else:
                output = dialogue(cli, root, env, state, [credential, (approval, "create")], 10)
            assert "No valid SSH enrollment proof received" in output and not (root / "ssh-calls").exists()
            assert "ssh-keygen" not in output and "fingerprint" not in output
            assert json.loads((state / "resource.json").read_text())["address"] == ADDRESS
            assert fixture.posts == ["/v2/account/keys", "/v2/droplets"]
            assert not (state / "target.json").exists()
            assert identity_path.read_bytes() == original_identity
            authorized = subprocess.check_output(["ssh-keygen", "-y", "-f", identity_path], text=True).split()[:2]
            assert fixture.ssh_keys[0]["public_key"].split() == authorized

            saved = json.loads((state / "session.json").read_text())["config"]
            secret_path = state / "ssh-enrollment.secret"
            secret = secret_path.read_bytes()
            assert len(secret) == 32 and secret_path.stat().st_mode & 0o777 == 0o600
            assert re.search(r'bytes.fromhex\("([0-9A-F]{64})"\)', fixture.user_data)[1] == secret.hex().upper()
            assert "PRIVATE KEY" not in fixture.user_data
            if not real_ssh:
                proof = hmac.new(secret, f"selfrun-seed-host-v1\n{saved['seedId']}\nssh-ed25519 {public}".encode(),
                                 hashlib.sha256).hexdigest()
                (root / "proof").write_text(f"selfrun-seed-host-v1 {proof}\n")
            config = {"consoleUrl": f"https://{DOMAIN}", "apiUrl": f"https://api.{DOMAIN}", "appDomain": APP_DOMAIN}
            identity = {"installationId": "4cacbcc7-c4d3-4f94-8848-fa700870238d", "operationId": "02f942fe-36f4-488f-9f35-4d508603d8de"}
            metadata = dict(identity, schemaVersion=1, product="selfrun", root="/opt/selfrun",
                            releaseTag=saved["serverVersion"], sourceRevision=revision,
                            recoveryDir=f"/var/lib/selfrun-seed/{saved['seedId']}/recovery", publicAddresses=[ADDRESS],
                            configuration=config, setupTokenFile="test-setup-token", phase="awaiting_dns_tls")
            observation = dict(identity, state="installed", progress="awaiting_dns_tls", release=saved["serverVersion"],
                               source=revision, health="pending", prepared=True, artifactTrust="operator-pinned-local-source",
                               sourceArchiveSha256=sha, configuration=config, publicAddresses=[ADDRESS])
            (root / "metadata.json").write_text(json.dumps(metadata))
            (root / "installed.json").write_text(json.dumps(observation))
            (root / "ready.json").write_text(json.dumps(dict(observation, progress="awaiting_operator", health="ready",
                                                             openBao="ready", browser="pending")))
            if real_ssh:
                sshd, pin = boot_fixture(root, fixture.user_data)
            try:
                output = dialogue(cli, root, env, state, [("Type 'install'", "install")], 10)
            except BaseException as error:
                if real_ssh:
                    sshd.terminate()
                    _, diagnostics = sshd.communicate(timeout=5)
                    error.add_note(re.sub(r"(?i)[0-9a-f]{64}", "[redacted]", diagnostics.decode(errors="replace")))
                raise
            finally:
                if real_ssh and sshd.poll() is None:
                    sshd.terminate()
                    sshd.communicate(timeout=5)
                if real_ssh:
                    (root / "ssh-port").unlink()
            assert not secret_path.exists() and "ssh-keygen" not in output and "Paste only" not in output
            assert json.loads((state / "target.json").read_text())["hostFingerprint"] == pin
            assert secret.hex().upper() not in output
            assert f"api.{DOMAIN}. A {ADDRESS}" in output and "Public HTTPS/readiness is not verified" in output
            assert (root / "uploaded-source").read_text().split()[0] == sha
            assert (root / "mutations").read_text().splitlines() == ["install"]
            assert len(fixture.posts) == 2  # No provider mutation on resume.
            if real_ssh:
                (root / "scan").write_text(f"{ADDRESS} ssh-ed25519 {(root / 'isolated-ssh' / 'ssh_host_ed25519_key.pub').read_text().split()[1]}\n")
            correct_scan = (root / "scan").read_text()
            wrong_public = (root / "wrong-host-key.pub").read_text().split()[1]
            (root / "scan").write_text(f"{ADDRESS} ssh-ed25519 {wrong_public}\n")
            calls = (root / "ssh-calls").read_bytes()
            output = dialogue(cli, root, env, state, [], 12)
            assert "differs from the independent console fingerprint" in output and (root / "ssh-calls").read_bytes() == calls
            (root / "scan").write_text(correct_scan)
            (root / "https-ready").touch()
            output = dialogue(cli, root, env, state, [("Export private recovery", "y")], 10)
            assert f"https://{DOMAIN}/setup" in output and "Protected setup token file:" in output
            assert "selfrun login" in output
            assert (state / "private-recovery" / "test-setup-token").stat().st_mode & 0o777 == 0o600
            (root / "ready.json").write_text(json.dumps(dict(observation, progress="ready", health="ready",
                                                             openBao="ready", browser="ready")))
            dialogue(cli, root, env, state, [("Export private recovery", "")], 0)
            assert (root / "mutations").read_text().splitlines() == ["install"]
            assert fixture.posts == ["/v2/account/keys", "/v2/droplets"]
            assert all(path.read_text() == '{"interrupted":' for path in abandoned)
            assert all(TOKEN.encode() not in file.read_bytes() for file in state.glob("**/*.json"))
            assert all(secret.hex().upper() not in file.read_text() for file in state.glob("**/*.json"))
            legacy = root / "legacy"
            shutil.copytree(state, legacy)
            legacy_dispatch = legacy / "create-dispatched.json"
            old = json.loads(legacy_dispatch.read_text())
            del old["enrollSsh"]
            legacy_dispatch.write_text(json.dumps(old))
            dialogue(cli, root, env, legacy, [("Export private recovery", "")], 0)
            (legacy / "target.json").unlink()
            output = dialogue(cli, root, env, legacy, [("Paste only its independent", "")], 10)
            assert "This older seed was created without automatic enrollment" in output
            check_hetzner_resume(cli, root, env, hetzner)
        finally:
            server.shutdown()
            worker.join()


if __name__ == "__main__":
    if sys.argv[1] == "--cloud-init-host":
        cloud_init_host(Path(sys.argv[2]), sys.argv[3])
    executable = Path(sys.argv[1]).resolve(strict=True)
    artifacts = Path(sys.argv[2]).resolve(strict=True)
    os.umask(0o077)
    with tempfile.TemporaryDirectory(prefix="guided-fixture-", dir=artifacts) as directory:
        lose_response = "--lost-create" in sys.argv[3:]
        run(executable, Path(directory), lose_response, "--real-ssh" in sys.argv[3:])
    print("Guided seed passed: missing signed release, prepare/plan, "
          f"{'lost create' if lose_response else 'create'}, pinned resume, install, DNS and protected setup; "
          "embedded source verified after cancellation and on resume, "
          "managed identity retained, and changed Hetzner token blocked.")
