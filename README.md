# Human Host Explosives Mod

A BepInEx 5 plugin for [Human Host](https://store.steampowered.com/) that adds throwable
explosives (grenade, Molotov) as real inventory items — equip from the hotbar, LMB to throw.

## Requirements

- Human Host (Steam)
- [BepInEx 5](https://github.com/BepInEx/BepInEx) installed to the game folder

## Building

1. Open `HumanHostExplosives.csproj` — set `<GameDir>` to your Human Host install path if it
   differs from the default (`C:\Program Files (x86)\Steam\steamapps\common\Human Host`).
2. Build with `dotnet build` or Visual Studio.
3. The built DLL is copied automatically to `BepInEx\plugins\HumanHostExplosives`.

## One-time setup before the items work

The mod registers grenade/Molotov as brand-new inventory items by cloning an **existing
one-handed tool or melee weapon** (a knife, hatchet, hammer, etc) for its structural wiring —
hand-equip, animation, IK rig — then overwriting its icon, 3D model and identity. It must be a
tool/weapon specifically, **not a consumable**: food/water/bandages currently have no working
hand-model/animation content of their own in this game, so there's nothing usable to clone there
yet. Avoid anything two-handed too (bow, rifle) - a plain one-handed melee tool is the closest
shape to a thrown grenade. Which exact item to clone can't be hardcoded — it depends on the
game's Addressables catalog, which isn't dumped anywhere. So, once, with the game running:

1. Leave `Diagnostics.EnableDiagnostics = true` (the default) in the generated config.
2. Pick up a **simple one-handed tool or melee weapon** (a knife/hatchet/hammer) with this mod
   loaded. The BepInEx log will print a line like:
   ```
   [ItemPickup] name='...' assetRefKey='...' iconGUID='...' tag='Knife' slotType='Hand_R' canStack=False modelRefGuid='...' picker='...'
   ```
3. Copy `iconGUID` into `Registry.TemplateIconGuid` and `modelRefGuid` into
   `Registry.TemplateModelGuid` in the config file
   (`BepInEx/config/com.nathanfeddema.humanhostexplosives.cfg`).
4. Restart, or let the mod's retry loop pick it up (it retries for ~30s after
   `Item_Slot_Mgr` wakes).

The clone is still forced to look/behave like a stackable consumable at the UI level
(`_Can_Stack`/`_Tag` are overwritten regardless of what the template item normally is — the
already-installed `MiningDrill` mod does the same thing to its own cloned template) and the
template's own attack/durability-per-swing behavior never runs, because `ExplosiveUseHook`
suppresses LMB-attack for our tag (via the game's own `Is_Belt_LMB_Use_Active` flag) before
`Weapon_Melee.On_Attack()` can fire.

Recipes work the same way: pick up whatever material you want to require (scrap metal, cloth,
etc.), copy its `iconGUID` into `Grenade.RecipeMaterial1Guid` / `Molotov.RecipeMaterial1Guid`
(up to 3 material slots per item; leave a slot's Guid empty to skip it), and set the matching
`...Count`. To find the right `WorkbenchType`/`CraftTabIndex`, open any crafting bench once — the
log prints:
```
[CraftDiag] Craft_Items opened: workbench=..., tabs=N
[CraftDiag]   tab[0] = '...'
```
Match the workbench/tab name you want against `Grenade.WorkbenchType` / `Grenade.CraftTabIndex`
(and the `Molotov.*` equivalents). A recipe with an empty material slot 1 (the default) is
skipped entirely — the item still exists and can be equipped/thrown even with no recipe
configured, it just won't be craftable yet.

Turn `Diagnostics.EnableDiagnostics` off once everything above is filled in, to stop the extra
log spam.

## How it works

- **Item registration** (`Registry/`): a custom `IResourceLocator`/`ResourceProviderBase` pair is
  installed into `Addressables.ResourceManager`, the same technique the already-installed
  `MiningDrill` mod on this machine uses for its own new item — it intercepts loads for our own
  invented GUIDs and serves runtime-built `GameObject`s instead of anything from the game's packed
  catalog. This is a real new item, not a reskin of an existing one.
- **Throw mechanic** (`Gameplay/ExplosiveUseHook.cs`): no custom weapon rig. The game already has
  a first-class "equip it, LMB uses it, consume 1 from the stack" pipeline (used by
  food/bandages/medkits/etc, normally on consumable-shaped items) - a grenade fits that use-shape
  exactly even though it has to clone a tool/weapon's model for its animation (see setup above).
  Two Harmony postfixes on `Item_Slot_Mgr` (`Is_Icon_Usable`, `Trigger_Use_For_Belt_Slot`) hook our
  item tags into it; whether LMB triggers "use" instead of the model's original melee attack is
  gated purely by the UI-level `Is_Belt_LMB_Use_Active` flag (`Is_Icon_Usable` for the current
  tag), not by anything on the cloned prefab, which is what makes cloning a tool/weapon (for its
  animation) safe even though the use-mechanic itself is consumable-shaped.
  (An earlier plan to model the throw on the bow's `Weapon_Range`/`Arrow_Impact` classes turned out
  to be the wrong fit — those are ~5,900 lines of draw/aim/animation state and arrow-sticking
  bookkeeping that a grenade doesn't need at all.)
- **Grenade** (`GrenadeProjectile.cs`): real Rigidbody flight, fixed fuse timer, then AoE falloff
  damage via `ExplosionDamage.cs` (`Physics.OverlapSphere` + the game's own
  `Smash_Fallen_Manager.Minus_Char_HP` damage pipeline).
- **Molotov** (`MolotovProjectile.cs` + `FirePool.cs`): detonates on first impact instead of a
  timed fuse, then leaves behind a `FirePool` that ticks falloff damage every 0.5s for a few
  seconds. The game has no burn/fire-DoT system of its own (confirmed absent from every decompiled
  managed assembly), so the whole fire effect — including its particle visual — is mod-side.
- **Crafting** (`Registry/CraftRecipeInjector.cs`): recipes are injected into an existing
  workbench's UI the first time it's opened, via reflection (`Craft_Items`'s recipe data is a
  private array of `internal` structs, invisible to us at compile time) — same pattern
  `MiningDrill` uses for its own recipe.
- **Debug test**: press **G** in-game (configurable, `Debug.ThrowGrenadeKey`) to throw a grenade
  directly from the camera position, bypassing the inventory system entirely — useful for testing
  `ExplosionDamage`/`GrenadeProjectile` independent of item registration.

## Configuration

Generated in `BepInEx/config/com.nathanfeddema.humanhostexplosives.cfg` after the first run:

- `[Explosives]` `ExplosionRadius` (default `5`), `ExplosionDamage` (default `120`) — grenade only;
  Molotov damage/radius/duration are constants on `MolotovProjectile` for now.
- `[Registry]` `TemplateIconGuid` / `TemplateModelGuid` — see setup above. **Required.**
- `[Grenade]` / `[Molotov]` — `WorkbenchType`, `CraftTabIndex`, `CraftSeconds`,
  `RecipeMaterial{1,2,3}Guid` / `RecipeMaterial{1,2,3}Count`.
- `[Diagnostics]` `EnableDiagnostics` (default `true`) — extra `ItemPickup`/`CraftDiag` log lines.
- `[Debug]` `ThrowGrenadeKey` (default `G`).

## Grenade model

`Assets/Grenade/` holds the game-ready grenade asset: `grenade.obj` (6,000 tris, decimated
from a 289k-tri AI-generated source using per-face UV transfer to keep the texture mapping
intact) and `grenade.png` (1024x1024 diffuse texture). Loaded at runtime with no Unity Editor
/ AssetBundle step required — see `ObjLoader.cs` and `TextureLoader.cs`.

## Molotov model — not built yet

`Assets/Molotov/molotov.obj` + `molotov.png` don't exist. Until they're added, `ExplosiveDef`'s
own mesh/texture check fails for the Molotov def and the registry silently skips registering it
(logged as a warning) — everything else (recipe config, use-hook, projectile/fire-pool code) is
already wired up and will start working the moment the art is dropped in.

## Status

Both items go through the same registration/throw/craft pipeline. Grenade is fully wired,
pending the one-time template/recipe GUID setup above and an actual in-game test pass (not done
yet this session — this was a "get it compiling and internally consistent" pass, not a "played it"
pass). Molotov is code-complete but has no 3D model yet.

Known rough edges to test for, not yet verified in-game:
- The grenade-in-hand transform is inherited from whatever template item is cloned; expect to
  need a scale/offset tweak (`_WeaponPosDatas` on the cloned model, or the mesh itself) once it's
  visibly wrong in first person.
- If the mod is ever uninstalled while grenades/Molotovs are sitting in a save file, loading that
  save will try to resolve our invented GUIDs and fail (`Slot_Info.LoadNewIcon` throws on an
  unresolvable GUID) — drop them from your inventory/storage before uninstalling.
- `FirePool`'s particle effect is a generic runtime-built `ParticleSystem`, not real fire VFX —
  visual placeholder only.
