#!/bin/bash
# Renders the gallery boards in ../gallery/*.html (copies of the design canvas's boards with local images) at 2x
# with headless Chrome, and writes ../export/<name>.jpg at the board's size, under 1 MB, 4:4:4 chroma.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; G="$HERE/../gallery"; OUT="$HERE/../export"; TMP="$HERE/../build"
mkdir -p "$OUT" "$TMP"
size() { case "$1" in Social) echo 1080,1080;; Support) echo 1260,360;; *) echo 1920,1080;; esac; }
for f in "$G"/*.html; do
  n="$(basename "$f" .html)"; s="$(size "$n")"
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu --hide-scrollbars \
    --allow-file-access-from-files --force-device-scale-factor=2 --window-size="$s" --virtual-time-budget=8000 \
    --screenshot="$TMP/$n@2x.png" "file://$f" 2>/dev/null
  "$HOME/Coding_Projects/hart-group/.venv/bin/python" - "$TMP/$n@2x.png" "$OUT/$n.jpg" "$s" <<'PY'
import io, sys
from PIL import Image, ImageCms
w, h = map(int, sys.argv[3].split(','))
im = Image.open(sys.argv[1]).convert('RGB').resize((w, h), Image.Resampling.LANCZOS)
srgb = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
for q in (96, 94, 92, 90, 88, 85):
    buf = io.BytesIO(); im.save(buf, 'JPEG', quality=q, subsampling=0, optimize=True, icc_profile=srgb)
    if buf.tell() <= 1000 * 1024: open(sys.argv[2], 'wb').write(buf.getvalue()); print(f'{sys.argv[2].split("/")[-1]} {w}x{h} q{q} {buf.tell()//1024} KB'); break
else: sys.exit('too big')
PY
  rm -f "$TMP/$n@2x.png"
done
