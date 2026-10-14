#!/usr/bin/env bash
# Renames the mod as players see it, in every place the name appears: tools/mod.conf, the manifest's DisplayName,
# Locales/en.json, DrivenRidesMod.DisplayName and the README. The mod id, folder and namespace stay DrivenRides.
# Usage: tools/rename-mod.sh "<new name>"
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
NEW="${1:?Pass the new name, for example: tools/rename-mod.sh \"Self Driving\"}"
# shellcheck source=mod.conf
. "$REPO/tools/mod.conf"
OLD="$MOD_NAME"
[ "$OLD" != "$NEW" ] || { echo "The mod is already called $NEW." >&2; exit 0; }

python3 - "$REPO" "$OLD" "$NEW" <<'EOF'
import json, sys
repo, old, new = sys.argv[1:]
if '"' in new or '\\' in new or '\n' in new:
    sys.exit("The name can't contain quotes, backslashes or line breaks.")
files = {
    "tools/mod.conf": [(f'MOD_NAME="{old}"', f'MOD_NAME="{new}"')],
    "unity/DrivenRides/ModManifest.asset": [(f"DisplayName: {old}\n", f"DisplayName: {new}\n")],
    "unity/DrivenRides/Scripts/DrivenRidesMod.cs": [(f'DisplayName = "{old}";', f'DisplayName = "{new}";')],
    "README.md": [(f"# {old}\n", f"# {new}\n")],
}
# Everything is checked before anything is written, so a failed rename leaves no file half done.
out = {}
for path, pairs in files.items():
    text = open(f"{repo}/{path}", encoding="utf-8").read()
    for a, b in pairs:
        if a not in text:
            sys.exit(f"{path}: '{a.strip()}' not found; nothing renamed.")
        text = text.replace(a, b)
    # The README names the mod in its text too ("Driven Rides adds nothing to your save").
    if path == "README.md":
        text = text.replace(old, new)
    out[path] = text
# Locale values only, never keys.
loc = "unity/DrivenRides/Locales/en.json"
data = json.load(open(f"{repo}/{loc}", encoding="utf-8"))
changed = 0
for key, value in data.items():
    if old in value:
        data[key] = value.replace(old, new)
        changed += 1
if changed == 0:
    sys.exit("Locales/en.json doesn't mention the old name; nothing renamed.")
out[loc] = json.dumps(data, ensure_ascii=False, indent=4) + "\n"
for path, text in out.items():
    with open(f"{repo}/{path}", "w", encoding="utf-8") as f:
        f.write(text)
print(f"Renamed '{old}' to '{new}' (en.json: {changed} texts). Rebuild with tools/build-mod.sh and tools/package-mod.sh.")
EOF
