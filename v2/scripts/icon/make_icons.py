"""Render every Vault Movies icon from vault-icon.svg (headless Edge + Pillow).

Usage: python make_icons.py <dir-with-vault-icon.svg> <v2-dir>
Writes the TV icons, the Expo icon sources and the generated Android mipmaps.
Intermediate SVG/PNG renders land in the first directory; use a scratch copy.
"""
import os, subprocess, sys
from PIL import Image, ImageDraw

ICON, V2 = sys.argv[1], sys.argv[2]
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
src = open(os.path.join(ICON, "vault-icon.svg"), encoding="utf-8").read()

head, rest = src.split("  <!-- tile -->", 1)
tile_rect, art = rest.split("  <!-- vault frame -->", 1)
art = "  <!-- vault frame -->" + art.replace("</svg>", "")
defs = head  # includes <svg ...> and <defs>

def svg(body):
    return defs + body + "</svg>\n"

# Artwork spans roughly x 110..980, y 184..856 (centre ~545,520). Adaptive
# icons are masked to anything from a circle to a squircle; keep the art inside
# the 66/108 safe zone, i.e. about 61% of the canvas.
scale = 0.76
cx, cy = 545, 520
fg_art = f'<g transform="translate(512 512) scale({scale}) translate({-cx} {-cy})">{art}</g>'

variants = {
    "full": svg(tile_rect + art),
    "background": svg('<rect width="1024" height="1024" fill="url(#tile)"/>'),
    "foreground": svg(fg_art),
}

def render(name, markup):
    path = os.path.join(ICON, name + ".svg")
    with open(path, "w", encoding="utf-8") as f:
        f.write(markup)
    out = os.path.join(ICON, name + ".png")
    url = "file:///" + path.replace("\\", "/")
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                    "--default-background-color=00000000", "--window-size=1024,1024",
                    f"--screenshot={out}", url], check=True, capture_output=True)
    return Image.open(out).convert("RGBA")

img = {k: render(k, v) for k, v in variants.items()}

# Monochrome (Android 13 themed icons) uses alpha only: keep the light parts
# of the artwork (steel, films, shelves, door), drop the dark ones.
fg = img["foreground"]
mono = Image.new("RGBA", fg.size, (0, 0, 0, 0))
px, mp = fg.load(), mono.load()
for y in range(fg.height):
    for x in range(fg.width):
        r, g, b, a = px[x, y]
        lum = 0.299 * r + 0.587 * g + 0.114 * b
        if a > 0 and lum > 110:
            mp[x, y] = (255, 255, 255, a)
img["monochrome"] = mono

def save(im, path, size, fmt=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im.resize((size, size), Image.LANCZOS).save(path, fmt) if fmt else \
        im.resize((size, size), Image.LANCZOS).save(path)

# --- TV (webOS): 80 px icon, 130 px large icon
save(img["full"], os.path.join(V2, "tv-webos", "icon.png"), 80)
save(img["full"], os.path.join(V2, "tv-webos", "largeIcon.png"), 130)

# --- Expo sources (used whenever the android/ folder is regenerated)
assets = os.path.join(V2, "mobile", "assets")
save(img["full"], os.path.join(assets, "icon.png"), 1024)
save(img["foreground"], os.path.join(assets, "android-icon-foreground.png"), 1024)
save(img["background"], os.path.join(assets, "android-icon-background.png"), 1024)
save(img["monochrome"], os.path.join(assets, "android-icon-monochrome.png"), 1024)

# --- The generated Android resources the build actually uses
res = os.path.join(V2, "mobile", "android", "app", "src", "main", "res")
dens = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}

def round_icon(size):
    # Legacy (pre-Android 8) icons: background + foreground, square and round.
    base = Image.alpha_composite(img["background"], img["foreground"])
    sq = base.resize((size, size), Image.LANCZOS)
    mask = Image.new("L", (size * 4, size * 4), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, size * 4 - 1, size * 4 - 1), fill=255)
    rnd = sq.copy(); rnd.putalpha(mask.resize((size, size), Image.LANCZOS))
    return sq, rnd

for d, k in dens.items():
    folder = os.path.join(res, "mipmap-" + d)
    layer = round(108 * k)
    for name in ("background", "foreground", "monochrome"):
        save(img[name], os.path.join(folder, f"ic_launcher_{name}.webp"), layer, "WEBP")
    legacy = round(48 * k)
    sq, rnd = round_icon(legacy)
    sq.save(os.path.join(folder, "ic_launcher.webp"), "WEBP")
    rnd.save(os.path.join(folder, "ic_launcher_round.webp"), "WEBP")

# Preview sheet: tile, adaptive in a circle mask and a squircle, monochrome.
def masked(shape):
    base = Image.alpha_composite(img["background"], img["foreground"])
    m = Image.new("L", base.size, 0)
    d = ImageDraw.Draw(m)
    (d.ellipse if shape == "circle" else lambda b, fill: d.rounded_rectangle(b, 300, fill=fill))((40, 40, 984, 984), fill=255)
    base.putalpha(m); return base
sheet = Image.new("RGBA", (4 * 300 + 50, 330), (36, 38, 44, 255))
mono_bg = Image.new("RGBA", (1024, 1024), (60, 70, 90, 255)); mono_bg.alpha_composite(img["monochrome"])
for i, im in enumerate([img["full"], masked("circle"), masked("squircle"), mono_bg]):
    t = im.resize((280, 280), Image.LANCZOS); sheet.alpha_composite(t, (10 + i * 300, 25))
sheet.save(os.path.join(ICON, "sheet.png"))
print("ok")
