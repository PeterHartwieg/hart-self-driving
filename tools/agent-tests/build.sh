#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"
host="${BA_GAME_HOST:-homepc}"
out="$repo/build/agent-tests"
mkdir -p "$out/input" "$out/output"
if [[ $# -gt 1 || ( $# -eq 1 && "$1" != --reuse-inputs ) ]]; then
  echo 'Usage: build.sh [--reuse-inputs]' >&2
  exit 2
fi
if [[ $# -eq 0 ]]; then
python3 - "$host" "$out/input" <<'PY'
import pathlib, subprocess, sys
host, dest = sys.argv[1], pathlib.Path(sys.argv[2])
base='C:/Program Files (x86)/Steam/steamapps/common/Big Ambitions/Big Ambitions_Data/'
paths={'globalgamemanagers':base+'globalgamemanagers', 'FoodTrucks.dll':'C:/Users/Peter/AppData/LocalLow/Hovgaard Games/Big Ambitions/ModsLocal/FoodTrucks/FoodTrucks.dll'}
# Cecil must resolve enum defaults across the game's managed assemblies when rewriting the mod loader.
subprocess.run(['scp','-q',host+':'+base+'Managed/*.dll',str(dest)], check=True, timeout=180)
for name, path in paths.items():
    subprocess.run(['scp','-q',host+':'+path,str(dest/name)], check=True, timeout=60)
PY
fi
if [ ! -x "$out/venv/bin/python" ]; then
  python3.13 -m venv "$out/venv"
  "$out/venv/bin/pip" install 'UnityPy==1.24.2'
fi
locks="${BA_BRIDGE_TOOLS:-$repo/../big-ambitions-food-trucks/tools}/bridge-locks"
dotnet build tools/agent-tests/AgentRuntime/AgentRuntime.csproj -p:BridgeControlProject="$locks/BridgeControl.csproj" -c Release -p:GameManaged="$out/input" --nologo
dotnet run --project tools/agent-tests/Patcher/Patcher.csproj -c Release -- "$out/input" "$out/output" tools/agent-tests/AgentRuntime/bin/Release/netstandard2.1/AgentTestRuntime.dll
cp "$locks/bin/Release/netstandard2.0/BridgeControl.dll" "$out/output/BridgeControl.dll"
"$out/venv/bin/python" tools/agent-tests/patch-player.py "$out/input/globalgamemanagers" "$out/output/globalgamemanagers"
echo 'Private test overlay built under build/agent-tests/output. Never redistribute the overlay or game files.'
