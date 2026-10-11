#!/usr/bin/env bash
# Parks the installed mod outside ModsLocal, or restores it, on the Windows game PC.
# Usage: tools/park-remote.sh park|restore [ssh host]   (or set BA_GAME_HOST)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ACTION="${1:?park or restore}"
HOST="${2:-${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}}"
scp -q "$REPO/tools/Park-Windows.ps1" "$HOST:Park-DrivenRides.ps1" < /dev/null
ssh "$HOST" "powershell -NoProfile -ExecutionPolicy Bypass -File Park-DrivenRides.ps1 -Action $ACTION" < /dev/null
