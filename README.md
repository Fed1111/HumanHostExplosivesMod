# Human Host Explosives Mod

A BepInEx 5 plugin for [Human Host](https://store.steampowered.com/) that adds explosives.

## Requirements

- Human Host (Steam)
- [BepInEx 5](https://github.com/BepInEx/BepInEx) installed to the game folder

## Building

1. Open `HumanHostExplosives.csproj` — set `<GameDir>` to your Human Host install path if it differs from the default (`C:\Program Files (x86)\Steam\steamapps\common\Human Host`).
2. Build with `dotnet build` or Visual Studio.
3. The built DLL is copied automatically to `BepInEx\plugins\HumanHostExplosives`.

## Configuration

Two settings are generated in `BepInEx/config/com.nathanfeddema.humanhostexplosives.cfg` after
the first run, both under `[Explosives]`:

- `ExplosionRadius` (default `5`) — radius in meters of the explosion.
- `ExplosionDamage` (default `120`) — damage applied at the center of the explosion, falling
  off linearly to zero at the edge of `ExplosionRadius`.

## Item pickup logger (diagnostic)

Every item picked up by any character is logged to the BepInEx console/log as:

```
[ItemPickup] name='...' assetRefKey='...' iconGUID='...' picker='...'
```

This is temporary tooling to identify the exact Addressables identity of existing in-game
items (e.g. a soda can) so a new item (grenade/molotov) can reuse its model/icon as a
placeholder, since new 3D art can't be authored from outside the game's Unity project.

## Grenade model

`Assets/Grenade/` holds the game-ready grenade asset: `grenade.obj` (6,000 tris, decimated
from a 289k-tri AI-generated source using per-face UV transfer to keep the texture mapping
intact) and `grenade.png` (1024x1024 diffuse texture). Loaded at runtime with no Unity Editor
/ AssetBundle step required — see `ObjLoader.cs` and `TextureLoader.cs`.

**Debug test**: press **G** in-game to throw a physical grenade prop (real Rigidbody, HDRP-lit
model) from the camera position. It detonates after a 3 second fuse, applying AoE damage to
any creatures in range (see below) before despawning.

## Status

Goal: throwable explosives (grenades, molotovs). Plan, informed by decompiling the game's
own assemblies (Human Host is built for BepInEx + ILSpy modding):

- Items are Addressables prefabs carrying an `Icon_Info`/`Item_Info` component — there's no
  code-only way to add new 3D art through the game's own item system, so a first version will
  reskin an existing item's inventory slot. The grenade's own visuals are fully custom (see
  above) and don't depend on this.
- AoE damage is wired up (`ExplosionDamage.cs`): a `Physics.OverlapSphere` query resolved
  against `Creature_Mgr.ins.capCol_To_Controller` (a `Dictionary<Collider, C_Controller_Base>`,
  not a radius query itself) finds nearby creatures, and
  `Smash_Fallen_Manager.ins.Minus_Char_HP(...)` (the same method traps/falls already use)
  applies damage with linear falloff by distance. The thrower is excluded; already-dead
  creatures are skipped since `Minus_Char_HP` doesn't check that itself. Out of scope for now:
  line-of-sight/occlusion, damage to buildables, and explosion VFX/SFX.
- The throw/equip mechanic will follow the bow-and-arrow pattern (`Arrow_Impact`): an
  `Equipment`-slot item that spawns a physical thrown prefab. Not wired up yet — for now the
  grenade only spawns via the debug keybind above.
