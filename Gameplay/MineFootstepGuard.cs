using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// A placed mine/charge sits on L_Scene so bullets and the pickup ray hit it (PlacedMine.Create). The
    /// game's footstep code treats every L_Scene collider as a Digger terrain mesh and walks
    /// transform.parent x4 to find its Sound_Terrain (Sound_FX Play_FootStep_Sound_At_Pos) - a mine has no
    /// parent, so every zombie step on one threw a NullReferenceException into the error panel (EA
    /// v0.8.317 report, 2026-10-01). A step on a mine plays no surface sound instead.
    /// </summary>
    [HarmonyPatch(typeof(Sound_Mgr), "Play_FootStep_Sound_At_Pos")]
    internal static class MineFootstepGuard
    {
        private static bool Prefix(Collider groundCol, bool isBoss)
        {
            return isBoss || groundCol == null || groundCol.GetComponentInParent<PlacedMine>() == null;
        }
    }
}
