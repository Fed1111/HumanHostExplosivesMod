using HarmonyLib;

namespace HumanHostExplosives
{
    /// <summary>
    /// Diagnostic-only: logs identifying data (prefab name, Addressables key/GUID) for every
    /// item the player picks up, so we can look up the real Addressables identity of existing
    /// items (e.g. "soda can") to reuse as a base for new items, without a working decompiled
    /// dump of the packed Addressables catalog.
    /// </summary>
    [HarmonyPatch(typeof(Item_Slot_Mgr), nameof(Item_Slot_Mgr.PickItemIn_Bag_Belt))]
    internal static class ItemPickupLogger_Patch
    {
        private static void Postfix(Drop_Pick_Item DPI, Char_Item_Icons charItemIcons)
        {
            if (DPI == null || DPI._ItemInfo == null)
            {
                return;
            }

            string iconGuid = DPI._ItemInfo._IconRef != null ? DPI._ItemInfo._IconRef.AssetGUID : "(none)";

            Plugin.Log.LogInfo(
                $"[ItemPickup] name='{DPI.gameObject.name}' assetRefKey='{DPI._ItemInfo.assetRef_Key}' iconGUID='{iconGuid}' picker='{charItemIcons.gameObject.name}'");
        }
    }
}
