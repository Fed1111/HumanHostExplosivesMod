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
            if (!Plugin.EnableDiagnostics.Value || DPI == null || DPI._ItemInfo == null)
            {
                return;
            }

            string iconGuid = DPI._ItemInfo._IconRef != null ? DPI._ItemInfo._IconRef.AssetGUID : "(none)";

            // Icon_Info lives on the icon prefab, not the dropped-world Item_Info - resolve it via
            // the same Addressables key so we can log _Tag/_SlotType/ModelRef alongside it. This is
            // exactly the data Registry.TemplateIconGuid/TemplateModelGuid and per-item RecipeMaterial
            // config need: pick up a stackable Hand_R item (a bandage/food) to find a template, or
            // any craftable material to find its recipe GUID.
            string tag = "(unresolved)";
            string slotType = "(unresolved)";
            bool canStack = false;
            string modelRefGuid = "(unresolved)";
            try
            {
                var iconObj = UnityEngine.AddressableAssets.Addressables
                    .LoadAssetAsync<UnityEngine.GameObject>(iconGuid).WaitForCompletion();
                Icon_Info iconInfo = iconObj != null ? iconObj.GetComponent<Icon_Info>() : null;
                if (iconInfo != null)
                {
                    tag = iconInfo._Tag;
                    slotType = iconInfo._SlotType.ToString();
                    canStack = iconInfo._Can_Stack;
                    modelRefGuid = iconInfo.ModelRef != null ? iconInfo.ModelRef.AssetGUID : "(none)";
                }
            }
            catch
            {
                // best-effort diagnostic only
            }

            Plugin.Log.LogInfo(
                $"[ItemPickup] name='{DPI.gameObject.name}' assetRefKey='{DPI._ItemInfo.assetRef_Key}' iconGUID='{iconGuid}' " +
                $"tag='{tag}' slotType='{slotType}' canStack={canStack} modelRefGuid='{modelRefGuid}' picker='{charItemIcons.gameObject.name}'");
        }
    }
}
