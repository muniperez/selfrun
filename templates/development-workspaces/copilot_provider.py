#!/usr/bin/env python3
"""Seed the pinned T3 provider instances before T3 starts; never handle credentials."""
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
# T3 drivers that are disabled by default; seeded only when the image installed their pinned executable.
# Claude Code needs no instance: T3 enables its default claudeAgent instance and finds `claude` on PATH.
AGENTS = (
    dict(instanceId="minicloud-antigravity", driver="antigravity", displayName="Antigravity",
         executable="/opt/minicloud-antigravity/agy_acp_server.par", environment=[]),
    dict(instanceId="minicloud-muse", driver="muse", displayName="Muse Code",
         executable="/usr/local/bin/muse",
         environment=[dict(name="MUSE_NO_AUTO_UPDATE", value="1", sensitive=False)]),
)
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


def installed_agents():
    return [agent for agent in AGENTS if Path(agent["executable"]).is_file()]


def configure(settings_path, agents=None):
    """Called only by ExecStartPre, under the same UID/HOME as the T3 service."""
    agents = installed_agents() if agents is None else agents
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
    existing = next((instance_id for instance_id, instance in instances.items() if is_copilot(instance)), None)
    if existing is not None:
        result = dict(state="AlreadyConfigured", instanceId=existing)
    else:
        require(INSTANCE_ID not in instances,
                "Provider ID minicloud-copilot is occupied; rename it in Settings → Providers")
        instances[INSTANCE_ID] = dict(
            driver="acpRegistry", displayName="GitHub Copilot CLI", enabled=True,
            environment=[dict(name="COPILOT_AUTO_UPDATE", value="false", sensitive=False)],
            config=dict(source="local", commandPath=EXECUTABLE, commandArgs=["--acp", "--stdio"]))
        result = dict(state="Configured", instanceId=INSTANCE_ID)
    changed = existing is None
    if agents:
        result["agents"] = {}
    for agent in agents:
        # Any instance of the driver, even a disabled one, is the user's choice; never add a second.
        if any(isinstance(instance, dict) and instance.get("driver") == agent["driver"]
               for instance in instances.values()):
            state = "AlreadyConfigured"
        elif agent["instanceId"] in instances:
            state = "InstanceIdOccupied"
        else:
            instances[agent["instanceId"]] = dict(
                driver=agent["driver"], displayName=agent["displayName"], enabled=True,
                environment=list(agent["environment"]), config=dict(binaryPath=agent["executable"]))
            state, changed = "Configured", True
        result["agents"][agent["driver"]] = state
    if changed:
        settings["providerInstances"] = instances
        settings_path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        atomic_json(settings_path, settings)
    return result


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
