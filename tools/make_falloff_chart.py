"""Damage-vs-distance chart for every explosive (Workshop description image).

Values are the defaults in Plugin.cs - keep ITEMS in step when a default changes. Every blast and
shrapnel item uses the same linear falloff: damage(d) = MaxDamage * clamp01(1 - d / Radius)
(ExplosionDamage.cs / Shrapnel.cs). The Molotov is a fire pool (damage per tick, not falloff), so it
is a note rather than a line.

Usage: make_falloff_chart.py <outDir>
"""
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = sys.argv[1] if len(sys.argv) > 1 else "."

BG = "#12110d"
GRID = "#2c2a24"
INK = "#e8e2d6"
DIM = "#8f897e"

# (name, max damage at 0 m, radius m, colour, note)
ITEMS = [
    ("Land mine", 3500, 10, "#e0b43c", ""),
    ("Grenade", 3000, 15, "#4a8fe0", ""),
    ("Contact grenade", 2400, 12, "#9b6be0", ""),
    ("A-P charge", 1800, 25, "#d9534f", "60° fan, front only"),
    ("Improvised mine", 1000, 12, "#6cbf6c", "shrapnel"),
    ("Nail bomb", 889, 20, "#e0703a", "shrapnel"),
    ("Demolition charge", 300, 8, "#c8c8c8", "+2,000 to walls"),
]


def main():
    plt.rcParams["font.family"] = "Segoe UI"
    fig = plt.figure(figsize=(12.8, 7.2), dpi=100, facecolor=BG)
    ax = fig.add_axes([0.135, 0.17, 0.56, 0.58], facecolor=BG)

    xmax = 26
    for name, dmg, radius, col, note in ITEMS:
        ax.plot([0, radius, xmax], [dmg, 0, 0], color=col, lw=2.2, solid_capstyle="round", zorder=3)
        ax.plot([0], [dmg], "o", color=col, ms=6, zorder=4)

    ax.set_xlim(0, xmax)
    ax.set_ylim(0, 3800)
    ax.set_xticks([0, 5, 10, 15, 20, 25])
    ax.set_xticklabels([f"{v}m" for v in [0, 5, 10, 15, 20, 25]])
    ax.set_yticks(range(0, 3501, 500))
    ax.set_yticklabels([f"{v:,}" for v in range(0, 3501, 500)])
    ax.tick_params(colors=DIM, labelsize=12, length=0, pad=10)
    for lab in ax.get_xticklabels() + ax.get_yticklabels():
        lab.set_fontfamily("Consolas")
    ax.grid(axis="y", color=GRID, lw=1)
    for s in ax.spines.values():
        s.set_visible(False)
    ax.axhline(0, color="#4a473f", lw=1.2)

    # Header
    fig.text(0.135, 0.955, "HUMANHOSTEXPLOSIVESMOD · ORDNANCE DATA", color=DIM, fontsize=9, fontfamily="Consolas")
    fig.text(0.135, 0.895, "DAMAGE VS. DISTANCE", color=INK, fontsize=22, fontweight="bold", fontfamily="Bahnschrift")
    fig.text(0.135, 0.815,
             "Every explosive deals linear falloff damage - full force at the blast centre, tapering to\n"
             "zero at the edge of its radius. Default values, fully exposed target.",
             color=INK, fontsize=10.5, linespacing=1.5)

    # Legend panel on the right, biggest first
    x0, y = 0.735, 0.745
    fig.text(x0, y + 0.04, "AT THE CENTRE / RADIUS", color=DIM, fontsize=9, fontfamily="Consolas")
    for name, dmg, radius, col, note in ITEMS:
        fig.patches.append(plt.Rectangle((x0, y - 0.004), 0.018, 0.006, transform=fig.transFigure, color=col))
        fig.text(x0 + 0.026, y, name, color=col, fontsize=12, fontweight="bold", va="center")
        fig.text(x0 + 0.026, y - 0.032, f"{dmg:,} dmg / {radius:g} m" + (f"  ·  {note}" if note else ""),
                 color=INK, fontsize=9.5, va="center", fontfamily="Consolas")
        y -= 0.078

    # Molotov note (not a falloff weapon)
    fig.text(x0, y - 0.005, "Molotov", color="#ff9a3c", fontsize=12, fontweight="bold", va="center")
    fig.text(x0, y - 0.037, "4.5 m fire pool, 20 s", color=INK, fontsize=9.5, va="center", fontfamily="Consolas")
    fig.text(x0, y - 0.062, "40/s in the fire, then burning", color=INK, fontsize=9.5, va="center", fontfamily="Consolas")
    fig.text(x0, y - 0.087, "24/s for 10 s - spreads", color=INK, fontsize=9.5, va="center", fontfamily="Consolas")

    fig.text(0.135, 0.045,
             "damage(d) = MaxDamage × clamp01(1 − d / Radius). Cover scales it down by the exposed fraction of the target. "
             "Shrapnel items fire per-target fragments,\n"
             "so cover matters more. Self damage follows the same curve × SelfDamageMultiplier. "
             "All values are adjustable in ModMenu / the Mod Manager.",
             color=DIM, fontsize=8.5, linespacing=1.6)

    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, "workshop_falloff_chart.png")
    fig.savefig(path, facecolor=BG)
    print(path)


if __name__ == "__main__":
    main()
