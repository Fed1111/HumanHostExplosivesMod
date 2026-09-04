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

A `ExplosionRadius` setting (default `5`) is generated in `BepInEx/config/com.nathanfeddema.humanhostexplosives.cfg` after the first run, controlling the radius in meters of the explosion effect.

## Item pickup logger (diagnostic)

Every item picked up by any character is logged to the BepInEx console/log as:

```
[ItemPickup] name='...' assetRefKey='...' iconGUID='...' picker='...'
```

This is temporary tooling to identify the exact Addressables identity of existing in-game
items (e.g. a soda can) so a new item (grenade/molotov) can reuse its model/icon as a
placeholder, since new 3D art can't be authored from outside the game's Unity project.

## Status

Goal: throwable explosives (grenades, molotovs). Plan, informed by decompiling the game's
own assemblies (Human Host is built for BepInEx + ILSpy modding):

- Items are Addressables prefabs carrying an `Icon_Info`/`Item_Info` component — there's no
  code-only way to add new 3D art, so a first version will reskin an existing item.
- AoE damage will reuse the game's own pipeline: `Creature_Mgr.ins.capCol_To_Controller`
  (radius query) + `Smash_Fallen_Manager.ins.Minus_Char_HP(...)` (the same method traps/falls
  already use).
- The throw/equip mechanic will follow the bow-and-arrow pattern (`Arrow_Impact`): an
  `Equipment`-slot item that spawns a physical thrown prefab.

Not yet implemented: no explosive item exists in-game yet. This PR only adds a diagnostic
logger to identify a real item to reskin.
