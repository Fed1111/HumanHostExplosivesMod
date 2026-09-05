# Human Host Explosives — Audit & Handoff

**Date:** 2026-09-05 · **Audited commit:** `b493e2f` · **Plugin version:** 0.1.0

A read-through of the whole mod as it stands, written for whoever picks it up next
(human or agent). It records what actually runs today, what only looks like it runs,
every defect found in the current code, and the shortest path to the stated goal
(throwable explosives that damage things).

---

## 1. How this audit was done

Static review of every file in the repository plus direct inspection of the shipped
asset data. **Nothing was compiled or run.** This checkout has no `dotnet` SDK, and
the project references seven game assemblies (`Assembly-CSharp.dll`, `Item_Info.dll`,
`Camera.dll`, `UI.dll`, …) that only exist inside a Human Host install, so a build is
not reproducible in this environment or on CI.

Findings are split accordingly:

- **Confirmed** — provable from the code or asset bytes alone.
- **Verify in-game** — a real risk in the reading, but the game has to be running to
  settle it.

Everything asserted about the asset was measured, not taken from the README:
`grenade.obj` is 6,000 triangles / 18,000 per-corner vertices / 18,000 UVs / 6,000
face normals, all-triangles, single-space delimited, positive indices only;
`grenade.png` is 1024×1024. Model bounding box (OBJ space, metres):
`x [-0.0369, 0.0368]`, `y [-0.000003, 0.0999]`, `z [-0.0358, 0.0356]` — a ~7.4 cm wide,
10 cm tall grenade whose **origin sits at the base of the model, not its centre**.
That last fact drives finding F2.

---

## 2. What actually works today

| Piece | File | State |
|---|---|---|
| BepInEx 5 plugin scaffold, config binding, Harmony bootstrap | `Plugin.cs` | Working |
| Item pickup diagnostic logger (Harmony postfix) | `ItemPickupLogger.cs` | Working, diagnostic-only |
| Runtime OBJ → `Mesh` loader | `ObjLoader.cs` | Working **for this asset** (see F5) |
| Runtime PNG → `Texture2D` loader | `TextureLoader.cs` | Working |
| Debug grenade throw on **G** | `Plugin.cs:74-110` | Working, with F1–F4 caveats |
| 3-second fuse → log → despawn | `GrenadeProjectile.cs` | Working |
| Auto-deploy of DLL + assets to `BepInEx\plugins\` | `HumanHostExplosives.csproj:71-81` | Working (Windows only) |

**Nothing in this repository does damage to anything.** The mod is, functionally, a
prop spawner with a timer. Every gameplay claim beyond that is still a plan.

---

## 3. Findings

Ordered by how much they cost to hit. File references are `path:line` at `b493e2f`.

### F1 — `ExplosionRadius` is bound, logged, and never used *(Confirmed)*

`Plugin.cs:19,30-34,41`. The config entry is created and printed at startup and read
nowhere else — the only four occurrences in the codebase are the declaration, the
bind, and the startup log line. `README.md:18` tells users it "controls the radius in
meters of the explosion effect", which is not true of any code path that exists.
Users will change the value, see the log echo it, and observe no effect.

Fix with the AoE work (§4) or note it as inert in the README until then. Do not leave
a documented knob that silently does nothing.

### F2 — Collider is centred on the model's base, so the grenade rests half-buried *(Confirmed)*

`Plugin.cs:100-101` adds a `SphereCollider` with `radius = 0.045` and the default
`center = (0,0,0)`. The mesh occupies `y ∈ [0, 0.1]` from that same origin. The
physical sphere therefore spans `y ∈ [-0.045, +0.045]` — it covers the bottom half of
the grenade and 4.5 cm of empty space beneath it. On the ground the visible model sits
sunk to its waist; in flight it pivots about its base rather than its centre of mass.

Fix: `collider.center = new Vector3(0f, 0.05f, 0f);` — or, better, recentre the mesh
once at load time (offset every vertex by `-bounds.center`) so the pivot is right for
every future use, including holding it in hand.

### F3 — Grenade spawns inside the thrower and collides with them *(Verify in-game)*

`Plugin.cs:92` spawns at `camTrans.position + camTrans.forward * 0.5f`. Half a metre
in front of the camera is typically still inside the player's own capsule collider.
Expect the grenade to shove the player, to be shoved back, or to be ejected sideways
on spawn. There is no `Physics.IgnoreCollision` between the projectile and the
thrower and no layer assignment — the new `GameObject` lands on the default layer,
which may or may not be what the game's physics matrix expects for pickups.

Fix: `Physics.IgnoreCollision(collider, playerCollider)` for the thrower's collider
(reachable from the same controller the camera hangs off), and put the projectile on
whatever layer the game uses for dropped items.

### F4 — Fast, small projectile with discrete collision will tunnel *(Verify in-game)*

`Plugin.cs:103-105`: mass 0.4 kg, launch velocity `forward * 8 + up * 2` ≈ 8.2 m/s,
collider diameter 9 cm, default `collisionDetectionMode = Discrete`. At a 0.02 s fixed
step the grenade advances ~16 cm per step — nearly twice its own diameter — so thin
geometry (railings, fences, door frames, floors of a certain thickness) can be passed
straight through.

Fix: `rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;`. Cheap
for one object, and it removes a whole class of "my grenade fell through the world"
reports before they happen.

### F5 — OBJ parser is correct for the shipped asset and fragile for any other *(Confirmed)*

`ObjLoader.cs`. Verified against `grenade.obj`: every face is a triangle, every index
is positive, and no line contains a double space — so the loader handles today's asset
exactly right, including the right-to-left-handed conversion (X negated at line 39,
winding reversed at lines 55-58).

Its assumptions are undocumented preconditions, though, and each one fails hard —
`IndexOutOfRangeException` or `FormatException` inside `Awake` — rather than
degrading:

- `line.Split(' ')` (lines 35, 43, 50) produces empty tokens on any run of spaces.
  Many exporters align columns with multiple spaces or tabs. Use
  `Split((char[])null, StringSplitOptions.RemoveEmptyEntries)`.
- Quads and n-gons are silently truncated to their first three corners (lines 51-58) —
  a mesh that loads and renders with holes, the worst failure mode of the three.
- Negative (relative) indices, legal in OBJ, are treated as literal indices and throw.
- Vertex normals are parsed by nobody: the file's 6,000 `vn` records are dropped and
  `RecalculateNormals()` (line 69) regenerates them. Harmless *here* — with per-corner
  vertices, recalculation reproduces the flat face normals the file already had — but
  any future smooth-shaded asset will come out faceted.

There is no `try`/`catch` anywhere in the load path. See F7 for why that matters more
than it looks.

### F6 — `Shader.Find("Standard")` can return `null`, and `new Material(null)` throws *(Verify in-game)*

`Plugin.cs:59-69`. The HDRP path is guarded; the fallback is not. In an HDRP game the
built-in `Standard` shader is normally *not* in the build at all, so if `HDRP/Lit`
ever fails to resolve, the fallback almost certainly fails too — and the code
dereferences the result immediately.

There is a second, likelier reason for `HDRP/Lit` to come back `null` here: `Awake`
runs at BepInEx chainloader time, before any scene has loaded, and `Shader.Find` only
sees shaders already loaded into memory. Even if the shader exists, this may be too
early to find it.

Fix both at once by deferring material creation to the first spawn (lazy `EnsureMaterial()`
called from `SpawnTestGrenade`), null-checking the fallback, and logging-and-disabling
the debug throw if neither shader resolves.

### F7 — Startup ordering: a Harmony failure takes the grenade down with it *(Confirmed)*

`Plugin.cs:36-39` runs `_harmony.PatchAll()` before `LoadGrenadeAssets()`, and neither
call is wrapped. If the patch in `ItemPickupLogger.cs` fails to apply — a renamed
method after a game update, an overload that makes `nameof` ambiguous, a postfix
parameter name that no longer matches the original's (`DPI`, `charItemIcons`; Harmony
matches these **by name**) — the exception escapes `Awake`, the assets never load, and
a diagnostic-only feature has disabled the actual mod.

Fix: wrap each subsystem in its own `try`/`catch`, log the failure, and keep going.
The pickup logger is scaffolding; it should never be able to break the payload.

### F8 — Pickup logger dereferences an unchecked parameter *(Confirmed)*

`ItemPickupLogger.cs:16-24` null-checks `DPI` and `DPI._ItemInfo` but then reads
`charItemIcons.gameObject.name` with no check on `charItemIcons`. If the game ever
calls `PickItemIn_Bag_Belt` with a null icon holder — an NPC pickup, a scripted
grant — this throws inside a Harmony postfix on every such pickup. Add
`charItemIcons` to the guard, or interpolate defensively.

### F9 — Debug keybind is hardcoded, unconditional, and undocumented as a conflict risk *(Confirmed)*

`Plugin.cs:76`. **G** is not configurable, fires regardless of UI state (chat box,
inventory, pause menu, any text field), and is not checked against the game's own
bindings. `Input.GetKeyDown` is also legacy Input Manager — if the game ships with the
new Input System and legacy input disabled, this throws every frame.

Fix: bind the key through `Config.Bind<KeyboardShortcut>` like the radius, and gate it
on whatever "player has control" state the game exposes.

### F10 — Assigning through `MeshFilter.mesh` instead of `sharedMesh` *(Verify in-game)*

`Plugin.cs:96`. `sharedMesh` is the correct property for a mesh owned by the plugin
and reused across many spawns. The `mesh` property carries per-instance ownership
semantics in Unity, and the failure it invites — the shared mesh being duplicated per
throw, or destroyed along with the first grenade — would show up as a leak or as the
second throw rendering nothing. One-word fix; take it rather than test it.

### F11 — Loaded assets are never released *(Confirmed, low)*

`Plugin.cs:56-69` creates a `Mesh`, a `Texture2D` and a `Material` and holds them for
the process lifetime with no `OnDestroy` cleanup. Correct enough for a plugin that
lives as long as the game, but it means a plugin reload leaks all three. A four-line
`OnDestroy` closes it.

### F12 — Build is unverifiable outside a Windows install *(Confirmed)*

`HumanHostExplosives.csproj:12` hardcodes a Windows `GameDir` and the project binds to
seven assemblies that ship only with the game. Consequences: no CI, no compile check
in any agent session, no way for a contributor without the game to confirm a change
even builds. Every change to date has been merged unbuilt by anything but the author.

Mitigation, in increasing order of effort: make `GameDir` overridable from the
environment or a `Directory.Build.props` (one line, helps immediately); or commit a
stub-reference assembly set generated from the real DLLs so `dotnet build` at least
type-checks on any machine.

### F13 — Deploy target ships the authoring files too *(Confirmed, cosmetic)*

`HumanHostExplosives.csproj:76` globs `Assets\Grenade\**\*.*`, so `preview.png`
(1560×780, 140 KB) and `grenade.mtl` — neither of which the loader reads — are copied
into every install alongside the two files that matter. Narrow the glob to
`grenade.obj;grenade.png`.

---

## 4. The three gaps between here and the goal

The README's plan is sound and matches what the code implies. Restating it here with
what each piece actually needs, since this is the part a new session most needs:

**1. AoE damage — the only thing standing between this and a working grenade.**
Target the game's own pipeline rather than inventing one: `Creature_Mgr.ins.capCol_To_Controller`
for the radius query and `Smash_Fallen_Manager.ins.Minus_Char_HP(...)` for the damage
application (the same path traps and falls already use, so it inherits their handling
of death, ragdolls and networking). `GrenadeProjectile.Detonate()` (`GrenadeProjectile.cs:29-34`)
is the insertion point, and this is where `ExplosionRadius` (F1) finally gets read.
**Verify the exact signatures against a fresh decompile before writing the call** —
these names come from the README, and nothing in this repo compiles against them
today.

**2. Inventory identity.** Items are Addressables prefabs carrying `Icon_Info` /
`Item_Info`; there is no code-only way to register new 3D art with the game's item
system. First version reskins an existing item's slot, which is exactly what
`ItemPickupLogger` exists to identify. **That logger has apparently not been run yet,
or its output was not recorded** — no captured `assetRefKey` / `iconGUID` for any
candidate item is committed anywhere in this repository. Running it and writing the
result down is a ten-minute task that unblocks the whole inventory strand, and it is
the single highest-value thing to do next.

**3. Throw / equip mechanic.** Follow the bow-and-arrow pattern (`Arrow_Impact`): an
`Equipment`-slot item that spawns a physical thrown prefab. The prefab construction in
`SpawnTestGrenade` (`Plugin.cs:82-110`) is most of that prefab already — fix F2/F3/F4
first so the real mechanic inherits corrected physics rather than copying the bugs.

---

## 5. Suggested order of work

1. **Capture the pickup logger's output** and commit the identities (§4.2). Unblocks
   the most work per minute spent, and needs no code.
2. **F2 + F4 + F3** — collider centre, continuous collision, thrower ignore. Small,
   local, and they fix a prop that currently sinks into the floor and can fall through
   it. Do these before anything is built on top of the spawn code.
3. **F7 + F8 + F6** — make startup survive a failed patch, a null picker, and a
   missing shader. Cheap insurance against a game update silently disabling the mod.
4. **F1 + AoE damage** (§4.1) — the first change that makes this a mod rather than a
   prop spawner. Confirm the two `Creature_Mgr` / `Smash_Fallen_Manager` signatures
   against a decompile first.
5. **F9, F10, F11, F13** — polish, any time.
6. **F5** — harden the OBJ parser when a second asset arrives, not before. It is
   correct for the one file it loads today.
7. **F12** — worth doing whenever someone wants CI or agent sessions that can compile.

## 6. Open questions for the owner

- Is the game HDRP-confirmed, and does `HDRP/Lit` actually resolve from a chainloader
  `Awake`? F6's fix depends on the answer, and one log line settles it.
- Legacy Input Manager or the new Input System? F9's severity turns on this.
- Has the pickup logger ever been run against a live game, and is there output
  recorded somewhere outside this repository?
- Damage model intent: fixed damage inside the radius, or falloff with distance? This
  changes what `ExplosionRadius` should mean before it gets wired up.
