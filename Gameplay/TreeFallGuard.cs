using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// When a tree starts to fall, Tree_Falling_Handler.Start walks every tree the game has turned into an
    /// object (TerrainTreeManager._allSpawnedTreeMCs) and the falling tree's own terrain colliders, calling
    /// Physics.IgnoreCollision between them. A tree destroyed since it was spawned (felled, cleaned up) can
    /// still be in that set, and a collider list can hold a destroyed entry - IgnoreCollision then throws a
    /// NullReferenceException, Start stops before starting its clean-up coroutine, and the fallen tree is
    /// never turned into logs. Felling many trees at once (demolition charges) made it show up.
    /// Just before Start runs, dead entries are dropped from both. The game's code is otherwise untouched.
    /// </summary>
    [HarmonyPatch(typeof(Tree_Falling_Handler), "Start")]
    internal static class TreeFallGuard
    {
        private static void Prefix(Tree_Falling_Handler __instance)
        {
            try
            {
                TerrainTreeManager.ins?._allSpawnedTreeMCs?.RemoveWhere(mc => mc == null);
                if (__instance._treeTerraCols != null && __instance._treeTerraCols.Any(c => c == null))
                {
                    __instance._treeTerraCols = __instance._treeTerraCols.Where(c => c != null).ToArray();
                }
                else if (__instance._treeTerraCols == null)
                {
                    __instance._treeTerraCols = new Collider[0];
                }
            }
            catch
            {
                // a safety check must never be the thing that breaks the tree
            }
        }
    }
}
