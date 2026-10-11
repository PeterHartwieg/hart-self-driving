#!/usr/bin/env bash
# Copies the game's managed DLLs that the SDK compiles against from the Windows game PC.
# Usage: tools/fetch-game-dlls.sh [ssh host]   (or set BA_GAME_HOST). Output: build/game-dlls/
# Rerun after every game update. The DLLs belong to Hovgaard Games and are never committed.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${1:-${BA_GAME_HOST:?Pass the ssh host of the Windows game PC, or set BA_GAME_HOST}}"
MANAGED="C:/Program Files (x86)/Steam/steamapps/common/Big Ambitions/Big Ambitions_Data/Managed"
OUT="$REPO/build/game-dlls"
mkdir -p "$OUT"
# The SDK's canonical list (Assets/Editor/Bootstrap/CanonicalGameDlls.cs).
for dll in BehaviorDesigner.Runtime BigAmbitions.AI BigAmbitions.Characters BigAmbitions.DebugMode BigAmbitions \
  BigAmbitions.Factories BigAmbitions.GameAnalytics BigAmbitions.InputSystem BigAmbitions.InteriorDesigner \
  BigAmbitions.Items BigAmbitions.Legacy BigAmbitions.ModAPI BigAmbitions.ModsInternal BigAmbitions.Neighborhoods \
  BigAmbitions.PlacementSystem BigAmbitions.Seasons BigAmbitions.SoundSystem DayNightCycle DOTween DOTween.Modules \
  ExternalPlugins Facepunch.Steamworks.Win64 Google.OrTools Google.Protobuf HBAO.HighDefinition.Runtime HGExtensions \
  HGPlugins JimmysUnityUtilities NaughtyAttributes.Core OdinSerializer System.Runtime.CompilerServices.Unsafe UnityUIExtensions; do
  scp -q "$HOST:$MANAGED/$dll.dll" "$OUT/$dll.dll" < /dev/null
done
shasum "$OUT/BigAmbitions.dll"
