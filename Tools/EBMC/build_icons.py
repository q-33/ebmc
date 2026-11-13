#!/usr/bin/env python3
"""Regenerate the EBMC outline icons (Lucide style) in StaticData/Icons from SVG sources.

Usage (from the repo root):
    <venv>/bin/python Tools/EBMC/build_icons.py . [--preview-dir DIR] [--compare-ref REF] [--check]

    --preview-dir DIR   also write contact sheets into DIR:
                          icons-contact-sheet.png      every mapped icon, old vs new, at 1x and 2x,
                                                       tinted on the dark and the light club theme
                          icons-contact-sheet-3x.png   the same sheet magnified 3x (nearest neighbour)
                          icons-custom-128.png         only the custom glyphs, at 128 px
    --compare-ref REF   git revision the "old" column is read from (default HEAD), via `git show`
    --check             write nothing; exit 1 if any mapped PNG differs from what would be generated

Requirements (not in the macOS system Python; install into a virtualenv):
    python3 -m venv /tmp/ebmc-venv
    /tmp/ebmc-venv/bin/pip install pillow resvg-py
Tested with Python 3.9, pillow 11.3, resvg-py 0.2.0 (prebuilt wheel, no system libraries).
Only the standard library, Pillow and resvg-py are used; git is needed only for --preview-dir.

Inputs (repo-relative):
    Tools/EBMC/icons/map.json      the single source of truth: target PNG (path under
                                   StaticData/Icons) -> source (lucide:<name> or custom:<name>),
                                   output size in px, notes
    Tools/EBMC/icons/lucide/*.svg  74 Lucide icons, vendored unmodified (version and URL in
                                   VERSION, licence in LICENSE)
    Tools/EBMC/icons/custom/*.svg  37 glyphs drawn for EBMC on the same 24-unit grid and 2-unit stroke

Safety: every map.json target must be a relative path ending in .png that resolves (after
symlinks) to a file inside StaticData/Icons. Outside the folders in NEW_TARGET_DIRS that file
must already exist (the refresh only replaces upstream icons); inside them (Shapes/, the shapes
palette's own glyphs) a target may be new and is created. All icons are rendered and checked in
memory first; files are written only if every source loaded and every check passed.

Outputs: every target in map.json, overwritten in place, as an RGBA PNG of the mapped size whose
pixels are all pure black (0,0,0) with the shape carried by alpha. The app tints such icons with
GrayToColor(theme.TextColor), which recolours every unsaturated pixel and keeps its alpha.
An entry may instead name "color": "#RRGGBB" (a saturated colour). Its pixels are then that colour,
again with the shape in alpha. GrayToColor leaves saturated pixels alone, so the colour survives
callers that tint (and shows as is where a caller does not tint). Used for the status icons
(error, warning, info) whose colour carries meaning.
An entry may also name "glyph": N (px, a multiple of 4 below size, with size - N a multiple of 8):
the 24-unit drawing is then rendered at N px and centred on the transparent size x size canvas, so
the edge offset stays a whole number of pixels at every halving down to size/4. Used where a caller
loads an icon into a larger box than the glyph should fill (the overflow button and the Z offset
arrows load 32 px icons; the old art left the same margin). The SVG geometry is not changed.

Rendering:
  * SVG is treated as untrusted data. Each file is parsed with the stdlib XML parser, DOCTYPE and
    entity declarations are refused, and only plain shape elements (path, circle, ellipse, rect,
    line, polyline, polygon, g) with geometry attributes are accepted; anything else (script,
    image, use, style, foreignObject, any href or url(...)) aborts the build. The root must be the
    24x24 viewBox with Lucide's stroke attributes. A clean SVG is then rebuilt from the accepted
    shapes with stroke and fill forced to black, at the target pixel size, so the 2-unit stroke
    scales with the icon (5.33 px at 64 px, 4.67 px at 56 px).
  * resvg rasterises with analytic anti-aliasing; the result is rendered at 4x the target size
    and box-filtered down, which keeps edge coverage exact and the result independent of
    resvg's own sub-pixel heuristics.
  * Output PNGs are written by Pillow with no ancillary chunks (no time, gamma, text or iCCP), so
    two runs produce identical bytes.
  * Edge clearance: the outermost size//24 pixels on every side (2 px at 56 and 64 px, a little
    under one grid unit) must stay empty (alpha <= 8), or the build fails. Lucide's own padding
    rule is one full grid unit; this check catches anything that strays into the edge pixels.
  * Sizes: 64 px for icons the code loads at 16x16, 56 px for 14x14 (4x the logical size); likewise
    48 px for 12x12, 96 px for 24x24 and 128 px for 32x32. When callers load one file at several
    sizes, the master suits the most common one (a 48 px chevron loaded at 14 or 10 px is box-halved
    to 24 px and then resampled bilinearly). Library/folder.png is loaded at its own size (256 px)
    as a library tile, so it stays 256.
    The shapes palette (Shapes/) uses 80 px masters for its 20 px glyphs. It loads them with its own
    loader (PaletteIcons in MatterControlLib/PartPreviewWindow/ShapesPalette/PaletteButton.cs), which
    box-halves the master straight to the device size, so 1x (80 -> 40 -> 20) and 2x (80 -> 40) are
    both exact box reductions.
    The app's loader (agg-sharp StaticData.LoadIcon) behaves differently per device scale:
      1x: the source is halved with a 2x2 box filter until it reaches the logical size, so
          64 -> 32 -> 16 (56 -> 28 -> 14) is an exact box reduction.
      2x: LoadIcon(path) first resamples the whole source up by the device scale (64 -> 128,
          bilinear), and the sized LoadIcon then halves that twice (128 -> 64 -> 32). The result is
          close to an exact 2x box reduction of the source but slightly softer, because of the
          bilinear upscale. The preview sheet emulates this path.
"""

import argparse
import io
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont

import resvg_py

SVG_NS = "http://www.w3.org/2000/svg"
ICONS_DIR = os.path.join("StaticData", "Icons")
SOURCE_DIR = os.path.join("Tools", "EBMC", "icons")
SUPERSAMPLE = 4
# Folders under StaticData/Icons whose targets may be created (EBMC's own icons, not upstream ones)
NEW_TARGET_DIRS = ("Shapes",)

# The keys a map.json entry may have; any other key is an error
ENTRY_KEYS = {"source", "size", "color", "glyph", "notes", "previously_untinted"}

ROOT_REQUIRED = {
    "viewBox": "0 0 24 24",
    "fill": "none",
    "stroke-width": "2",
    "stroke-linecap": "round",
    "stroke-linejoin": "round",
}
ROOT_ALLOWED = set(ROOT_REQUIRED) | {"width", "height", "stroke", "class"}
SHAPE_ATTRS = {
    "path": {"d"},
    "circle": {"cx", "cy", "r"},
    "ellipse": {"cx", "cy", "rx", "ry"},
    "rect": {"x", "y", "width", "height", "rx", "ry"},
    "line": {"x1", "y1", "x2", "y2"},
    "polyline": {"points"},
    "polygon": {"points"},
    "g": set(),
}
PAINT_VALUES = {"none", "currentColor", "#000", "#000000", "black"}
NUMERIC = re.compile(r"^[-+0-9.eE ,]*$")
PATH_DATA = re.compile(r"^[MmLlHhVvCcSsQqTtAaZz0-9eE.,+\- \t\r\n]*$")

# Preview colours: the club themes in StaticData/Themes/System/EBMC/
DARK = ("#EDE6DA", "#1F1C19")    # EBMC-Dark text, workspace background
LIGHT = ("#050505", "#FBF6ED")   # EBMC-Paper text, workspace background


def fail(message):
    sys.exit("build_icons: " + message)


def local(tag):
    return tag.split("}", 1)[1] if tag.startswith("{") else tag


def load_shapes(svg_path):
    """Parse an SVG as data and return its accepted shape elements as (tag, attrs) tuples."""
    with open(svg_path, "rb") as f:
        raw = f.read()
    text = raw.decode("utf-8")
    if re.search(r"<!(DOCTYPE|ENTITY)", text, re.IGNORECASE):
        fail(f"{svg_path}: DOCTYPE/ENTITY declarations are not allowed")
    if re.search(r"href|url\s*\(|<script|<image|<use|<style|foreignObject", text, re.IGNORECASE):
        fail(f"{svg_path}: scripts, external references, embedded images and styles are not allowed")
    root = ET.fromstring(text)
    if local(root.tag) != "svg":
        fail(f"{svg_path}: root element is not <svg>")
    for key, value in root.attrib.items():
        if key not in ROOT_ALLOWED:
            fail(f"{svg_path}: unexpected attribute on <svg>: {key}")
    for key, value in ROOT_REQUIRED.items():
        if root.attrib.get(key) != value:
            fail(f"{svg_path}: <svg> must have {key}=\"{value}\" (Lucide grid and stroke)")
    if root.attrib.get("stroke") not in ("currentColor", "#000", "#000000", "black"):
        fail(f"{svg_path}: <svg> stroke must be currentColor or black")

    shapes = []

    def walk(element, depth):
        for child in element:
            tag = local(child.tag)
            if tag not in SHAPE_ATTRS:
                fail(f"{svg_path}: element <{tag}> is not allowed")
            attrs = {}
            for key, value in child.attrib.items():
                if key in ("fill", "stroke"):
                    if value not in PAINT_VALUES:
                        fail(f"{svg_path}: <{tag}> {key}={value!r} is not allowed")
                    attrs[key] = "none" if value == "none" else "#000"
                elif key in SHAPE_ATTRS[tag]:
                    pattern = PATH_DATA if key == "d" else NUMERIC
                    if not pattern.match(value):
                        fail(f"{svg_path}: <{tag}> {key} has unexpected characters")
                    attrs[key] = value
                else:
                    fail(f"{svg_path}: <{tag}> attribute {key!r} is not allowed")
            if (child.text or "").strip():
                fail(f"{svg_path}: <{tag}> must not contain text")
            if tag == "g":
                if depth > 4:
                    fail(f"{svg_path}: groups nested too deeply")
                walk(child, depth + 1)
            else:
                shapes.append((tag, attrs))

    walk(root, 0)
    if not shapes:
        fail(f"{svg_path}: no shapes")
    return shapes


def clean_svg(shapes, px):
    """Rebuild a minimal SVG from accepted shapes, black stroke, at px x px."""
    out = [
        f'<svg xmlns="{SVG_NS}" width="{px}" height="{px}" viewBox="0 0 24 24" fill="none" '
        'stroke="#000" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
    ]
    for tag, attrs in shapes:
        body = " ".join(f'{k}="{v}"' for k, v in sorted(attrs.items()))
        out.append(f"<{tag} {body}/>")
    out.append("</svg>")
    return "".join(out)


def render(shapes, size, color=None, glyph=None):
    """Rasterise to a black-on-transparent (or color-on-transparent) RGBA image of size x size.

    With glyph, the drawing is rendered at glyph x glyph px and centred on the size x size canvas."""
    if glyph is not None and glyph != size:
        inner = render(shapes, glyph, color)
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0) if color is None else
                           tuple(int(color[i:i + 2], 16) for i in (1, 3, 5)) + (0,))
        offset = (size - glyph) // 2
        canvas.paste(inner, (offset, offset))
        return canvas
    big = size * SUPERSAMPLE
    png = bytes(resvg_py.svg_to_bytes(svg_string=clean_svg(shapes, big)))
    image = Image.open(io.BytesIO(png))
    image.load()
    if image.size != (big, big):
        fail(f"rasteriser returned {image.size}, expected {big}x{big}")
    alpha = image.convert("RGBA").getchannel("A").reduce(SUPERSAMPLE)  # box filter
    if color is None:
        black = Image.new("L", (size, size), 0)
        return Image.merge("RGBA", (black, black, black, alpha))
    r, g, b = (Image.new("L", (size, size), int(color[i:i + 2], 16)) for i in (1, 3, 5))
    return Image.merge("RGBA", (r, g, b, alpha))


def png_bytes(image):
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=False, compress_level=9)
    return buffer.getvalue()


def source_path(repo, source):
    kind, _, name = source.partition(":") if isinstance(source, str) else ("", "", "")
    if kind not in ("lucide", "custom") or not re.match(r"^[a-z0-9-]+$", name):
        fail(f"bad source {source!r}")
    path = os.path.join(repo, SOURCE_DIR, kind, name + ".svg")
    if not os.path.isfile(path):
        fail(f"source {source!r}: {path} does not exist")
    return path


def resolve_target(repo, target):
    """Return the absolute path of a map.json target, refusing anything outside StaticData/Icons."""
    icons_root = os.path.realpath(os.path.join(repo, ICONS_DIR))
    if not isinstance(target, str) or not target or os.path.isabs(target) or "\\" in target:
        fail(f"target {target!r}: must be a relative path under {ICONS_DIR}")
    if not target.lower().endswith(".png"):
        fail(f"target {target!r}: must be a .png file")
    dest = os.path.realpath(os.path.join(icons_root, target))
    if not dest.startswith(icons_root + os.sep):
        fail(f"target {target!r}: resolves to {dest}, outside {icons_root}")
    if not os.path.isfile(dest):
        new_dirs = [os.path.join(icons_root, d) for d in NEW_TARGET_DIRS]
        if os.path.exists(dest) or os.path.dirname(dest) not in new_dirs:
            fail(f"target {target!r}: does not exist (the refresh only replaces existing icons, "
                 f"except directly inside {', '.join(NEW_TARGET_DIRS)}/)")
    return dest


def check_edges(target, image):
    """The outermost size//24 pixels (2 px at 56 and 64 px) must stay empty."""
    size = image.size[0]
    margin = size // 24
    alpha = image.getchannel("A")
    bbox = alpha.point(lambda a: 255 if a > 8 else 0).getbbox()
    if bbox is None:
        fail(f"{target}: rendered empty")
    if bbox[0] < margin or bbox[1] < margin or bbox[2] > size - margin or bbox[3] > size - margin:
        fail(f"{target}: glyph touches the canvas edge (bbox {bbox} in {size}px)")


# ---------------------------------------------------------------------------------------------
# Preview: emulate the app's load path (box halving to device size, then GrayToColor tinting)


def app_scale(image, logical, scale):
    """Emulate agg-sharp CreateScaledImage for an icon loaded at logical size on a scale x display."""
    device = logical * scale
    src = image.convert("RGBA")
    if scale != 1:
        # LoadIcon(path) resamples the whole source by the device scale first
        src = src.resize((src.width * scale, src.height * scale), Image.BILINEAR)
    if src.width == device:
        return src
    while src.width >= device * 2:
        src = src.resize((src.width // 2, src.height // 2), Image.BOX)
        if src.width == device:
            return src
    return src.resize((device, device), Image.BILINEAR)


def gray_to_color(image, hex_color):
    """Emulate agg-sharp GrayToColor (FromColor): unsaturated pixels take the colour, keep alpha."""
    r0, g0, b0 = (int(hex_color[i:i + 2], 16) for i in (1, 3, 5))
    out = image.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            mx, mn = max(r, g, b) / 255.0, min(r, g, b) / 255.0
            light = (mx + mn) / 2
            if mx == mn:
                sat = 0.0
            else:
                sat = (mx - mn) / (mx + mn) if light <= 0.5 else (mx - mn) / (2 - mx - mn)
            if sat < 0.01:
                px[x, y] = (r0, g0, b0, a)
    return out


def git_old(repo, ref, rel_path):
    try:
        data = subprocess.run(
            ["git", "-C", repo, "show", f"{ref}:{rel_path}"],
            check=True, capture_output=True).stdout
        return Image.open(io.BytesIO(data)).convert("RGBA")
    except (subprocess.CalledProcessError, OSError):
        return None


def get_font(size):
    for candidate in ("/System/Library/Fonts/Supplemental/Arial.ttf", "/System/Library/Fonts/Helvetica.ttc",
                      "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"):
        if os.path.exists(candidate):
            try:
                return ImageFont.truetype(candidate, size)
            except OSError:
                pass
    return ImageFont.load_default()


def contact_sheet(repo, ref, entries, new_images, out_dir):
    """Rows: one per icon. Columns: dark theme old 1x/2x | new 1x/2x, light theme old | new."""
    font = get_font(11)
    label_w, cell, gap, row_h = 230, 40, 8, 44
    panel_w = 4 * cell + 3 * gap
    width = label_w + 2 * panel_w + 3 * 16
    header_h = 34
    columns = 2
    rows_per_col = (len(entries) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * width, header_h + rows_per_col * row_h), "#8A8580")
    draw = ImageDraw.Draw(sheet)
    for c in range(columns):
        x0 = c * width
        draw.text((x0 + 8, 4), "file  (logical pt)", fill="#000", font=font)
        for p, (name, (text, bg)) in enumerate((("dark", DARK), ("light", LIGHT))):
            px = x0 + label_w + p * (panel_w + 16)
            draw.text((px, 4), f"{name}: old 1x   old 2x   new 1x   new 2x", fill="#000", font=font)
    for index, (target, entry) in enumerate(entries):
        c, r = divmod(index, rows_per_col)
        x0, y0 = c * width, header_h + r * row_h
        logical = entry["size"] // 4
        draw.text((x0 + 8, y0 + 8), f"{target}  ({logical})", fill="#000", font=font)
        draw.text((x0 + 8, y0 + 22), entry["source"], fill="#222", font=font)
        old = git_old(repo, ref, f"{ICONS_DIR}/{target}".replace(os.sep, "/"))
        for p, (text, bg) in enumerate((DARK, LIGHT)):
            px = x0 + label_w + p * (panel_w + 16)
            draw.rectangle([px - 4, y0 + 1, px + panel_w + 4, y0 + row_h - 2], fill=bg)
            versions = [(old, not entry.get("previously_untinted")), (new_images[target], True)]
            for v, (image, tinted) in enumerate(versions):
                for s, scale in enumerate((1, 2)):
                    cx = px + (v * 2 + s) * (cell + gap)
                    if image is None:
                        draw.text((cx, y0 + 14), "n/a", fill=text, font=font)
                        continue
                    shown = app_scale(image, logical, scale)
                    if tinted:
                        shown = gray_to_color(shown, text)
                    oy = y0 + (row_h - shown.height) // 2
                    sheet.paste(shown, (cx + (cell - shown.width) // 2, oy), shown)
    sheet.save(os.path.join(out_dir, "icons-contact-sheet.png"))
    sheet.resize((sheet.width * 3, sheet.height * 3), Image.NEAREST).save(
        os.path.join(out_dir, "icons-contact-sheet-3x.png"))


def custom_sheet(repo, entries, out_dir):
    font = get_font(14)
    customs = sorted({e["source"] for _, e in entries if e["source"].startswith("custom:")})
    cols, cell, label = 4, 128, 22
    rows = (len(customs) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * (cell + 24) + 24, rows * (cell + label + 24) + 24), "#FBF6ED")
    draw = ImageDraw.Draw(sheet)
    for i, source in enumerate(customs):
        r, c = divmod(i, cols)
        x, y = 24 + c * (cell + 24), 24 + r * (cell + label + 24)
        draw.rectangle([x, y, x + cell - 1, y + cell - 1], outline="#D9CFBF")
        # 24-unit grid guides, Lucide's 1-unit padding box
        unit = cell / 24
        draw.rectangle([x + unit, y + unit, x + cell - unit, y + cell - unit], outline="#ECE3D5")
        glyph = render(load_shapes(source_path(repo, source)), cell)
        sheet.paste(gray_to_color(glyph, LIGHT[0]), (x, y), glyph)
        draw.text((x, y + cell + 4), source[7:], fill="#000", font=font)
    sheet.save(os.path.join(out_dir, "icons-custom-128.png"))


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    parser.add_argument("repo", help="repository root")
    parser.add_argument("--preview-dir")
    parser.add_argument("--compare-ref", default="HEAD")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    repo = os.path.abspath(args.repo)

    with open(os.path.join(repo, SOURCE_DIR, "map.json"), encoding="utf-8") as f:
        mapping = json.load(f)["icons"]
    entries = sorted(mapping.items())

    # Pass 1: validate and render everything in memory; any failure stops before a file is written
    new_images, pending, stale, seen = {}, [], [], set()
    for target, entry in entries:
        if not isinstance(entry, dict):
            fail(f"{target!r}: entry must be an object")
        unknown = sorted(set(entry) - ENTRY_KEYS)
        if unknown:
            # a typo such as "colour" would otherwise be ignored and its effect silently lost
            fail(f"{target}: unknown key(s) {', '.join(unknown)}; allowed: {', '.join(sorted(ENTRY_KEYS))}")
        dest = resolve_target(repo, target)
        if dest in seen:
            fail(f"{target!r}: maps to the same file as another target")
        seen.add(dest)
        size = entry.get("size")
        if not isinstance(size, int) or size <= 0 or size % 4 or size > 1024:
            fail(f"{target}: size {size!r} must be a positive multiple of 4, at most 1024")
        color = entry.get("color")
        if color is not None:
            if not isinstance(color, str) or not re.fullmatch(r"#[0-9A-Fa-f]{6}", color):
                fail(f"{target}: color {color!r} must be #RRGGBB")
            r, g, b = (int(color[i:i + 2], 16) for i in (1, 3, 5))
            if max(r, g, b) - min(r, g, b) < 32:
                fail(f"{target}: color {color} is too grey; GrayToColor would overwrite it")
        glyph = entry.get("glyph")
        if glyph is not None and (not isinstance(glyph, int) or glyph <= 0 or glyph % 4 or glyph >= size
                                  or (size - glyph) % 8):
            fail(f"{target}: glyph {glyph!r} must be a multiple of 4 below size, with size - glyph a multiple of 8")
        image = render(load_shapes(source_path(repo, entry.get("source"))), size, color, glyph)
        check_edges(target, image)
        new_images[target] = image
        data = png_bytes(image)
        current = None
        if os.path.isfile(dest):
            with open(dest, "rb") as f:
                current = f.read()
        if current != data:
            stale.append(target)
            pending.append((dest, data))

    # Pass 2: write
    if not args.check:
        for dest, data in pending:
            # only a NEW_TARGET_DIRS folder can be missing here (resolve_target checked the parent)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with open(dest, "wb") as f:
                f.write(data)

    if args.check:
        for target in stale:
            print("out of date:", target)
        sys.exit(1 if stale else 0)
    print(f"{len(entries)} icons, {len(stale)} rewritten")

    if args.preview_dir:
        os.makedirs(args.preview_dir, exist_ok=True)
        contact_sheet(repo, args.compare_ref, entries, new_images, args.preview_dir)
        custom_sheet(repo, entries, args.preview_dir)
        print("previews in", args.preview_dir)


if __name__ == "__main__":
    main()
