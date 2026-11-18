#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
git check-ignore -q .local/artifacts/issue110/runtime/check
mkdir -p .local/artifacts/issue110/runtime
PYTHONDONTWRITEBYTECODE=1 python3 -B -m unittest discover \
  -s templates/development-workspaces/tests -v
