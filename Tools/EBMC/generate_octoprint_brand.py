#!/usr/bin/env python3
"""Regenerate the EBMC badge images of the OctoPrint brand plugin (Tools/octoprint-ebmc-brand).

Usage (from the repo root):
    python3 Tools/EBMC/generate_octoprint_brand.py .

Requirements: the same as Tools/EBMC/generate_brand_assets.py, whose tracing and rendering this
script imports (install into a virtualenv, e.g.
    python3 -m venv /tmp/ebmc-venv
    /tmp/ebmc-venv/bin/pip install numpy pillow scipy scikit-image shapely trimesh mapbox_earcut manifold3d
    /tmp/ebmc-venv/bin/python Tools/EBMC/generate_octoprint_brand.py .
). Only numpy, pillow, scipy, scikit-image and shapely are used here, but importing
generate_brand_assets needs the others too.

Source: Tools/EBMC/assets/ebmc-badge-source.png, traced exactly as for the app's icons, so the
plugin shows the same mark as StaticData/Icons/application.png and the bed watermark.

Outputs (Tools/octoprint-ebmc-brand/octoprint_ebmc_brand/static/img/):
    badge.svg                       the traced badge as one even-odd path, black; used as a CSS
                                    mask for the navbar mark (drawn in the navbar's text colour) and
                                    as Safari's pinned-tab icon
    favicon-32x32.png               the app icon (club orange tile, cream badge), tile cropped
    favicon-256x256.png             same, for OctoPrint's favicon recolouring canvas
    apple-touch-icon-114x114.png    full-bleed orange square with the badge (iOS rounds it)
    apple-touch-icon-144x144.png
    apple-touch-icon-180x180.png
    icon-192x192.png, icon-512x512.png   web app manifest icons (the app icon with its margin)
    graph-watermark.png             500x500, ink at low alpha: the bed watermark style, lighter
                                    so the temperature curves and labels stay readable
And Tools/octoprint-ebmc-brand/octoprint_ebmc_brand/static/css/ebmc_palette.css: the club colours
as CSS variables, used by ebmc_brand.css and ebmc_brand_login.css. The script prints the
contrast ratio of every text/background pairing those stylesheets use.

Palette: ink, soft ink, paper, cream and orange are imported from generate_brand_assets.py. The
other club colours have no importable constant in this repo (generate_ebmc_files.py runs on
import and only holds orange, deep orange and yellow), so they are defined below, with the same
values as EBMC.md "Theme palette" and the CSS variables of eastbaymakersclub.com (checked
2026-11-19).
"""
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate_brand_assets as gba  # noqa: E402

OUT_REL = "Tools/octoprint-ebmc-brand/octoprint_ebmc_brand/static/img"
CSS_REL = "Tools/octoprint-ebmc-brand/octoprint_ebmc_brand/static/css/ebmc_palette.css"


def hexc(rgb):
    return "#%02X%02X%02X" % tuple(rgb)


def rgb_of(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


# The club website's palette (site variable name in brackets).
CLUB = {
    "ink": hexc(gba.INK),               # --ink
    "soft-ink": hexc(gba.SOFT_INK),     # --soft-ink
    "muted": "#71685E",                 # --muted
    "paper": hexc(gba.PAPER),           # --paper (page ground)
    "paper-warm": "#FBF6ED",            # --paper-warm (cards, default buttons)
    "cream": hexc(gba.CREAM),           # --cream-on-dark (text on blue/orange/ink)
    "blue": "#0648D8",                  # --blue (links on hover, nav underline, focus, "Join")
    "blue-deep": "#0034A8",             # --blue-deep
    "orange": hexc(gba.ORANGE),         # --orange (accent, "Donate", kickers)
    "orange-deep": "#D42609",           # --orange-deep
    "green": "#087F5B",                 # --green
    "yellow": "#FFDF69",                # --yellow (highlights, never text)
}
WHITE = "#FFFFFF"


def mix(fg, bg, alpha):
    a, b = rgb_of(fg), rgb_of(bg)
    return hexc(round(a[i] * alpha + b[i] * (1 - alpha)) for i in range(3))


# Derived colours, all from the palette above (site: --line is ink at 24 %, focus ring blue at 28-35 %).
DERIVED = {
    "line": mix(CLUB["ink"], CLUB["paper"], 0.24),
    "line-soft": mix(CLUB["ink"], CLUB["paper-warm"], 0.12),
    "hover": mix(CLUB["cream"], CLUB["paper-warm"], 0.5),
    "success-bg": mix(CLUB["green"], CLUB["paper-warm"], 0.14),
    "success-line": mix(CLUB["green"], CLUB["paper-warm"], 0.35),
    "info-bg": mix(CLUB["blue"], CLUB["paper-warm"], 0.10),
    "info-line": mix(CLUB["blue"], CLUB["paper-warm"], 0.30),
    "warning-bg": mix(CLUB["yellow"], CLUB["paper-warm"], 0.45),
    "warning-line": CLUB["yellow"],
    # Danger is deliberately NOT a club colour. Error notices keep Bootstrap's red family; only
    # their text uses Bootstrap's own darker error red (its .text-error hover colour), because
    # the stock #B94A48 on #F2DEDE is 3.9:1.
    "error-text": "#953B39",
    "error-bg": "#F2DEDE",
}

# (label, text, background) pairings the stylesheets use; WCAG AA needs 4.5:1 for normal text.
PAIRS = [
    ("body text: ink on paper", "ink", "paper"),
    ("text in panels: ink on warm paper", "ink", "paper-warm"),
    ("navbar links: soft ink on paper", "soft-ink", "paper"),
    ("links: blue on warm paper", "blue", "paper-warm"),
    ("links: blue on paper", "blue", "paper"),
    ("link hover: deep blue on warm paper", "blue-deep", "paper-warm"),
    ("primary button: cream on blue", "cream", "blue"),
    ("primary button hover: cream on deep blue", "cream", "blue-deep"),
    ("default button: ink on warm paper", "ink", "paper-warm"),
    ("inactive tab: soft ink on paper", "soft-ink", "paper"),
    ("secondary text (sizes, dates): muted on warm paper", "muted", "paper-warm"),
    ("placeholder: muted on white field", "muted", WHITE),
    ("success notice: soft ink on success tint (green edge)", "soft-ink", "success-bg"),
    ("success marks: green on warm paper", "green", "paper-warm"),
    ("info notice: deep blue on info tint", "blue-deep", "info-bg"),
    ("warning notice: soft ink on yellow tint", "soft-ink", "warning-bg"),
    ("error notice: dark error red on Bootstrap error tint", "error-text", "error-bg"),
    ("dropdown hover / active list item: cream on blue", "cream", "blue"),
    ("progress bar text: cream on blue", "cream", "blue"),
]


def luminance(h):
    def ch(c):
        c = c / 255
        return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    r, g, b = (ch(c) for c in rgb_of(h))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(a, b):
    la, lb = sorted((luminance(a), luminance(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def palette_css():
    allc = dict(CLUB)
    allc.update(DERIVED)
    lines = ["/* GENERATED by Tools/EBMC/generate_octoprint_brand.py - do not edit by hand.",
             "   The East Bay Makers Club palette (eastbaymakersclub.com CSS variables). */",
             ":root {"]
    for k, v in allc.items():
        lines.append("    --ebmc-%s: %s;" % (k, v))
    lines.append("    --ebmc-focus-ring: rgba(%d, %d, %d, 0.30);" % rgb_of(CLUB["blue"]))
    lines.append("}")
    return "\n".join(lines) + "\n"


def contrast_table():
    allc = dict(CLUB)
    allc.update(DERIVED)
    rows = []
    for label, fg, bg in PAIRS:
        f, b = allc.get(fg, fg), allc.get(bg, bg)
        r = contrast(f, b)
        rows.append((label, f, b, r))
    return rows
ICON_VARIANT = "paper-on-orange"   # the app icon's variant (generate_brand_assets.py default)
# The app's bed watermark is ink at alpha 70/255 on the bed. OctoPrint's own octopus behind the
# graph is at about 36/255; the badge has more solid area, so it is drawn lighter still.
GRAPH_ALPHA = 18
SVG_TOL = 0.02  # extra simplification for the SVG path, source px (256 px source)


def badge_svg(rings):
    bx0, by0, bx1, by1 = gba.rings_bounds(rings)
    pad = 0.5
    w, h = bx1 - bx0 + 2 * pad, by1 - by0 + 2 * pad
    parts = []
    for r in rings:
        from shapely.geometry import LinearRing

        pts = np.asarray(LinearRing(r).simplify(SVG_TOL).coords)[:-1]
        pts = pts - (bx0 - pad, by0 - pad)
        parts.append("M" + " ".join("%.2f %.2f" % (x, y) for x, y in pts) + "Z")
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %.2f %.2f" width="256" height="%d">'
        "<!-- EBMC badge, (c) East Bay Makers Club, used with permission; generated by "
        "Tools/EBMC/generate_octoprint_brand.py -->"
        '<path fill="#000" fill-rule="evenodd" d="%s"/></svg>\n'
        % (w, h, round(256 * h / w), "".join(parts))
    )


def full_bleed(rings, size):
    """Orange square with the cream badge, no transparent margin (for apple-touch icons)."""
    tile_rgb, badge_rgb = gba.ICON_VARIANTS[ICON_VARIANT]
    n = 1024
    b = n * gba.BADGE_FRAC * 0.92
    c = n / 2
    badge = np.asarray(gba.render_mask(rings, (n, n), (c - b / 2, c - b / 2, c + b / 2, c + b / 2)),
                       np.float64)[..., None] / 255
    rgb = np.array(tile_rgb, np.float64) * (1 - badge) + np.array(badge_rgb, np.float64) * badge
    img = Image.fromarray(np.round(rgb).astype(np.uint8), "RGB")
    return img.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
    out = os.path.join(root, OUT_REL)
    os.makedirs(out, exist_ok=True)
    rings = gba.trace_badge(os.path.join(root, gba.SRC_REL))
    print("traced %d rings, %d vertices" % (len(rings), sum(len(r) for r in rings)))

    with open(os.path.join(out, "badge.svg"), "w") as f:
        f.write(badge_svg(rings))

    icon = gba.make_icon(rings, ICON_VARIANT)          # 1024x1024, tile 824 with a margin
    m0 = (1024 - gba.TILE) // 2
    tile = icon.crop((m0, m0, m0 + gba.TILE, m0 + gba.TILE))
    for s in (32, 256):
        gba.save_png(tile.resize((s, s), Image.LANCZOS), os.path.join(out, "favicon-%dx%d.png" % (s, s)))
    for s in (192, 512):
        gba.save_png(icon.resize((s, s), Image.LANCZOS), os.path.join(out, "icon-%dx%d.png" % (s, s)))
    for s in (114, 144, 180):
        gba.save_png(full_bleed(rings, s), os.path.join(out, "apple-touch-icon-%dx%d.png" % (s, s)))

    mask = gba.render_mask(rings, (500, 500), (10, 10, 490, 490), ss=4)
    gba.save_png(gba.mono(mask, gba.INK, alpha_scale=GRAPH_ALPHA / 255), os.path.join(out, "graph-watermark.png"))

    with open(os.path.join(root, CSS_REL), "w") as f:
        f.write(palette_css())
    print("wrote %s" % CSS_REL)
    print("contrast (WCAG AA normal text needs 4.5):")
    for label, f, b, r in contrast_table():
        print("  %-52s %s on %s  %5.2f:1 %s" % (label, f, b, r, "ok" if r >= 4.5 else "FAILS AA"))

    for name in sorted(os.listdir(out)):
        p = os.path.join(out, name)
        extra = ""
        if name.endswith(".png"):
            extra = " %dx%d" % Image.open(p).size
        print("%-34s %7d bytes%s" % (name, os.path.getsize(p), extra))


if __name__ == "__main__":
    main()
