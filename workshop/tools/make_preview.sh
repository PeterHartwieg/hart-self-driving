#!/bin/bash
# Renders tools/thumbnail.html at 3x with headless Chrome, then writes ../preview.jpg: the largest square
# (1600, 1280 or 1024 px) that fits Steam's 1 MB preview limit at JPEG quality 92 or better, 4:4:4 chroma
# so the lettering stays sharp. Needs the hart-group venv for Pillow.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../build"; mkdir -p "$OUT"
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" --headless=new --disable-gpu --hide-scrollbars \
  --allow-file-access-from-files --force-device-scale-factor=3 --window-size=1024,1024 --virtual-time-budget=8000 \
  --screenshot="$OUT/thumbnail@3x.png" "file://$HERE/thumbnail.html" 2>/dev/null
"$HOME/Coding_Projects/hart-group/.venv/bin/python" - "$OUT/thumbnail@3x.png" "$HERE/../preview.jpg" <<'PY'
import io, sys
from PIL import Image, ImageCms
src = Image.open(sys.argv[1]).convert('RGB')
assert src.size == (3072, 3072), src.size
srgb = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
for side in (1600, 1280, 1024):
    im = src.resize((side, side), Image.Resampling.LANCZOS)
    for q in (97, 96, 95, 94, 93, 92):
        buf = io.BytesIO(); im.save(buf, 'JPEG', quality=q, subsampling=0, optimize=True, icc_profile=srgb)
        if buf.tell() <= 1000 * 1024:
            open(sys.argv[2], 'wb').write(buf.getvalue()); print(f'preview.jpg {side}x{side}, quality {q}, {buf.tell()} bytes'); sys.exit()
sys.exit('No size fits 1 MB at quality 92.')
PY
