#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
output=${1:?Usage: site/build.sh NEW_OUTPUT_DIRECTORY}

mkdir -p "$(dirname "$output")"
mkdir "$output"
install -m 644 "$root/site/public/"{index.html,downloads.html,styles.css,mark.svg} "$output/"
mkdir "$output/media"
assets=(
    selfrun-architecture.svg
    selfrun-seed.svg
)
for asset in "${assets[@]}"; do
    install -m 644 "$root/docs/assets/$asset" "$output/media/"
done
