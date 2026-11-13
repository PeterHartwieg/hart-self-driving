#!/usr/bin/env bash
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
if [ $# -ge 1 ] && [[ "$1" != send && "$1" != stop && "$1" != status && "$1" != fetch ]]; then HOST="$1"; shift; else HOST="${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}"; fi
ACTION="${1:?send, stop, status or fetch}"; shift || true
REMOTE="AppData/LocalLow/Hovgaard Games/Big Ambitions/DrivenRides/tests"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
case "$ACTION" in
  send)
    [ $# -ge 1 ] || { echo "send needs a command" >&2; exit 1; }
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
