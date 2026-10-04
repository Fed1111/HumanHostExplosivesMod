using HarmonyLib;

namespace HumanHostExplosives.Diagnostics
{
    /// <summary>
    /// Temporary observation patches - log every time the VANILLA game itself calls the two
    /// methods our own explosion damage relies on (Battle_Info.MinusHP and
    /// TopOnHit.Process_Smashed_Shard), so a real axe hit against a stubborn structure (like the
    /// ZoneSmashBI "AB_Pool" wall that our own sweep struggles to damage) shows exactly which
    /// object identity and code path the game's own, definitely-working system uses. Not meant to
    /// stay long-term; remove once the buildable-damage investigation is done.
    /// </summary>
    [HarmonyPatch(typeof(Battle_Info), "MinusHP")]
    internal static class Battle_Info_MinusHP_Diagnostic
    {
        private static void Postfix(Battle_Info __instance, float minusValue)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            string fatherName = __instance.FatherBI != null ? __instance.FatherBI.name : "null";
            string itemType = __instance.FatherBI != null ? __instance.FatherBI._ItemType.ToString() : "?";
            Plugin.Log.LogInfo($"[VanillaDiag] Battle_Info.MinusHP called: piece='{__instance.name}' nameIndex={__instance.nameIndex} FatherBI='{fatherName}' (ItemType={itemType}) minusValue={minusValue:F0}");
        }
    }

    [HarmonyPatch(typeof(TopOnHit), "Process_Smashed_Shard")]
    internal static class TopOnHit_ProcessSmashedShard_Diagnostic
    {
        private static void Postfix(Battle_Info SmashedShard, bool fromPlayer, bool entityBulletHit, bool forceRun)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            string fatherName = SmashedShard.FatherBI != null ? SmashedShard.FatherBI.name : "null";
            string itemType = SmashedShard.FatherBI != null ? SmashedShard.FatherBI._ItemType.ToString() : "?";
            Plugin.Log.LogInfo($"[VanillaDiag] TopOnHit.Process_Smashed_Shard called: piece='{SmashedShard.name}' FatherBI='{fatherName}' (ItemType={itemType}) fromPlayer={fromPlayer}");
        }
    }

    // Turns out there are (at least) two separate walls in play: our sweep is reaching a 1000 HP
    // one successfully, but the ORIGINAL 80 HP wall the whole investigation started with still
    // takes zero damage from grenades. Since Battle_Info.MinusHP/Process_Smashed_Shard never fired
    // for it either, these two patches check whether vanilla calls ZoneSmash_BI_MinusHP or
    // SysHouse_BigWall_MinusHP for it specifically - if NEITHER fires on an axe hit, there's a
    // fifth damage system (or a dedicated non-Build_Info Door script) we haven't found yet.
    [HarmonyPatch(typeof(Smash_Fallen_Manager), "ZoneSmash_BI_MinusHP")]
    internal static class SmashFallenManager_ZoneSmashBIMinusHP_Diagnostic
    {
        private static void Postfix(Build_Info BI, UnityEngine.Vector3 hitPoint, UnityEngine.Collider hitCol, float damage, bool isFromPlayer)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            Plugin.Log.LogInfo($"[VanillaDiag] ZoneSmash_BI_MinusHP called: BI='{BI.name}' (ItemType={BI._ItemType}) hitCol='{hitCol.name}' damage={damage:F0} isFromPlayer={isFromPlayer}");
        }
    }

    [HarmonyPatch(typeof(Smash_Fallen_Manager), "SysHouse_BigWall_MinusHP")]
    internal static class SmashFallenManager_SysHouseBigWallMinusHP_Diagnostic
    {
        private static void Postfix(Build_Info bigWallBI, float damage, UnityEngine.Vector3 hitPoint)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            Plugin.Log.LogInfo($"[VanillaDiag] SysHouse_BigWall_MinusHP called: BI='{bigWallBI.name}' (ItemType={bigWallBI._ItemType}) damage={damage:F0}");
        }
    }

    // ZoneSmash_Shard_MinusHP is a close sibling of ZoneSmash_BI_MinusHP - also drives the same
    // UI_Control.ins.Show_Target_Block_HP_Bar UI, but for a cell that's already been partially
    // sliced into a smaller fragment shard from earlier damage, rather than a fresh whole cell.
    [HarmonyPatch(typeof(Smash_Fallen_Manager), "ZoneSmash_Shard_MinusHP")]
    internal static class SmashFallenManager_ZoneSmashShardMinusHP_Diagnostic
    {
        private static void Postfix(Build_Info BI, UnityEngine.Vector3 hitPoint, UnityEngine.Collider hitCol, float damage, bool isFromPlayer)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            Plugin.Log.LogInfo($"[VanillaDiag] ZoneSmash_Shard_MinusHP called: BI='{BI.name}' (ItemType={BI._ItemType}) hitCol='{hitCol.name}' damage={damage:F0} isFromPlayer={isFromPlayer}");
        }
    }

    // The most direct approach: instead of guessing which HP-mutating method the game calls for
    // this specific structure, patch the ONE method we know for certain gets called (it drives the
    // "X / Y" HP bar UI the player actually sees) and print a full stack trace - this reveals the
    // real caller immediately, regardless of what it's named or which class it lives on.
    [HarmonyPatch(typeof(UI_Control), "Show_Target_Block_HP_Bar")]
    internal static class UIControl_ShowTargetBlockHPBar_Diagnostic
    {
        private static void Prefix(string text, float fillRate, UnityEngine.Color backGroundColor)
        {
            if (!Plugin.EnableDiagnostics.Value)
            {
                return;
            }
            Plugin.Log.LogInfo($"[VanillaDiag] Show_Target_Block_HP_Bar called: text='{text}' fillRate={fillRate:F2}\n{System.Environment.StackTrace}");
        }
    }
}
