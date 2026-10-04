# Explosives 0.3.0 — working notes (2026-09-25)

State at the end of the 2026-09-25 session. Everything below is built, deployed to the dev install,
staged in `Workshop/upload/HumanHostExplosives` (≈17 MB) and committed. **Not uploaded** — the user
uploads. Parts are play-tested; see "Verified" vs "Unverified".

## Items (8)

| Item | Kind | Bench / tab | Recipe |
|---|---|---|---|
| Grenade | thrown, 3 s fuse | GunWorkbench / Ammo | 3 Iron Ingot, 10 Gun Powder, 8 Spring, 10 Scrap Brass |
| Contact grenade | thrown, impact fuse | GunWorkbench / Ammo | 3 Iron Ingot, 8 Gun Powder, 10 Spring, 15 Scrap Brass |
| Land mine | placed, pressure-release | GunWorkbench / Ammo | 4 Steel Ingot, 15 Gun Powder, 15 Spring, 2 Electrical Wire |
| Demolition charge | placed, remote | GunWorkbench / Ammo | 20 Gun Powder, 15 Scrap Plastic, 3 Electrical Wire, 10 Duct Tape |
| A-P charge (claymore) | placed, remote, directional | GunWorkbench / Ammo | 2 Steel Ingot, 12 Gun Powder, 40 Nails, 3 Electrical Wire, 10 Scrap Plastic |
| Molotov | thrown, fire | HandMade / Melee | 2 Alcohol, 10 Torn Cloth, 10 Tree Sap |
| Nail bomb | thrown, shrapnel | HandMade / Melee | 25 Nails, 20 any ammo, 10 Duct Tape, 10 Scrap Iron |
| Improvised mine | placed, pressure-release | HandMade / Trap | 15 Scrap Iron, 40 Nails, 25 any ammo, 10 Spring |

Recipe rules the user set: counts scaled to vanilla (1–2 of common loot is "nothing"); hand-made items
must not need Nitrate/Gun Powder (late-game Biochemical bench) — they use 20–25 rounds of ANY ammo
(`Registry/AnyAmmoMaterial.cs`, transpiler over the Craft_Items GUID compares).

## Systems added this version

- **Fire** (`FirePool`, `FireFx`, `BurnManager`, `FireDamage`): patchy spilled-fuel pool with a taper
  to the rim, fringe, core, smoke, ignition flare, uneven burn-out; burning characters get 7 bone-tracked
  flames, smoke, light (nearest 4), scorch tint (MaterialPropertyBlock), panic run; **spreads between
  characters** (FireSpreadChance 0.35 / radius 1.3 m / spread burn ×0.7, never re-lights a live burn).
- **Mines / charges** (`PlacedMine`, `MinePlacer`, `MinePersistence`, `RemoteCharges`): kneel to place
  (crouch + `_RightHandPutItemAnim`), beep, pickup with interact key, saved with the game
  (`Save/Auto_Save/HHX_Mines.txt`, ID+seed stamped), shootable (Scene-layer collider +
  `Directly_Interact` prefix), chain detonation. Remote charges: HUD widget middle-left, B detonates all
  armed in range (150 m).
- **Demolition**: breaks every block it touches completely; big walls take all remaining HP; zone cells
  destroyed outright. Always logs a `[Demolition]` summary per blast.
- **Loot**: military containers only + `LootChance` 0.3 (`Registry/LootRarity.cs` transpiles the pick).
- **Sounds**: 40 picked Stable-Audio takes in `Assets/Sounds/<cat>/` (grenade 8, heavy 5, ap 7,
  shrapnel 8 [improvised items only], spoon 5, beep 3, molotov 4); random take per play, pitch jitter,
  distance delay/falloff; heavy/ap/molotov loudness baked in (-8/-9/-12 dB). Regenerate:
  `tools/gen_explosion_sfx.py` (+ `index.html` picker via launch.json `sfx-preview`), install:
  `tools/install_sfx.py tools/sfx_picks.txt`.
- **Config**: `ConfigVersion` (now 4) replaces the MigratedDefaults030 flag; bump it and add the old
  value to `BindMigrated(... oldDefaults)` whenever a default changes.

## Verified in-game (user)
Recipes (after migration fix), models/icons look right, remote charges detonate, sounds picked by ear,
fire pool previously looked like a "ring" (fixed since — re-check).

## Unverified / open — check next session
1. **Demolition collapse** — last report: "still isn't collapsing building structures". Build `f67af71`
   added big-wall/zone overkill + a `[Demolition] N block(s) (M world-building): X broken, Y refused …`
   log line. Read those lines first. Remember vanilla only collapses what loses its path to ground; a
   charge on one wall section won't drop a building standing on its other walls.
2. Fire rework (taper/patches/smoke), burning-zombie visuals, scorch, panic, spread — never seen in-game.
3. Mine pickup / save-reload / shooting / kneel animation look.
4. Held-model offsets for Molotov (0.25 m bottle), mines and charges — all start at the grenade's values.
5. Loot rarity feel (0.3), fire balance, demo damage (2000 block / 300 creature), sound levels after boost.
6. Weapon Paint freeze (RMB + pause menu dead after spraying) — separate mod, not investigated.

## Next feature: a PERK / SKILL for explosives (user, 2026-09-25 — after tuning)

Not started. Research first: the game's talent/skill system — `Char_Skills` / `_charSkills` (fields
like `_woodJackYield_Factor`, `_lootCountRateBoost`, `_fighterHitDown_Add`), talent data
(`G_Save._config._InitTalentEngName`, `_InitTalentLv`), the Talent UI (Player_HotKeys `Talent`). Find
how a vanilla perk is defined and whether a mod can add one (likely a ScriptableObject/talent table
entry + reading our own factor at use sites), or fall back to a mod-side perk with its own unlock.

Candidate effects (pick with the user):
- Demolitions: blast radius / structure damage +X%, faster arming, longer detonate range.
- Safety: reduced self-damage from own explosives; immune to own mines; slower fuse cook-off.
- Crafting: CraftCount +1 for explosives, fewer materials, shorter craft time.
- Throwing: faster charge, longer throw.
- Pyro: longer fire, bigger pool, fire-spread chance up; fire resistance for the player.
Every numeric effect should be a multiplier read live at the use site (Blast params, BurnManager,
recipe injection), so the perk only has to set factors.

## Pointers
- Cross-mod lessons: `HumanHost Audit/MOD_CONVENTIONS.md` §54 (fire kit), §55 (save/shoot/loot odds),
  §59 (per-zombie effects), plus the any-of recipe material note after §54.
- Planning doc from the start of the session: in this conversation's Plan agent output (not saved);
  README "0.3.0 items" section summarises the design.
