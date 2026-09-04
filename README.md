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

## Status

Early scaffold — plugin loads and logs to the BepInEx console. Explosive items/logic not yet implemented.
