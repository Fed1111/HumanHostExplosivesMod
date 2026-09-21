using System;
using System.Runtime.CompilerServices;

namespace HumanHostExplosives
{
    /// <summary>
    /// Optional ModMenu (Workshop 3794527105, GUID humanhost.modmenu) page. Soft dependency: every
    /// mention of a ModMenu type stays inside the two NoInlining methods, which are only called after
    /// the runtime presence check, so the JIT never touches ModMenu.dll when it is not installed.
    /// See MOD_CONVENTIONS.md #38.
    ///
    /// Curated: registry GUIDs, per-item recipe/hand-offset entries (read once at startup), debug
    /// keys, throw-origin nudges and the Molotov (no art) stay config-file only.
    /// </summary>
    internal static class ModMenuBridge
    {
        internal const string ModMenuGuid = "humanhost.modmenu";
        private const string Restart = " Takes effect after restarting the game.";
        private const string KeyNote = " Keys can only be changed in the config file.";

        private static bool Present => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ModMenuGuid);

        internal static void TryRegister(Plugin plugin)
        {
            if (!Present) return;
            try { Register(plugin); }
            catch (Exception ex) { Plugin.Log.LogWarning("[ModMenu] registration failed; the config file still works. " + ex.Message); }
        }

        internal static void TryUnregister(Plugin plugin)
        {
            if (!Present) return;
            try { Unregister(plugin); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Register(Plugin plugin)
        {
            ModMenu.ModMenuApi.Register(plugin, "Explosives", new ModMenu.ModMenuItem[]
            {
                // --- Grenade ---
                new ModMenu.ModMenuSetting(Plugin.ExplosionDamage, "Damage at center", "Grenade",
                    "Grenade damage at the blast point, falling off to zero at the damage radius."),
                new ModMenu.ModMenuSetting(Plugin.ExplosionRadius, "Damage radius (m)", "Grenade",
                    "How far creature and player damage reaches. Does not change the visual size or structure damage."),
                new ModMenu.ModMenuSetting(Plugin.GrenadeEffectRadius, "Effect radius (m)", "Grenade",
                    "Radius of the visual explosion and of structure damage."),
                new ModMenu.ModMenuSetting(Plugin.BuildableDamage, "Structure damage", "Grenade",
                    "Flat damage to blocks and buildables within the effect radius."),
                new ModMenu.ModMenuSetting(Plugin.GrenadeNoiseRadius, "Noise radius (m)", "Grenade",
                    "How far zombies can hear a grenade."),
                new ModMenu.ModMenuSetting(Plugin.GrenadeFlashScale, "Flash scale", "Grenade",
                    "Multiplier on the bright flash and fireball. 1 is the original look."),
                new ModMenu.ModMenuSetting(Plugin.GrenadeParticulateScale, "Debris and smoke scale", "Grenade",
                    "Multiplier on debris and smoke. 1 is the original look."),

                // --- Nail bomb ---
                new ModMenu.ModMenuSetting(Plugin.EnableNailbomb, "Enable nail bomb", "Nail bomb",
                    "Register the nail bomb item." + Restart),
                new ModMenu.ModMenuSetting(Plugin.NailbombDamage, "Damage at center", "Nail bomb",
                    "Total shrapnel damage at the blast point, split across how much of a target is exposed."),
                new ModMenu.ModMenuSetting(Plugin.NailbombRadius, "Fragment range (m)", "Nail bomb",
                    "How far fragments travel. Cover still protects: fragments only hurt what they can reach in a straight line."),
                new ModMenu.ModMenuSetting(Plugin.NailbombFragmentDamage, "Fragment damage multiplier", "Nail bomb",
                    "Raise to make close range brutal without widening the range."),
                new ModMenu.ModMenuSetting(Plugin.NailbombBlockDamage, "Structure damage", "Nail bomb",
                    "Flat damage to blocks. Token by design: nails do not bring down walls."),
                new ModMenu.ModMenuSetting(Plugin.NailbombCausesBleed, "Causes bleeding", "Nail bomb",
                    "Apply the bleeding debuff when shrapnel hits the player. The game's bleed system is player-only."),
                new ModMenu.ModMenuSetting(Plugin.NailbombNoiseRadius, "Noise radius (m)", "Nail bomb",
                    "How far zombies can hear a nail bomb."),
                new ModMenu.ModMenuSetting(Plugin.NailbombVisualRadiusMultiplier, "Visual size", "Nail bomb",
                    "Fraction of the fragment range used for the visual effect only."),
                new ModMenu.ModMenuSetting(Plugin.NailbombFlashScale, "Flash scale", "Nail bomb",
                    "Multiplier on the bright flash and fireball. 1 is the original look."),
                new ModMenu.ModMenuSetting(Plugin.NailbombParticulateScale, "Debris and smoke scale", "Nail bomb",
                    "Multiplier on debris and smoke. 1 is the original look."),

                // --- Throwing ---
                new ModMenu.ModMenuSetting(Plugin.MinThrowSpeed, "Tap throw speed (m/s)", "Throwing",
                    "Throw speed for a tap with no hold."),
                new ModMenu.ModMenuSetting(Plugin.MaxThrowSpeed, "Charged throw speed (m/s)", "Throwing",
                    "Throw speed after holding for the full charge time."),
                new ModMenu.ModMenuSetting(Plugin.MaxChargeSeconds, "Charge time (s)", "Throwing",
                    "How long the button must be held to reach full throw speed."),
                new ModMenu.ModMenuSetting(Plugin.QuickThrowKey, "Quick throw key", "Throwing",
                    "Hold to charge and throw the first explosive on your hotbar." + KeyNote),
                new ModMenu.ModMenuSetting(Plugin.CancelChargeKey, "Cancel charge key", "Throwing",
                    "Press while charging to abort. A right-click tap always cancels too." + KeyNote),
                new ModMenu.ModMenuSetting(Plugin.EnableThrowAnimation, "Throw animation", "Throwing",
                    "Play a throw animation on release in third person."),
                new ModMenu.ModMenuSetting(Plugin.ThrowAnimationSpeed, "Animation speed", "Throwing",
                    "Playback speed of the throw clip. The release point scales with it automatically."),
                new ModMenu.ModMenuSetting(Plugin.ThrowAnimationInFirstPerson, "Animate in first person", "Throwing",
                    "Off by default: the clip is a third-person throw and looks like flailing arms from inside the head."),
                new ModMenu.ModMenuSetting(Plugin.EnableSwingSound, "Swing sound", "Throwing",
                    "Play the game's melee swing whoosh during the throw."),
                new ModMenu.ModMenuSetting(Plugin.SwingSoundVolume, "Swing sound volume", "Throwing",
                    "Multiplier on the swing sound. 1 is as loud as the axe."),

                // --- Danger and sound ---
                new ModMenu.ModMenuSetting(Plugin.AllowSelfDamage, "Self damage", "Danger",
                    "Your own explosives can hurt you."),
                new ModMenu.ModMenuSetting(Plugin.SelfDamageMultiplier, "Self damage multiplier", "Danger",
                    "Applied only to damage you take from your own explosives. 1 is full risk."),
                new ModMenu.ModMenuSetting(Plugin.ExplosionsAttractZombies, "Explosions attract zombies", "Danger",
                    "Zombies path to the blast, not to you, so a thrown explosive works as a distraction."),
                new ModMenu.ModMenuSetting(Plugin.ExplosionVolume, "Explosion volume", "Sound",
                    "Volume of the detonation. Values above 1 amplify."),
                new ModMenu.ModMenuSetting(Plugin.EchoDelay, "Echo delay (s)", "Sound",
                    "Seconds before the returning crack off distant geometry. 0 with echo volume 0 disables it."),
                new ModMenu.ModMenuSetting(Plugin.EchoVolume, "Echo volume", "Sound",
                    "Level of the echo relative to the blast. 0 disables it."),

                // --- Loot ---
                new ModMenu.ModMenuSetting(Plugin.EnableLootSpawning, "Spawn in loot", "Loot",
                    "Let explosives appear in world containers, inheriting the rarity of the loot tags below." + Restart),
                new ModMenu.ModMenuSetting(Plugin.LootTags, "Loot tags", "Loot",
                    "Comma-separated loot tags to add explosives to. Open a container once with diagnostics on to see the list in the log." + Restart),
                new ModMenu.ModMenuSetting(Plugin.EnableDiagnostics, "Diagnostics logging", "Diagnostics",
                    "Log extra detail about items, crafting tabs and loot tags."),
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Unregister(Plugin plugin)
        {
            ModMenu.ModMenuApi.Unregister(plugin);
        }
    }
}
