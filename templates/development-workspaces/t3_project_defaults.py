#!/usr/bin/env python3
"""Seed supported project defaults before T3 starts; this grants no permissions."""
import json
import os
from pathlib import Path
import sys

from runtime import Blocked, atomic_json, require


def configure(settings_path):
    settings_path = Path(settings_path)
    require(not settings_path.is_symlink(), "Project defaults refuse symlinked T3 settings")
    try:
        settings = json.loads(settings_path.read_text()) if settings_path.exists() else {}
    except (OSError, ValueError):
        raise Blocked("Cannot read T3 settings; repair userdata/settings.json before restarting T3") from None
    require(isinstance(settings, dict), "T3 settings must be an object")
    current = settings.get("addProjectBaseDirectory", "")
    require(isinstance(current, str), "T3 addProjectBaseDirectory must be a string")
    if current.strip():
        return dict(state="AlreadyConfigured")
    settings["addProjectBaseDirectory"] = "/workspace/repos"
    atomic_json(settings_path, settings)
    return dict(state="Configured")


def main():
    os.umask(0o077)
    try:
        require(os.geteuid() == 2000, "Run T3 project defaults as the coding user (UID 2000)")
        print(json.dumps(configure("/workspace/t3/userdata/settings.json"), sort_keys=True))
    except Blocked as error:
        print(json.dumps(dict(state="SetupRequired", reason=str(error))), file=sys.stderr)
        return 1
    except OSError:
        print(json.dumps(dict(state="SetupRequired", reason="Cannot write T3 project defaults")),
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
