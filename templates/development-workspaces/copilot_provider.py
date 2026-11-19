#!/usr/bin/env python3
"""Seed the pinned T3 local ACP provider before T3 starts; never handle credentials."""
import argparse
import json
import os
from pathlib import Path
import selectors
import subprocess
import sys
import time

from runtime import Blocked, atomic_json, require

VERSION = "1.0.114"
EXECUTABLE = "/usr/local/bin/copilot"
INSTANCE_ID = "minicloud-copilot"
SIGN_IN = ("Run copilot login --device-code as coding (HOME=/workspace/home) in an "
           "unrecorded management terminal, then retry the provider in Settings → Providers.")


def is_copilot(instance):
    if not isinstance(instance, dict) or instance.get("driver") != "acpRegistry":
        return False
    config = instance.get("config")
    if not isinstance(config, dict):
        return False
    arguments = config.get("commandArgs", [])
    command = config.get("commandPath")
    return (config.get("source") == "local" and
            isinstance(command, str) and Path(command).name == "copilot" and
            isinstance(arguments, list) and "--acp" in arguments and
            not any(isinstance(argument, str) and argument.startswith("--port")
                    for argument in arguments))


def configure(settings_path):
    """Called only by ExecStartPre, under the same UID/HOME as the T3 service."""
    settings_path = Path(settings_path)
    require(not settings_path.is_symlink(), "Copilot setup refuses symlinked T3 settings")
    try:
        settings = json.loads(settings_path.read_text()) if settings_path.exists() else {}
    except (OSError, ValueError):
        raise Blocked("Cannot read T3 settings; repair userdata/settings.json before restarting T3") from None
    require(isinstance(settings, dict), "T3 settings must be an object")
    instances = settings.get("providerInstances", {})
    require(isinstance(instances, dict), "T3 providerInstances must be an object")
    # Keep user overrides, disabled providers, Codex settings and thread IDs intact.
    for instance_id, instance in instances.items():
        if is_copilot(instance):
            return dict(state="AlreadyConfigured", instanceId=instance_id)
    require(INSTANCE_ID not in instances,
            "Provider ID minicloud-copilot is occupied; rename it in Settings → Providers")
    instances[INSTANCE_ID] = dict(
        driver="acpRegistry", displayName="GitHub Copilot CLI", enabled=True,
        environment=[dict(name="COPILOT_AUTO_UPDATE", value="false", sensitive=False)],
        config=dict(source="local", commandPath=EXECUTABLE, commandArgs=["--acp", "--stdio"]))
    settings["providerInstances"] = instances
    settings_path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    atomic_json(settings_path, settings)
    return dict(state="Configured", instanceId=INSTANCE_ID)


def probe(executable=EXECUTABLE, expected_version=VERSION, timeout=30, environment=None):
    """Verify real stdio negotiation without opening sessions or exporting login material."""
    process = None
    try:
        process = subprocess.Popen(
            [executable, "--acp", "--stdio"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            env=dict(os.environ if environment is None else environment, COPILOT_AUTO_UPDATE="false"),
            start_new_session=True)
        request = dict(jsonrpc="2.0", id=1, method="initialize",
                       params=dict(protocolVersion=1, clientCapabilities={}))
        process.stdin.write((json.dumps(request) + "\n").encode())
        process.stdin.flush()
        deadline = time.monotonic() + timeout
        data = b""
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while time.monotonic() < deadline:
                if not selector.select(max(0, deadline - time.monotonic())):
                    break
                chunk = os.read(process.stdout.fileno(), 65536)
                require(chunk and len(data) + len(chunk) <= 1048576,
                        "Copilot ACP returned invalid output; rebuild the qualified image")
                data += chunk
                while b"\n" in data:
                    line, data = data.split(b"\n", 1)
                    message = json.loads(line)
                    if message.get("id") != 1:
                        continue
                    result = message.get("result") or {}
                    agent = result.get("agentInfo") or {}
                    require(result.get("protocolVersion") == 1 and
                            agent.get("name") == "Copilot" and agent.get("version") == expected_version,
                            "Incompatible Copilot ACP; rebuild with the pinned CLI " + expected_version)
                    return dict(state="Compatible", version=expected_version,
                                authentication="NotChecked", signIn=SIGN_IN)
        raise Blocked("Copilot ACP timed out; rebuild the qualified image and retry")
    except (OSError, ValueError, TypeError, AttributeError):
        raise Blocked("Copilot ACP unavailable or incompatible; rebuild the qualified image") from None
    finally:
        if process is not None:
            try:
                process.stdin.close()
            except OSError:
                pass
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("configure", "probe"))
    parser.add_argument("--base-dir", default="/workspace/t3")
    args = parser.parse_args()
    os.umask(0o077)
    try:
        require(os.geteuid() == 2000, "Run Copilot provider setup as the coding user (UID 2000)")
        result = configure(Path(args.base_dir) / "userdata/settings.json") \
            if args.action == "configure" else probe()
        print(json.dumps(result, sort_keys=True))
    except Blocked as error:
        print(json.dumps(dict(state="SetupRequired", reason=str(error), signIn=SIGN_IN)), file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
