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
    [BepInDependency(ModMenuBridge.ModMenuGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nf.humanhostexplosives";
        public const string Name = "Human Host Explosives";
        public const string Version = "0.2.2";

        // Invented, fixed Addressables GUIDs for our own items - not reused from anything in the
        // game's own catalog. Kept as constants (not config) since nothing needs to override them.
        private const string GrenadeIconGuid = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const string GrenadeModelGuid = "0f9e8d7c6b5a4938271605f4e3d2c1b0";
        private const string NailbombIconGuid = "5a6b7c8d9e0f1a2b3c4d5e6f70819202";
        private const string NailbombModelGuid = "2029180716f5e4d3c2b1a0f9e8d7c6b5";
        private const string MolotovIconGuid = "11223344556677889900aabbccddeeff";
        private const string MolotovModelGuid = "ffeeddccbbaa00998877665544332211";

        // Real vanilla GUIDs for the Iron Pickaxe (icon + held model), decoded straight out of
        // catalog.json the same way the material GUIDs below are - see tools/catalog_guids.py in
        // the audit repo. This is the template item ExplosiveItemRegistry clones to give our
        // items a working hand-equip rig/animation (see TemplateIconGuid/TemplateModelGuid below).
        // Baked in as a compiled default so a fresh install works out of the box: these config
        // entries used to default to "", which meant registration silently no-opped for every
        // subscriber who hadn't manually run the in-game discovery step and hand-copied the
        // logged GUIDs into their own .cfg - the published Workshop upload never shipped a .cfg,
        // so that was every fresh subscriber, not just one tester's broken install.
        internal const string DefaultTemplateIconGuid = "2e401ea56b72b83498758b35d380b689";
        internal const string DefaultTemplateModelGuid = "ca8cb07ee3ffece4d9206ebb29fe9a87";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<float> ExplosionRadius;
        internal static ConfigEntry<float> GrenadeEffectRadius;
        internal static ConfigEntry<float> GrenadeFlashScale;
        internal static ConfigEntry<float> GrenadeParticulateScale;
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

        internal static ConfigEntry<bool> ThrowAnimationInFirstPerson;

        internal static ConfigEntry<float> FirstPersonOriginForward;

        internal static ConfigEntry<float> FirstPersonOriginRight;

        internal static ConfigEntry<float> FirstPersonOriginUp;
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
        internal static ConfigEntry<float> NailbombVisualRadiusMultiplier;
        internal static ConfigEntry<float> NailbombFlashScale;
        internal static ConfigEntry<float> NailbombParticulateScale;
        internal static ConfigEntry<float> NailbombRadius;
        internal static ConfigEntry<float> NailbombFragmentDamage;
        internal static ConfigEntry<float> NailbombBlockDamage;
        internal static ConfigEntry<bool> NailbombCausesBleed;
        internal static ConfigEntry<float> NailbombEchoExtraDelay;
        internal static ConfigEntry<float> NailbombEchoGain;

        internal static ConfigEntry<bool> EnableLootSpawning;
        internal static ConfigEntry<string> LootTags;

        internal static ConfigEntry<KeyCode> QuickThrowKey;
        internal static ConfigEntry<KeyCode> CancelChargeKey;

        internal static ConfigEntry<string> TemplateIconGuid;
        internal static ConfigEntry<string> TemplateModelGuid;

        internal static ConfigEntry<bool> EnableDiagnostics;
        internal static ConfigEntry<KeyCode> DebugThrowGrenadeKey;
        internal static ConfigEntry<bool> MigratedStaleDefaults20260918;
        internal static ConfigEntry<string> DebugThrowKind;
        internal static ConfigEntry<KeyCode> ToggleThrowKindKey;
        internal static ConfigEntry<bool> TraceHandBone;

        internal static List<ExplosiveDef> Defs { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            // This game re-fires plugin Awake() on a scene reload (confirmed via BepInEx log:
            // "New Item_Slot_Mgr detected (scene reload)" from another mod lines up exactly with
            // a second "Human Host Explosives loaded" entry). A second run would rebuild Defs as
            // a fresh, never-registered set of ExplosiveDefs - but ExplosiveItemRegistry.TryBuildAll
            // short-circuits on its already-_built flag without touching them, so their
            // RuntimeIconInfo/mesh/material stay null forever and CraftRecipeInjector silently
            // skips them with no log line at all. Harmony patches and the already-built items from
            // the first Awake remain valid across the reload, so there is nothing a second Awake
            // needs to do - just skip it entirely.
            if (Instance != null)
            {
                Logger.LogInfo($"[{Name}] Awake() called again (scene reload) - already initialized, skipping.");
                return;
            }

            Log = Logger;
            Instance = this;

            CleanupLooseDuplicates();
            WarnIfDuplicateCopies();

            ExplosionRadius = Config.Bind(
                "Explosives", "ExplosionRadius", 15f,
                new ConfigDescription("Radius in meters the CREATURE/PLAYER damage falls off across. Roughly matches a real frag " +
                "grenade's cited danger/wounding radius (vs. ~5m for the near-certain casualty radius) - see " +
                "ExplosionDamage. Deliberately NOT used for structural damage or the visual effect - see " +
                "GrenadeEffectRadius - so extending this for damage balance doesn't also blow out how big the " +
                "explosion looks or how far it damages structures.", new AcceptableValueRange<float>(1f, 50f)));
            GrenadeEffectRadius = Config.Bind(
                "Explosives", "GrenadeEffectRadius", 5f,
                new ConfigDescription("Radius (meters) used for structural/buildable damage AND the visual explosion effect - kept " +
                "separate from ExplosionRadius so the two can be tuned independently. This is the original " +
                "ExplosionRadius value from before the danger-radius change; BuildableDamage's amount-per-hit and " +
                "the visual's tuned size both stay exactly as they were.", new AcceptableValueRange<float>(1f, 20f)));
            GrenadeFlashScale = Config.Bind(
                "Explosives", "GrenadeFlashScale", 0.75f,
                new ConfigDescription("Multiplier on the grenade's Flash/Fireball particle count and size (the bright, hot layers). " +
                "Grenade is meant to read as a bigger, dirtier boom - debris/smoke over flash - so this is toned " +
                "down relative to the nail bomb's. 1 = the original, unweighted look.", new AcceptableValueRange<float>(0f, 3f)));
            GrenadeParticulateScale = Config.Bind(
                "Explosives", "GrenadeParticulateScale", 1.6f,
                new ConfigDescription("Multiplier on the grenade's Debris/Smoke particle count and size (the matter/lingering layers). " +
                "Raised so the grenade reads as bigger and more particulate than the nail bomb. 1 = the original, " +
                "unweighted look.", new AcceptableValueRange<float>(0f, 3f)));
            ExplosionDamage = Config.Bind(
                "Explosives", "ExplosionDamage", 3000f,
                new ConfigDescription("Grenade damage at the center (0m), falling off linearly to zero at ExplosionRadius. At " +
                "ExplosionRadius=15 this puts damage at 5m from the blast around 2000 (3000 * (1 - 5/15)).", new AcceptableValueRange<float>(0f, 10000f)));
            BuildableDamage = Config.Bind(
                "Explosives", "BuildableDamage", 100f,
                new ConfigDescription("Flat (no falloff) damage applied to buildable structures/blocks within ExplosionRadius, via the game's own wall-damage system (Smash_Fallen_Manager.ZoneSmash_BI_MinusHP).", new AcceptableValueRange<float>(0f, 1000f)));
            AllowSelfDamage = Config.Bind(
                "Explosives", "AllowSelfDamage", true,
                "If true, the thrower can be hurt by their own grenade (real risk to standing too close/short fuse/bad throw). If false, the thrower is always excluded like before.");
            SelfDamageMultiplier = Config.Bind(
                "Explosives", "SelfDamageMultiplier", 1f,
                new ConfigDescription("Multiplier applied only to self-damage (on top of the normal distance falloff), independent of damage dealt to others. 1 = full risk, same as anyone else caught in the blast.", new AcceptableValueRange<float>(0f, 2f)));
            ExplosionVolume = Config.Bind(
                "Explosives", "ExplosionVolume", 4f,
                new ConfigDescription("AudioSource volume for the detonation sound. Unity allows values above 1 (amplification beyond unity gain) - the synthesized waveform itself is already near max amplitude, so getting louder means turning this up, not the waveform.", new AcceptableValueRange<float>(0f, 8f)));

            ExplosionsAttractZombies = Config.Bind(
                "Explosives", "ExplosionsAttractZombies", true,
                "Explosions draw zombies, the same way gunfire and falling trees do - it uses the game's own noise " +
                "event (Smash_Fallen_Manager._attctZombies), so hearing range interacts with the AI exactly as vanilla " +
                "noise does. Zombies path to the blast position, not to you, so a thrown explosive works as a distraction.");
            GrenadeNoiseRadius = Config.Bind(
                "Explosives", "GrenadeNoiseRadius", 60f,
                new ConfigDescription("How far (meters) a grenade blast can be heard by zombies. Well beyond its 15m damage radius - a " +
                "detonation is far louder than it is lethal.", new AcceptableValueRange<float>(0f, 200f)));
            NailbombNoiseRadius = Config.Bind(
                "Nailbomb", "NailbombNoiseRadius", 45f,
                new ConfigDescription("How far (meters) a nail bomb can be heard. Smaller charge than a grenade, so it carries less far.", new AcceptableValueRange<float>(0f, 200f)));

            EchoDelay = Config.Bind(
                "Explosives", "EchoDelay", 0.26f,
                new ConfigDescription("Seconds after the main blast before the returning crack (slap-back off distant geometry) is heard. " +
                "Roughly distance/343m-s, so 0.35 reads as surfaces about 60m away. Set 0 with EchoVolume 0 to disable.", new AcceptableValueRange<float>(0f, 1f)));
            EchoVolume = Config.Bind(
                "Explosives", "EchoVolume", 0.45f,
                new ConfigDescription("Level of that returning crack relative to the main blast. It is deliberately duller and decays faster " +
                "than the blast so it reads as a reflection rather than a second explosion. 0 disables it.", new AcceptableValueRange<float>(0f, 2f)));

            MinThrowSpeed = Config.Bind(
                "Explosives", "MinThrowSpeed", 5f,
                new ConfigDescription("Throw speed (m/s) for a tap (no hold) throw.", new AcceptableValueRange<float>(1f, 30f)));
            MaxThrowSpeed = Config.Bind(
                "Explosives", "MaxThrowSpeed", 28f,
                new ConfigDescription("Throw speed (m/s) for a fully-charged (held for MaxChargeSeconds or longer) throw.", new AcceptableValueRange<float>(5f, 60f)));
            MaxChargeSeconds = Config.Bind(
                "Explosives", "MaxChargeSeconds", 1.2f,
                new ConfigDescription("How long (seconds) LMB must be held to reach MaxThrowSpeed. Holding longer doesn't charge further.", new AcceptableValueRange<float>(0.2f, 5f)));

            EnableThrowAnimation = Config.Bind(
                "Throw Animation", "EnableThrowAnimation", true,
                "Play a throw animation on release. Requires Assets/Anim/throwanim.bundle next to the plugin DLL, " +
                "built with Unity 2022.3.62f3 (the game's version). With no bundle present the grenade is thrown " +
                "instantly, exactly as before, so leaving this on costs nothing.");
            ThrowAnimationSpeed = Config.Bind(
                "Throw Animation", "ThrowAnimationSpeed", 1.4f,
                new ConfigDescription("Playback speed multiplier for the throw clip. The shipped bundle is 1.5s, so 1.4 plays it in about " +
                "1.07s - roughly in line with a vanilla melee swing (0.77-1.6s). Raise for a snappier throw. The " +
                "projectile release scales with this automatically, so changing it does not desync the spawn.", new AcceptableValueRange<float>(0.5f, 3f)));
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

            ThrowAnimationInFirstPerson = Config.Bind(
                "Throw Animation", "ThrowAnimationInFirstPerson", false,
                "Play the throw clip while the player is in FIRST person too. Off by default: the clip is a " +
                "third-person over-the-shoulder throw, and seen from inside the head it reads as the arms flailing " +
                "across the screen. With this off, a first-person throw releases instantly from the camera instead - " +
                "which is also the only way the grenade is visible leaving, since the hand bone the clip throws from " +
                "spends the windup BEHIND the first-person camera.");
            FirstPersonOriginForward = Config.Bind(
                "Throw Animation", "FirstPersonOriginForward", 0.55f,
                "First person only: how far (metres) in front of the camera the grenade spawns. Must clear the near " +
                "clip plane or the throw starts invisible.");
            FirstPersonOriginRight = Config.Bind(
                "Throw Animation", "FirstPersonOriginRight", 0.18f,
                "First person only: sideways offset (metres) from the camera. Positive = to the right, so the throw " +
                "reads as coming from the right hand rather than out of the player's face.");
            FirstPersonOriginUp = Config.Bind(
                "Throw Animation", "FirstPersonOriginUp", -0.12f,
                "First person only: vertical offset (metres) from the camera. Negative = below eye level.");

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
                new ConfigDescription("Multiplier on the set's own per-clip volume. 1 = exactly as loud as the axe.", new AcceptableValueRange<float>(0f, 2f)));
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
                "Nailbomb", "EnableNailbomb", true,
                "Register the nail bomb. Defaults to ON - its art (Nailbomb/nailbomb.obj, nailbomb.png, " +
                "nailbomb_icon.png) ships alongside the DLL. Set false to hide it.");
            NailbombDamage = Config.Bind(
                "Nailbomb", "NailbombDamage", 888.89f,
                new ConfigDescription("Total shrapnel damage at the center (0m), falling off linearly to zero at NailbombRadius, split " +
                "across how much of a target's body is exposed to the blast point. Kept at the same ratio to the " +
                "grenade's ExplosionDamage as before (666.67 -> 888.89 tracks 2250 -> 3000).", new AcceptableValueRange<float>(0f, 5000f)));
            NailbombVisualRadiusMultiplier = Config.Bind(
                "Nailbomb", "NailbombVisualRadiusMultiplier", 0.4f,
                new ConfigDescription("Fraction of NailbombRadius used for the VISUAL explosion effect only - damage range is unaffected. " +
                "NailbombRadius (fragment travel distance) is deliberately larger than the grenade's ExplosionRadius, " +
                "but the nail bomb should still LOOK smaller, so this keeps the visual well under the grenade's size " +
                "despite that. Was hardcoded to 0.7 (visually bigger than the grenade, backwards from intent).", new AcceptableValueRange<float>(0.1f, 1f)));
            NailbombFlashScale = Config.Bind(
                "Nailbomb", "NailbombFlashScale", 1.8f,
                new ConfigDescription("Multiplier on the nail bomb's Flash/Fireball particle count and size (the bright, hot layers). " +
                "Nail bomb is meant to read as a smaller but sharper crack - flash over debris - so this is raised " +
                "relative to the grenade's. 1 = the original, unweighted look.", new AcceptableValueRange<float>(0f, 3f)));
            NailbombParticulateScale = Config.Bind(
                "Nailbomb", "NailbombParticulateScale", 0.6f,
                new ConfigDescription("Multiplier on the nail bomb's Debris/Smoke particle count and size (the matter/lingering layers). " +
                "Lowered so the nail bomb reads as smaller and less particulate than the grenade. 1 = the original, " +
                "unweighted look.", new AcceptableValueRange<float>(0f, 3f)));
            NailbombRadius = Config.Bind(
                "Nailbomb", "NailbombRadius", 20f,
                new ConfigDescription("How far fragments travel. Bumped up to roughly match a real fragmentation grenade's cited danger/" +
                "wounding radius (~15-20m, vs. ~5m for the near-certain casualty radius). Larger than the grenade's " +
                "blast radius - fragments carry - but they only hurt what they can actually reach in a straight " +
                "line, so cover still protects.", new AcceptableValueRange<float>(1f, 50f)));
            NailbombFragmentDamage = Config.Bind(
                "Nailbomb", "NailbombFragmentDamage", 1f,
                new ConfigDescription("Multiplier on each fragment's share of NailbombDamage. Raise to make close range brutal without " +
                "widening the effective radius.", new AcceptableValueRange<float>(0f, 5f)));
            NailbombBlockDamage = Config.Bind(
                "Nailbomb", "NailbombBlockDamage", 10f,
                new ConfigDescription("Flat damage to buildable structures - an absolute value, NOT a fraction of BuildableDamage. Token by " +
                "design: nails do not bring down walls, which is what keeps the nail bomb distinct from the grenade " +
                "rather than a straight upgrade to it.", new AcceptableValueRange<float>(0f, 500f)));
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
                "Loot", "LootTags", "军用装备,弹药,Military Gear,Ammo",
                "Comma-separated loot tags to add explosives to. NOTE: this game's own tag names are localized - a " +
                "Chinese client reports 军用装备 (Military Equipment) and 弹药 (Ammunition), an English client reports " +
                "Military Gear and Ammo for the same tags, so the default covers both. " +
                "Matched case-insensitively as substrings, so use full tags: 军用装备 rather than 用装备, which would also " +
                "match 民用装备 (civilian) and 警用装备 (police). Open any " +
                "container once with the mod loaded and the log lists every available tag ('[Loot] available loot " +
                "tags: ...') - set this to the ones that fit your client's language.");

            TemplateIconGuid = Config.Bind(
                "Registry", "TemplateIconGuid", DefaultTemplateIconGuid,
                "assetRef_Key/icon GUID of an existing item to clone as the structural base for our own items - it supplies the hand-equip animation and IK rig, so it must be a SIMPLE ONE-HANDED TOOL OR MELEE WEAPON (a knife, hatchet, hammer, etc) with real Hand_R animation content, not a consumable (food/water/bandages currently have no hand-model/animation of their own in this game) and not anything two-handed (bow, rifle). " +
                "Defaults to the vanilla Iron Pickaxe, which works out of the box. Override by picking up a different tool in-game with Diagnostics.EnableItemPickupLogger on and reading the BepInEx log. Leave non-empty - registration is skipped while empty.");
            TemplateModelGuid = Config.Bind(
                "Registry", "TemplateModelGuid", DefaultTemplateModelGuid,
                "ModelRef GUID (the held 3D model, not the icon) of the same template tool/weapon, from the same log line. Defaults to the vanilla Iron Pickaxe. Leave non-empty - registration is skipped while empty.");

            MigratedStaleDefaults20260918 = Config.Bind(
                "Registry", "MigratedStaleDefaults20260918", false,
                "Internal bookkeeping - do not edit. True once the one-time 2026-09-18 stale-default migration " +
                "(see MigrateStaleDefaults) has run for this install, so it never re-runs and re-overwrites a " +
                "value you've since customized yourself back to one of the old numbers.");
            MigrateStaleDefaults();

            EnableDiagnostics = Config.Bind(
                "Diagnostics", "EnableDiagnostics", false,
                "Logs extra detail (Icon_Info fields, craft window tab layout) needed to fill in the Registry/* and per-item recipe config above. Safe to leave on; turn off once configured.");
            DebugThrowGrenadeKey = Config.Bind(
                "Debug", "ThrowGrenadeKey", KeyCode.None,
                "Press this key in-game to throw the explosive named by ThrowKind directly, bypassing the inventory/equip " +
                "system - for testing damage/projectile behaviour independent of item registration and materials. OFF by " +
                "default (KeyCode.None) and only fires while EnableDiagnostics is also on - this is a dev tool that spawns " +
                "explosives for free, not something a player should be able to trigger by accident. Was bound to G; G is " +
                "now QuickThrowKey below for everyone.");
            QuickThrowKey = Config.Bind(
                "Explosives", "QuickThrowKey", KeyCode.G,
                "Hold this key to charge and throw the first explosive found on your hotbar (belt slots only, not the " +
                "backpack) - same hold-for-distance charge as the normal equip-and-click throw " +
                "(MinThrowSpeed..MaxThrowSpeed over MaxChargeSeconds), consuming it for real. Does nothing if none of " +
                "your belt slots have one. Set to None to disable.");
            CancelChargeKey = Config.Bind(
                "Explosives", "CancelChargeKey", KeyCode.R,
                "Press this key while charging (either LMB or QuickThrowKey) to abort - nothing is thrown, nothing is " +
                "consumed. A RIGHT-CLICK TAP always cancels too, regardless of this setting (a held RMB is what our own " +
                "charge already puts you into, so a fresh press reads as 'stop'). Change this if R conflicts with " +
                "something else on your keyboard layout; set to None to rely on the RMB tap only.");
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
            ToggleThrowKindKey = Config.Bind(
                "Debug", "ToggleThrowKindKey", KeyCode.End,
                "Press this key to cycle ThrowKind through the currently-registered explosives (skips anything " +
                "disabled in config, e.g. Molotov by default), so ThrowGrenadeKey can be tested against each one " +
                "back-to-back without hand-editing ThrowKind + pressing F7 each time. Same EnableDiagnostics gate as " +
                "ThrowGrenadeKey itself.");
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

            ModMenuBridge.TryRegister(this);
            Log.LogInfo($"{Name} v{Version} loaded.");
        }

        private void OnDestroy()
        {
            ModMenuBridge.TryUnregister(this);
        }

        /// <summary>
        /// Workshop-comment reports (2026-09-18..21): a second copy of this DLL at the plugins root -
        /// from installing the item with both the in-game browser and the Human Host Mod Manager, or
        /// copying the DLL by hand - makes BepInEx load only one copy, and the loose one could not find
        /// its art. Asset lookup now searches the wrapper folder too, but say so loudly anyway.
        /// </summary>
        private void WarnIfDuplicateCopies()
        {
            try
            {
                string me = System.IO.Path.GetFullPath(typeof(Plugin).Assembly.Location);
                string name = System.IO.Path.GetFileName(me);
                foreach (string f in System.IO.Directory.GetFiles(BepInEx.Paths.PluginPath, name, System.IO.SearchOption.AllDirectories))
                {
                    if (string.Equals(System.IO.Path.GetFullPath(f), me, System.StringComparison.OrdinalIgnoreCase)) continue;
                    Log.LogWarning($"[Cleanup] another copy of {name} exists at '{f}' (this one runs from '{me}'). BepInEx loads only one; " +
                                   "delete the copy that is NOT inside the mod's own folder. Usual cause: the item installed both by the in-game " +
                                   "mod browser and by the Mod Manager (HHMM), or a DLL copied by hand.");
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning("[Cleanup] duplicate-copy check failed: " + ex.Message);
            }
        }

        /// <summary>
        /// BUG WORKAROUND (2026-09-18): the actual published Workshop item was uploaded without its
        /// intended "HumanHostExplosives/" wrapper folder (confirmed by downloading the live item
        /// directly and inspecting its raw contents - entries like "Grenade/grenade.png" sat at the
        /// item root, not nested, same mistake independently confirmed on the Suppressor mod). The
        /// game's own in-game mod installer copies Workshop content straight into
        /// BepInEx/plugins/<relative path>, so every existing subscriber ended up with this mod's
        /// DLL and item folders loose directly in plugins/ instead of grouped under one wrapper.
        ///
        /// The republished item now includes the wrapper correctly, but Steam's own per-item content
        /// sync doesn't retroactively clean up files an OLDER upload already placed in plugins/ - the
        /// game's installer only adds files, it doesn't diff/remove stale ones (confirmed empirically
        /// on this project: loose duplicate DLLs for two other mods needed removing by hand this
        /// session). Without this cleanup, an existing subscriber's plugins/ could end up with BOTH
        /// the old loose files and the new correctly-wrapped copy - and BepInEx's own de-dup logic
        /// isn't guaranteed to pick the complete, correctly-wrapped one over the stale loose one.
        ///
        /// Deliberately does NOT include the loose "Assets" folder some players' installs also carry -
        /// that name is too generic to be confident it's actually this mod's (an unrelated "Assets"
        /// folder was found sitting in plugins/ this same session, of unclear origin) and an automated
        /// delete script should never remove something it can't positively identify as its own.
        ///
        /// Runs on every launch, but only ever acts on the CORRECTLY-LOADED, wrapped copy (see the
        /// directory-name check below) - if it ever finds itself running as a loose copy instead, it
        /// does nothing rather than guess. Never touches whichever file it's actually running from.
        /// Every step is defensive/best-effort: a permissions failure here must never prevent the mod
        /// from loading normally. See HumanHostSuppressorMod/Plugin.cs for the sibling implementation.
        /// </summary>
        private void CleanupLooseDuplicates()
        {
            try
            {
                string myPath = typeof(Plugin).Assembly.Location;
                string myDir = System.IO.Path.GetDirectoryName(myPath);
                string myDirName = System.IO.Path.GetFileName(myDir);

                if (!string.Equals(myDirName, "HumanHostExplosives", System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string pluginsRoot = System.IO.Path.GetDirectoryName(myDir);
                if (string.IsNullOrEmpty(pluginsRoot) || !System.IO.Directory.Exists(pluginsRoot))
                {
                    return;
                }

                string myFullPath = System.IO.Path.GetFullPath(myPath);
                string[] looseNames = { "HumanHostExplosives.dll", "Grenade", "Nailbomb" };

                foreach (string name in looseNames)
                {
                    string loosePath = System.IO.Path.Combine(pluginsRoot, name);
                    if (string.Equals(System.IO.Path.GetFullPath(loosePath), myFullPath, System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // never delete the copy we're actually running from
                    }

                    try
                    {
                        if (System.IO.Directory.Exists(loosePath))
                        {
                            System.IO.Directory.Delete(loosePath, recursive: true);
                            Log.LogInfo($"[Cleanup] removed stale loose folder '{loosePath}' (leftover from an older install layout).");
                        }
                        else if (System.IO.File.Exists(loosePath))
                        {
                            System.IO.File.Delete(loosePath);
                            Log.LogInfo($"[Cleanup] removed stale loose file '{loosePath}' (leftover from an older install layout).");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Log.LogWarning($"[Cleanup] failed to remove stale loose '{loosePath}': {ex.Message}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning("[Cleanup] loose-duplicate cleanup failed: " + ex.Message);
            }
        }

        /// <summary>
        /// One-time migration (2026-09-18): BepInEx's Config.Bind only writes a key's compiled default the
        /// FIRST time it's ever created in a player's .cfg file - confirmed directly on the sibling
        /// Suppressor mod's dev machine this same session (a controlled test: an artificial value survived
        /// a relaunch on a newer DLL with a different compiled default, completely untouched). Any
        /// subscriber who already has a persisted .cfg from before one of these defaults changed is stuck
        /// on the OLD value forever unless something actively migrates it - a better compiled default alone
        /// never reaches them.
        ///
        /// Covers two kinds of stale value, both confirmed via this file's own history/comments:
        /// - TemplateIconGuid/TemplateModelGuid: used to default to "", which silently disabled item
        ///   registration entirely for every subscriber who ran that version (2026-09-08 incident, see
        ///   MOD_CONVENTIONS.md #1a). Unambiguously safe to migrate - the code itself already treats empty
        ///   as broken ("registration is skipped while empty"), so no legitimate customization could ever
        ///   be an empty string.
        /// - ExplosionRadius (5->15), ExplosionDamage (2250->3000), NailbombDamage (666.67->888.89): a
        ///   later balance change, documented directly in GrenadeEffectRadius's and NailbombDamage's own
        ///   comments ("this is the original ExplosionRadius value from before the danger-radius change";
        ///   "666.67 -> 888.89 tracks 2250 -> 3000").
        ///
        /// Backs up the entire .cfg file first (once, only if something actually needs migrating) so a
        /// player who deliberately set one of the float values to exactly match an old default (unlikely,
        /// but the empty-string case aside, not impossible) can always recover their exact prior settings.
        /// Gated behind MigratedStaleDefaults20260918 so it only ever runs once per install.
        /// </summary>
        private void MigrateStaleDefaults()
        {
            try
            {
                if (MigratedStaleDefaults20260918.Value)
                {
                    return;
                }

                var floatMigrations = new List<(string Label, ConfigEntry<float> Entry, float OldDefault, float NewDefault)>
                {
                    ("ExplosionRadius", ExplosionRadius, 5f, 15f),
                    ("ExplosionDamage", ExplosionDamage, 2250f, 3000f),
                    ("NailbombDamage", NailbombDamage, 666.67f, 888.89f),
                };
                var staleFloats = new List<(string Label, ConfigEntry<float> Entry, float OldDefault, float NewDefault)>();
                foreach (var m in floatMigrations)
                {
                    if (Mathf.Approximately(m.Entry.Value, m.OldDefault))
                    {
                        staleFloats.Add(m);
                    }
                }

                var stringMigrations = new List<(string Label, ConfigEntry<string> Entry, string NewDefault)>
                {
                    ("TemplateIconGuid", TemplateIconGuid, DefaultTemplateIconGuid),
                    ("TemplateModelGuid", TemplateModelGuid, DefaultTemplateModelGuid),
                };
                var staleStrings = new List<(string Label, ConfigEntry<string> Entry, string NewDefault)>();
                foreach (var m in stringMigrations)
                {
                    if (string.IsNullOrEmpty(m.Entry.Value))
                    {
                        staleStrings.Add(m);
                    }
                }

                if (staleFloats.Count > 0 || staleStrings.Count > 0)
                {
                    BackupConfigFileBeforeMigration();

                    foreach (var m in staleFloats)
                    {
                        Log.LogInfo($"[Migration] '{m.Label}' was {m.OldDefault:F2} (the old default) - updated to " +
                            $"{m.NewDefault:F2}. If you'd deliberately set this yourself, your original .cfg was " +
                            "backed up - see the .bak file next to it.");
                        m.Entry.Value = m.NewDefault;
                    }
                    foreach (var m in staleStrings)
                    {
                        Log.LogInfo($"[Migration] '{m.Label}' was empty (the old broken default, registration was " +
                            $"silently disabled) - updated to '{m.NewDefault}'.");
                        m.Entry.Value = m.NewDefault;
                    }

                    Config.Save();
                }

                MigratedStaleDefaults20260918.Value = true;
                Config.Save();
            }
            catch (System.Exception ex)
            {
                Log.LogWarning("[Migration] stale-default migration failed: " + ex.Message);
            }
        }

        private void BackupConfigFileBeforeMigration()
        {
            try
            {
                string cfgPath = Config.ConfigFilePath;
                if (!System.IO.File.Exists(cfgPath))
                {
                    return;
                }
                string backupPath = cfgPath + ".pre-2026-09-18-migration.bak";
                if (!System.IO.File.Exists(backupPath))
                {
                    System.IO.File.Copy(cfgPath, backupPath);
                    Log.LogInfo($"[Migration] backed up your existing config to '{backupPath}' before making any changes.");
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning("[Migration] failed to back up config before migrating: " + ex.Message);
            }
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
            AddRecipeSlot(grenade, "Grenade", 2, MatGunPowder, 5, "Gun Powder - filler");
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
            AddRecipeSlot(nailbomb, "Nailbomb", 1, MatNails, 5, "Nails - the shrapnel");
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

        // Shared between ExplosiveUseHook's equip-and-click charge and Plugin's own G quick-throw
        // charge, so both look and feel the same while charging.
        //
        // THIRD ATTEMPT (2026-09-08) at mimicking RMB's aim state during a charge via
        // C_Controller_Base.Start_Aiming_Mode()/Cancel_Aiming_Mode(). Two earlier attempts both broke
        // on the same root cause: Cancel_Aiming_Mode unconditionally sets _InFirstPerson = false on
        // exit (Creature.dll:4180), so calling it while the player is CURRENTLY in first person - even
        // if they started the charge in third person and switched mid-charge - force-pulls them into
        // third person without the real transition that restores the head mesh. Fix this time: check
        // _InFirstPerson LIVE at exit, not a remembered start-of-charge value. If currently third
        // person, exit through the real Cancel_Aiming_Mode (matches how the game itself would). If
        // currently first person, DON'T call it - instead clear only _InAimingMode directly (also a
        // plain public bool, Creature.dll:605), which ends our own aim-state ownership (so
        // Start_Aiming_Mode's Allow_Aim_Condition guard doesn't stay permanently blocked, e.g. RMB
        // getting stuck unusable) without ever touching _InFirstPerson. This does skip whatever else
        // Cancel_Aiming_Mode normally does (Aim_Action_For_Inherit, Change_AnimType, push-box reset,
        // _On_AimingMode.Invoke) in that one branch - unverified whether any of that leaves a visible
        // mismatch (e.g. anim type) for someone who switches perspective mid-charge specifically. If
        // this breaks a third way, revert to the charge-bar-only version further up this file's history
        // rather than attempting a fourth guess.
        private static bool _chargeVisualActive;
        private static float _chargeVisualFraction;
        private static bool _chargeAimingActive;

        internal static void SetChargeVisual(bool active, float fraction = 0f)
        {
            _chargeVisualActive = active;
            _chargeVisualFraction = Mathf.Clamp01(fraction);

            Player_Input player = Player_Input.ins;
            if (player == null)
            {
                return;
            }

            if (active)
            {
                if (!_chargeAimingActive && !player._InFirstPerson)
                {
                    player.Start_Aiming_Mode();
                    _chargeAimingActive = true;
                }
                return;
            }

            if (!_chargeAimingActive)
            {
                return;
            }
            _chargeAimingActive = false;

            if (Input.GetMouseButton(1))
            {
                // Real RMB is separately held right now - it owns this state, don't touch anything.
                return;
            }

            if (!player._InFirstPerson)
            {
                // Currently still third person (whether we started there or never left) - safe to
                // use the real exit path, same as the game's own RMB release would.
                player.Cancel_Aiming_Mode(forceCancel: true);
            }
            else
            {
                // Currently first person (switched mid-charge, since entry requires third person) -
                // Cancel_Aiming_Mode would force _InFirstPerson = false and break this exact case.
                // Just release our own ownership of the aiming flag instead.
                player._InAimingMode = false;
            }
        }

        /// <summary>
        /// True the frame the player explicitly asks to abort an in-progress charge (either
        /// charge - LMB or QuickThrowKey). A right-click TAP always counts regardless of
        /// CancelChargeKey's setting: a held RMB is exactly the state our own charge already puts
        /// the player into via Start_Aiming_Mode, so a fresh press on top of that reads as "stop",
        /// not "aim harder".
        /// </summary>
        internal static bool CancelChargeRequested()
        {
            return Input.GetMouseButtonDown(1) || Input.GetKeyDown(CancelChargeKey.Value);
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

        /// <summary>
        /// Cycles DebugThrowKind to the next currently-registered explosive (Defs, so it only ever
        /// lands on something actually enabled), wrapping around. Lets ThrowGrenadeKey be tested
        /// against each item back-to-back without hand-editing the config each time.
        /// </summary>
        private void ToggleThrowKind()
        {
            if (Defs == null || Defs.Count == 0)
            {
                Toast("No explosives registered");
                return;
            }

            int currentIndex = -1;
            if (System.Enum.TryParse(DebugThrowKind.Value, ignoreCase: true, out ExplosiveKind currentKind))
            {
                currentIndex = Defs.FindIndex(d => d.Kind == currentKind);
            }

            ExplosiveKind next = Defs[(currentIndex + 1) % Defs.Count].Kind;
            DebugThrowKind.Value = next.ToString();
            Toast($"ThrowKind -> {next}");
            Log.LogInfo($"[Debug] ThrowKind toggled to {next}");
        }

        // Real gameplay hotkey (default G): hold-to-charge throw of whichever explosive is found
        // first scanning the player's HOTBAR (belt slots only - not the backpack, which would be
        // surprising and gets messy once carrying several explosive types), consuming one from
        // that stack exactly like the normal equip-and-click throw does - same charge curve
        // (MinThrowSpeed..MaxThrowSpeed over MaxChargeSeconds), just keyboard-driven instead of
        // tied to an equipped belt slot. Mirrors ExplosiveUseHook's charge state machine, kept
        // separate since this one isn't anchored to a specific equipped Slot_Info the same way.
        private Slot_Info _quickChargeSlot;
        private ExplosiveDef _quickChargeDef;
        private float _quickChargeStart;

        private void StartQuickThrowCharge()
        {
            Item_Slot_Mgr mgr = Item_Slot_Mgr.ins;
            if (mgr == null || mgr._slotsBelt_Player == null)
            {
                return;
            }

            // Hotbar/belt only, not the full backpack - reaching into the bag for this would be
            // surprising (grabbing whatever explosive is buried in storage rather than what's
            // actually equipped-ready) and messy once a player is carrying several explosive types.
            foreach (Slot_Info slot in mgr._slotsBelt_Player)
            {
                if (slot == null || slot._IsEmptySlot || slot._iconInfoPrefab == null)
                {
                    continue;
                }
                ExplosiveDef def = ExplosiveItemRegistry.FindByTag(slot._iconInfoPrefab._Tag);
                if (def != null)
                {
                    _quickChargeSlot = slot;
                    _quickChargeDef = def;
                    _quickChargeStart = Time.time;
                    SetChargeVisual(true, 0f);
                    return;
                }
            }

            Toast("No explosive in inventory");
        }

        /// <summary>Call once per frame. No-op unless a quick-throw charge is in progress.</summary>
        private void PollQuickThrowCharge()
        {
            if (_quickChargeSlot == null)
            {
                return;
            }

            // Re-validate every frame rather than trusting the cached reference - the stack could
            // get consumed/dropped/swapped elsewhere mid-charge.
            bool stillValid =
                !_quickChargeSlot._IsEmptySlot &&
                _quickChargeSlot._iconInfoPrefab != null &&
                ExplosiveItemRegistry.FindByTag(_quickChargeSlot._iconInfoPrefab._Tag) == _quickChargeDef;

            if (!stillValid)
            {
                CancelQuickThrowCharge();
                return;
            }

            SetChargeVisual(true, (Time.time - _quickChargeStart) / MaxChargeSeconds.Value);

            if (CancelChargeRequested())
            {
                CancelQuickThrowCharge();
                Toast("Throw cancelled");
                return;
            }

            if (Input.GetKeyUp(QuickThrowKey.Value))
            {
                ReleaseQuickThrow();
                return;
            }

            if (!Input.GetKey(QuickThrowKey.Value))
            {
                // Key-up missed this frame (alt-tab, focus loss) - cancel rather than throw stale.
                CancelQuickThrowCharge();
            }
        }

        private void ReleaseQuickThrow()
        {
            Slot_Info slot = _quickChargeSlot;
            ExplosiveDef def = _quickChargeDef;
            float heldSeconds = Time.time - _quickChargeStart;
            float chargeFraction = Mathf.Clamp01(heldSeconds / MaxChargeSeconds.Value);
            float throwSpeed = Mathf.Lerp(MinThrowSpeed.Value, MaxThrowSpeed.Value, chargeFraction);
            CancelQuickThrowCharge();

            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null)
            {
                return;
            }

            // Same animation timing as the normal equip-and-click throw (ExplosiveUseHook) - play
            // the clip and hold the projectile back until the hand actually opens, rather than
            // spawning instantly like DebugThrow does.
            float animDuration = ThrowAnimation.Play(ThrowAnimationSpeed.Value);
            if (animDuration > 0f)
            {
                float releaseDelay = animDuration * Mathf.Clamp01(ReleaseNormalized.Value);
                StartCoroutine(SpawnQuickThrowAtRelease(releaseDelay, def, throwSpeed));
            }
            else
            {
                ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins, throwSpeed);
            }

            Item_Slot_Mgr.ins?.Item_Stack_Minus_1(slot);
            Toast($"{def.TooltipName} thrown");
        }

        private System.Collections.IEnumerator SpawnQuickThrowAtRelease(float delay, ExplosiveDef def, float throwSpeed)
        {
            yield return new WaitForSeconds(delay);

            Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (camTrans == null || Player_Input.ins == null)
            {
                yield break;
            }

            ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins, throwSpeed);
        }

        private void CancelQuickThrowCharge()
        {
            _quickChargeSlot = null;
            _quickChargeDef = null;
            SetChargeVisual(false);
        }

        private static GUIStyle _barBgStyle;
        private static GUIStyle _barFillStyle;

        private void OnGUI()
        {
            if (_chargeVisualActive)
            {
                DrawChargeBar();
            }

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

        /// <summary>
        /// Bottom-center fill bar showing charge progress (0..1) for either the equip-and-click
        /// throw or the G quick-throw - same visual either way, since they share the same charge
        /// curve. Plain GUI textures rather than reusing any of the game's own HUD elements, to
        /// stay independent of whatever the crosshair/scope UI is doing.
        /// </summary>
        private void DrawChargeBar()
        {
            if (_barBgStyle == null)
            {
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.5f));
                bg.Apply();
                _barBgStyle = new GUIStyle { normal = { background = bg } };

                var fill = new Texture2D(1, 1);
                fill.SetPixel(0, 0, new Color(1f, 0.55f, 0.15f, 0.9f));
                fill.Apply();
                _barFillStyle = new GUIStyle { normal = { background = fill } };
            }

            const float width = 220f;
            const float height = 14f;
            float x = (Screen.width - width) * 0.5f;
            float y = Screen.height * 0.72f;

            GUI.Box(new Rect(x, y, width, height), GUIContent.none, _barBgStyle);
            GUI.Box(new Rect(x, y, width * _chargeVisualFraction, height), GUIContent.none, _barFillStyle);
        }

        private void Update()
        {
            ExplosiveUseHook.PollCharge();

            // Gated behind EnableDiagnostics on top of defaulting to KeyCode.None - this spawns
            // explosives for free, bypassing inventory entirely, so it must not be reachable by a
            // normal player even if they happen to press whatever key it's bound to.
            if (EnableDiagnostics.Value && Input.GetKeyDown(DebugThrowGrenadeKey.Value))
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

            if (EnableDiagnostics.Value && Input.GetKeyDown(ToggleThrowKindKey.Value))
            {
                ToggleThrowKind();
            }

            if (Input.GetKeyDown(QuickThrowKey.Value))
            {
                StartQuickThrowCharge();
            }
            PollQuickThrowCharge();
        }
    }
}
