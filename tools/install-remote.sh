#!/usr/bin/env bash
# Copies build/DrivenRides to the Windows game PC over ssh and installs it into ModsLocal.
# Usage: tools/install-remote.sh [ssh host]   (or set BA_GAME_HOST)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${1:-${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}}"
PKG="$REPO/build/DrivenRides"
[ -f "$PKG/DrivenRides.dll" ] || { echo "Build first: tools/build-mod.sh" >&2; exit 1; }
STAGE="DrivenRides-staged-$(date +%Y%m%d%H%M%S)"
scp -q -r "$PKG" "$HOST:$STAGE" < /dev/null
scp -q "$REPO/build/DrivenRides.sha256" "$HOST:$STAGE.sha256" < /dev/null
scp -q "$REPO/tools/Install-Windows.ps1" "$HOST:Install-DrivenRides.ps1" < /dev/null
ssh "$HOST" "powershell -NoProfile -ExecutionPolicy Bypass -File Install-DrivenRides.ps1 -Source $STAGE -Manifest $STAGE.sha256" < /dev/null
