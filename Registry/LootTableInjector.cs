using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.AddressableAssets;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Makes explosives spawn in world containers, by adding our icon to an EXISTING loot tag
    /// rather than inventing a new spawn rate.
    ///
    /// How the game's loot works (Loot_Mgr, UI.dll):
    ///   - Loot_Mgr._All_Loot_Icons is All_Loot_Icons[] - each is { string _spawnLootTag;
    ///     AssetReference[] _all_Icons_Ref; } i.e. "this tag can produce these items".
    ///   - Each container references a Loot_Rate_Sets ScriptableObject holding
    ///     Loot_Spawn_Rate[] { _spawnLootTag, _spawnRateRange, _stackFactor }.
    ///   - On fill, for each rate entry it finds the All_Loot_Icons with the same tag and weights
    ///     it by _spawnRateRange * G_Save._config._Loot_Rate_Total * (1 + _lootCountRateBoost),
    ///     then picks icons from that tag's array.
    ///
    /// So appending one AssetReference to a tag's _all_Icons_Ref is all that is needed: the
    /// grenade inherits that tag's existing rarity and appears only in containers that already
    /// roll it. That keeps it in-context by construction - no new rate to balance, and it
    /// automatically respects the player's loot-rate config and looting skill.
    /// </summary>
    internal static class LootTableInjector
    {
        private static readonly FieldInfo AllLootIconsField =
            AccessTools.Field(typeof(Loot_Mgr), "_All_Loot_Icons");

        private static readonly Type AllLootIconsType =
            AccessTools.Inner(typeof(Loot_Mgr), "All_Loot_Icons");

        private static readonly FieldInfo TagField =
            (AllLootIconsType != null) ? AccessTools.Field(AllLootIconsType, "_spawnLootTag") : null;

        private static readonly FieldInfo IconsField =
            (AllLootIconsType != null) ? AccessTools.Field(AllLootIconsType, "_all_Icons_Ref") : null;

        private static bool _injected;

        internal static void Reset()
        {
            _injected = false;
        }

        /// <summary>
        /// Runs once, the first time a loot window is opened - by then Loot_Mgr.ins exists and its
        /// serialized arrays are populated.
        /// </summary>
        internal static void TryInject()
        {
            if (_injected || !Plugin.EnableLootSpawning.Value)
            {
                return;
            }

            Loot_Mgr mgr = Loot_Mgr.ins;
            if (mgr == null || AllLootIconsField == null || TagField == null || IconsField == null)
            {
                return;
            }

            _injected = true;

            try
            {
                var array = AllLootIconsField.GetValue(mgr) as Array;
                if (array == null || array.Length == 0)
                {
                    Plugin.Log.LogWarning("[Loot] Loot_Mgr._All_Loot_Icons is empty; cannot inject.");
                    return;
                }

                // The tag names live in serialized data, not code, so log them once - picking the
                // right tag is the whole game here and it cannot be determined statically.
                var tags = new List<string>();
                for (int i = 0; i < array.Length; i++)
                {
                    object entry = array.GetValue(i);
                    tags.Add(TagField.GetValue(entry) as string ?? "<null>");
                }
                Plugin.Log.LogInfo("[Loot] available loot tags: " + string.Join(", ", tags.ToArray()));

                string[] wanted = Plugin.LootTags.Value
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                int added = 0;
                foreach (ExplosiveDef def in Plugin.Defs)
                {
                    if (def == null || string.IsNullOrEmpty(def.IconGuid) || !def.SpawnsInLoot)
                    {
                        continue;
                    }

                    for (int i = 0; i < array.Length; i++)
                    {
                        object entry = array.GetValue(i);
                        string tag = TagField.GetValue(entry) as string;
                        if (string.IsNullOrEmpty(tag) || !MatchesAny(tag, wanted))
                        {
                            continue;
                        }

                        var refs = IconsField.GetValue(entry) as AssetReference[];
                        var list = new List<AssetReference>(refs ?? new AssetReference[0]);

                        // Idempotent: re-running must not stack duplicates, which would silently
                        // multiply our spawn weight within the tag.
                        bool already = list.Exists(r => r != null && r.AssetGUID == def.IconGuid);
                        if (already)
                        {
                            continue;
                        }

                        list.Add(new AssetReference(def.IconGuid));
                        IconsField.SetValue(entry, list.ToArray());
                        // Structs live in the array by value - write the box back or the edit is lost.
                        array.SetValue(entry, i);
                        added++;

                        Plugin.Log.LogInfo($"[Loot] '{def.Tag}' added to loot tag '{tag}'.");
                    }
                }

                if (added == 0)
                {
                    Plugin.Log.LogWarning(
                        $"[Loot] no loot tag matched '{Plugin.LootTags.Value}'. Set LootTags to one or more of " +
                        "the tags logged above (comma-separated, case-insensitive substring match).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Loot] injection failed: " + ex.Message);
            }
        }

        private static bool MatchesAny(string tag, string[] wanted)
        {
            foreach (string w in wanted)
            {
                string t = w.Trim();
                if (t.Length > 0 && tag.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Loot_Mgr is alive and its serialized arrays are populated by the time a loot window
        /// opens. This must be a PREFIX: Enable_Loot_Window fills the container's slots inside
        /// itself, so injecting afterwards would miss the very first container opened.
        /// </summary>
        [HarmonyPatch(typeof(Loot_Mgr), nameof(Loot_Mgr.Enable_Loot_Window))]
        private static class EnableLootWindowPatch
        {
            private static void Prefix()
            {
                TryInject();
            }
        }
    }
}
