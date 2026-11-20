#!/usr/bin/env python3
"""Renders EsoAgent's brand assets — the GOLDEN WING (owner's pick,
2026-11-18): angular gold wing + signal waves on matte black.

The geometry below is the SINGLE source of truth: this script emits the
SVG masters (assets/brand/svg/) from the same coordinates it rasterizes
into PNG/ICO with PIL, so the vector and raster sets can never drift.
Never ship the AI concept raster as a product asset — only these renders.

Outputs:
  assets/brand/svg/mark.svg                 wing on transparent
  assets/brand/svg/mark-tile.svg            wing in the matte-black tile
  assets/brand/svg/logo-lockup-{dark,light}.svg
  assets/brand/png/mark-{16..512}.png       transparent mark
  assets/brand/png/app-icon-{16..512}.png  tile form (app icon)
  assets/brand/app.ico                     multi-res app icon (16..256,
                                           PNG entries) — THE exe/shortcut
                                           icon; copied to
                                           src/EsoAgent.App/Assets/
  assets/brand/png/logo-lockup-{dark,light}.png
  assets/brand/png/tray-{online,offline,controlled,locked}.ico (+ -48.png)
  assets/brand/png/tray-controlled-pulse.ico   (pulse frame B)
  src/EsoAgent.App/Assets/tray-{online,offline,controlled}.png
  src/EsoAgent.Tray/Assets/*.ico (copies of the tray ICOs)
"""
import io, math, os, shutil, struct
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
OUT = os.path.join(ROOT, "assets", "brand", "png")
SVG_OUT = os.path.join(ROOT, "assets", "brand", "svg")
os.makedirs(OUT, exist_ok=True)
os.makedirs(SVG_OUT, exist_ok=True)
SS = 4

# ---------------- geometry (100x100 viewBox) ----------------
# Four stacked angular blades sweeping from the origin (16,54) up-right,
# plus the lower fin. Gaps between blades are the black of the ground.
BLADES = [
    [(20, 52), (68, 26), (58, 36), (29, 50)],   # top blade — longest
    [(28, 54), (64, 36), (54, 48), (34, 57)],   # second
    [(35, 59), (54, 49), (48, 60), (37, 66)],   # third
    [(36, 67), (51, 62), (46, 69), (36, 72)],   # fourth — shortest
]
FIN = [(21, 54), (38, 60), (34, 79)]            # lower fin triangle
WAVE_CENTER = (48, 51)
WAVE_RADII = (13, 23, 33)
WAVE_HALF_ANGLE = 50                            # degrees either side of +x
WAVE_WIDTH = 5.0

GOLD = [(0.0, (247, 220, 143)), (0.45, (233, 190, 87)), (0.75, (201, 150, 46)), (1.0, (154, 116, 24))]
GREY = [(0.0, (201, 205, 210)), (0.5, (154, 160, 166)), (1.0, (96, 99, 104))]
SLATE = [(0.0, (167, 176, 190)), (0.5, (122, 134, 153)), (1.0, (76, 88, 102))]
HIGHLIGHT = (255, 239, 176)                     # controlled: waves lit
TILE_TOP, TILE_BOT = (28, 28, 28), (17, 17, 17)  # matte black


def gradient_image(size, stops):
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        for x in range(0, size, 8):  # coarse blocks; smooth enough at SS
            t = (x + y) / (2 * size)
            for i in range(len(stops) - 1):
                (t0, c0), (t1, c1) = stops[i], stops[i + 1]
                if t0 <= t <= t1:
                    f = 0 if t1 == t0 else (t - t0) / (t1 - t0)
                    col = tuple(int(c0[k] + (c1[k] - c0[k]) * f) for k in range(3)) + (255,)
                    break
            else:
                col = stops[-1][1] + (255,)
            for dx in range(8):
                if x + dx < size:
                    px[x + dx, y] = col
    return img


def mark_mask(size, waves=True):
    """Wing + waves as a white mask at `size` (already supersampled)."""
    sc = size / 100.0
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    for poly in BLADES + [FIN]:
        d.polygon([(x * sc, y * sc) for x, y in poly], fill=255)
    if waves:
        cx, cy = WAVE_CENTER[0] * sc, WAVE_CENTER[1] * sc
        for r100 in WAVE_RADII:
            r = r100 * sc
            w = max(2, int(WAVE_WIDTH * sc))
            d.arc([cx - r, cy - r, cx + r, cy + r], start=-WAVE_HALF_ANGLE, end=WAVE_HALF_ANGLE, fill=255, width=w)
            for ang in (-WAVE_HALF_ANGLE, WAVE_HALF_ANGLE):
                ex = cx + r * math.cos(math.radians(ang))
                ey = cy + r * math.sin(math.radians(ang))
                d.ellipse([ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2], fill=255)
    return mask


def waves_mask(size):
    sc = size / 100.0
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    cx, cy = WAVE_CENTER[0] * sc, WAVE_CENTER[1] * sc
    for r100 in WAVE_RADII:
        r = r100 * sc
        w = max(2, int(WAVE_WIDTH * sc))
        d.arc([cx - r, cy - r, cx + r, cy + r], start=-WAVE_HALF_ANGLE, end=WAVE_HALF_ANGLE, fill=255, width=w)
        for ang in (-WAVE_HALF_ANGLE, WAVE_HALF_ANGLE):
            ex = cx + r * math.cos(math.radians(ang))
            ey = cy + r * math.sin(math.radians(ang))
            d.ellipse([ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2], fill=255)
    return mask


def render_mark(size, stops=GOLD, waves_highlight=False):
    s = size * SS
    grad = gradient_image(s, stops)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mark_mask(s))
    if waves_highlight:
        hi = Image.new("RGBA", (s, s), HIGHLIGHT + (255,))
        img.paste(hi, (0, 0), waves_mask(s))
    return img.resize((size, size), Image.LANCZOS)


def render_tile(size, stops=GOLD, waves_highlight=False):
    s = size * SS
    tile = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    grad = Image.new("RGBA", (s, s))
    px = grad.load()
    for y in range(s):
        t = y / s
        col = tuple(int(TILE_TOP[i] + (TILE_BOT[i] - TILE_TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(s):
            px[x, y] = col
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=255)
    tile.paste(grad, (0, 0), mask)
    # the mark occupies the tile with margin, as in the concept
    inner = int(s * 0.86)
    mark = render_mark(inner, stops, waves_highlight)
    tile.paste(mark, ((s - inner) // 2, (s - inner) // 2), mark)
    return tile.resize((size, size), Image.LANCZOS)


def write_ico(path, images):
    entries, offset, blobs = [], 6 + 16 * len(images), []
    for im in images:
        buf = io.BytesIO()
        im.save(buf, format="PNG")
        data = buf.getvalue()
        blobs.append(data)
        entries.append(struct.pack("<BBBBHHII",
                                   im.width if im.width < 256 else 0,
                                   im.height if im.height < 256 else 0,
                                   0, 0, 1, 32, len(data), offset))
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(images)))
        for e in entries: f.write(e)
        for b in blobs: f.write(b)


def font(size):
    path = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
    return ImageFont.truetype(path, size) if os.path.exists(path) else ImageFont.load_default()


def draw_lockup(variant):
    mark = render_mark(256)
    text_color = (245, 240, 230) if variant == "dark" else (20, 20, 20)
    f = font(150)
    probe = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    bbox = probe.textbbox((0, 0), "EsoAgent", font=f)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    img = Image.new("RGBA", (256 + 40 + tw, 256), (0, 0, 0, 0))
    img.paste(mark, (0, 0), mark)
    d = ImageDraw.Draw(img)
    d.text((256 + 40 - bbox[0], (256 - th) // 2 - bbox[1]), "EsoAgent", font=f, fill=text_color + (255,))
    return img


# ---------------- SVG masters (same coordinates) ----------------

def _pts(poly):
    return " ".join(f"{x},{y}" for x, y in poly)


def _wave_paths(cx, cy):
    out = []
    for r in WAVE_RADII:
        a0 = math.radians(-WAVE_HALF_ANGLE)
        a1 = math.radians(WAVE_HALF_ANGLE)
        x0, y0 = cx + r * math.cos(a0), cy + r * math.sin(a0)
        x1, y1 = cx + r * math.cos(a1), cy + r * math.sin(a1)
        out.append(f"M {x0:.1f},{y0:.1f} A {r},{r} 0 0 1 {x1:.1f},{y1:.1f}")
    return out


def mark_svg(tile=False):
    cx, cy = WAVE_CENTER
    polys = "\n  ".join(f'<polygon points="{_pts(p)}"/>' for p in BLADES + [FIN])
    waves = "\n  ".join(f'<path d="{d}" fill="none" stroke-width="{WAVE_WIDTH}" stroke-linecap="round"/>' for d in _wave_paths(cx, cy))
    tile_rect = ''
    if tile:
        tile_rect = ('<defs><linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">'
                     '<stop offset="0" stop-color="#1C1C1C"/><stop offset="1" stop-color="#111111"/>'
                     '</linearGradient></defs>\n  <rect x="1" y="1" width="98" height="98" rx="22" fill="url(#tile)"/>\n  '
                     '<g transform="translate(7,7) scale(0.86)">')
    grad = ('<defs><linearGradient id="gold" x1="0" y1="0" x2="1" y2="1">'
            '<stop offset="0" stop-color="#F7DC8F"/><stop offset="0.45" stop-color="#E9BE57"/>'
            '<stop offset="0.75" stop-color="#C9962E"/><stop offset="1" stop-color="#9A7418"/>'
            '</linearGradient></defs>')
    body = f'{grad}\n  <g fill="url(#gold)">\n  {polys}\n  </g>\n  <g stroke="url(#gold)">\n  {waves}\n  </g>'
    if tile:
        body = tile_rect + body + "\n  </g>"
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">\n  <!-- EsoAgent golden wing — owner pick 2026-11-18. Generated by scripts/render-brand.py; edit geometry there. -->\n  {body}\n</svg>\n'


def lockup_svg(variant):
    text = "#F5F0E6" if variant == "dark" else "#141414"
    mark = mark_svg().replace('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">', '<g>').replace('</svg>', '</g>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 460 100">\n  {mark}\n'
            f'  <text x="118" y="65" font-family="\'Segoe UI\', Inter, Arial, sans-serif" font-size="42" font-weight="700" fill="{text}">EsoAgent</text>\n</svg>\n')


def main():
    for size in (16, 24, 32, 48, 64, 128, 256, 512):
        render_mark(size).save(os.path.join(OUT, f"mark-{size}.png"))
        render_tile(size).save(os.path.join(OUT, f"app-icon-{size}.png"))
    render_tile(256).save(os.path.join(OUT, "app-icon.png"))
    # THE application icon: every Windows size incl. 20 (taskbar small),
    # all entries PNG-compressed (write_ico stores PNG data throughout).
    app_ico = os.path.join(ROOT, "assets", "brand", "app.ico")
    write_ico(app_ico, [render_tile(s) for s in (16, 20, 24, 32, 48, 64, 128, 256)])
    app_assets_dir = os.path.join(ROOT, "src", "EsoAgent.App", "Assets")
    os.makedirs(app_assets_dir, exist_ok=True)
    shutil.copyfile(app_ico, os.path.join(app_assets_dir, "app.ico"))
    for variant in ("dark", "light"):
        draw_lockup(variant).save(os.path.join(OUT, f"logo-lockup-{variant}.png"))
    states = {
        "online": dict(stops=GOLD),
        "offline": dict(stops=GREY),
        "controlled": dict(stops=GOLD, waves_highlight=True),
        "locked": dict(stops=SLATE),
    }
    app_assets = os.path.join(ROOT, "src", "EsoAgent.App", "Assets")
    tray_assets = os.path.join(ROOT, "src", "EsoAgent.Tray", "Assets")
    os.makedirs(tray_assets, exist_ok=True)
    for state, kw in states.items():
        render_tile(48, **kw).save(os.path.join(OUT, f"tray-{state}-48.png"))
        write_ico(os.path.join(OUT, f"tray-{state}.ico"), [render_tile(s, **kw) for s in (16, 24, 32, 48)])
        write_ico(os.path.join(tray_assets, f"tray-{state}.ico"), [render_tile(s, **kw) for s in (16, 24, 32, 48)])
    # controlled pulse frame B: waves at normal gold (frame A has them lit)
    write_ico(os.path.join(OUT, "tray-controlled-pulse.ico"), [render_tile(s) for s in (16, 24, 32, 48)])
    write_ico(os.path.join(tray_assets, "tray-controlled-pulse.ico"), [render_tile(s) for s in (16, 24, 32, 48)])
    for state in ("online", "offline", "controlled"):
        render_tile(48, **states[state]).save(os.path.join(app_assets, f"tray-{state}.png"))
    with open(os.path.join(SVG_OUT, "mark.svg"), "w") as f: f.write(mark_svg())
    with open(os.path.join(SVG_OUT, "mark-tile.svg"), "w") as f: f.write(mark_svg(tile=True))
    for variant in ("dark", "light"):
        with open(os.path.join(SVG_OUT, f"logo-lockup-{variant}.svg"), "w") as f: f.write(lockup_svg(variant))
    print("golden-wing assets rendered:", OUT, "+ svg masters")


if __name__ == "__main__":
    main()
