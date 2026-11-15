#!/usr/bin/env python3
"""Sensitive stdout: pipe directly to the control-plane secret store, never a log."""
import argparse
import fcntl
import os
from pathlib import Path
import subprocess
import sys

from runtime import Runtime, load_config, require, run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="/etc/minicloud-workspace/runtime.json")
    parser.add_argument("--vm-id", required=True)
    parser.add_argument("--generation", required=True, type=int)
    parser.add_argument("--operation-id", required=True)
    parser.add_argument("--subject", required=True)
    parser.add_argument("--ttl", type=int, default=300)
    parser.add_argument("--scope", action="append", required=True)
    parser.add_argument("--state-dir", default="/var/lib/minicloud-workspace")
    args = parser.parse_args()
    require(os.geteuid() == 0 and not sys.stdout.isatty(), "Root, non-terminal secret capture required")
    c = load_config(args.config)
    require(c["vmId"] == args.vm_id and c["generation"] == args.generation, "Stale generation")
    require(1 <= args.ttl <= 3600, "Bounded session TTL required")
    help_text = run(["t3", "auth", "session", "issue", "--help"])
    require(all(x in help_text for x in ("--token-only", "--ttl", "--subject", "--scope", "--base-dir")),
            "Actual runtime does not support verified headless auth; gateway remains blocked")
    # No bind/dpop option: the gateway needs an unbound server-to-server bearer.
    argv = ["runuser", "-u", "coding", "--", "env", "-i", "HOME=/workspace/home",
            "CODEX_HOME=/workspace/home/.codex", "PATH=/usr/local/bin:/usr/bin:/bin",
            "t3", "auth", "session", "issue", "--token-only", "--ttl", str(args.ttl) + "s",
            "--label", "minicloud-" + args.vm_id + "-g" + str(args.generation),
            "--subject", args.subject, "--base-dir", "/workspace/t3"]
    for scope in args.scope:
        require(scope and not scope.startswith("-"), "Explicit authorization scope required")
        argv.extend(("--scope", scope))
    with (Path(args.state_dir) / "control.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        runtime = Runtime(c, args.state_dir)
        runtime.validate_request(dict(vmId=args.vm_id, generation=args.generation,
                                      operationId=args.operation_id))
        require(runtime.s["phase"] == "Ready", "Runtime not admitting sessions")
        result = subprocess.run(argv, capture_output=True, timeout=30, text=True)
        require(result.returncode == 0 and result.stdout.strip() and "\n" not in result.stdout.strip(),
                "Headless auth failed; no token logged, gateway remains blocked")
        # stdout is intentionally only the secret, with no lifecycle receipt or log text.
        sys.stdout.write(result.stdout.strip() + "\n")


if __name__ == "__main__":
    main()
