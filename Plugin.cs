using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nathanfeddema.humanhostexplosives";
        public const string Name = "Human Host Explosives";
        public const string Version = "0.2.0";

        // Invented, fixed Addressables GUIDs for our own items - not reused from anything in the
        // game's own catalog. Kept as constants (not config) since nothing needs to override them.
        private const string GrenadeIconGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const string GrenadeModelGuid = "0f9e8d7c6b5a4938271605f4e3d2c1b0";
        private const string NailbombIconGuid = "5a6b7c8d9e0f1a2b3c4d5e6f70819202";
        private const string NailbombModelGuid = "2029180716f5e4d3c2b1a0f9e8d7c6b5";
        private const string MolotovIconGuid = "11223344556677889900aabbccddeeff";
        private const string MolotovModelGuid = "ffeeddccbbaa00998877665544332211";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<float> ExplosionRadius;
        internal static ConfigEntry<float> ExplosionDamage;
        internal static ConfigEntry<float> BuildableDamage;
        internal static ConfigEntry<bool> AllowSelfDamage;
        internal static ConfigEntry<float> SelfDamageMultiplier;
        internal static ConfigEntry<float> ExplosionVolume;
        internal static ConfigEntry<bool> ExplosionsAttractZombies;
        internal static ConfigEntry<float> GrenadeNoiseRadius;
        internal static ConfigEntry<float> NailbombNoiseRadius;
        internal static ConfigEntry<float> EchoDelay;
        internal static ConfigEntry<float> EchoVolume;

        internal static ConfigEntry<float> MinThrowSpeed;
        internal static ConfigEntry<float> MaxThrowSpeed;
        internal static ConfigEntry<float> MaxChargeSeconds;

        internal static ConfigEntry<bool> EnableThrowAnimation;
        internal static ConfigEntry<float> ThrowAnimationSpeed;
        internal static ConfigEntry<float> ReleaseNormalized;

        internal static ConfigEntry<float> ThrowOriginRight;
        internal static ConfigEntry<float> ThrowOriginUp;
        internal static ConfigEntry<float> ThrowOriginForward;

        internal static ConfigEntry<bool> EnableSwingSound;
        internal static ConfigEntry<string> SwingSoundSetName;
        internal static ConfigEntry<float> SwingSoundVolume;
        internal static ConfigEntry<float> SwingSoundNormalized;

        internal static ConfigEntry<bool> EnableMolotov;
        internal static ConfigEntry<bool> EnableNailbomb;
        internal static ConfigEntry<float> NailbombDamage;
        internal static ConfigEntry<float> NailbombRadius;
        internal static ConfigEntry<float> NailbombFragmentDamage;
        internal static ConfigEntry<float> NailbombBlockDamage;
        internal static ConfigEntry<bool> NailbombCausesBleed;
        internal static ConfigEntry<float> NailbombEchoExtraDelay;
        internal static ConfigEntry<float> NailbombEchoGain;

        internal static ConfigEntry<bool> EnableLootSpawning;
        internal static ConfigEntry<string> LootTags;

        internal static ConfigEntry<string> TemplateIconGuid;
        internal static ConfigEntry<string> TemplateModelGuid;

        internal static ConfigEntry<bool> EnableDiagnostics;
        internal static ConfigEntry<KeyCode> DebugThrowGrenadeKey;
        internal static ConfigEntry<string> DebugThrowKind;
        internal static ConfigEntry<KeyCode> ReloadConfigKey;
        internal static ConfigEntry<bool> TraceHandBone;

        internal static List<ExplosiveDef> Defs { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            ExplosionRadius = Config.Bind(
                "Explosives", "ExplosionRadius", 5f,
                "Radius in meters of the grenade's explosion effect.");
            ExplosionDamage = Config.Bind(
                "Explosives", "ExplosionDamage", 1000f,
                "Grenade damage applied at the center of the explosion, falling off linearly to zero at the edge of ExplosionRadius.");
            BuildableDamage = Config.Bind(
                "Explosives", "BuildableDamage", 100f,
                "Flat (no falloff) damage applied to buildable structures/blocks within ExplosionRadius, via the game's own wall-damage system (Smash_Fallen_Manager.ZoneSmash_BI_MinusHP).");
            AllowSelfDamage = Config.Bind(
                "Explosives", "AllowSelfDamage", true,
                "If true, the thrower can be hurt by their own grenade (real risk to standing too close/short fuse/bad throw). If false, the thrower is always excluded like before.");
            SelfDamageMultiplier = Config.Bind(
                "Explosives", "SelfDamageMultiplier", 1f,
                "Multiplier applied only to self-damage (on top of the normal distance falloff), independent of damage dealt to others. 1 = full risk, same as anyone else caught in the blast.");
            ExplosionVolume = Config.Bind(
                "Explosives", "ExplosionVolume", 4f,
                "AudioSource volume for the detonation sound. Unity allows values above 1 (amplification beyond unity gain) - the synthesized waveform itself is already near max amplitude, so getting louder means turning this up, not the waveform.");

            ExplosionsAttractZombies = Config.Bind(
                "Explosives", "ExplosionsAttractZombies", true,
                "Explosions draw zombies, the same way gunfire and falling trees do - it uses the game's own noise " +
                "event (Smash_Fallen_Manager._attctZombies), so hearing range interacts with the AI exactly as vanilla " +
                "noise does. Zombies path to the blast position, not to you, so a thrown explosive works as a distraction.");
            GrenadeNoiseRadius = Config.Bind(
                "Explosives", "GrenadeNoiseRadius", 60f,
                "How far (meters) a grenade blast can be heard by zombies. Well beyond its 15m damage radius - a " +
                "detonation is far louder than it is lethal.");
            NailbombNoiseRadius = Config.Bind(
                "Nailbomb", "NailbombNoiseRadius", 45f,
                "How far (meters) a nail bomb can be heard. Smaller charge than a grenade, so it carries less far.");

            EchoDelay = Config.Bind(
                "Explosives", "EchoDelay", 0.26f,
                "Seconds after the main blast before the returning crack (slap-back off distant geometry) is heard. " +
                "Roughly distance/343m-s, so 0.35 reads as surfaces about 60m away. Set 0 with EchoVolume 0 to disable.");
            EchoVolume = Config.Bind(
                "Explosives", "EchoVolume", 0.45f,
                "Level of that returning crack relative to the main blast. It is deliberately duller and decays faster " +
                "than the blast so it reads as a reflection rather than a second explosion. 0 disables it.");

            MinThrowSpeed = Config.Bind(
                "Explosives", "MinThrowSpeed", 5f,
                "Throw speed (m/s) for a tap (no hold) throw.");
            MaxThrowSpeed = Config.Bind(
                "Explosives", "MaxThrowSpeed", 28f,
                "Throw speed (m/s) for a fully-charged (held for MaxChargeSeconds or longer) throw.");
            MaxChargeSeconds = Config.Bind(
                "Explosives", "MaxChargeSeconds", 1.2f,
                "How long (seconds) LMB must be held to reach MaxThrowSpeed. Holding longer doesn't charge further.");

            EnableThrowAnimation = Config.Bind(
                "Throw Animation", "EnableThrowAnimation", true,
                "Play a throw animation on release. Requires Assets/Anim/throwanim.bundle next to the plugin DLL, " +
                "built with Unity 2022.3.62f3 (the game's version). With no bundle present the grenade is thrown " +
                "instantly, exactly as before, so leaving this on costs nothing.");
            ThrowAnimationSpeed = Config.Bind(
                "Throw Animation", "ThrowAnimationSpeed", 1.4f,
                "Playback speed multiplier for the throw clip. The shipped bundle is 1.5s, so 1.4 plays it in about " +
                "1.07s - roughly in line with a vanilla melee swing (0.77-1.6s). Raise for a snappier throw. The " +
                "projectile release scales with this automatically, so changing it does not desync the spawn.");
            ReleaseNormalized = Config.Bind(
                "Throw Animation", "ReleaseNormalized", 0.45f,
                "Point in the clip (0-1) where the projectile leaves the hand. Measured in-game by tracing the right " +
                "hand bone (see ThrowAnimation.TraceHand, EnableDiagnostics): the hand peaks overhead at n=0.37 " +
                "(height 1.83), is still high and driving forward at n=0.45 (1.70, fwd 0.25), then whips down to 1.22 " +
                "by n=0.52 and is back to hip height (1.14) by n=0.60. Releasing later than ~0.5 looks like the grenade " +
                "is lobbed from the hip. Note the FBX's finger-open frame suggests 0.60, which is too late - the fingers " +
                "uncurl well after the visual release.");

            // The grenade leaves the player's right hand bone (_EquipBones.rightHand - the same
            // transform the game parents held weapons to). These offsets are applied in the
            // CHARACTER's space, not the hand's: a hand bone's axes are rotated unintuitively and
            // spin through the throw, so hand-space nudges would neither match what you see nor
            // hold still. Here +right is the character's right, +up is up, +forward is where they face.
            ThrowOriginRight = Config.Bind(
                "Throw Animation", "ThrowOriginRight", 0.10f,
                "Sideways nudge (metres) from the hand, in the CHARACTER's space. Positive = further out to the " +
                "character's right, away from the body.");
            ThrowOriginUp = Config.Bind(
                "Throw Animation", "ThrowOriginUp", -0.20f,
                "Vertical nudge (metres) from the hand, in the CHARACTER's space. Negative = lower.");
            ThrowOriginForward = Config.Bind(
                "Throw Animation", "ThrowOriginForward", 0.10f,
                "Forward nudge (metres) from the hand, in the CHARACTER's space - the direction the body faces, not " +
                "the camera. Keeps the grenade clear of the player's own collider at spawn.");

            EnableSwingSound = Config.Bind(
                "Throw Animation", "EnableSwingSound", true,
                "Play the game's own melee swing whoosh during the throw. Uses a real vanilla Melee_Swing_Sound_Set " +
                "(the same asset and per-clip volumes the axe uses), not an imitation.");
            SwingSoundSetName = Config.Bind(
                "Throw Animation", "SwingSoundSetName", "Hand_Swing_SFX",
                "Which Melee_Swing_Sound_Set to borrow, by ScriptableObject name. The game registers exactly six: " +
                "Hand_Swing_SFX, Axe_Iron_Swing_SFX, Club_Wood_Swing_SFX, Hammer_Iron_Swing_SFX, Knife_Iron_Swing_SFX, " +
                "Machete_Swing_SFX. Hand_Swing_SFX is the default because the metal sets ring like a blade, which reads " +
                "wrong for a thrown object. Matched exactly first, then case-insensitively as a substring, then falls " +
                "back to the first registered set.");
            SwingSoundVolume = Config.Bind(
                "Throw Animation", "SwingSoundVolume", 1f,
                "Multiplier on the set's own per-clip volume. 1 = exactly as loud as the axe.");
            SwingSoundNormalized = Config.Bind(
                "Throw Animation", "SwingSoundNormalized", 0.35f,
                "Point in the clip (0-1) the whoosh plays. Slightly before ReleaseNormalized so it leads the release, " +
                "matching how vanilla fires it at the start of a swing rather than on contact.");

            EnableMolotov = Config.Bind(
                "Molotov", "EnableMolotov", false,
                "Register the Molotov. OFF by default: it has no art, and registering an item whose invented " +
                "Addressables GUID never resolves makes the engine log 'Invalid path in AssetBundleProvider' - one of " +
                "the two strings the game treats as proof that Steam corrupted the install, which then latches its " +
                "corruption dialog on for the rest of the session. Turn on only once it has a model.");
            EnableNailbomb = Config.Bind(
                "Nailbomb", "EnableNailbomb", false,
                "Register the nail bomb. OFF by default because it currently has no art of its own - drop " +
                "Nailbomb/nailbomb.obj, nailbomb.png and nailbomb_icon.png next to the DLL and turn this on.");
            NailbombDamage = Config.Bind(
                "Nailbomb", "NailbombDamage", 200f,
                "Total shrapnel damage against creatures and players, split across how much of a target's body is " +
                "exposed to the blast point. Far below the grenade's, but it lands concentrated on whatever is " +
                "actually exposed rather than spread over an area.");
            NailbombRadius = Config.Bind(
                "Nailbomb", "NailbombRadius", 9f,
                "How far fragments travel. Larger than the grenade's blast radius - fragments carry - but they only " +
                "hurt what they can actually reach in a straight line, so cover protects.");
            NailbombFragmentDamage = Config.Bind(
                "Nailbomb", "NailbombFragmentDamage", 1f,
                "Multiplier on each fragment's share of NailbombDamage. Raise to make close range brutal without " +
                "widening the effective radius.");
            NailbombBlockDamage = Config.Bind(
                "Nailbomb", "NailbombBlockDamage", 10f,
                "Flat damage to buildable structures - an absolute value, NOT a fraction of BuildableDamage. Token by " +
                "design: nails do not bring down walls, which is what keeps the nail bomb distinct from the grenade " +
                "rather than a straight upgrade to it.");
            NailbombCausesBleed = Config.Bind(
                "Nailbomb", "NailbombCausesBleed", true,
                "Apply the game's Bleeding debuff when shrapnel hits the player. NOTE: the game's bleed system " +
                "(Skill_Mgr.Start_Bleeding) is player-only - it stacks a player buff and shows a player HUD icon - so " +
                "this cannot be applied to zombies or NPCs. There is no per-creature status system to hook.");

            NailbombEchoExtraDelay = Config.Bind(
                "Nailbomb", "NailbombEchoExtraDelay", 0.10f,
                "Extra seconds on top of EchoDelay for the nail bomb's echo, so its tail lands a little later than the " +
                "grenade's.");
            NailbombEchoGain = Config.Bind(
                "Nailbomb", "NailbombEchoGain", 1.7f,
                "Multiplier on EchoVolume for the nail bomb's echo - louder than the grenade's.");

            EnableLootSpawning = Config.Bind(
                "Loot", "EnableLootSpawning", true,
                "Let explosives spawn in world containers. This adds our item to an EXISTING loot tag rather than " +
                "creating a new spawn rate, so it inherits that tag's rarity and only appears in containers that " +
                "already roll it - and it automatically respects your loot-rate setting and looting skill.");
            LootTags = Config.Bind(
                "Loot", "LootTags", "军用装备,弹药",
                "Comma-separated loot tags to add explosives to. NOTE: the game is Chinese-developed and these tags are " +
                "Chinese strings - the defaults are 军用装备 (Military Equipment) and 弹药 (Ammunition). " +
                "Matched case-insensitively as substrings, so use full tags: 军用装备 rather than 用装备, which would also " +
                "match 民用装备 (civilian) and 警用装备 (police). Open any " +
                "container once with the mod loaded and the log lists every available tag ('[Loot] available loot " +
                "tags: ...') - set this to the ones that fit.");

            TemplateIconGuid = Config.Bind(
                "Registry", "TemplateIconGuid", "",
                "assetRef_Key/icon GUID of an existing item to clone as the structural base for our own items - it supplies the hand-equip animation and IK rig, so it must be a SIMPLE ONE-HANDED TOOL OR MELEE WEAPON (a knife, hatchet, hammer, etc) with real Hand_R animation content, not a consumable (food/water/bandages currently have no hand-model/animation of their own in this game) and not anything two-handed (bow, rifle). " +
                "Find it by picking up such a tool in-game with Diagnostics.EnableItemPickupLogger on and reading the BepInEx log. Required - registration is skipped while empty.");
            TemplateModelGuid = Config.Bind(
                "Registry", "TemplateModelGuid", "",
                "ModelRef GUID (the held 3D model, not the icon) of the same template tool/weapon, from the same log line. Required - registration is skipped while empty.");

            EnableDiagnostics = Config.Bind(
                "Diagnostics", "EnableDiagnostics", false,
                "Logs extra detail (Icon_Info fields, craft window tab layout) needed to fill in the Registry/* and per-item recipe config above. Safe to leave on; turn off once configured.");
            DebugThrowGrenadeKey = Config.Bind(
                "Debug", "ThrowGrenadeKey", KeyCode.G,
                "Press this key in-game to throw the explosive named by ThrowKind directly, bypassing the inventory/equip " +
                "system - for testing damage/projectile behaviour independent of item registration and materials.");
            TraceHandBone = Config.Bind(
                "Debug", "TraceHandBone", false,
                "Log the right hand bone's position through each throw, in character space, ~12 lines per throw. This is " +
                "how ReleaseNormalized was measured: find the sample where up/fwd peak and use its n value. Separate " +
                "from EnableDiagnostics so the general logs can stay on without this.");
            DebugThrowKind = Config.Bind(
                "Debug", "ThrowKind", "Nailbomb",
                "Which explosive ThrowGrenadeKey throws: Grenade, Nailbomb or Molotov. Bypasses inventory, crafting and " +
                "material requirements entirely, so it is the way to test a new explosive before its materials are " +
                "obtainable. Kept as one key rather than one per item because spare keys are scarce - H is the game's " +
                "camera toggle, for instance.");
            ReloadConfigKey = Config.Bind(
                "Debug", "ReloadConfigKey", KeyCode.F7,
                "Press this key in-game to re-read this .cfg from disk. The game has no hot reload, so without it every " +
                "tweak to a value like ThrowOriginRight costs a full relaunch. Note BepInEx rewrites this file on exit, " +
                "so edit it while the game is running and press this - do not edit and then quit, or your edit is lost. " +
                "Was F10; moved to F7 to free F10 for another installed mod's own settings key. F8/F9/F11 are taken " +
                "too (F8 is the game's own error-hint key, F9 is the camera mod, F11 is the shield mod).");

            Defs = BuildDefs();

            ExplosiveItemRegistry.Initialize(Defs);

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            foreach (ExplosiveDef def in Defs)
            {
                if (!def.TryLoadMeshAndMaterial())
                {
                    Log.LogWarning($"[{Name}] '{def.Tag}': mesh/texture not found under the plugin folder; debug throw and item registration for it are disabled.");
                }
            }

            Log.LogInfo($"{Name} v{Version} loaded.");
        }

        private List<ExplosiveDef> BuildDefs()
        {
            var grenade = new ExplosiveDef
            {
                Kind = ExplosiveKind.Grenade,
                Tag = "HHX_Grenade",
                IconGuid = GrenadeIconGuid,
                ModelGuid = GrenadeModelGuid,
                ObjFileName = "Grenade/grenade.obj",
                PngFileName = "Grenade/grenade.png",
                IconPngFileName = "Grenade/grenade_icon.png",
                MaxStack = 5,
                TooltipName = "Grenade",
                TooltipType = "Explosive",
                TooltipInstruction = "A hand grenade. Equip and press LMB to throw it; detonates after a short fuse.",
                WorkbenchTypeName = Config.Bind("Grenade", "WorkbenchType", "GunWorkbench",
                    "Craft_Mgr.WorkbenchType this recipe should appear under. See CraftDiag log lines for the workbench you open. Default confirmed via CraftDiag: GunWorkbench has tabs [Gun, Ammo, Tool].").Value,
                TabIndex = Config.Bind("Grenade", "CraftTabIndex", 1,
                    "Which tab (0-based) in that workbench's crafting UI to add the recipe to. See CraftDiag log lines. Default is GunWorkbench's 'Ammo' tab.").Value,
                CraftSeconds = Config.Bind("Grenade", "CraftSeconds", 15f, "Crafting time in seconds.").Value,
                // ABSOLUTE target for the hand-bone-local position (not deltas added to the
                // pickaxe's own tuned value). Confirmed correct at (-0.01, 0.12, 0.03) after an
                // extended empirical search (this space's axes are rotated relative to
                // character-perspective directions, and Y in particular moved the grenade opposite
                // to intuition, so this was found by isolating one axis at a time rather than by
                // reasoning about the coordinate space directly).
                HandOffset = new Vector3(
                    Config.Bind("Grenade", "HandOffsetX", -0.01f, "Absolute local-X target for the held model's hand-bone position (replaces the template's own value, does not add to it). Confirmed correct.").Value,
                    Config.Bind("Grenade", "HandOffsetY", 0.12f, "Absolute local-Y target for the held model's hand-bone position. Confirmed correct.").Value,
                    Config.Bind("Grenade", "HandOffsetZ", 0.03f, "Absolute local-Z target for the held model's hand-bone position. Confirmed correct.").Value),
                // Separate system from HandOffset above: this is what actually governs what the
                // player sees holding the item in first person (Tool_Interacter._1stCamMod), never
                // touched until now. A first attempt at (0,0,0) - by analogy with HandOffset,
                // where the hand bone's own local origin was a safe baseline - instead put the
                // grenade floating near the ground well off to the side, confirming (like
                // HandOffset's own (0,0,0) attempt did) that the origin isn't a meaningful
                // baseline in this coordinate space either. This value is presumably
                // camera-relative (X=right, Y=up, Z=forward from the camera), given the pickaxe's
                // own logged values grow mostly in Z as the look-angle tilts down (mid Z=0.64 ->
                // down Z=1.08) - i.e. compensating to keep the held item looking stable on screen
                // as the camera pitches. Starting from the pickaxe's own tuned "mid" value instead
                // of the origin, same interpolate-from-a-known-point approach that worked for
                // HandOffset.
                FirstPersonOffset = new Vector3(
                    Config.Bind("Grenade", "FirstPersonOffsetX", 0.05f, "Absolute local-X target for all six Tool_Interacter._1stCamMod entries - the actual first-person viewmodel position (separate from HandOffset, which only affects third-person). Starting point is the pickaxe's own original tuned 'mid' value logged on first launch.").Value,
                    Config.Bind("Grenade", "FirstPersonOffsetY", -0.5f, "Absolute local-Y target for all six Tool_Interacter._1stCamMod entries. Isolating this axis: X/Z held at the pickaxe's tuned values, only Y lowered, to see which axis actually controls screen-vertical position (the grenade appeared near the shoulder at Y=-0.05, need it down at the hand).").Value,
                    Config.Bind("Grenade", "FirstPersonOffsetZ", 0.64f, "Absolute local-Z target for all six Tool_Interacter._1stCamMod entries.").Value),
            };
            // Final recipe. A proper manufactured explosive: forged (not raw scrap) iron for the
            // casing, and Gun_Powder - a Chemistry-workbench product, not loot-findable - as filler,
            // so this lands as a real mid-game craft rather than something makeable on day one. That
            // also frees up Scrap Iron to be the nail bomb's cruder, unrefined pipe material below,
            // instead of both explosives competing for the same resource.
            AddRecipeSlot(grenade, "Grenade", 1, MatForgedIron, 2, "Iron Ingot - casing");
            AddRecipeSlot(grenade, "Grenade", 2, MatGunPowder, 10, "Gun Powder - filler");
            AddRecipeSlot(grenade, "Grenade", 3, MatDuctTape, 3, "Duct Tape - binding");

            var nailbomb = new ExplosiveDef
            {
                Kind = ExplosiveKind.Nailbomb,
                Tag = "HHX_Nailbomb",
                IconGuid = NailbombIconGuid,
                ModelGuid = NailbombModelGuid,
                ObjFileName = "Nailbomb/nailbomb.obj",
                PngFileName = "Nailbomb/nailbomb.png",
                IconPngFileName = "Nailbomb/nailbomb_icon.png",
                MaxStack = 5,
                TooltipName = "Nail Bomb",
                TooltipType = "Explosive",
                TooltipInstruction = "A pipe packed with powder and nails, taped together by hand. Equip and press LMB to throw; sprays shrapnel on a short fuse. Devastating in the open, useless against cover.",
                // Hand-crafted, no workbench: it is improvised junk taped together in the field,
                // which is also what separates it from the grenade (GunWorkbench). HandMade is the
                // player's own craft window - still a Craft_Items component, so recipe injection
                // works there unchanged.
                WorkbenchTypeName = Config.Bind("Nailbomb", "WorkbenchType", "HandMade",
                    "Craft_Mgr.WorkbenchType this recipe appears under. HandMade = craftable from the player's own " +
                    "crafting menu with no workbench required.").Value,
                TabIndex = Config.Bind("Nailbomb", "CraftTabIndex", 0,
                    "Which tab (0-based) of that menu. HandMade's tab layout is logged by CraftDiag when you open the " +
                    "player craft window - adjust if 0 is not the right one.").Value,
                CraftSeconds = Config.Bind("Nailbomb", "CraftSeconds", 12f, "Crafting time in seconds. Slightly quicker than a grenade - it is a cruder device.").Value,
                // Hand and viewmodel placement are copied verbatim from the grenade, which was
                // tuned empirically over many relaunches. Keep the nailbomb model the same size as
                // the grenade and these stay correct; if the model's scale differs these need
                // re-tuning one axis at a time, and the axes do NOT behave intuitively.
                HandOffset = new Vector3(
                    Config.Bind("Nailbomb", "HandOffsetX", -0.01f, "Absolute local-X target for the held model's hand-bone position. Copied from the grenade's tuned value.").Value,
                    Config.Bind("Nailbomb", "HandOffsetY", 0.12f, "Absolute local-Y target. Copied from the grenade's tuned value.").Value,
                    Config.Bind("Nailbomb", "HandOffsetZ", 0.03f, "Absolute local-Z target. Copied from the grenade's tuned value.").Value),
                FirstPersonOffset = new Vector3(
                    Config.Bind("Nailbomb", "FirstPersonOffsetX", 0.05f, "Absolute local-X for the first-person viewmodel. Copied from the grenade.").Value,
                    Config.Bind("Nailbomb", "FirstPersonOffsetY", -0.5f, "Absolute local-Y for the first-person viewmodel. Copied from the grenade.").Value,
                    Config.Bind("Nailbomb", "FirstPersonOffsetZ", 0.64f, "Absolute local-Z for the first-person viewmodel. Copied from the grenade.").Value),
            };
            // Final recipe. Crude and hand-made on purpose, to contrast with the grenade above:
            // Nitrate_Powder instead of Gun_Powder as filler, since Gun_Powder isn't loot-findable
            // and requires its own separate Chemistry craft first - Nitrate_Powder is the cruder,
            // less-refined precursor, needed in a larger amount to compensate for being weaker.
            AddRecipeSlot(nailbomb, "Nailbomb", 1, MatNails, 8, "Nails - the shrapnel");
            AddRecipeSlot(nailbomb, "Nailbomb", 2, MatNitratePowder, 5, "Nitrate Powder - crude filler");
            AddRecipeSlot(nailbomb, "Nailbomb", 3, MatDuctTape, 3, "Duct Tape - binding");

            var molotov = new ExplosiveDef
            {
                Kind = ExplosiveKind.Molotov,
                // Blocked on 3D art - keep it out of loot until there is a model to see in hand.
                SpawnsInLoot = false,
                Tag = "HHX_Molotov",
                IconGuid = MolotovIconGuid,
                ModelGuid = MolotovModelGuid,
                ObjFileName = "Molotov/molotov.obj",
                PngFileName = "Molotov/molotov.png",
                MaxStack = 5,
                TooltipName = "Molotov Cocktail",
                TooltipType = "Explosive",
                TooltipInstruction = "A improvised firebomb. Equip and press LMB to throw it; bursts into a burning area on impact.",
                WorkbenchTypeName = Config.Bind("Molotov", "WorkbenchType", "HandMade",
                    "Craft_Mgr.WorkbenchType this recipe should appear under. Default confirmed via CraftDiag: HandMade has tabs [Tool, Melee, Bow, Armor, Build, Trap].").Value,
                TabIndex = Config.Bind("Molotov", "CraftTabIndex", 1,
                    "Which tab (0-based) in that workbench's crafting UI to add the recipe to. Default is HandMade's 'Melee' tab.").Value,
                CraftSeconds = Config.Bind("Molotov", "CraftSeconds", 15f, "Crafting time in seconds.").Value,
                HandOffset = new Vector3(
                    Config.Bind("Molotov", "HandOffsetX", 0f, "Local-space X offset (meters) applied to the held model, to correct for it sitting away from the template weapon's grip point.").Value,
                    Config.Bind("Molotov", "HandOffsetY", 0f, "Local-space Y offset (meters) applied to the held model.").Value,
                    Config.Bind("Molotov", "HandOffsetZ", -0.3f, "Local-space Z offset (meters) applied to the held model. Negative pulls it back toward the hand/character.").Value),
            };
            // Molotov is still blocked on 3D art, so its recipe is left unset deliberately - a
            // registered item with no model is worse than no item. Fill these in when the art lands.
            AddRecipeSlot(molotov, "Molotov", 1);
            AddRecipeSlot(molotov, "Molotov", 2);
            AddRecipeSlot(molotov, "Molotov", 3);

            var defs = new List<ExplosiveDef> { grenade };
            if (EnableMolotov.Value)
            {
                defs.Add(molotov);
            }
            // Gated: the nailbomb has no art of its own yet, and registering an item whose model
            // cannot load leaves a broken entry in the crafting UI.
            if (EnableNailbomb.Value)
            {
                defs.Add(nailbomb);
            }
            return defs;
        }

        // Real material icon GUIDs, decoded straight out of the game's Addressables catalog
        // (StreamingAssets/aa/catalog.json) rather than discovered by picking items up in-game -
        // see tools/catalog_guids.py in the audit repo. All of these are genuine craftable
        // materials under Assets/In_Use/Conts/Mods/Recipes/.
        internal const string MatGunPowder = "70f597caadb363e45b9bf02fa62072fa";   // Recipes/Chemistry/Gun_Powder
        internal const string MatNitratePowder = "cff973d2e35809f4aae157ddd4b502ec"; // Recipes/Chemistry/Nitrate_Powder
        internal const string MatScrapIron = "819d1d5e9f4684745913ea7b3442fa77";    // Recipes/Building/Scrap Iron
        internal const string MatForgedIron = "7cdd1222de58be149815c0d6647bb151";   // Recipes/Ingot/Forged_Iron (Iron Ingot)
        internal const string MatScrapBrass = "ba7ca915ae348f54ca5ecd8d0f8561d2";   // Recipes/Building/Scrap_Brass
        internal const string MatNails = "961f0034fb6dde6478ff2931ec2e6932";        // Recipes/Building/Nails
        internal const string MatDuctTape = "e0233852a8ff00642ba77bfb8f46203a";     // Recipes/Tool/Duct_Tape
        internal const string MatTornCloth = "88e47f080fb36644c81bb2ff11a8bb9c";    // Recipes/Cloth/Torn_Cloth

        private void AddRecipeSlot(ExplosiveDef def, string section, int slotNumber,
                                   string defaultGuid = "", int defaultCount = 1, string what = null)
        {
            string guid = Config.Bind(section, $"RecipeMaterial{slotNumber}Guid", defaultGuid,
                $"Material #{slotNumber}'s icon GUID for this recipe{(what != null ? $" (default: {what})" : "")}. " +
                "Leave empty to skip this slot. GUIDs can be decoded offline from StreamingAssets/aa/catalog.json - " +
                "see tools/catalog_guids.py in the audit repo - or found in-game with Diagnostics.EnableItemPickupLogger.").Value;
            int count = Config.Bind(section, $"RecipeMaterial{slotNumber}Count", defaultCount,
                $"How many of material #{slotNumber} the recipe needs.").Value;
            if (!string.IsNullOrEmpty(guid))
            {
                def.Recipe.Add((guid, count));
            }
        }

        // BepInEx logs to a file and (optionally) a console window, neither of which is visible
        // while the game has focus - so a config reload gave no feedback at all in-game. This puts
        // a short-lived confirmation on screen instead.
        private static string _toastText;
        private static float _toastUntil;
        private static GUIStyle _toastStyle;

        internal static void Toast(string text, float seconds = 3f)
        {
            _toastText = text;
            _toastUntil = Time.unscaledTime + seconds;
        }

        /// <summary>
        /// Throws one of our explosives with no inventory, crafting or material requirement.
        /// Testing-only path; it skips the equip/animation flow, so it exercises the projectile,
        /// damage, model and sound but not the hand-held or throw-animation behaviour.
        /// </summary>
        private void DebugThrow(ExplosiveKind kind)
        {
            ExplosiveDef def = Defs.Find(d => d.Kind == kind);
            if (def == null)
            {
                Log.LogWarning($"[Debug] no registered def for {kind} (is it enabled in config?).");
                Toast($"{kind}: not registered");
                return;
            }
            if (def.RuntimeMesh == null || def.RuntimeMaterial == null)
            {
                Log.LogWarning($"[Debug] {kind} has no loaded mesh/material - check its art files.");
                Toast($"{kind}: art failed to load");
                return;
            }

            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null)
            {
                Log.LogWarning("[Debug] no camera; cannot throw.");
                return;
            }

            ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins, MaxThrowSpeed.Value * 0.6f);
            Toast($"thrown: {kind}");
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_toastText) || Time.unscaledTime > _toastUntil)
            {
                return;
            }

            if (_toastStyle == null)
            {
                _toastStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                };
            }

            var rect = new Rect(14f, 14f, 640f, 110f);
            // Cheap drop shadow so it stays readable over bright terrain.
            _toastStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), _toastText, _toastStyle);
            _toastStyle.normal.textColor = new Color(0.6f, 1f, 0.6f);
            GUI.Label(rect, _toastText, _toastStyle);
        }

        private void Update()
        {
            ExplosiveUseHook.PollCharge();

            // Re-read the .cfg from disk without restarting the game. The game has no hot reload,
            // so tuning a number like ThrowOriginRight otherwise costs a full relaunch cycle - and
            // BepInEx rewrites the config on exit, which clobbers edits made while it is running.
            // Alt-tab, edit the file, press this key, and the new values are live.
            if (Input.GetKeyDown(ReloadConfigKey.Value))
            {
                Config.Reload();
                SwingSound.ResetCache();
                // Loot injection latches after its first run, so without this a LootTags change
                // would not take effect until a relaunch. Re-injection is idempotent (it checks
                // for our GUID before appending), so this cannot stack duplicate spawn weight.
                Registry.LootTableInjector.Reset();
                Toast("Config reloaded\n" +
                      $"speed {ThrowAnimationSpeed.Value:F2}   release {ReleaseNormalized.Value:F2}\n" +
                      $"origin  R {ThrowOriginRight.Value:F2}   U {ThrowOriginUp.Value:F2}   F {ThrowOriginForward.Value:F2}\n" +
                      $"echo {EchoDelay.Value:F2}s x{EchoVolume.Value:F2}   swing '{SwingSoundSetName.Value}'");
                Log.LogInfo(
                    $"[Config] reloaded. ThrowAnimationSpeed={ThrowAnimationSpeed.Value:F2} " +
                    $"ReleaseNormalized={ReleaseNormalized.Value:F2} " +
                    $"Origin(right={ThrowOriginRight.Value:F2}, up={ThrowOriginUp.Value:F2}, " +
                    $"fwd={ThrowOriginForward.Value:F2})");
            }

            if (Input.GetKeyDown(DebugThrowGrenadeKey.Value))
            {
                ExplosiveKind kind = ExplosiveKind.Nailbomb;
                try
                {
                    kind = (ExplosiveKind)System.Enum.Parse(
                        typeof(ExplosiveKind), DebugThrowKind.Value, ignoreCase: true);
                }
                catch (System.Exception)
                {
                    Log.LogWarning($"[Debug] ThrowKind '{DebugThrowKind.Value}' is not a known kind; using Nailbomb.");
                }
                DebugThrow(kind);
            }
        }
    }
}
