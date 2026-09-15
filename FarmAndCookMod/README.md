# Farm and Cook Mod

A BepInEx 5 plugin for [Human Host](https://store.steampowered.com/) that adds farming and
cooking. Lives alongside the explosives mod in this repo but builds as its own separate
plugin DLL — the two don't depend on each other.

## Requirements

- Human Host (Steam)
- [BepInEx 5](https://github.com/BepInEx/BepInEx) installed to the game folder

## Building

1. Open `HumanHostFarmAndCook.csproj` — set `<GameDir>` to your Human Host install path if it
   differs from the default (`C:\Program Files (x86)\Steam\steamapps\common\Human Host`).
2. Build with `dotnet build` or Visual Studio.
3. The built DLL is copied automatically to `BepInEx\plugins\HumanHostFarmAndCook`.

## Configuration

Settings are generated in `BepInEx/config/com.nathanfeddema.humanhostfarmandcook.cfg` after
the first run:

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| `Farming` | `GrowthRateMultiplier` | `1` | Multiplier on crop growth speed (`2` = twice as fast). |
| `Cooking` | `CookTimeMultiplier` | `1` | Multiplier on cooking duration (`0.5` = half the time). |

## Status

Scaffold only. The plugin loads, binds its config and runs `Harmony.PatchAll()`, but no
gameplay patches exist yet — the config values are read and logged, not applied.

Next steps, following the approach that worked for the explosives mod:

- Decompile the game's assemblies with ILSpy to find the real types behind growing and
  cooking (the explosives mod found AoE damage via `Smash_Fallen_Manager.Minus_Char_HP`;
  the equivalent entry points for crops/cooking still need to be identified).
- Add a diagnostic Harmony logger — the same trick as `ItemPickupLogger.cs` in the
  explosives mod — to dump the Addressables identity of edible/plantable items, so new
  crops can reuse an existing item's model and icon.
- Wire `GrowthRateMultiplier` and `CookTimeMultiplier` into those patches once the hooks
  are known.
- Custom 3D art, if needed, can be loaded at runtime from OBJ + PNG with no Unity Editor
  step — see `ObjLoader.cs` / `TextureLoader.cs` in the explosives mod.
