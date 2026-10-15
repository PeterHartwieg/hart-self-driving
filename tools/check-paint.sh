#!/usr/bin/env bash
# Regression check of the built mod's paint transfer in Unity, using real game components.
# Usage: tools/check-paint.sh [path/to/DrivenRides.dll]
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
SDK="${BA_SDK:-$REPO/build/sdk}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
ASSEMBLY="${1:-$REPO/build/DrivenRides/DrivenRides.dll}"
LOG="$REPO/build/logs/check-paint.log"
"$UNITY" -batchmode -nographics -projectPath "$SDK" -buildTarget Win64 \
    -executeMethod DrivenRides.BuildEditor.PaintChecks.Run -paintAssembly "$ASSEMBLY" -logFile "$LOG" \
    || { tail -60 "$LOG" >&2; exit 1; }
grep 'DrivenRidesPaintChecks' "$LOG"
