using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace HumanHostExplosives.Registry
{
    /// <summary>
    /// Makes explosives rarer than an ordinary item of their loot tag.
    ///
    /// The game picks each container slot's item uniformly from the chosen tag's array:
    /// <c>array[Random.Range(0, array.Length)]</c>, inside Loot_Mgr's local function
    /// Init_Loot_Data (the only Random.Range(int,int) -> ldelem.ref in it, verified against the
    /// IL). A transpiler passes that pick through Filter: when it lands on one of our items, it is
    /// kept only with probability Loot.LootChance; otherwise the slot re-picks a vanilla item from
    /// the same tag. Every other item's odds are untouched, and the tag's own spawn weight is too.
    /// </summary>
    internal static class LootRarity
    {
        /// <summary>Our GUID -> the tag arrays it was injected into (for the re-pick).</summary>
        private static readonly Dictionary<string, List<AssetReference[]>> Injected = new Dictionary<string, List<AssetReference[]>>();
        private static readonly HashSet<string> Ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static void Record(string guid, AssetReference[] array)
        {
            Ours.Add(guid);
            if (!Injected.TryGetValue(guid, out List<AssetReference[]> list))
            {
                Injected[guid] = list = new List<AssetReference[]>();
            }
            list.Add(array);
        }

        public static AssetReference Filter(AssetReference picked)
        {
            try
            {
                if (picked == null || Ours.Count == 0 || !Ours.Contains(picked.AssetGUID))
                {
                    return picked;
                }
                if (UnityEngine.Random.value < Plugin.LootChance.Value)
                {
                    return picked;
                }
                if (!Injected.TryGetValue(picked.AssetGUID, out List<AssetReference[]> arrays) || arrays.Count == 0)
                {
                    return picked;
                }
                AssetReference[] array = arrays[UnityEngine.Random.Range(0, arrays.Count)];
                for (int tries = 0; tries < 8; tries++)
                {
                    AssetReference alt = array[UnityEngine.Random.Range(0, array.Length)];
                    if (alt != null && !Ours.Contains(alt.AssetGUID))
                    {
                        return alt;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Loot] rarity filter: " + ex.Message);
            }
            // Never null - the game dereferences the pick (UI:9213).
            return picked;
        }

        /// <summary>Applied by hand (not PatchAll) so a changed game method can't abort other patches.</summary>
        internal static void Apply(Harmony harmony)
        {
            try
            {
                MethodInfo target = typeof(Loot_Mgr).GetMethods(AccessTools.all | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name.Contains("Init_Loot_Data"));
                if (target == null)
                {
                    Plugin.Log.LogWarning("[Loot] Init_Loot_Data not found - explosives keep ordinary loot odds.");
                    return;
                }
                harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(LootRarity), nameof(Transpiler))));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Loot] rarity patch failed - explosives keep ordinary loot odds: " + ex.Message);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo filter = AccessTools.Method(typeof(LootRarity), nameof(Filter));
            List<CodeInstruction> list = instructions.ToList();
            int patched = 0;
            for (int i = 0; i < list.Count - 1; i++)
            {
                if (list[i].operand is MethodInfo mi && mi.Name == "Range" && mi.DeclaringType == typeof(UnityEngine.Random) &&
                    mi.ReturnType == typeof(int) && list[i + 1].opcode == OpCodes.Ldelem_Ref)
                {
                    list.Insert(i + 2, new CodeInstruction(OpCodes.Call, filter));
                    patched++;
                    i += 2;
                }
            }
            Plugin.Log.LogInfo(patched == 1
                ? "[Loot] rarity filter installed."
                : $"[Loot] rarity filter matched {patched} pick site(s) (expected 1).");
            return list;
        }
    }
}
