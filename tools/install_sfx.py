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

# Loudness targets: RMS (dBFS) of the first 0.4 s - the punch you actually hear. In-game these already
# play at the AudioSource maximum, so louder has to be baked into the file. Reached with gain into a
# soft (tanh) limiter, so peaks never clip. Categories not listed are left as generated.
# (User, 2026-09-25: heavy, ap and molotov needed more volume; grenade at -12.5 is the reference.)
LOUDNESS = {"heavy": -8.0, "ap": -9.0, "molotov": -12.0}


def loudness(x, sr):
    n = max(1, int(0.4 * sr))
    return 20 * np.log10(np.sqrt(np.mean(x[:n] ** 2)) + 1e-9)


def make_louder(x, sr, target_db, ceiling=0.97):
    """Binary-search a gain so the limited signal hits target_db."""
    lo, hi = 1.0, 40.0
    y = x
    for _ in range(30):
        g = (lo + hi) / 2
        y = ceiling * np.tanh(g * x / ceiling)
        if loudness(y, sr) < target_db:
            lo = g
        else:
            hi = g
    return y.astype(np.float32)

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
    cat = p.split("/")[0]
    if cat in LOUDNESS and loudness(data, sr) < LOUDNESS[cat]:
        data = make_louder(data, sr, LOUDNESS[cat])
    out = os.path.join(DEST, p)
    sf.write(out, data, sr, subtype="PCM_16")
    print(f"{p}: {before:.2f}s -> {len(data) / sr:.2f}s, {loudness(data, sr):.1f} dB")
print("installed", len(picks), "take(s) in", ", ".join(cats))
