using HarmonyLib;

namespace HumanHostExplosives
{
    /// <summary>
    /// Diagnostic-only, broader net than ItemPickupLogger: Start_Swap_Slots is the general
    /// "move an item between two slots" primitive used by drag-and-drop (Slot_Drag.OnEndDragMy)
    /// for backpack reorganization, belt equips, and storage-box/chest transfers - none of which
    /// go through Item_Slot_Mgr.PickItemIn_Bag_Belt (that's world-pickup only). Confirmed missing
    /// data points for two different transfer paths already (a crafted item, then items pulled
    /// from a chest into the backpack) before adding this - rather than keep guessing which
    /// specific method a given in-game action routes through, this logs both ends of every swap.
    /// Unlike ItemPickupLogger, Slot_Info._iconInfoPrefab is already resolved here (no Addressables
    /// call needed) since it's just reading a slot that already has its icon loaded.
    /// </summary>
    [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.Start_Swap_Slots))]
    internal static class SlotTransferLogger_Patch
    {
        private static void Postfix(Slot_Info thisSlot, Slot_Info targetSlot)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }

            LogSlot("thisSlot", thisSlot);
            LogSlot("targetSlot", targetSlot);
        }

        private static void LogSlot(string label, Slot_Info slot)
        {
            if (slot == null || slot._IsEmptySlot || slot._iconInfoPrefab == null)
            {
                return;
            }

            Icon_Info iconInfo = slot._iconInfoPrefab;
            string iconGuid = slot._IconRef != null ? slot._IconRef.AssetGUID : "(none)";
            string modelRefGuid = iconInfo.ModelRef != null ? iconInfo.ModelRef.AssetGUID : "(none)";

            Plugin.Log.LogInfo(
                $"[SlotTransfer] {label}: iconGUID='{iconGuid}' tag='{iconInfo._Tag}' slotType='{iconInfo._SlotType}' " +
                $"canStack={iconInfo._Can_Stack} modelRefGuid='{modelRefGuid}' stack='{slot._StackText}'");
        }
    }
}
