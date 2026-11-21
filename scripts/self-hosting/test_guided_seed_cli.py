#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Linux black-box seed rehearsal. Pass an already-published standalone executable."""

import errno
import hashlib
import http.server
import json
import os
from pathlib import Path
import pty
import select
import shutil
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
ADDRESS = "203.0.113.1"
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

    def api(self, path, body):
        if body is not None:
            self.posts.append(path)
            if path == "/v2/account/keys":
                key = {"id": 7, **body}
                self.ssh_keys.append(key)
                return {"ssh_key": key}
            assert path == "/v2/droplets" and self.droplet is None
            assert body["ssh_keys"] == [7] and body["vpc_uuid"] == "existing-network"
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
                assert self.path.startswith("/muniventures/minicloud/releases/download/server-v0.0.0-unavailable/")
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
                if answer in TOKENS:
                    while termios.tcgetattr(slave)[3] & termios.ECHO:
                        assert time.monotonic() < deadline, "Credential prompt did not hide input."
                        time.sleep(.01)
                os.write(master, (answer + "\n").encode())
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
        ("Server architecture", "linux/amd64"), ("owner-only OpenSSH private key", "~/user-key"),
        ("SSH agent", ""), ("Hetzner project identifier", "fixture-project"), credential, (approval, ""),
    ], 10)
    session_file = state / "session.json"
    session = json.loads(session_file.read_text())
    assert session["keyPath"] == str(root / "user-key")
    scope = session["hetznerCredentialScope"]
    seed_id = session["config"]["seedId"]
    assert scope == hashlib.sha256(f"minicloud-seed-scope:{seed_id}:{TOKEN}".encode()).hexdigest()
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


def run(cli, root, lose_create_response):
    path = root / "bin"
    path.mkdir()
    for tool in ("stat", "id", "sync", "ssh-keygen", "cat", "sha256sum", "touch"):
        (path / tool).symlink_to(shutil.which(tool))
    for key in ("user-key", "host-key"):
        subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", root / key], check=True)
    public = (root / "host-key.pub").read_text().split()[1]
    pin = subprocess.check_output(["ssh-keygen", "-lf", root / "host-key.pub"], text=True).split()[1]
    (path / "ssh-keyscan").write_text(f"#!/bin/bash\nprintf '%s\\n' '{ADDRESS} ssh-ed25519 {public}'\n")
    (path / "ssh").write_text("""#!/bin/bash
set -eu
[[ "$1" != -V ]] || exit 0
[[ " $* " == *' StrictHostKeyChecking=yes '* && " $* " == *' ForwardAgent=no '* ]]
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
                   "HTTPS_PROXY": f"http://127.0.0.1:{server.server_address[1]}"}
            info = subprocess.check_output([cli, "--version"], cwd=root, env=env, text=True)
            revision = info.strip().splitlines()[-1].removeprefix("source ")
            assert len(revision) == 40, info
            help_text = subprocess.check_output([cli, "help", "seed"], cwd=root, env=env, text=True)
            assert f"Embedded TEST source: server-v0.0.1-local.{revision}" in help_text.splitlines()

            choices = [("Provider (", "digitalocean"), ("Region ID", "fixture"), ("Server size ID", "fixture-size"),
                       ("Dashboard hostname", DOMAIN), ("Separate application DNS suffix", APP_DOMAIN),
                       ("Server architecture", "linux/amd64"), ("owner-only OpenSSH private key", str(root / "user-key")),
                       ("SSH agent", ""), ("credential (hidden): ", TOKEN)]

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
            fingerprint = "Paste only its independent"
            credential = ("credential (hidden): ", TOKEN)
            output = dialogue(cli, root, env, state, [("Server source:", "local"), *choices, (approval, "")], 10)
            with (state / "source.tar.gz").open("rb") as source:
                sha = hashlib.file_digest(source, "sha256").hexdigest()
            with tarfile.open(state / "source.tar.gz") as source:
                descriptor = json.load(source.extractfile("minicloud-local-source.json"))
                assert descriptor["sourceRevision"] == revision and descriptor["cliCompatibility"]["sourceRevisions"] == [revision]
                assert source.getmember("modules/api/Dockerfile").isfile()
            assert sha in output and revision in output and fixture.posts == []
            assert json.loads(report.read_text())["plan"]["releaseDigest"] == sha

            handoff = []
            if lose_create_response:
                fixture.lose_response = True
                dialogue(cli, root, env, state, [credential, (approval, "create")], 12)
                output = dialogue(cli, root, env, state, [credential], 10)
                assert "inventory absence never authorizes creation" in output
                fixture.visible = True
                handoff = [credential]
            else:
                dialogue(cli, root, env, state, [credential, (approval, "create"), (fingerprint, "")], 10)
                assert json.loads((state / "resource.json").read_text())["address"] == ADDRESS
            assert fixture.posts == ["/v2/account/keys", "/v2/droplets"]
            output = dialogue(cli, root, env, state, [*handoff, (fingerprint, "SHA256:" + "A" * 43)], 12)
            assert "differs from the independent console fingerprint" in output and not (root / "ssh-calls").exists()
            assert not (state / "target.json").exists()

            saved = json.loads((state / "session.json").read_text())["config"]
            config = {"consoleUrl": f"https://{DOMAIN}", "apiUrl": f"https://api.{DOMAIN}", "appDomain": APP_DOMAIN}
            identity = {"installationId": "4cacbcc7-c4d3-4f94-8848-fa700870238d", "operationId": "02f942fe-36f4-488f-9f35-4d508603d8de"}
            metadata = dict(identity, schemaVersion=1, product="minicloud", root="/opt/minicloud",
                            releaseTag=saved["serverVersion"], sourceRevision=revision,
                            recoveryDir=f"/var/lib/minicloud-seed/{saved['seedId']}/recovery", publicAddresses=[ADDRESS],
                            configuration=config, setupTokenFile="test-setup-token", phase="awaiting_dns_tls")
            observation = dict(identity, state="installed", progress="awaiting_dns_tls", release=saved["serverVersion"],
                               source=revision, health="pending", prepared=True, artifactTrust="operator-pinned-local-source",
                               sourceArchiveSha256=sha, configuration=config, publicAddresses=[ADDRESS])
            (root / "metadata.json").write_text(json.dumps(metadata))
            (root / "installed.json").write_text(json.dumps(observation))
            (root / "ready.json").write_text(json.dumps(dict(observation, progress="awaiting_operator", health="ready",
                                                             openBao="ready", browser="pending")))
            output = dialogue(cli, root, env, state, [*handoff, (fingerprint, pin), ("Type 'install'", "install")], 10)
            assert f"api.{DOMAIN}. A {ADDRESS}" in output and "Public HTTPS/readiness is not verified" in output
            assert (root / "uploaded-source").read_text().split()[0] == sha
            assert (root / "mutations").read_text().splitlines() == ["install"]
            assert len(fixture.posts) == 2  # No provider mutation on resume.
            (root / "https-ready").touch()
            output = dialogue(cli, root, env, state, [("Export private recovery", "y")], 10)
            assert f"https://{DOMAIN}/setup" in output and "Protected setup token file:" in output
            assert "minicloud login" in output
            assert (state / "private-recovery" / "test-setup-token").stat().st_mode & 0o777 == 0o600
            (root / "ready.json").write_text(json.dumps(dict(observation, progress="ready", health="ready",
                                                             openBao="ready", browser="ready")))
            dialogue(cli, root, env, state, [("Export private recovery", "")], 0)
            assert (root / "mutations").read_text().splitlines() == ["install"]
            assert fixture.posts == ["/v2/account/keys", "/v2/droplets"]
            assert all(path.read_text() == '{"interrupted":' for path in abandoned)
            assert all(TOKEN.encode() not in file.read_bytes() for file in state.glob("**/*.json"))
            check_hetzner_resume(cli, root, env, hetzner)
        finally:
            server.shutdown()
            worker.join()


if __name__ == "__main__":
    executable = Path(sys.argv[1]).resolve(strict=True)
    artifacts = Path(sys.argv[2]).resolve(strict=True)
    os.umask(0o077)
    with tempfile.TemporaryDirectory(prefix="guided-fixture-", dir=artifacts) as directory:
        lose_response = "--lost-create" in sys.argv[3:]
        run(executable, Path(directory), lose_response)
    print("Guided seed passed: missing signed release, prepare/plan, "
          f"{'lost create' if lose_response else 'create'}, pinned resume, install, DNS and protected setup; "
          "abandoned staging ignored, Unix SSH path expanded, and changed Hetzner token blocked.")
