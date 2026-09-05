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
        private const string MolotovIconGuid = "11223344556677889900aabbccddeeff";
        private const string MolotovModelGuid = "ffeeddccbbaa00998877665544332211";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<float> ExplosionRadius;
        internal static ConfigEntry<float> ExplosionDamage;

        internal static ConfigEntry<string> TemplateIconGuid;
        internal static ConfigEntry<string> TemplateModelGuid;

        internal static ConfigEntry<bool> EnableDiagnostics;
        internal static ConfigEntry<KeyCode> DebugThrowGrenadeKey;

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
                "Explosives", "ExplosionDamage", 120f,
                "Grenade damage applied at the center of the explosion, falling off linearly to zero at the edge of ExplosionRadius.");

            TemplateIconGuid = Config.Bind(
                "Registry", "TemplateIconGuid", "",
                "assetRef_Key/icon GUID of an existing stackable Hand_R consumable (e.g. a bandage or food item) to clone as the structural base for our own items. " +
                "Find it by picking up such an item in-game with Diagnostics.EnableItemPickupLogger on and reading the BepInEx log. Required - registration is skipped while empty.");
            TemplateModelGuid = Config.Bind(
                "Registry", "TemplateModelGuid", "",
                "ModelRef GUID (the held 3D model, not the icon) of the same template item, from the same log line. Required - registration is skipped while empty.");

            EnableDiagnostics = Config.Bind(
                "Diagnostics", "EnableDiagnostics", true,
                "Logs extra detail (Icon_Info fields, craft window tab layout) needed to fill in the Registry/* and per-item recipe config above. Safe to leave on; turn off once configured.");
            DebugThrowGrenadeKey = Config.Bind(
                "Debug", "ThrowGrenadeKey", KeyCode.G,
                "Press this key in-game to throw a grenade directly, bypassing the inventory/equip system - for testing ExplosionDamage/GrenadeProjectile independent of item registration.");

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
                MaxStack = 5,
                TooltipName = "Grenade",
                TooltipType = "Explosive",
                TooltipInstruction = "A hand grenade. Equip and press LMB to throw it; detonates after a short fuse.",
                WorkbenchTypeName = Config.Bind("Grenade", "WorkbenchType", "BiochemicalWorkbench",
                    "Craft_Mgr.WorkbenchType this recipe should appear under. See CraftDiag log lines for the workbench you open.").Value,
                TabIndex = Config.Bind("Grenade", "CraftTabIndex", 0,
                    "Which tab (0-based) in that workbench's crafting UI to add the recipe to. See CraftDiag log lines.").Value,
                CraftSeconds = Config.Bind("Grenade", "CraftSeconds", 30f, "Crafting time in seconds.").Value,
            };
            AddRecipeSlot(grenade, "Grenade", 1);
            AddRecipeSlot(grenade, "Grenade", 2);
            AddRecipeSlot(grenade, "Grenade", 3);

            var molotov = new ExplosiveDef
            {
                Kind = ExplosiveKind.Molotov,
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
                    "Craft_Mgr.WorkbenchType this recipe should appear under.").Value,
                TabIndex = Config.Bind("Molotov", "CraftTabIndex", 0,
                    "Which tab (0-based) in that workbench's crafting UI to add the recipe to.").Value,
                CraftSeconds = Config.Bind("Molotov", "CraftSeconds", 15f, "Crafting time in seconds.").Value,
            };
            AddRecipeSlot(molotov, "Molotov", 1);
            AddRecipeSlot(molotov, "Molotov", 2);
            AddRecipeSlot(molotov, "Molotov", 3);

            return new List<ExplosiveDef> { grenade, molotov };
        }

        private void AddRecipeSlot(ExplosiveDef def, string section, int slotNumber)
        {
            string guid = Config.Bind(section, $"RecipeMaterial{slotNumber}Guid", "",
                $"Material #{slotNumber}'s icon GUID for this recipe. Leave empty to skip this slot. Find candidate material GUIDs the same way as the template (Diagnostics.EnableItemPickupLogger).").Value;
            int count = Config.Bind(section, $"RecipeMaterial{slotNumber}Count", 1,
                $"How many of material #{slotNumber} the recipe needs.").Value;
            if (!string.IsNullOrEmpty(guid))
            {
                def.Recipe.Add((guid, count));
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(DebugThrowGrenadeKey.Value))
            {
                ExplosiveDef grenade = Defs.Find(d => d.Kind == ExplosiveKind.Grenade);
                Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
                if (camTrans == null)
                {
                    Log.LogWarning("No camera found; cannot spawn test grenade.");
                    return;
                }
                ExplosiveSpawner.Throw(grenade, camTrans, Player_Input.ins);
            }
        }
    }
}
