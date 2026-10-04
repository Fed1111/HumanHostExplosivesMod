using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// The save record of a cut world-building section (Smash_Fallen_Manager._zoneData.data, one SmashedData per
    /// section) says whether pieces of it are still standing (hasShardsLeft) and which (existShards). On load,
    /// every section marked "pieces left" is cut AGAIN from scratch - slowly, one at a time, holding the game's
    /// single cutting slot - just to restore those pieces.
    ///
    /// The game only corrects the record once it finds the section's piece container empty; when the last piece
    /// goes the container is already gone, so a section destroyed outright stays "pieces left: yes, which: none".
    /// Every section a demolition charge took out ended up like that: hundreds of pointless re-cuts after
    /// loading, the slot busy for minutes, and explosives doing nothing until they were done.
    ///
    ///  - MarkGone: the demolition records its sections as fully gone when it breaks them.
    ///  - SmashCell prefix: a loading record that claims pieces but lists none is loaded as gone (exactly what
    ///    the re-cut would have ended with - it deletes every piece not listed), which also repairs saves
    ///    that already have them.
    /// </summary>
    internal static class ZoneSaveFix
    {
        private static int _repaired;

        private static IDictionary Data(Smash_Fallen_Manager smash) =>
            Traverse.Create(smash).Field("_zoneData").Field("data").GetValue() as IDictionary;

        /// <summary>Records a section as destroyed with nothing left standing.</summary>
        internal static void MarkGone(Smash_Fallen_Manager smash, Build_Info bi, Vector3Int posRound, int childIndex, float sliceSize)
        {
            IDictionary data = Data(smash);
            if (data == null)
            {
                return;
            }
            object rec = data.Contains(posRound) ? data[posRound] : null;
            if (rec == null)
            {
                Type recType = data.GetType().GetGenericArguments()[1];
                rec = Activator.CreateInstance(recType);
                Traverse r = Traverse.Create(rec);
                r.Field("houseSib").SetValue(bi.SysHouseTopSibling);
                r.Field("smashChildSib").SetValue(childIndex);
                r.Field("sliceSize").SetValue(sliceSize);
                data[posRound] = rec;
            }
            Traverse t = Traverse.Create(rec);
            t.Field("hasShardsLeft").SetValue(0);
            t.Field("existShards").GetValue<HashSet<int>>()?.Clear();
        }

        [HarmonyPatch(typeof(Smash_Fallen_Manager), "SmashCell")]
        private static class LoadRepair
        {
            private static void Prefix(Smash_Fallen_Manager __instance, object[] __args)
            {
                try
                {
                    Traverse p = Traverse.Create(__args[0]);
                    if (!p.Field("isLoad").GetValue<bool>())
                    {
                        return;
                    }
                    IDictionary data = Data(__instance);
                    Vector3Int pos = p.Field("realCellPosRound").GetValue<Vector3Int>();
                    if (data == null || !data.Contains(pos))
                    {
                        return;
                    }
                    Traverse rec = Traverse.Create(data[pos]);
                    HashSet<int> exist = rec.Field("existShards").GetValue<HashSet<int>>();
                    if (rec.Field("hasShardsLeft").GetValue<int>() != 0 && (exist == null || exist.Count == 0))
                    {
                        rec.Field("hasShardsLeft").SetValue(0);
                        if (_repaired++ == 0)
                        {
                            Plugin.Log.LogInfo("[Demolition] loading destroyed wall sections as gone instead of re-cutting them (sections with no pieces left).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_repaired++ == 0)
                    {
                        Plugin.Log.LogWarning("[Demolition] save repair skipped: " + ex.Message);
                    }
                }
            }
        }
    }
}
