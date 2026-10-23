#!/usr/bin/env bash
# Builds the Driven Rides mod package with the official SDK in Unity batch mode.
# Usage: tools/build-mod.sh
# Output: build/DrivenRides/ (the folder that goes into the game's ModsLocal).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
# A dedicated SDK clone, so other mods in a personal SDK project cannot break this build.
SDK="${BA_SDK:-$REPO/build/sdk}"
SDK_URL="https://github.com/hovgaardgames/bigambitions.git"
SDK_COMMIT="1e03ddd5071b77a32cfd8bba89f0c130ba6c7d73"
GAME_DLLS="${BA_GAME_DLLS:-$REPO/build/game-dlls}"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
HARMONY_URL="https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.2/lib.harmony.2.4.2.nupkg"
HARMONY_SHA256="d64592e53090464559fce48612c9ca7c8dc73113841376b7aa3455f46fc5d579"
HARMONY_DLL_SHA256="7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c"
MOD_DIR="$REPO/unity/DrivenRides"
DEPS="$MOD_DIR/Dependencies"
LOG_DIR="$REPO/build/logs"
mkdir -p "$LOG_DIR" "$DEPS"

[ -x "$UNITY" ] || { echo "Unity 2022.3.62f2 not found at $UNITY" >&2; exit 1; }
[ -f "$GAME_DLLS/BigAmbitions.dll" ] || { echo "Game DLLs not found in $GAME_DLLS; run tools/fetch-game-dlls.sh" >&2; exit 1; }
if [ ! -d "$SDK/.git" ]; then
  git clone -q "$SDK_URL" "$SDK"
  git -C "$SDK" checkout -q "$SDK_COMMIT"
fi
# Refuse a clone at another commit or with edited SDK sources, rather than resetting it.
[ "$(git -C "$SDK" rev-parse HEAD)" = "$SDK_COMMIT" ] || { echo "$SDK is not at the pinned SDK commit $SDK_COMMIT" >&2; exit 1; }
SDK_INPUTS=(Assets/Editor Assets/Mods/Example-Vehicle "Assets/_BaDependencies/Vehicle Dependencies" Packages ProjectSettings)
if [ -n "$(git -C "$SDK" status --porcelain --untracked-files=no -- "${SDK_INPUTS[@]}")" ]; then
  echo "$SDK has local changes in ${SDK_INPUTS[*]}; refusing to build" >&2; exit 1
fi
# The SDK compiles against the installed game's DLLs; copy only changed files to avoid reimports.
rsync -a --checksum "$GAME_DLLS/" "$SDK/Assets/_BaDependencies/GameDlls/" --include='*.dll' --exclude='*'
python3 "$REPO/tools/write-dll-metas.py" "$SDK/Assets/_BaDependencies/GameDlls"

# Harmony, checksum-pinned. Never committed.
if [ ! -f "$DEPS/0Harmony.dll" ]; then
  tmp="$(mktemp -d)"
  curl -fsSL "$HARMONY_URL" -o "$tmp/harmony.nupkg"
  echo "$HARMONY_SHA256  $tmp/harmony.nupkg" | shasum -a 256 -c - >/dev/null
  unzip -q -o "$tmp/harmony.nupkg" 'lib/net472/0Harmony.dll' LICENSE -d "$tmp"
  cp "$tmp/lib/net472/0Harmony.dll" "$DEPS/0Harmony.dll"
  cp "$tmp/LICENSE" "$REPO/build/Harmony-LICENSE.txt"
  rm -rf "$tmp"
fi
echo "$HARMONY_DLL_SHA256  $DEPS/0Harmony.dll" | shasum -a 256 -c - >/dev/null || { echo "Cached 0Harmony.dll does not match the pinned hash; delete it and rebuild" >&2; exit 1; }

# Core, compiled for netstandard2.1 and shipped next to the mod DLL.
dotnet build "$REPO/Core/DrivenRides.Core.csproj" -c Release -nologo -v quiet
cp "$REPO/Core/bin/Release/netstandard2.1/DrivenRides.Core.dll" "$DEPS/DrivenRides.Core.dll"

# Every script needs a committed .meta with a stable GUID.
python3 "$REPO/tools/write-script-metas.py" "$MOD_DIR" "$REPO/unity/BuildEditor"

# Link the mod folder and the build helper into the SDK project.
link() { # link <target> <link path>
  if [ -L "$2" ]; then [ "$(readlink "$2")" = "$1" ] || { rm "$2"; ln -s "$1" "$2"; }
  elif [ -e "$2" ]; then echo "$2 exists and is not a symlink; refusing to replace it" >&2; exit 1
  else ln -s "$1" "$2"; fi
}
link "$MOD_DIR" "$SDK/Assets/Mods/DrivenRides"
link "$REPO/unity/BuildEditor" "$SDK/Assets/Editor/DrivenRidesBuild"

run_unity() { # run_unity <method> <log name>
  local log="$LOG_DIR/$2.log"
  if ! "$UNITY" -batchmode -nographics -projectPath "$SDK" -buildTarget Win64 \
      -executeMethod "$1" -modId DrivenRides -logFile "$log"; then
    grep -E "DrivenRidesBuild|error CS|Exception" "$log" | tail -40 >&2
    echo "Unity step $1 failed; full log: $log" >&2
    exit 1
  fi
  grep "DrivenRidesBuild" "$log" || true
}

run_unity DrivenRides.BuildEditor.BatchBuild.Build build

OUT="$REPO/build/DrivenRides"
rm -rf "$OUT"
cp -R "$SDK/Output/DrivenRides" "$OUT"
cp "$REPO/build/Harmony-LICENSE.txt" "$OUT/Dependencies/Harmony-LICENSE.txt" 2>/dev/null || true
cp "$REPO/README.md" "$REPO/CHANGELOG.md" "$REPO/LICENSE.txt" "$OUT/"
( cd "$OUT" && find . -type f | sort | xargs shasum -a 256 ) > "$REPO/build/DrivenRides.sha256"
echo "Built $OUT"
cat "$REPO/build/DrivenRides.sha256"
