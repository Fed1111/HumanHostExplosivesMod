using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace HumanHostExplosives
{
    /// <summary>
    /// Resources knocked loose by an explosion land on the ground as pickups, instead of teleporting
    /// into the player's bag.
    ///
    /// Breaking a block rewards resources through Item_Slot_Mgr.Pick_Enviro_Item (Build_System's
    /// Hit_To_Collect_Resource, "from player" hits) - which puts them straight in the inventory. While an
    /// explosive is doing the breaking (Scope &gt; 0: a blast, the queued zone-wall hits, demolition
    /// retries), that call is redirected to the game's own ground drop: the same temp-slot DropItem call
    /// vanilla uses when your bag is full (Drop_Remaining_Items_On_Ground, UI.decompiled), but at the broken
    /// block instead of at the player. Melee and everything else is untouched.
    /// </summary>
    internal static class ExplosionDrops
    {
        /// <summary>&gt; 0 while an explosive is breaking things.</summary>
        internal static int Scope;

        /// <summary>
        /// Where explosives are breaking things right now. Zone walls pay out from inside the slice coroutine
        /// (Build_System:20153), and the collapse they start pays out for every block that comes down with
        /// it - all frames or seconds later, outside Scope, and for blocks the blast never touched. So any
        /// reward for an object within MarkRadius of a live mark also lands on the ground. Each queued
        /// wall cut refreshes its mark, so the window lasts as long as the demolition does.
        /// </summary>
        private static readonly List<(Vector3 Point, float Until)> Marks = new List<(Vector3, float)>();
        private const float MarkRadius = 25f;

        internal static void MarkRecent(GameObject building, Vector3 point)
        {
            float until = Time.time + 8f;
            for (int i = Marks.Count - 1; i >= 0; i--)
            {
                if (Marks[i].Until < Time.time)
                {
                    Marks.RemoveAt(i);
                }
                else if ((Marks[i].Point - point).sqrMagnitude < 1f)
                {
                    Marks[i] = (Marks[i].Point, until);
                    return;
                }
            }
            Marks.Add((point, until));
        }

        /// <summary>The nearest live mark within radius of pos.</summary>
        internal static bool NearMark(Vector3 pos, float radius, out Vector3 point)
        {
            point = pos;
            float best = radius * radius;
            bool found = false;
            foreach (var m in Marks)
            {
                float d = (m.Point - pos).sqrMagnitude;
                if (m.Until >= Time.time && d <= best)
                {
                    best = d;
                    point = m.Point;
                    found = true;
                }
            }
            return found;
        }

        private static readonly MethodInfo DropItemMI = AccessTools.Method(typeof(Item_Slot_Mgr), "DropItem");

        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Pick_Enviro_Item))]
        private static class PickPatch
        {
            private static bool Prefix(Item_Slot_Mgr __instance, GameObject pickedBI_Obj, int pickStack, AssetReference pickIconRef,
                                       bool destroyOrigBI, bool isPropBI, ref bool __result)
            {
                Vector3 markPoint = Vector3.zero;
                bool recent = pickedBI_Obj != null && NearMark(pickedBI_Obj.transform.position,
                    pickedBI_Obj.CompareTag("ZoneSmashBI") ? 80f : MarkRadius, out markPoint); // a whole building's origin can be far off
                if ((Scope <= 0 && !recent) || !Plugin.ExplosionResourcesOnGround.Value || DropItemMI == null || pickStack <= 0 ||
                    pickIconRef == null || string.IsNullOrEmpty(pickIconRef.AssetGUID))
                {
                    return true;
                }
                try
                {
                    // A zone wall's "BI" is the whole building, whose origin can be metres away - use the hit point.
                    Vector3 at = pickedBI_Obj == null ? Player_Input.ins.transform.position
                        : recent && pickedBI_Obj.CompareTag("ZoneSmashBI") ? markPoint
                        : pickedBI_Obj.transform.position;
                    at += new Vector3(UnityEngine.Random.Range(-0.4f, 0.4f), 0.4f, UnityEngine.Random.Range(-0.4f, 0.4f));
                    if (!Drop(__instance, pickIconRef, pickStack, at))
                    {
                        return true; // couldn't drop - fall back to vanilla (inventory)
                    }
                    // What vanilla does on a successful pick, minus the inventory and the pickup notice.
                    if (destroyOrigBI && isPropBI && pickedBI_Obj != null)
                    {
                        __instance._On_Pick_EnviroBI.Invoke(pickedBI_Obj);
                    }
                    __result = true;
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Drops] ground drop failed, giving to inventory instead: " + ex.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// When a support under your own build is removed, the game hands every block that falls off INTACT
        /// back to you (CheckFallen.Get_All_FallenGroups_AtOnce -> Get_Deleted_BI_Icon_Into_Bag_Belt). After a
        /// demolition charge that turned into a "Backpack and belt are both full." per block. Blown-down blocks
        /// land on the ground where they fell instead, like everything else the blast knocks loose.
        /// </summary>
        [HarmonyPatch(typeof(Build_System), nameof(Build_System.Get_Deleted_BI_Icon_Into_Bag_Belt))]
        private static class FallenBlockPatch
        {
            private static bool Prefix(Build_Info BI)
            {
                if (Time.time > ExplosionDamage.DemolitionWindowUntil || !Plugin.ExplosionResourcesOnGround.Value ||
                    BI == null || BI.ItemInfo == null || BI.ItemInfo._IconRef == null || string.IsNullOrEmpty(BI.ItemInfo._IconRef.AssetGUID) ||
                    Item_Slot_Mgr.Ins == null || DropItemMI == null)
                {
                    return true;
                }
                try
                {
                    return !Drop(Item_Slot_Mgr.Ins, BI.ItemInfo._IconRef, 1, BI.transform.position + Vector3.up * 0.3f);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Drops] fallen block ground drop failed: " + ex.Message);
                    return true;
                }
            }
        }

        private static bool Drop(Item_Slot_Mgr mgr, AssetReference iconRef, int stack, Vector3 at)
        {
            AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(iconRef.AssetGUID);
            handle.WaitForCompletion();
            try
            {
                Icon_Info icon = handle.Result != null ? handle.Result.GetComponent<Icon_Info>() : null;
                if (icon == null)
                {
                    return false;
                }
                var temp = new GameObject("HHE_Temp_ExplosionDrop");
                try
                {
                    Slot_Info slot = temp.AddComponent<Slot_Info>();
                    slot._slotIndex = -1;
                    slot._assetRefForSplit = iconRef;
                    slot._iconInfoPrefab = icon;
                    slot._initAlready = true;
                    DropItemMI.Invoke(mgr, new object[] { slot, true, (Vector3?)at, (int?)stack });
                }
                finally
                {
                    UnityEngine.Object.Destroy(temp);
                }
                return true;
            }
            finally
            {
                if (handle.IsValid())
                {
                    Safe_Addressables_Release.Release(handle);
                }
            }
        }
    }
}
