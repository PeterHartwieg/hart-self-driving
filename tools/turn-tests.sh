#!/usr/bin/env bash
# Drives the mod's turn test rig on the Windows game PC (docs/turn-tests.md).
# Usage: tools/turn-tests.sh <ssh host> <action> [command words]   (or set BA_GAME_HOST and omit the host)
#   send <command...>   e.g. send run kinds=turn,uturn limit=20     (also: cars, catalogue, rerun <results file>)
#   stop                stops the running session after the current case
#   status              prints status.json
#   fetch               copies the tests folder to build/tests/<time>/ and summarises the latest results
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
if [ $# -ge 1 ] && [[ "$1" != send && "$1" != stop && "$1" != status && "$1" != fetch ]]; then HOST="$1"; shift; else HOST="${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}"; fi
ACTION="${1:?send, stop, status or fetch}"; shift || true
REMOTE="AppData/LocalLow/Hovgaard Games/Big Ambitions/DrivenRides/tests"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
case "$ACTION" in
  send)
    [ $# -ge 1 ] || { echo "send needs a command" >&2; exit 1; }
    # The mod waits for the closing "end" line, so a half-copied file is never read.
    printf '%s\nend\n' "$*" > "$TMP/command.txt"
    scp -q "$TMP/command.txt" "$HOST:$REMOTE/command.txt" < /dev/null
    echo "Sent: $*" ;;
  stop)
    : > "$TMP/stop.txt"
    scp -q "$TMP/stop.txt" "$HOST:$REMOTE/stop.txt" < /dev/null
    echo "Stop sent." ;;
  status)
    scp -q "$HOST:$REMOTE/status.json" "$TMP/status.json" < /dev/null && cat "$TMP/status.json" ;;
  fetch)
    OUT="$REPO/build/tests/$(date +%m%d-%H%M%S)"
    mkdir -p "$OUT"
    scp -q -r "$HOST:$REMOTE/*" "$OUT/" < /dev/null
    echo "Copied to $OUT"
    LATEST="$(ls -t "$OUT"/results-*.jsonl 2>/dev/null | head -1 || true)"
    [ -n "$LATEST" ] && python3 "$REPO/tools/summarise-tests.py" "$LATEST" ;;
  *) echo "Unknown action $ACTION" >&2; exit 1 ;;
esac
