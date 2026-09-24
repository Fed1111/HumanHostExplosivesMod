using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.AddressableAssets;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Injects a craft recipe for each configured ExplosiveDef into the matching workbench's
    /// crafting UI, the first time that workbench window is opened. Ported from the
    /// already-installed "MiningDrill" mod's Craft_Items.OnEnable pattern.
    ///
    /// Craft_Items.CraftItemData/PerIconData/PerMatData are `internal struct`s with `internal`
    /// fields, invisible to our assembly at compile time, so this reads/writes them through
    /// reflection instead of direct field access.
    /// </summary>
    internal static class CraftRecipeInjector
    {
        private static readonly Type CraftItemDataType = AccessTools.Inner(typeof(Craft_Items), "CraftItemData");
        private static readonly Type PerIconDataType = AccessTools.Inner(typeof(Craft_Items), "PerIconData");
        private static readonly Type PerMatDataType = AccessTools.Inner(typeof(Craft_Items), "PerMatData");

        private static readonly FieldInfo CraftItemsDataField = AccessTools.Field(typeof(Craft_Items), "_CraftItemsData");
        private static readonly FieldInfo WorkbenchTypeField = AccessTools.Field(typeof(Craft_Items), "_workbenchType");
        private static readonly FieldInfo BigCategoryField = (CraftItemDataType != null) ? AccessTools.Field(CraftItemDataType, "BigCategory") : null;
        private static readonly FieldInfo PerIconDataField = (CraftItemDataType != null) ? AccessTools.Field(CraftItemDataType, "perIconData") : null;
        private static readonly FieldInfo IconRefField = (PerIconDataType != null) ? AccessTools.Field(PerIconDataType, "iconRef") : null;
        private static readonly FieldInfo IconInfoField = (PerIconDataType != null) ? AccessTools.Field(PerIconDataType, "iconInfo") : null;
        private static readonly FieldInfo CraftNumField = (PerIconDataType != null) ? AccessTools.Field(PerIconDataType, "craftNum") : null;
        private static readonly FieldInfo CraftSecondsField = (PerIconDataType != null) ? AccessTools.Field(PerIconDataType, "craftSeconds") : null;
        private static readonly FieldInfo MatsDataField = (PerIconDataType != null) ? AccessTools.Field(PerIconDataType, "matsData") : null;
        private static readonly FieldInfo MatIconField = (PerMatDataType != null) ? AccessTools.Field(PerMatDataType, "matIcon") : null;
        private static readonly FieldInfo MatNeedCountField = (PerMatDataType != null) ? AccessTools.Field(PerMatDataType, "matNeedCount") : null;

        private static readonly HashSet<Craft_Items> InjectedWindows = new HashSet<Craft_Items>();
        private static bool _reflectionWarningLogged;

        private static bool ReflectionReady =>
            CraftItemsDataField != null && WorkbenchTypeField != null && BigCategoryField != null &&
            PerIconDataField != null && IconRefField != null && IconInfoField != null &&
            CraftNumField != null && CraftSecondsField != null && MatsDataField != null &&
            MatIconField != null && MatNeedCountField != null;

        [HarmonyPatch(typeof(Craft_Items), "OnEnable")]
        private static class OnEnablePatch
        {
            private static void Postfix(Craft_Items __instance)
            {
                try
                {
                    if (Plugin.EnableDiagnostics.Value)
                    {
                        LogTabLayout(__instance);
                    }

                    if (!ReflectionReady)
                    {
                        if (!_reflectionWarningLogged)
                        {
                            _reflectionWarningLogged = true;
                            Plugin.Log.LogError("[Registry] Craft_Items reflection layout changed; recipe injection disabled");
                        }
                        return;
                    }

                    if (InjectedWindows.Contains(__instance))
                    {
                        return;
                    }

                    if (!ExplosiveItemRegistry.TryBuildAll())
                    {
                        return;
                    }

                    foreach (ExplosiveDef def in AllDefsWithRecipes())
                    {
                        TryInject(__instance, def);
                    }

                    InjectedWindows.Add(__instance);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[Registry] recipe injection failed: " + ex);
                }
            }
        }

        private static IEnumerable<ExplosiveDef> AllDefsWithRecipes()
        {
            if (Plugin.Defs == null)
            {
                yield break;
            }
            foreach (ExplosiveDef def in Plugin.Defs)
            {
                if (def.Recipe.Count > 0)
                {
                    yield return def;
                }
            }
        }

        private static void TryInject(Craft_Items craftItems, ExplosiveDef def)
        {
            if (def.Recipe.Count == 0 || string.IsNullOrEmpty(def.WorkbenchTypeName))
            {
                return;
            }
            if (def.RuntimeIconInfo == null)
            {
                // Item never finished registering (e.g. its mesh/texture is missing - see
                // ExplosiveItemRegistry.TryBuildAll). Injecting a recipe entry with a null
                // Icon_Info would corrupt this workbench's craft list, so skip it entirely
                // rather than fail partially.
                return;
            }
            if (!Enum.TryParse(def.WorkbenchTypeName, out Craft_Mgr.WorkbenchType targetType))
            {
                Plugin.Log.LogWarning($"[Registry] '{def.Tag}': unknown WorkbenchType '{def.WorkbenchTypeName}', skipping recipe injection.");
                return;
            }

            object workbenchValue = WorkbenchTypeField.GetValue(craftItems);
            if (!(workbenchValue is Craft_Mgr.WorkbenchType actualType) || actualType != targetType)
            {
                return;
            }

            Array craftItemsData = (Array)CraftItemsDataField.GetValue(craftItems);
            if (craftItemsData == null || def.TabIndex < 0 || def.TabIndex >= craftItemsData.Length)
            {
                Plugin.Log.LogWarning($"[Registry] '{def.Tag}': tab index {def.TabIndex} out of range for {targetType} ({craftItemsData?.Length ?? 0} tabs); skipping.");
                return;
            }

            object craftItemData = craftItemsData.GetValue(def.TabIndex);
            Array perIconData = PerIconDataField.GetValue(craftItemData) as Array;
            int existingCount = perIconData?.Length ?? 0;

            for (int i = 0; i < existingCount; i++)
            {
                object existing = perIconData.GetValue(i);
                if (IconRefField.GetValue(existing) is AssetReference existingRef &&
                    string.Equals(existingRef.AssetGUID, def.IconGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return; // already injected (e.g. tab reopened)
                }
            }

            Array newPerIconData = Array.CreateInstance(PerIconDataType, existingCount + 1);
            if (perIconData != null)
            {
                Array.Copy(perIconData, newPerIconData, existingCount);
            }
            newPerIconData.SetValue(BuildRecipeEntry(def), existingCount);

            PerIconDataField.SetValue(craftItemData, newPerIconData);
            craftItemsData.SetValue(craftItemData, def.TabIndex);

            Plugin.Log.LogInfo($"[Registry] '{def.Tag}' recipe injected into {targetType} tab {def.TabIndex}");
        }

        private static object BuildRecipeEntry(ExplosiveDef def)
        {
            Array mats = Array.CreateInstance(PerMatDataType, def.Recipe.Count);
            for (int i = 0; i < def.Recipe.Count; i++)
            {
                object mat = Activator.CreateInstance(PerMatDataType);
                MatIconField.SetValue(mat, new AssetReference(def.Recipe[i].Guid));
                MatNeedCountField.SetValue(mat, def.Recipe[i].Count);
                mats.SetValue(mat, i);
            }

            object entry = Activator.CreateInstance(PerIconDataType);
            IconRefField.SetValue(entry, new AssetReference(def.IconGuid));
            IconInfoField.SetValue(entry, def.RuntimeIconInfo);
            CraftNumField.SetValue(entry, 1);
            CraftSecondsField.SetValue(entry, def.CraftSeconds);
            MatsDataField.SetValue(entry, mats);
            return entry;
        }

        // Diagnostic only: helps find the right WorkbenchTypeName/TabIndex without guessing -
        // open any workbench once with Diagnostics.EnableItemPickupLogger on and read the log.
        private static void LogTabLayout(Craft_Items craftItems)
        {
            if (WorkbenchTypeField == null || CraftItemsDataField == null || BigCategoryField == null)
            {
                return;
            }
            object workbenchType = WorkbenchTypeField.GetValue(craftItems);
            Array craftItemsData = CraftItemsDataField.GetValue(craftItems) as Array;
            int tabCount = craftItemsData?.Length ?? 0;
            Plugin.Log.LogInfo($"[CraftDiag] Craft_Items opened: workbench={workbenchType}, tabs={tabCount}");
            for (int i = 0; i < tabCount; i++)
            {
                object tab = craftItemsData.GetValue(i);
                object bigCategory = BigCategoryField.GetValue(tab);
                string tabName = (bigCategory as UnityEngine.Object)?.name ?? "(null)";
                Plugin.Log.LogInfo($"[CraftDiag]   tab[{i}] = '{tabName}'");
            }
        }
    }
}
