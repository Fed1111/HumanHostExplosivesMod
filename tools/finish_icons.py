"""Turns tools/_render/<stem>_raw.png (1024, transparent, from make_models.py) into the shipped
256x256 icon: crop to the alpha bounding box, pad to a centred square so the subject spans ~88%
of the canvas, LANCZOS down (MOD_CONVENTIONS §47 - render big, downsample outside Unity).

Usage: python tools/finish_icons.py
"""
import os
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW = os.path.join(REPO, "tools", "_render")
ITEMS = {
    "molotov": "Molotov",
    "contact_grenade": "ContactGrenade",
    "mine": "Mine",
    "improvised_mine": "ImprovisedMine",
}
SIZE = 256
FILL = 0.88

for stem, folder in ITEMS.items():
    src = os.path.join(RAW, stem + "_raw.png")
    im = Image.open(src).convert("RGBA")
    box = im.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
    im = im.crop(box)
    side = int(max(im.size) / FILL)
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(im, ((side - im.width) // 2, (side - im.height) // 2))
    out = os.path.join(REPO, "Assets", folder, stem + "_icon.png")
    canvas.resize((SIZE, SIZE), Image.LANCZOS).save(out)
    print("icon", out)
