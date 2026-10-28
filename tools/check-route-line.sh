#!/usr/bin/env bash
# Real Unity component/physics checks in the isolated SDK; no game or save access.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
SDK="${BA_SDK:-$REPO/build/sdk}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
ASSEMBLY="${1:-$REPO/build/DrivenRides/DrivenRides.dll}"
LOG="$REPO/build/logs/check-route-line.log"
"$UNITY" -batchmode -nographics -projectPath "$SDK" -buildTarget Win64 \
    -executeMethod DrivenRides.BuildEditor.RouteLineChecks.Run -routeAssembly "$ASSEMBLY" -logFile "$LOG" \
    || { tail -60 "$LOG" >&2; exit 1; }
grep 'DrivenRidesRouteLineChecks' "$LOG"
