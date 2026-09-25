"""Installs picked sound takes into Assets/Sounds/<category>/ (what the mod loads - SoundBank).

Usage (stable-audio-3 venv, for numpy/soundfile):
    python tools/install_sfx.py picks.txt

picks.txt holds lines like "grenade/grenade_01.wav" (the preview page's "Copy picks" output). Each
file is trimmed after its last audible sample (gen_explosion_sfx.trim_tail) as it is copied. A
category that appears in the list is REPLACED wholesale; categories not in the list are left alone.
"""
import os
import sys

import numpy as np
import soundfile as sf

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_explosion_sfx import OUT, trim_tail  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEST = os.path.join(REPO, "Assets", "Sounds")

picks = [l.strip().replace("\\", "/") for l in open(sys.argv[1], encoding="utf-8") if l.strip()]
cats = sorted({p.split("/")[0] for p in picks})
for cat in cats:
    d = os.path.join(DEST, cat)
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        if f.endswith(".wav"):
            os.remove(os.path.join(d, f))
for p in picks:
    data, sr = sf.read(os.path.join(OUT, p), dtype="float32")
    if data.ndim > 1:
        data = data.mean(axis=1)
    before = len(data) / sr
    data = trim_tail(data, sr)
    out = os.path.join(DEST, p)
    sf.write(out, data, sr, subtype="PCM_16")
    print(f"{p}: {before:.2f}s -> {len(data) / sr:.2f}s")
print("installed", len(picks), "take(s) in", ", ".join(cats))
