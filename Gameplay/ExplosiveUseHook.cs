using HarmonyLib;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// The whole throw mechanic. The game already has a first-class "equip a stackable
    /// consumable to the hand, LMB uses it, consume 1 from the stack" pipeline (used by
    /// food/bandages/etc) - a grenade fits that shape exactly, so there's no need to build a
    /// custom weapon rig. Two hooks:
    ///
    ///  - Is_Icon_Usable: makes the game treat our tag as "usable" at all (this gates whether
    ///    the "LMB" prompt appears when the item is equipped, and whether Trigger_Use_For_Belt_Slot
    ///    even gets called from UI_Control.Update()).
    ///  - Trigger_Use_For_Belt_Slot: the vanilla body no-ops for an unrecognized tag (its own
    ///    Click_Use_Button() switch falls through without matching), so our postfix does the
    ///    actual throw + stack decrement here.
    /// </summary>
    internal static class ExplosiveUseHook
    {
        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Is_Icon_Usable))]
        private static class IsIconUsablePatch
        {
            private static void Postfix(Icon_Info iconPrefab, ref bool __result)
            {
                if (!__result && iconPrefab != null && ExplosiveItemRegistry.FindByTag(iconPrefab._Tag) != null)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Trigger_Use_For_Belt_Slot))]
        private static class TriggerUsePatch
        {
            private static void Postfix(Item_Slot_Mgr __instance, Slot_Info slot)
            {
                if (slot == null || slot._iconInfoPrefab == null)
                {
                    return;
                }

                ExplosiveDef def = ExplosiveItemRegistry.FindByTag(slot._iconInfoPrefab._Tag);
                if (def == null)
                {
                    return;
                }

                Transform camTrans = CamController.ins != null ? CamController.ins._mainCamTrans : null;
                if (camTrans == null)
                {
                    return;
                }

                ExplosiveSpawner.Throw(def, camTrans, Player_Input.ins);

                __instance.Item_Stack_Minus_1(slot);

                // Item_Stack_Minus_1 -> Clear_Slot_Info never removes the game object welded to the
                // player's hand (_playerInput._On_Hand_Item) - only DropItem/swap paths do that
                // normally. Without this, throwing your last grenade leaves a ghost grenade in hand.
                if (string.IsNullOrEmpty(slot._StackText))
                {
                    __instance.Del_OnHand_Slot_Item(slot);
                    __instance.Hide_UseClickIcon();
                }
            }
        }
    }
}
