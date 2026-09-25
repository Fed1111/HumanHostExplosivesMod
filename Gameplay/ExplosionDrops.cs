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
        /// Buildings an explosion just hit, and where. Zone walls pay their resources out from inside the
        /// slice coroutine (Build_System:20153), frames after the hit and outside Scope - so a reward from
        /// one of these within a few seconds also lands on the ground, at the blast point.
        /// </summary>
        private static readonly Dictionary<GameObject, (Vector3 Point, float Until)> Recent = new Dictionary<GameObject, (Vector3, float)>();

        internal static void MarkRecent(GameObject building, Vector3 point)
        {
            if (building != null)
            {
                Recent[building] = (point, Time.time + 6f);
            }
        }

        private static readonly MethodInfo DropItemMI = AccessTools.Method(typeof(Item_Slot_Mgr), "DropItem");

        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Pick_Enviro_Item))]
        private static class PickPatch
        {
            private static bool Prefix(Item_Slot_Mgr __instance, GameObject pickedBI_Obj, int pickStack, AssetReference pickIconRef,
                                       bool destroyOrigBI, bool isPropBI, ref bool __result)
            {
                bool recent = pickedBI_Obj != null && Recent.TryGetValue(pickedBI_Obj, out var mark) && Time.time <= mark.Until;
                if ((Scope <= 0 && !recent) || !Plugin.ExplosionResourcesOnGround.Value || DropItemMI == null || pickStack <= 0 ||
                    pickIconRef == null || string.IsNullOrEmpty(pickIconRef.AssetGUID))
                {
                    return true;
                }
                try
                {
                    // A zone wall's "BI" is the whole building, whose origin can be metres away - use the hit point.
                    Vector3 at = recent ? Recent[pickedBI_Obj].Point
                        : pickedBI_Obj != null ? pickedBI_Obj.transform.position : Player_Input.ins.transform.position;
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
