"""Generates candidate explosion sounds with the local Stable Audio 3 Small-SFX model, and a preview
page to audition and pick them.

Run with the stable-audio-3 venv (it has stable_audio_tools and the cached model):
    C:/ai-workspace/stable-audio-3/env/Scripts/python.exe tools/gen_explosion_sfx.py [--per 8] [--only grenade,heavy]

Writes tools/_sfx/<category>/<category>_NN.wav (44.1 kHz mono PCM16: leading silence trimmed, peak
normalised, faded tail) and tools/_sfx/index.html. Picked files go to Assets/Sounds/<category>/ -
the mod loads every .wav there and picks one at random per blast (ExplosionSound).

Uses the GPU when it has room (>= 3.5 GB free), else the CPU (slow - minutes per clip).
"""
import argparse
import html
import os
import sys
import time

import numpy as np
import soundfile as sf
import torch

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "tools", "_sfx")
NEG = "music, melody, speech, voice, talking, beeping, low quality, muffled, distorted clipping"
# Extra negatives per category (appended to NEG).
_NO_SURFACE = (", debris, dirt, rubble, gravel, stones, glass, falling objects, clatter, crumbling, "
               "building collapse, metal clanking, long rumble, thunder")
NEG_EXTRA = {"grenade": _NO_SURFACE, "heavy": _NO_SURFACE, "ap": _NO_SURFACE}
# Low-cut (Hz) per category: "too low-endy" - the sub rumble is what makes a close blast sound like
# a distant collapse.
HIGHPASS = {"heavy": 90.0, "ap": 80.0, "grenade": 50.0}
# Trim the tail at the last sample louder than this (dB below peak), then a short fade.
TAIL_DB = -45.0

# category -> (seconds, [prompts]); seeds cycle through the prompts so each category gets variety
# in both wording and seed.
CATEGORIES = {
    # The explosion ITSELF, nothing else: no dirt, debris, rubble or glass (the blast lands on anything,
    # and a surface-specific tail sounds wrong everywhere else). Heard from 10-20 m.
    "grenade": (4, [
        "hand grenade explosion nearby, sharp powerful blast, short natural echo, clean isolated explosion sound",
        "frag grenade detonation 15 meters away, loud punchy bang, brief reverb tail",
        "close explosion, crisp loud boom with a quick decay, outdoors, isolated sound effect",
    ]),
    "heavy": (4, [
        "big explosion nearby, very loud powerful blast, punchy, short echo, clean isolated explosion sound",
        "large explosive detonation 15 meters away, heavy hard boom, brief natural reverb",
        "powerful close explosion, loud deep crack and boom, quick decay, isolated sound effect",
    ]),
    "ap": (3, [
        "very sharp loud explosion close by, violent crack, short echo, clean isolated explosion sound",
        "directional mine detonation 15 meters away, sharp punchy blast, brief tail, isolated sound effect",
    ]),
    "shrapnel": (4, [
        "pipe bomb explosion, sharp crack with metal shrapnel scattering and pinging, outdoors",
        "improvised nail bomb blast, bright bang with metal fragments hitting surfaces",
        "claymore mine explosion, sharp directional blast, fragments whizzing and ricocheting",
    ]),
    "spoon": (2, [
        "grenade pin pulled with a metallic click, then the safety lever spoon flicking off and clinking on the ground",
        "small metal spring lever snapping off a grenade, light metallic ping and a short tinkle as it lands",
        "metal pin and ring pulled from a grenade, quick metallic snap and clatter",
    ]),
    "beep": (1, [
        "short electronic double beep from a small device arming, clean piezo tone",
        "single short high electronic beep, device armed, clean",
    ]),
    "molotov": (3, [
        "glass bottle smashing on the ground and petrol igniting with a loud whoosh of fire",
        "molotov cocktail shattering, fuel bursting into flames, crackling fire",
    ]),
}


def pick_device():
    if torch.cuda.is_available():
        free, total = torch.cuda.mem_get_info()
        print(f"[sfx] GPU free {free / 2**30:.1f} of {total / 2**30:.1f} GB")
        if free >= 3.5 * 2**30:
            return "cuda"
    print("[sfx] using CPU (GPU busy or absent) - this is slow")
    return "cpu"


def trim_tail(mono, sr):
    """Cut right after the last sample above TAIL_DB (relative to peak), with a 30 ms fade."""
    peak = np.max(np.abs(mono)) or 1.0
    loud = np.nonzero(np.abs(mono) > peak * 10 ** (TAIL_DB / 20))[0]
    end = int(loud[-1]) + 1 if len(loud) else len(mono)
    fade = min(end, int(0.03 * sr))
    mono = mono[:end].copy()
    mono[end - fade:] *= np.linspace(1.0, 0.0, fade)
    return mono


def highpass(mono, sr, hz):
    import torchaudio
    t = torch.from_numpy(mono.astype(np.float32))[None]
    for _ in range(2):  # two biquads = 24 dB/oct
        t = torchaudio.functional.highpass_biquad(t, sr, hz)
    return t[0].numpy()


def clean(audio, sr, seconds, cat=""):
    """(C, N) float -> mono, low-cut, trimmed to the attack and after the last audible sound, normalised."""
    mono = audio.mean(axis=0)
    mono = mono[: int(seconds * sr)]
    if cat in HIGHPASS:
        mono = highpass(mono, sr, HIGHPASS[cat])
    peak = np.max(np.abs(mono)) or 1.0
    thresh = 0.04 * peak
    start = int(np.argmax(np.abs(mono) > thresh))
    start = max(0, start - int(0.004 * sr))            # keep 4 ms of lead-in
    mono = mono[start:]
    mono = mono / (np.max(np.abs(mono)) or 1.0) * 0.95
    return trim_tail(mono, sr)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--per", type=int, default=8, help="candidates per category")
    ap.add_argument("--only", default=",".join(CATEGORIES))
    ap.add_argument("--steps", type=int, default=8)
    ap.add_argument("--start", type=int, default=1, help="first file number (keep earlier picks)")
    args = ap.parse_args()

    from stable_audio_tools import get_pretrained_model
    from stable_audio_tools.inference.generation import generate_diffusion_cond_inpaint

    device = pick_device()
    t0 = time.time()
    model, config = get_pretrained_model("stabilityai/stable-audio-3-small-sfx")
    sr = int(config["sample_rate"])
    sample_size = int(config["sample_size"])
    dtype = torch.float16 if device == "cuda" else torch.float32
    model = model.to(device).to(dtype)
    print(f"[sfx] model ready in {time.time() - t0:.1f}s (sr={sr})")

    for cat in args.only.split(","):
        seconds, prompts = CATEGORIES[cat]
        os.makedirs(os.path.join(OUT, cat), exist_ok=True)
        for i in range(args.start - 1, args.start - 1 + args.per):
            prompt = prompts[i % len(prompts)]
            seed = 1000 + i * 97 + hash(cat) % 1000
            t1 = time.time()
            with torch.no_grad():
                out = generate_diffusion_cond_inpaint(
                    model, steps=args.steps, cfg_scale=1.0,
                    conditioning=[{"prompt": prompt, "seconds_total": seconds}],
                    negative_conditioning=[{"prompt": NEG + NEG_EXTRA.get(cat, ""), "seconds_total": seconds}],
                    sample_size=sample_size, sampler_type="pingpong", seed=seed, device=device,
                    sigma_max=1.0, apg_scale=1.0, duration_padding_sec=6.0)
            audio = out[0].float().clamp(-1, 1).cpu().numpy()
            mono = clean(audio, sr, seconds, cat)
            path = os.path.join(OUT, cat, f"{cat}_{i + 1:02d}.wav")
            sf.write(path, mono, sr, subtype="PCM_16")
            with open(path + ".txt", "w", encoding="utf-8") as f:
                f.write(f"{prompt}\nseed {seed}\n")
            print(f"[sfx] {path} ({time.time() - t1:.1f}s)", flush=True)

    write_index()
    print("[sfx] done ->", os.path.join(OUT, "index.html"))


def write_index():
    # Pre-tick what is already installed (tools/sfx_picks.txt), so "Copy picks" gives the full list.
    picked = set()
    try:
        picked = {l.strip() for l in open(os.path.join(REPO, "tools", "sfx_picks.txt"), encoding="utf-8") if l.strip()}
    except OSError:
        pass
    rows = []
    for cat in CATEGORIES:
        d = os.path.join(OUT, cat)
        if not os.path.isdir(d):
            continue
        files = sorted(f for f in os.listdir(d) if f.endswith(".wav"))
        cards = []
        for f in files:
            prompt = ""
            try:
                prompt = open(os.path.join(d, f + ".txt"), encoding="utf-8").readline().strip()
            except OSError:
                pass
            cards.append(f'<label class="card"><input type="checkbox" value="{cat}/{f}"{" checked" if f"{cat}/{f}" in picked else ""}> <b>{f[:-4]}</b>'
                         f'<audio controls preload="metadata" src="{cat}/{f}"></audio><small>{html.escape(prompt)}</small></label>')
        rows.append(f'<h2>{cat} <button onclick="chain(\'{cat}\')">play 3 in a row (chain test)</button></h2>'
                    f'<div class="grid">{"".join(cards)}</div>')
    page = """<!doctype html><meta charset="utf-8"><title>Explosion SFX picks</title>
<style>
body{font:14px system-ui;background:#1b1a18;color:#e8e2d6;margin:24px}
h2{margin-top:28px;text-transform:uppercase;letter-spacing:.05em;color:#d68a2e}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:10px}
.card{background:#262420;border:1px solid #444;border-radius:8px;padding:10px;display:flex;flex-direction:column;gap:6px}
.card:has(input:checked){border-color:#d68a2e;background:#312a1f}
audio{width:100%} small{color:#9a948a} button{background:#3a352c;color:#e8e2d6;border:1px solid #666;border-radius:5px;padding:3px 9px;cursor:pointer}
#out{width:100%;height:70px;margin-top:10px}
</style>
<h1>Explosion sounds - tick the ones you want</h1>
<p>The mod plays a random pick per blast, so choose several per category. "Chain test" plays 3 of the
ticked ones (or any, if none ticked) overlapping, like several charges going off.</p>
""" + "".join(rows) + """
<h2>Your picks</h2><button onclick="copyPicks()">Copy picks</button><textarea id="out" readonly></textarea>
<script>
function ticked(cat){return [...document.querySelectorAll('input:checked')].map(i=>i.value).filter(v=>!cat||v.startsWith(cat+'/'))}
function chain(cat){let l=ticked(cat);if(!l.length)l=[...document.querySelectorAll('input')].map(i=>i.value).filter(v=>v.startsWith(cat+'/'));
 for(let k=0;k<3;k++){const f=l[Math.floor(Math.random()*l.length)];setTimeout(()=>new Audio(f).play(),k*(160+Math.random()*140))}}
function copyPicks(){const t=ticked().join('\\n');document.getElementById('out').value=t;navigator.clipboard&&navigator.clipboard.writeText(t)}
</script>"""
    with open(os.path.join(OUT, "index.html"), "w", encoding="utf-8") as f:
        f.write(page)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--index-only":
        write_index()
    else:
        main()
