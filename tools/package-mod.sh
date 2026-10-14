#!/usr/bin/env bash
# Builds the Workshop-ready folder and checks it.
# Usage: tools/package-mod.sh [--no-build]
#   --no-build  package the existing build/DrivenRides instead of running tools/build-mod.sh first
# Output: build/package/<MOD_FOLDER>/ (copy it into ModsLocal, then upload it from the game's Mods menu),
#         build/package/<MOD_FOLDER>-<version>.zip, and build/package/<MOD_FOLDER>-<version>.manifest.json with
#         the name, version, target game build and each file's SHA-256.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
if [ "${1:-}" != "--no-build" ]; then
  "$REPO/tools/build-mod.sh"
fi
[ -f "$REPO/build/DrivenRides/DrivenRides.dll" ] || { echo "No build in build/DrivenRides; run tools/build-mod.sh" >&2; exit 1; }
python3 "$REPO/tools/package-mod.py" "$REPO"
