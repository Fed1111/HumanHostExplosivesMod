"""Offline sanity checks for every shipped explosive's art - run before packaging.

A registered item whose art is missing or broken is worse than no item (the invented GUID never
resolves and the game shows its install-corrupted dialog), so this fails loudly.

Usage: python tools/check_assets.py [assetsDir]
"""
import os
import sys
from PIL import Image, ImageStat

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "Assets")

# folder -> (obj path relative to ASSETS, texture, icon)
ITEMS = {
    "Grenade": ("Grenade/grenade.obj", "Grenade/grenade.png", "Grenade/grenade_icon.png"),
    "Nailbomb": ("Nailbomb/nailbomb.obj", "Nailbomb/nailbomb.png", "Nailbomb/nailbomb_icon.png"),
    "Molotov": ("Molotov/molotov.obj", "Molotov/molotov.png", "Molotov/molotov_icon.png"),
    "ContactGrenade": ("Grenade/grenade.obj", "ContactGrenade/contact_grenade.png", "ContactGrenade/contact_grenade_icon.png"),
    "Mine": ("Mine/mine.obj", "Mine/mine.png", "Mine/mine_icon.png"),
    "ImprovisedMine": ("ImprovisedMine/improvised_mine.obj", "ImprovisedMine/improvised_mine.png", "ImprovisedMine/improvised_mine_icon.png"),
}

errors = []


def err(item, msg):
    errors.append(f"{item}: {msg}")


def check_obj(item, path):
    v = vt = f = 0
    lo = [1e9] * 3
    hi = [-1e9] * 3
    for line in open(path, encoding="utf-8", errors="replace"):
        parts = line.split()
        if not parts or parts[0].startswith("#"):
            continue
        tag = parts[0]
        if tag == "v":
            v += 1
            for i in range(3):
                c = float(parts[i + 1])
                lo[i] = min(lo[i], c)
                hi[i] = max(hi[i], c)
        elif tag == "vt":
            vt += 1
            u, w = float(parts[1]), float(parts[2])
            if not (-0.001 <= u <= 1.001 and -0.001 <= w <= 1.001):
                err(item, f"UV out of range {u},{w}")
                return
        elif tag == "f":
            f += 1
            if len(parts) != 4:
                err(item, f"non-triangle face: {line.strip()}")
                return
            if any(len(c.split("/")) < 2 or c.split("/")[1] == "" for c in parts[1:]):
                err(item, "face corner without a UV index")
                return
        elif tag not in ("vn", "o", "g", "s", "mtllib", "usemtl", "l"):
            err(item, f"unexpected OBJ line '{tag}'")
    if vt == 0:
        err(item, "no UVs")
    if f == 0 or f > 20000:
        err(item, f"triangle count {f}")
    # Base origin (grenade and every new model) or centred (the shipped nail bomb) - anything
    # else means a wrong up-axis on export.
    if lo[1] < -0.002 and abs(lo[1] + hi[1]) > 0.01:
        err(item, f"origin neither at the base nor centred (Y {lo[1]:.4f}..{hi[1]:.4f})")
    size = [hi[i] - lo[i] for i in range(3)]
    if max(size) > 0.4 or max(size) < 0.05:
        err(item, f"implausible size {size}")
    print(f"  {item}: {f} tris, size {size[0]:.3f} x {size[1]:.3f} x {size[2]:.3f} m, min Y {lo[1]:.4f}")


def check_texture(item, path):
    im = Image.open(path)
    if im.size != (1024, 1024):
        err(item, f"texture size {im.size}")
    if max(ImageStat.Stat(im.convert("RGB")).stddev) < 8:
        err(item, "texture is a flat colour")


def check_icon(item, path):
    im = Image.open(path)
    if im.size != (256, 256) or im.mode != "RGBA":
        err(item, f"icon {im.size} {im.mode}")
        return
    a = im.getchannel("A")
    if any(a.getpixel(p) > 8 for p in ((0, 0), (255, 0), (0, 255), (255, 255))):
        err(item, "icon corners not transparent")
    box = a.point(lambda x: 255 if x > 8 else 0).getbbox()
    if not box or max(box[2] - box[0], box[3] - box[1]) < 0.6 * 256:
        err(item, f"icon subject too small {box}")


sizes = {}
for item, (obj, tex, icon) in ITEMS.items():
    paths = [os.path.join(ASSETS, p) for p in (obj, tex, icon)]
    missing = [p for p in paths if not os.path.isfile(p)]
    if missing:
        err(item, "missing " + ", ".join(missing))
        continue
    check_obj(item, paths[0])
    check_texture(item, paths[1])
    check_icon(item, paths[2])
    sizes.setdefault(os.path.getsize(paths[2]), []).append(item)

for size, items in sizes.items():
    if len(items) > 1:
        err("/".join(items), f"icons have identical file size {size} (flat render? see MOD_CONVENTIONS §47)")

if errors:
    print("FAILED:")
    for e in errors:
        print("  " + e)
    sys.exit(1)
print("all assets OK")
