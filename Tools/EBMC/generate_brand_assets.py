#!/usr/bin/env python3
"""Regenerate every EBMC brand asset from the club badge PNG.

Usage (from the repo root):
    python3 Tools/EBMC/generate_brand_assets.py . [--icon ink-on-paper|paper-on-ink|paper-on-orange]
                                                  [--spinner-mesh plate|silhouette] [--preview-dir DIR]

Requirements (not in the macOS system Python; install into a virtualenv, e.g.
    python3 -m venv /tmp/ebmc-venv
    /tmp/ebmc-venv/bin/pip install numpy pillow scipy scikit-image shapely trimesh mapbox_earcut manifold3d
    /tmp/ebmc-venv/bin/python Tools/EBMC/generate_brand_assets.py .
):
    numpy, pillow, scipy, scikit-image   - tracing + raster output
    shapely, trimesh, mapbox_earcut,     - 3D badge mesh (extrusion, boolean union,
    manifold3d                             watertight check)
Tested with Python 3.9, numpy 2.0, pillow 11.3, scipy 1.13, scikit-image 0.24,
shapely 2.0, trimesh 4.12, mapbox_earcut 2.1, manifold3d 3.5.

Source: Tools/EBMC/assets/ebmc-badge-source.png (256x256, black badge carried by alpha).

Tracing: the alpha channel is spline-upsampled 8x (pixel-area convention), the 0.5
iso-contour is extracted with marching squares, resampled at 0.05 source px, smoothed
with a 0.5 px Gaussian along the arc (removes pixel-grid wobble, does not move edges
measurably), and simplified to 0.01 px. Rings are combined even-odd. Every raster is
rendered from these vectors with 4-8x supersampling; the STL is extruded from them.

Outputs (repo-relative):
    StaticData/Icons/application.png             1024x1024 app icon (chosen with --icon)
    application.ico                              16/32/48/64/128/256
    StaticData/Icons/mh.png                      64x64 black+alpha title-bar mark
    StaticData/Icons/Library/design_apps_icon.png 64x64 black+alpha
    StaticData/OEMSettings/bedimage.png          460x460 black at alpha<=70 bed watermark
    StaticData/Images/mh-logo.png                460x460 black on opaque white
    StaticData/Images/PartSheetLogo.png          482x121 badge + "Generated with EBMC"
    StaticData/OEMSettings/SampleParts/EBMC Badge.stl  binary STL, printable plate (sample part)
    StaticData/Stls/EBMC Badge.stl               spinner mesh: art-only silhouette by default, or the
                                                 same plate with --spinner-mesh plate
    StaticData/Stls/sheet_icon.mcx               Variable Sheet 3D icon: the white "MC" letters part
                                                 becomes the badge and the teal square's MC-shaped
                                                 holes are filled; edited in place
    StaticData/Images/Thumbnails/16091828042932597356-256x256.png  Variable Sheet / Sheet library
    StaticData/Images/Thumbnails/8315973372694813834-256x256.png   thumbnails: teal square repainted
                                                 with the badge, edited in place
    The last three are rewritten from their own current contents and only the badge part changes,
    so re-running is a no-op (idempotent).
Previews (--preview-dir, default $TMPDIR/ebmc-brand-previews):
    icons/icon-*.png, icons/icons-contact-sheet.png, bed-preview-*.png, small-mark-*.png,
    stl-preview-*.png
"""
import argparse
import os
import struct
import sys
import tempfile

import numpy as np
from PIL import Image, ImageDraw, ImageFont

try:
    from scipy import ndimage
    from skimage import measure
    from shapely.geometry import LinearRing, Polygon, MultiPolygon, box
    from shapely.ops import unary_union
    import trimesh
except ImportError as e:  # pragma: no cover
    sys.exit("missing dependency (%s); see the requirements at the top of this script" % e)

# ---------------------------------------------------------------- palette
INK = (0x05, 0x05, 0x05)
SOFT_INK = (0x38, 0x32, 0x2C)
PAPER = (0xF4, 0xED, 0xE3)
CREAM = (0xFF, 0xF7, 0xEA)
ORANGE = (0xFF, 0x3D, 0x12)
NEAR_BLACK = (0x15, 0x13, 0x11)
BED_DARK = (0x62, 0x5B, 0x53)
BED_LIGHT = (0xF4, 0xED, 0xE3)

ICON_VARIANTS = {
    # name: (tile colour, badge colour)
    "ink-on-paper": (PAPER, INK),
    "paper-on-ink": (NEAR_BLACK, PAPER),
    "paper-on-orange": (ORANGE, CREAM),
}

SRC_REL = "Tools/EBMC/assets/ebmc-badge-source.png"
FONT_REL = "StaticData/Fonts/Nunito_Bold.ttf"      # preview labels
LOGO_FONT_REL = "StaticData/Fonts/Poppins.ttf"     # Poppins Bold, part-sheet text


# ---------------------------------------------------------------- tracing
def trace_badge(path, upsample=8, sigma=0.5, step=0.05, tol=0.01):
    """Return list of closed rings (Nx2 arrays, source-pixel units, y down)."""
    alpha = np.asarray(Image.open(path).convert("RGBA"))[..., 3].astype(np.float64) / 255.0
    pad = 4
    field = ndimage.zoom(np.pad(alpha, pad), upsample, order=3, mode="nearest", grid_mode=True)
    rings = []
    for c in measure.find_contours(field, 0.5):
        # (row, col) of upsampled grid -> source pixel-edge coordinates
        xy = np.stack([(c[:, 1] + 0.5) / upsample - pad, (c[:, 0] + 0.5) / upsample - pad], 1)
        if np.allclose(xy[0], xy[-1]):
            xy = xy[:-1]
        closed = np.vstack([xy, xy[:1]])
        d = np.r_[0, np.cumsum(np.linalg.norm(np.diff(closed, axis=0), axis=1))]
        n = max(int(d[-1] / step), 16)
        t = np.linspace(0, d[-1], n, endpoint=False)
        xs = np.interp(t, d, closed[:, 0])
        ys = np.interp(t, d, closed[:, 1])
        xs = ndimage.gaussian_filter1d(xs, sigma / step, mode="wrap")
        ys = ndimage.gaussian_filter1d(ys, sigma / step, mode="wrap")
        r = LinearRing(np.stack([xs, ys], 1)).simplify(tol)
        rings.append(np.asarray(r.coords)[:-1])
    # deterministic order: by enclosed area, largest first
    rings.sort(key=lambda r: -abs(Polygon(r).area))
    return rings


def rings_bounds(rings):
    allp = np.vstack(rings)
    return allp[:, 0].min(), allp[:, 1].min(), allp[:, 0].max(), allp[:, 1].max()


def badge_geometry(rings):
    """Even-odd combination of the rings as a shapely (Multi)Polygon."""
    g = Polygon()
    for r in rings:
        g = g.symmetric_difference(Polygon(r).buffer(0))
    return g.buffer(0)


# ---------------------------------------------------------------- raster helpers
def render_mask(rings, size, fit_box, ss=4):
    """Anti-aliased coverage mask (L) of the badge, scaled to fit fit_box=(x0,y0,x1,y1)
    in a canvas of size=(W,H), centred, aspect preserved."""
    W, H = size
    bx0, by0, bx1, by1 = rings_bounds(rings)
    fx0, fy0, fx1, fy1 = fit_box
    s = min((fx1 - fx0) / (bx1 - bx0), (fy1 - fy0) / (by1 - by0))
    ox = (fx0 + fx1) / 2 - s * (bx0 + bx1) / 2
    oy = (fy0 + fy1) / 2 - s * (by0 + by1) / 2
    acc = np.zeros((H * ss, W * ss), bool)
    for r in rings:
        im = Image.new("1", (W * ss, H * ss), 0)
        pts = [((x * s + ox) * ss, (y * s + oy) * ss) for x, y in r]
        ImageDraw.Draw(im).polygon(pts, fill=1)
        acc ^= np.asarray(im)
    big = Image.fromarray(acc.astype(np.uint8) * 255)
    return big.resize((W, H), Image.BOX)


def rounded_rect_mask(size, rect, radius, ss=4):
    W, H = size
    im = Image.new("L", (W * ss, H * ss), 0)
    x0, y0, x1, y1 = rect
    ImageDraw.Draw(im).rounded_rectangle(
        (x0 * ss, y0 * ss, x1 * ss - 1, y1 * ss - 1), radius=radius * ss, fill=255)
    return im.resize((W, H), Image.BOX)


def mono(mask, rgb=(0, 0, 0), alpha_scale=1.0):
    """Solid colour + alpha from coverage mask."""
    a = np.asarray(mask, np.float64) * alpha_scale
    out = np.zeros(a.shape + (4,), np.uint8)
    out[..., :3] = rgb
    out[..., 3] = np.round(a).astype(np.uint8)
    return Image.fromarray(out)


def save_png(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, "PNG", optimize=True)


# ---------------------------------------------------------------- app icon
TILE = 824
TILE_RADIUS = 185
BADGE_FRAC = 0.68  # badge bounding box (incl. sparkle) as fraction of tile width


def make_icon(rings, variant):
    tile_rgb, badge_rgb = ICON_VARIANTS[variant]
    N = 1024
    m0 = (N - TILE) // 2
    tile = np.asarray(rounded_rect_mask((N, N), (m0, m0, m0 + TILE, m0 + TILE), TILE_RADIUS),
                      np.float64) / 255
    b = TILE * BADGE_FRAC
    c = N / 2
    badge = np.asarray(render_mask(rings, (N, N), (c - b / 2, c - b / 2, c + b / 2, c + b / 2)),
                       np.float64) / 255
    t = np.array(tile_rgb, np.float64)
    k = np.array(badge_rgb, np.float64)
    rgb = t * (1 - badge[..., None]) + k * badge[..., None]
    out = np.zeros((N, N, 4), np.uint8)
    out[..., :3] = np.round(rgb).astype(np.uint8)
    out[..., 3] = np.round(tile * 255).astype(np.uint8)
    return Image.fromarray(out)


def save_ico(icon1024, path):
    sizes = [16, 32, 48, 64, 128, 256]
    imgs = [icon1024.resize((s, s), Image.LANCZOS) for s in sizes]
    imgs[-1].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=imgs[:-1])


def contact_sheet(icons, font_path, path):
    names = list(icons)
    cell, gap = 300, 40
    W = gap + len(names) * (cell + gap)
    H = 520
    sheet = Image.new("RGBA", (W, H), (128, 128, 128, 255))
    d = ImageDraw.Draw(sheet)
    font = ImageFont.truetype(font_path, 20)
    for i, n in enumerate(names):
        x = gap + i * (cell + gap)
        sheet.alpha_composite(icons[n].resize((cell, cell), Image.LANCZOS), (x, 20))
        label = "icon-%s" % n
        tw = d.textlength(label, font=font)
        d.text((x + (cell - tw) / 2, 330), label, font=font, fill=(255, 255, 255, 255))
        i64 = icons[n].resize((64, 64), Image.LANCZOS)
        i32 = icons[n].resize((32, 32), Image.LANCZOS)
        sheet.alpha_composite(i64, (x + 80, 390))
        sheet.alpha_composite(i32, (x + 180, 406))
        d.text((x + 92, 462), "64", font=font, fill=(230, 230, 230, 255))
        d.text((x + 184, 462), "32", font=font, fill=(230, 230, 230, 255))
    save_png(sheet, path)


# ---------------------------------------------------------------- part sheet logo
def part_sheet_logo(rings, font_path):
    W, H = 482, 121
    pad = 8
    bh = H - 2 * pad  # badge height (bbox incl. sparkle is ~square)
    big = ImageFont.truetype(font_path, 58)
    small = ImageFont.truetype(font_path, 28)
    t1, t2 = "Generated with", "EBMC"
    tmp = ImageDraw.Draw(Image.new("L", (1, 1)))
    w1 = tmp.textlength(t1, font=small)
    w2 = tmp.textlength(t2, font=big)
    gap = 18
    tw = max(w1, w2)
    total = bh + gap + tw
    x0 = round((W - total) / 2)
    badge = render_mask(rings, (W, H), (x0, pad, x0 + bh, pad + bh), ss=8)
    text = Image.new("L", (W, H), 0)
    td = ImageDraw.Draw(text)
    tx = x0 + bh + gap
    # small line on top, big line below, block vertically centred on the badge
    a1, d1 = small.getmetrics()
    a2, d2 = big.getmetrics()
    cap_small = small.getbbox("G")  # (x0, y0, x1, y1) relative to draw origin
    cap_big = big.getbbox("E")
    line_gap = 10
    block = (cap_small[3] - cap_small[1]) + line_gap + (cap_big[3] - cap_big[1])
    top = (H - block) / 2
    td.text((tx, top - cap_small[1]), t1, font=small, fill=255)
    y2 = top + (cap_small[3] - cap_small[1]) + line_gap
    td.text((tx, y2 - cap_big[1]), t2, font=big, fill=255)
    out = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    out.alpha_composite(mono(badge, INK))
    # "Generated with" in soft ink, "EBMC" in ink: split masks by row
    tarr = np.asarray(text)
    upper = np.zeros_like(tarr)
    split = int(round(y2 - line_gap / 2))
    upper[:split] = tarr[:split]
    lower = tarr.copy()
    lower[:split] = 0
    out.alpha_composite(mono(Image.fromarray(upper), SOFT_INK))
    out.alpha_composite(mono(Image.fromarray(lower), INK))
    return out


# ---------------------------------------------------------------- STL
PLATE_MM = 50.0     # plate outer size (approx; plate is the art bbox + margin)
MARGIN_MM = 1.5     # plate margin around artwork
BASE_MM = 2.0       # base plate thickness
RELIEF_MM = 1.2     # artwork height above the plate
STL_TOL_MM = 0.01   # contour simplification


def ring_corner_radius(rings):
    """Estimate the outer corner radius of the badge outline (the ring with the largest
    bounding box), from its top-left corner, in source px."""
    r = max(rings, key=lambda q: np.ptp(q[:, 0]) * np.ptp(q[:, 1]))
    x0, y0 = r[:, 0].min(), r[:, 1].min()
    d = np.min(np.hypot(r[:, 0] - x0, r[:, 1] - y0))
    return d / (np.sqrt(2) - 1)


def build_mesh(rings, silhouette=False):
    """Plate (default): single watertight printable solid, rounded-square base plate with the
    badge raised on +Z. silhouette=True: the badge art alone extruded 2 mm (several disjoint
    closed bodies, like the old MH Logo.stl) - reads face-on in the uniformly shaded spinner."""
    bx0, by0, bx1, by1 = rings_bounds(rings)
    s = (PLATE_MM - 2 * MARGIN_MM) / max(bx1 - bx0, by1 - by0)  # mm per source px
    cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2

    def to_mm(r):  # centre on origin, flip y so the art reads correctly from +Z
        return np.stack([(r[:, 0] - cx) * s, (cy - r[:, 1]) * s], 1)

    art = Polygon()
    for r in rings:
        art = art.symmetric_difference(Polygon(to_mm(r)).buffer(0))
    art = art.buffer(0).simplify(STL_TOL_MM)
    polys = art.geoms if isinstance(art, MultiPolygon) else [art]
    if silhouette:
        mesh = trimesh.util.concatenate([trimesh.creation.extrude_polygon(p, BASE_MM) for p in polys])
        mesh.merge_vertices()
        mesh.fix_normals()
        return mesh, s
    hw, hh = (bx1 - bx0) * s / 2 + MARGIN_MM, (by1 - by0) * s / 2 + MARGIN_MM
    R = ring_corner_radius(rings) * s + MARGIN_MM
    print("plate corner radius %.2f mm" % R)
    plate = box(-hw + R, -hh + R, hw - R, hh - R).buffer(R, quad_segs=24)

    base = trimesh.creation.extrude_polygon(plate, BASE_MM)
    parts = [base]
    overlap = 0.1  # sink the relief into the plate so the union is a clean single solid
    for p in polys:
        m = trimesh.creation.extrude_polygon(p, RELIEF_MM + overlap)
        m.apply_translation((0, 0, BASE_MM - overlap))
        parts.append(m)
    mesh = trimesh.boolean.union(parts, engine="manifold")
    mesh.merge_vertices()
    mesh.fix_normals()
    return mesh, s


def binary_stl_bytes(mesh):
    tris = mesh.triangles.astype(np.float32)
    normals = mesh.face_normals.astype(np.float32)
    header = b"EBMC Badge - generated by Tools/EBMC/generate_brand_assets.py"
    header = header.ljust(80, b" ")[:80]
    rec = np.zeros(len(tris), dtype=np.dtype([("n", "<f4", 3), ("v", "<f4", (3, 3)), ("a", "<u2")]))
    rec["n"] = normals
    rec["v"] = tris
    return header + struct.pack("<I", len(tris)) + rec.tobytes()


def write_binary_stl(mesh, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(binary_stl_bytes(mesh))


# ---------------------------------------------------------------- Variable Sheet icon
# StaticData/Stls/sheet_icon.mcx is a binary MCX (zip: scene.mcx JSON + Assets\<SHA1>.stl). In the
# upstream file a white "MC" letters part fills MC-shaped through-holes in a teal square. Only those
# two parts change: the square loses its holes (outer outline re-extruded, same height) and the
# letters part becomes the badge. Object IDs/names/colours/Matrices are kept; the new meshes are
# named by their SHA1 like the others.
SHEET_MCX_REL = "StaticData/Stls/sheet_icon.mcx"
SHEET_BADGE_ID = "a394c33f-9238-4ddd-9cc2-51083fcd6db4"  # "Cube (1) - Linear Extrude (1)", #F0EEEE
SHEET_TILE_ID = "c627b884-6431-47de-b656-ebb369a3b2e9"   # "Linear Extrude (9)", teal #37C7DF
SHEET_BADGE_FRAC = 0.76     # badge bbox (incl. sparkle) as fraction of the teal square
SHEET_BADGE_PROUD = 0.1     # badge top above the square's top (parent units), avoids z-fighting
# Library thumbnails of the "Variable Sheet"/"Sheet" generator items (FNV hashes of the item IDs;
# hand-drawn 2D art, not renders). The teal square's interior is repainted with the white badge.
SHEET_THUMBS_REL = ("StaticData/Images/Thumbnails/16091828042932597356-256x256.png",
                    "StaticData/Images/Thumbnails/8315973372694813834-256x256.png")
SHEET_THUMB_SQUARE = (46.45, 46.45, 117.55, 117.55)  # teal square edges (px, measured)
SHEET_THUMB_FILL = (48, 48, 116, 116)                # repainted interior (x0, y0, x1, y1 excl.)
SHEET_THUMB_TEAL = (6, 160, 184)
SHEET_THUMB_INK = (241, 250, 251)                    # the old letters' white


def _matrix(node):
    import json
    m = node.get("Matrix")  # a JSON array serialised as a string
    return np.eye(4) if m is None else np.array(json.loads(m), np.float64).reshape(4, 4)


def _find(node, ident):
    if node.get("ID") == ident:
        return node
    for c in node.get("Children", []):
        hit = _find(c, ident)
        if hit is not None:
            return hit
    return None


def update_sheet_mcx(rings, path):
    """Rewrite the tile and letters parts of sheet_icon.mcx in place; idempotent. Returns the badge
    mesh (part-local coordinates), the tile box and badge bounds in the shared parent space, and the
    badge asset name."""
    import hashlib
    import io
    import json
    import zipfile

    with zipfile.ZipFile(path) as z:
        infos = z.infolist()
        data = {i.filename: z.read(i.filename) for i in infos}
    scene_text = data["scene.mcx"].decode("utf-8")
    scene = json.loads(scene_text)
    badge, tile = _find(scene, SHEET_BADGE_ID), _find(scene, SHEET_TILE_ID)
    assert badge is not None and tile is not None, "sheet_icon.mcx: expected parts not found"
    for part in (badge, tile):
        assert "Assets\\" + part["MeshPath"] in data, "sheet_icon.mcx: %s not embedded" % part["MeshPath"]
    replace = {}  # old asset name -> (new asset name, stl bytes)

    def stage(part, mesh):
        stl = binary_stl_bytes(mesh)
        name = hashlib.sha1(stl).hexdigest().upper() + ".stl"
        if name != part["MeshPath"]:
            replace[part["MeshPath"]] = (name, stl)
        return name

    # tile: fill the letter holes = re-extrude the largest outline of a mid-height section
    tm = trimesh.load(io.BytesIO(data["Assets\\" + tile["MeshPath"]]), file_type="stl")
    z0, z1 = tm.bounds[0][2], tm.bounds[1][2]
    loops = tm.section(plane_origin=(0, 0, (z0 + z1) / 2), plane_normal=(0, 0, 1)).discrete
    if len(loops) > 1:
        outline = max((Polygon(l[:, :2]) for l in loops), key=lambda q: q.area)
        solid = trimesh.creation.extrude_polygon(Polygon(outline.exterior), z1 - z0)
        solid.apply_translation((0, 0, z0))
        solid.merge_vertices()
        solid.fix_normals()
        stage(tile, solid)
        tm = solid
    # tile and badge are siblings; Matrix is row-vector (translation in the last row)
    tv = np.c_[tm.vertices, np.ones(len(tm.vertices))] @ _matrix(tile)
    (tx0, ty0, _), (tx1, ty1, ttop) = tv[:, :3].min(0), tv[:, :3].max(0)
    T = _matrix(badge)
    assert np.allclose(T[:3, :3], np.eye(3)), "badge part Matrix expected to be a translation"
    T = T[3, :3]

    bx0, by0, bx1, by1 = rings_bounds(rings)
    side = SHEET_BADGE_FRAC * min(tx1 - tx0, ty1 - ty0)
    s = side / max(bx1 - bx0, by1 - by0)
    cx, cy = (tx0 + tx1) / 2 - T[0], (ty0 + ty1) / 2 - T[1]
    art = Polygon()
    for r in rings:  # flip y so the art reads correctly from +Z (as the "MC" did)
        p = np.stack([cx + (r[:, 0] - (bx0 + bx1) / 2) * s, cy - (r[:, 1] - (by0 + by1) / 2) * s], 1)
        art = art.symmetric_difference(Polygon(p).buffer(0))
    art = art.buffer(0).simplify(STL_TOL_MM)
    polys = art.geoms if isinstance(art, MultiPolygon) else [art]
    height = ttop + SHEET_BADGE_PROUD - T[2]
    mesh = trimesh.util.concatenate([trimesh.creation.extrude_polygon(p, height) for p in polys])
    mesh.merge_vertices()
    mesh.fix_normals()
    badge_name = stage(badge, mesh)

    for old, (new, _) in replace.items():
        old_ref = '"MeshPath": "%s"' % old
        assert scene_text.count(old) == 1 and scene_text.count(old_ref) == 1
        scene_text = scene_text.replace(old_ref, '"MeshPath": "%s"' % new)
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w") as z:
        for i in infos:
            zi = zipfile.ZipInfo(i.filename, i.date_time)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi.create_system = i.create_system
            zi.external_attr = i.external_attr
            old = i.filename.split("\\")[-1]
            if i.filename.startswith("Assets\\") and old in replace:
                zi.filename = "Assets\\" + replace[old][0]
                z.writestr(zi, replace[old][1])
            elif i.filename == "scene.mcx":
                z.writestr(zi, scene_text.encode("utf-8"))
            else:
                z.writestr(zi, data[i.filename])
    with open(path, "wb") as f:
        f.write(out.getvalue())
    lo, hi = mesh.bounds
    return mesh, (tx0, ty0, tx1, ty1, ttop), (lo + T, hi + T), badge_name


def update_sheet_thumbnail(rings, path):
    """Repaint the teal square's interior with the white badge; everything else (sheet grid, pie
    chart, square edges) keeps its original pixels, so this is idempotent."""
    im = np.asarray(Image.open(path).convert("RGBA")).copy()
    x0, y0, x1, y1 = SHEET_THUMB_SQUARE
    side = SHEET_BADGE_FRAC * min(x1 - x0, y1 - y0)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    cov = np.asarray(render_mask(rings, im.shape[1::-1], (cx - side / 2, cy - side / 2,
                                                         cx + side / 2, cy + side / 2), ss=8),
                     np.float64) / 255
    fx0, fy0, fx1, fy1 = SHEET_THUMB_FILL
    c = cov[fy0:fy1, fx0:fx1, None]
    rgb = np.array(SHEET_THUMB_TEAL, np.float64) * (1 - c) + np.array(SHEET_THUMB_INK, np.float64) * c
    im[fy0:fy1, fx0:fx1, :3] = np.round(rgb).astype(np.uint8)
    im[fy0:fy1, fx0:fx1, 3] = 255
    save_png(Image.fromarray(im), path)


def render_mesh(mesh, rot, size=800):
    """Tiny orthographic z-buffer renderer (numpy only). rot: 3x3 world->camera, camera looks
    down -Z_cam with +Y_cam up on screen. Returns RGB PIL image."""
    v = mesh.vertices @ rot.T
    n = mesh.face_normals @ rot.T
    lo, hi = v.min(0), v.max(0)
    s = 0.9 * size / (hi[:2] - lo[:2]).max()
    c = (lo + hi) / 2
    X = (v[:, 0] - c[0]) * s + size / 2
    Y = size / 2 - (v[:, 1] - c[1]) * s
    Z = v[:, 2]
    light = np.array([-0.35, 0.45, 0.82])
    light /= np.linalg.norm(light)
    shade = 0.25 + 0.75 * np.clip(n @ light, 0, 1)
    # colour by world height: plate in paper, raised artwork in orange
    zw = mesh.vertices[mesh.faces][:, :, 2].mean(1)
    t = np.clip((zw - BASE_MM) / (RELIEF_MM * 0.5), 0, 1)[:, None]
    fcol = (np.array(PAPER, np.float64) * (1 - t) + np.array(ORANGE, np.float64) * t) * shade[:, None]
    zbuf = np.full((size, size), -np.inf)
    img = np.zeros((size, size, 3), np.float64)
    for f, sh in zip(mesh.faces, fcol):
        x, y, z = X[f], Y[f], Z[f]
        x0, x1 = max(int(np.floor(x.min())), 0), min(int(np.ceil(x.max())), size - 1)
        y0, y1 = max(int(np.floor(y.min())), 0), min(int(np.ceil(y.max())), size - 1)
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        d = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2])
        if abs(d) < 1e-12:
            continue
        w0 = ((y[1] - y[2]) * (gx - x[2]) + (x[2] - x[1]) * (gy - y[2])) / d
        w1 = ((y[2] - y[0]) * (gx - x[2]) + (x[0] - x[2]) * (gy - y[2])) / d
        w2 = 1 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        zz = w0 * z[0] + w1 * z[1] + w2 * z[2]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        upd = inside & (zz > sub)
        sub[upd] = zz[upd]
        img[y0:y1 + 1, x0:x1 + 1][upd] = sh
    bg = np.isinf(zbuf)
    rgb = img
    rgb[bg] = (128, 128, 128)
    return Image.fromarray(np.round(rgb).astype(np.uint8))


def mesh_previews(mesh, prefix):
    def rx(a):
        c, s = np.cos(a), np.sin(a)
        return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])

    def rz(a):
        c, s = np.cos(a), np.sin(a)
        return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])

    views = {
        # camera above the bed looking down -Z, +Y up on screen = how the spinner shows it at angle 0
        "top": np.eye(3),
        # tilted 55 degrees from vertical and turned 25 degrees
        "angle": rx(np.radians(-55)) @ rz(np.radians(-25)),
        # seen from below (-Z): the back of the plate
        "bottom": rx(np.radians(180)),
    }
    for name, rot in views.items():
        save_png(render_mesh(mesh, rot), "%s-%s.png" % (prefix, name))


# ---------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("repo", help="repo root")
    ap.add_argument("--icon", choices=sorted(ICON_VARIANTS), default="paper-on-orange",
                    help="which candidate becomes application.png / application.ico")
    ap.add_argument("--spinner-mesh", choices=("plate", "silhouette"), default="silhouette",
                    help="what goes in StaticData/Stls/EBMC Badge.stl (the startup/About spinner): "
                         "'plate' = same file as the sample part; 'silhouette' = badge art only, "
                         "which stays legible face-on under the spinner's single-colour shading")
    ap.add_argument("--preview-dir", default=os.path.join(tempfile.gettempdir(), "ebmc-brand-previews"))
    args = ap.parse_args()
    root = os.path.abspath(args.repo)
    P = lambda rel: os.path.join(root, rel)
    pv = os.path.abspath(args.preview_dir)
    os.makedirs(os.path.join(pv, "icons"), exist_ok=True)
    font = P(FONT_REL)

    rings = trace_badge(P(SRC_REL))
    print("traced %d rings, %d vertices" % (len(rings), sum(len(r) for r in rings)))

    # A/B: app icon candidates + install chosen one
    icons = {v: make_icon(rings, v) for v in ICON_VARIANTS}
    for v, im in icons.items():
        save_png(im, os.path.join(pv, "icons", "icon-%s.png" % v))
    contact_sheet(icons, font, os.path.join(pv, "icons", "icons-contact-sheet.png"))
    save_png(icons[args.icon], P("StaticData/Icons/application.png"))
    save_ico(icons[args.icon], P("application.ico"))
    print("app icon: %s" % args.icon)

    # C: monochrome marks
    mh = mono(render_mask(rings, (64, 64), (0, 0, 64, 64), ss=8))
    save_png(mh, P("StaticData/Icons/mh.png"))
    design = mono(render_mask(rings, (64, 64), (6, 6, 58, 58), ss=8))
    save_png(design, P("StaticData/Icons/Library/design_apps_icon.png"))
    bed_mask = render_mask(rings, (460, 460), (10, 10, 450, 450), ss=4)
    bed = mono(bed_mask, (0, 0, 0), alpha_scale=70 / 255)
    save_png(bed, P("StaticData/OEMSettings/bedimage.png"))
    logo = mono(bed_mask, (0, 0, 0))
    flat = Image.new("RGBA", (460, 460), (255, 255, 255, 255))
    flat.alpha_composite(logo)
    save_png(flat, P("StaticData/Images/mh-logo.png"))
    save_png(part_sheet_logo(rings, P(LOGO_FONT_REL)), P("StaticData/Images/PartSheetLogo.png"))

    # previews for the marks
    for name, col in (("dark", BED_DARK), ("light", BED_LIGHT)):
        b = Image.new("RGBA", (1024, 1024), col + (255,))
        b.alpha_composite(bed, ((1024 - 460) // 2, (1024 - 460) // 2))
        save_png(b, os.path.join(pv, "bed-preview-%s.png" % name))
    sm = Image.new("RGBA", (64 * 3 + 32 + 16 + 4 * 16, 64 + 32), (0xED, 0xE6, 0xDA, 255))
    x = 16
    for img in (mh, mh.resize((32, 32), Image.LANCZOS), mh.resize((16, 16), Image.LANCZOS),
                mh.resize((16, 16), Image.BILINEAR), design):
        sm.alpha_composite(img, (x, 16))
        x += img.width + 16
    save_png(sm, os.path.join(pv, "small-mark-native.png"))
    save_png(sm.resize((sm.width * 6, sm.height * 6), Image.NEAREST),
             os.path.join(pv, "small-mark-zoom6x.png"))

    # D: STL
    mesh, s = build_mesh(rings)
    write_binary_stl(mesh, P("StaticData/OEMSettings/SampleParts/EBMC Badge.stl"))
    spin = mesh if args.spinner_mesh == "plate" else build_mesh(rings, silhouette=True)[0]
    write_binary_stl(spin, P("StaticData/Stls/EBMC Badge.stl"))
    for label, m in (("sample part", mesh), ("spinner (%s)" % args.spinner_mesh, spin)):
        lo, hi = m.bounds
        print("STL %s: %d triangles, bounds %s .. %s mm, size %s mm, watertight=%s, "
              "winding_consistent=%s, bodies=%d, volume=%.1f mm^3"
              % (label, len(m.faces), np.round(lo, 3), np.round(hi, 3), np.round(hi - lo, 3),
                 m.is_watertight, m.is_winding_consistent, len(m.split(only_watertight=False)),
                 m.volume))
    print("scale %.4f mm per source px" % s)
    mesh_previews(mesh, os.path.join(pv, "stl-preview"))

    # E: Variable Sheet icon (3D model + library thumbnails)
    sm, tile_box, (lo, hi), name = update_sheet_mcx(rings, P(SHEET_MCX_REL))
    print("sheet_icon.mcx: badge part -> %s, %d triangles, watertight=%s, bodies=%d; in parent space "
          "x %.2f..%.2f y %.2f..%.2f z %.2f..%.2f on tile x %.2f..%.2f y %.2f..%.2f top %.2f"
          % (name, len(sm.faces), sm.is_watertight, len(sm.split(only_watertight=False)),
             lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], tile_box[0], tile_box[2], tile_box[1],
             tile_box[3], tile_box[4]))
    for rel in SHEET_THUMBS_REL:
        update_sheet_thumbnail(rings, P(rel))
    print("previews in %s" % pv)


if __name__ == "__main__":
    main()
