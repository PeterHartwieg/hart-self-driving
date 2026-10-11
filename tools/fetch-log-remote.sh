#!/usr/bin/env bash
# Copies Player.log and Player-prev.log from the Windows game PC into build/logs/game/<label>-<time>/.
# Usage: tools/fetch-log-remote.sh <label> [ssh host]   (or set BA_GAME_HOST)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="${1:?label, for example phase-b}"
HOST="${2:-${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}}"
OUT="$REPO/build/logs/game/$LABEL-$(date +%H%M%S)"
mkdir -p "$OUT"
LOGS="AppData/LocalLow/Hovgaard Games/Big Ambitions"
if ! scp -q "$HOST:$LOGS/Player.log" "$OUT/Player.log" < /dev/null || [ ! -s "$OUT/Player.log" ]; then
  echo "Could not copy Player.log. Don't start the game again until it is copied." >&2
  exit 1
fi
scp -q "$HOST:$LOGS/Player-prev.log" "$OUT/Player-prev.log" < /dev/null || echo "(no Player-prev.log)"
grep -h "Mod:Driven Rides\|DrivenRides\|Driven Rides" "$OUT/Player.log" | cut -c1-240 || true
echo "Saved to $OUT"
