#!/usr/bin/env bash
# Copies build/DrivenRides to the Windows game PC over ssh and installs it into ModsLocal.
# Usage: tools/install-remote.sh [ssh host]   (or set BA_GAME_HOST)
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${1:-${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}}"
PKG="$REPO/build/DrivenRides"
[ -f "$PKG/DrivenRides.dll" ] || { echo "Build first: tools/build-mod.sh" >&2; exit 1; }
# The full traffic soak (500 seeds a template, with the ratchet) runs before every install; plain `dotnet test` runs
# 100 seeds with the hard invariants only. DR_SKIP_SOAK=1 skips it.
if [ "${DR_SKIP_SOAK:-}" != 1 ]; then
  echo "Running the 500-seed traffic soak before installing (DR_SKIP_SOAK=1 skips it)..."
  DR_TRAFFIC_SEEDS=500 DOTNET_CLI_HOME="$REPO/build/dotnet-home" dotnet test "$REPO/tests" -c Release -nologo --filter "FullyQualifiedName~TrafficTests.Soak" \
    || { echo "The traffic soak failed; not installing." >&2; exit 1; }
fi
# Both mod installers use the normal game's token lock, independent of the agent rig.
BRIDGE_TOOLS="${BA_BRIDGE_TOOLS:-$REPO/../big-ambitions-food-trucks/tools}"
BA_GAME_HOST="$HOST" python3 "$BRIDGE_TOOLS/bridge-lock" assert
STAGE="DrivenRides-staged-$(date +%Y%m%d%H%M%S)"
scp -q -r "$PKG" "$HOST:$STAGE" < /dev/null
scp -q "$REPO/build/DrivenRides.sha256" "$HOST:$STAGE.sha256" < /dev/null
scp -q "$REPO/tools/Install-Windows.ps1" "$HOST:Install-DrivenRides.ps1" < /dev/null
BA_GAME_HOST="$HOST" python3 "$BRIDGE_TOOLS/bridge-lock" exec --script Install-DrivenRides.ps1 --parameters "{\"Source\":\"$STAGE\",\"Manifest\":\"$STAGE.sha256\"}"
