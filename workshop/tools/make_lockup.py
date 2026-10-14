"""Outlined SVG lockups for HART. Self Driving.

Same construction as the HART brand kit (hart-group tools/build_brand.py):
Archivo 800 wordmark at cap height 100 with the round baseline dot, and the
mod name in IBM Plex Mono 500 at the brand tracking, fitted to the width of
the HART lettering, as on the Campaign Objectives thumbnail. A muted
"BIG AMBITIONS MOD" line sits below. Run with the hart-group venv:

  ~/Coding_Projects/hart-group/.venv/bin/python workshop/tools/make_lockup.py
"""
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen

BRAND = Path.home() / 'Coding_Projects/hart-group/brand'
OUT = Path(__file__).resolve().parent.parent / 'brand'
C = {'dark': '#0D100F', 'paper': '#E9ECE6', 'green': '#43C07A', 'deep': '#00703A', 'muted': '#9AA39D', 'ink': '#15181A', 'inkmuted': '#4A524D'}

FT = {'A800': instantiateVariableFont(TTFont(BRAND / 'fonts/Archivo-variable.ttf'), {'wght': 800, 'wdth': 100}, inplace=False),
      'Mono': TTFont(BRAND / 'fonts/IBMPlexMono-Medium.ttf')}


def layout(text, font, size, tracking=0):
    f = FT[font]; gs = f.getGlyphSet(); cm = f.getBestCmap(); s = size / f['head'].unitsPerEm; x = 0; items = []
    for ch in text:
        name = cm[ord(ch)]; items.append((gs[name], x, s, gs)); x += gs[name].width * s + tracking
    return items


def edges(items):
    out = []
    for g, x, s, gs in (items[0], items[-1]):
        p = BoundsPen(gs); g.draw(p); out.append((x + p.bounds[0] * s, x + p.bounds[2] * s))
    return out[0][0], out[1][1]


def cap(font):
    f = FT[font]; gs = f.getGlyphSet(); p = BoundsPen(gs); gs['H'].draw(p); return p.bounds[3] / f['head'].unitsPerEm


A_CAP, M_CAP = cap('A800'), cap('Mono')
TRACK = 3.1 / 22


def wordmark():
    size = 100 / A_CAP
    items = layout('HART', 'A800', size, -.012 * size)
    left, right = edges(items)
    items = [(g, x - left, s, gs) for g, x, s, gs in items]
    right -= left
    return items, right, right + 16 + 9.5, 9.5


def paths(items, x0, baseline, fill):
    el = []
    for g, x, s, gs in items:
        pen = SVGPathPen(gs); g.draw(pen)
        el.append(f'<path fill="{fill}" transform="translate({x0 + x:.4f} {baseline:.4f}) scale({s:.7f} {-s:.7f})" d="{pen.getCommands()}"/>')
    return el


def lockup(tone, label=True):
    fg = C['paper'] if tone == 'dark' else C['ink']
    dot = C['green'] if tone == 'dark' else C['deep']
    sub = C['muted'] if tone == 'dark' else C['inkmuted']
    items, letters, cx, r = wordmark()
    unit = layout('SELF DRIVING', 'Mono', 100, 100 * TRACK); l, rr = edges(unit)
    size = 100 * letters / (rr - l)
    name = layout('SELF DRIVING', 'Mono', size, size * TRACK); nl = edges(name)[0]
    drop = (40 - 22 * M_CAP) + size * M_CAP
    pad, base = 50, 150
    el = paths(items, pad, base, fg)
    el.append(f'<circle fill="{dot}" cx="{pad + cx:.4f}" cy="{base - r}" r="{r}"/>')
    el += paths(name, pad - nl, base + drop, fg)
    h = base + drop + 50
    if label:
        ls = 100 * 25 / 145
        lab = layout('BIG AMBITIONS MOD', 'Mono', ls, ls * 2.4 / 25); ll = edges(lab)[0]
        ldrop = drop + 54
        el += paths(lab, pad - ll, base + ldrop, sub)
        h = base + ldrop + 50
    w = pad * 2 + cx + r
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w:.4f} {h:.4f}" role="img" aria-label="HART. Self Driving">' + ''.join(el) + '</svg>'


OUT.mkdir(exist_ok=True)
for tone in ['dark', 'light']:
    (OUT / f'self-driving-lockup-on-{tone}.svg').write_text(lockup(tone))
    (OUT / f'self-driving-name-on-{tone}.svg').write_text(lockup(tone, label=False))
print('\n'.join(str(p) for p in sorted(OUT.glob('*.svg'))))
