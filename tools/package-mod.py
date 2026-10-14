#!/usr/bin/env python3
"""Builds build/package/<MOD_FOLDER>/ from build/DrivenRides and checks it (tools/package-mod.sh runs this).

Refuses the package when the name or version disagree between files, a locale key is missing or unused, a file is
missing or extra, or a hash is off. Never ships game DLLs, decompiled code, saves or anything outside the list.
"""
import hashlib
import json
import re
import shutil
import struct
import sys
import zipfile
import zlib
from pathlib import Path

REPO = Path(sys.argv[1]).resolve()
BUILD = REPO / "build" / "DrivenRides"
MOD = REPO / "unity" / "DrivenRides"
PREVIEW_LIMIT = 1024 * 1024  # Steam's limit for a Workshop preview image
errors = []


def fail(message):
    errors.append(message)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def conf():
    values = {}
    for line in (REPO / "tools" / "mod.conf").read_text(encoding="utf-8").splitlines():
        m = re.match(r'^([A-Z_]+)="([^"]*)"$', line.strip())
        if m:
            values[m.group(1)] = m.group(2)
    for key in ("MOD_NAME", "MOD_FOLDER", "GAME_BUILD"):
        if key not in values:
            sys.exit(f"tools/mod.conf has no {key}.")
    return values


def one(pattern, text, what):
    m = re.search(pattern, text, re.M)
    if not m:
        sys.exit(f"Can't find {what}.")
    return m.group(1)


def placeholder_png(path, width=1280, height=720):
    """A plain two-tone 16:9 PNG, written with the standard library: a stand-in until the real thumbnail exists."""
    top, band = (32, 38, 46), (240, 180, 40)
    rows = bytearray()
    for y in range(height):
        colour = band if height * 0.62 <= y < height * 0.70 else top
        rows.append(0)
        rows.extend(bytes(colour) * width)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(rows), 9)) + chunk(b"IEND", b"")
    path.write_bytes(png)


cfg = conf()
name, folder, game_build = cfg["MOD_NAME"], cfg["MOD_FOLDER"], cfg["GAME_BUILD"]

# Name and version agree everywhere players see them.
manifest_asset = (MOD / "ModManifest.asset").read_text(encoding="utf-8")
mod_cs = (MOD / "Scripts" / "DrivenRidesMod.cs").read_text(encoding="utf-8")
readme = (REPO / "README.md").read_text(encoding="utf-8")
locale_path = MOD / "Locales" / "en.json"
try:
    locale = json.loads(locale_path.read_text(encoding="utf-8"))
except json.JSONDecodeError as e:
    sys.exit(f"Locales/en.json is not valid JSON: {e}")
version = one(r'public const string Version = "([^"]+)";', mod_cs, "DrivenRidesMod.Version")
names = {
    "ModManifest.asset DisplayName": one(r"^  DisplayName: (.+)$", manifest_asset, "the manifest's DisplayName"),
    "DrivenRidesMod.DisplayName": one(r'public const string DisplayName = "([^"]+)";', mod_cs, "DrivenRidesMod.DisplayName"),
    "en.json options_header": locale.get("drivenrides:options_header", ""),
    "README title": one(r"^# (.+)$", readme, "the README's title"),
}
for where, value in names.items():
    if value != name:
        fail(f"{where} is '{value}', tools/mod.conf says '{name}' (tools/rename-mod.sh sets them all).")
manifest_version = one(r"^  Version: (.+)$", manifest_asset, "the manifest's Version")
if manifest_version != version:
    fail(f"ModManifest.asset Version {manifest_version} differs from DrivenRidesMod.Version {version}.")
if f"Version {version}" not in readme:
    fail(f"The README doesn't say 'Version {version}'.")
if f"build {game_build}" not in readme:
    fail(f"The README doesn't name the target game build {game_build}.")

# Every key the code uses is in the locale file, and every key there is used.
used, prefixes = set(), set()
for script in (MOD / "Scripts").glob("*.cs"):
    for key in re.findall(r"drivenrides:[a-z0-9_]+", script.read_text(encoding="utf-8")):
        (prefixes if key.endswith("_") else used).add(key)
for key in sorted(used - locale.keys()):
    fail(f"Locale key {key} is used in the code but missing from Locales/en.json.")
for prefix in sorted(prefixes):
    if not any(k.startswith(prefix) for k in locale):
        fail(f"No locale key starts with {prefix}, which the code builds keys from.")
for key in sorted(locale.keys() - used):
    if not any(key.startswith(p) for p in prefixes):
        fail(f"Locale key {key} is in Locales/en.json but no code uses it.")
for key, value in locale.items():
    if not isinstance(value, str) or not value.strip():
        fail(f"Locale key {key} has no text.")

# The package: build/DrivenRides as tools/build-mod.sh left it, plus the preview image.
out_root = REPO / "build" / "package"
out = out_root / folder
if out.exists():
    shutil.rmtree(out)
out_root.mkdir(parents=True, exist_ok=True)
shutil.copytree(BUILD, out)
preview_source = next((p for p in (REPO / "workshop" / "preview.png", REPO / "workshop" / "preview.jpg") if p.exists()), None)
if preview_source:
    preview = out / preview_source.name
    shutil.copyfile(preview_source, preview)
    placeholder = False
else:
    preview = out / "preview.png"
    placeholder_png(preview)
    placeholder = True
if preview.stat().st_size > PREVIEW_LIMIT:
    fail(f"{preview.name} is {preview.stat().st_size} bytes; the Workshop takes at most {PREVIEW_LIMIT}.")

expected = {
    "DrivenRides.dll", "Dependencies/DrivenRides.Core.dll", "Dependencies/0Harmony.dll",
    "Dependencies/Harmony-LICENSE.txt", "Locales/en.json", "README.md", "LICENSE.txt", preview.name,
}
files = {p.relative_to(out).as_posix(): p for p in sorted(out.rglob("*")) if p.is_file()}
for missing in sorted(expected - files.keys()):
    fail(f"Missing from the package: {missing}")
for extra in sorted(files.keys() - expected):
    fail(f"Not expected in the package: {extra}")
roots = [p for p in out.glob("*.dll")]
if len(roots) != 1:
    fail(f"The game needs exactly one DLL in the mod folder's root; found {len(roots)}.")

# Nothing of the game's own: no file named like a game DLL (Harmony is ours, pinned).
game_dlls = {p.name.lower() for p in (REPO / "build" / "game-dlls").glob("*.dll")}
for rel in files:
    base = Path(rel).name.lower()
    if base in game_dlls and base != "0harmony.dll":
        fail(f"{rel} has the name of a game DLL.")

# Hashes: Harmony as pinned, Core as just built, the rest as tools/build-mod.sh recorded them, the texts as in git.
harmony_pin = one(r'^HARMONY_DLL_SHA256="([0-9a-f]{64})"$', (REPO / "tools" / "build-mod.sh").read_text(encoding="utf-8"), "Harmony's pinned hash")
if "Dependencies/0Harmony.dll" in files and sha256(files["Dependencies/0Harmony.dll"]) != harmony_pin:
    fail("Dependencies/0Harmony.dll doesn't match the pinned Harmony 2.4.2 hash.")
core_built = REPO / "Core" / "bin" / "Release" / "netstandard2.1" / "DrivenRides.Core.dll"
if "Dependencies/DrivenRides.Core.dll" in files and (not core_built.exists() or sha256(core_built) != sha256(files["Dependencies/DrivenRides.Core.dll"])):
    fail("Dependencies/DrivenRides.Core.dll differs from the Core just built.")
recorded = {}
for line in (REPO / "build" / "DrivenRides.sha256").read_text(encoding="utf-8").splitlines():
    digest, rel = line.split(None, 1)
    recorded[rel.removeprefix("./")] = digest
for rel, path in files.items():
    if rel == preview.name:
        continue
    if recorded.get(rel) != sha256(path):
        fail(f"{rel} doesn't match build/DrivenRides.sha256.")
for rel, source in (("Locales/en.json", locale_path), ("README.md", REPO / "README.md"), ("LICENSE.txt", REPO / "LICENSE.txt")):
    if rel in files and sha256(files[rel]) != sha256(source):
        fail(f"{rel} differs from the repo's copy; rebuild with tools/build-mod.sh.")

# The mod DLL carries this version (Log line "Version ... loading.", a UTF-16 string in the assembly).
dll = out / "DrivenRides.dll"
if dll.exists() and version.encode("utf-16-le") not in dll.read_bytes():
    fail(f"DrivenRides.dll doesn't contain the version {version}; rebuild with tools/build-mod.sh.")

if errors:
    print("Package refused:", file=sys.stderr)
    for e in errors:
        print("  " + e, file=sys.stderr)
    sys.exit(1)

stem = f"{folder}-{version}"
listing = {rel: {"sha256": sha256(p), "bytes": p.stat().st_size} for rel, p in files.items()}
(out_root / f"{stem}.manifest.json").write_text(json.dumps({
    "name": name, "folder": folder, "version": version, "targetGameBuild": int(game_build),
    "previewPlaceholder": placeholder, "files": listing,
}, indent=2) + "\n", encoding="utf-8")
zip_path = out_root / f"{stem}.zip"
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
    for rel, path in files.items():
        info = zipfile.ZipInfo(f"{folder}/{rel}", date_time=(2026, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, path.read_bytes())

print(f"Packaged {name} {version} for game build {game_build}: {out}")
for rel, entry in listing.items():
    print(f"  {entry['sha256']}  {rel}")
print(f"Manifest {out_root / (stem + '.manifest.json')}, zip {zip_path}.")
if placeholder:
    print("The preview image is a placeholder; put the real one at workshop/preview.png (or .jpg, at most 1 MB).")
