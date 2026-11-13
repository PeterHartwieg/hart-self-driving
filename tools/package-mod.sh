#!/usr/bin/env bash
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
if [ "${1:-}" != "--no-build" ]; then
  "$REPO/tools/build-mod.sh"
fi
[ -f "$REPO/build/DrivenRides/DrivenRides.dll" ] || { echo "No build in build/DrivenRides; run tools/build-mod.sh" >&2; exit 1; }
python3 "$REPO/tools/package-mod.py" "$REPO"
