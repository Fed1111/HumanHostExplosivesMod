using System;
using HarmonyLib;

namespace HumanHostExplosives.Gameplay
{
    /// <summary>
    /// Suppresses one specific, known-benign Unity warning from reaching Error_Report_Panel's
    /// "Error detected, press F8 to open" hint - not a broad error-hiding patch.
    ///
    /// "Non-convex MeshCollider with non-kinematic Rigidbody is no longer supported since Unity 5"
    /// is Unity's own generic PhysX warning, logged for numerous vanilla world props (crates,
    /// suitcases, cardboard boxes, foliage) that ship with a non-convex mesh collider on a
    /// non-kinematic rigidbody - a pre-existing configuration issue in the game's own assets, not
    /// anything this mod creates. It only becomes visible because a grenade's explosion applies
    /// physics force to every dynamic rigidbody in its blast radius at once, which reliably sweeps
    /// up 10-20+ of these mis-configured props in a single throw and fires the warning once per
    /// prop - lighting up the game's generic error hint on every single explosion, for something
    /// that is not a bug, is not caused by this mod's logic, and is not actionable by the player.
    /// Confirmed cosmetic: physics on the affected props behaves normally regardless.
    ///
    /// Filters ONLY this exact message; every other error/exception still reaches the panel and
    /// the hint exactly as vanilla.
    /// </summary>
    internal static class BenignErrorFilter
    {
        private const string NonConvexMeshColliderWarning =
            "Non-convex MeshCollider with non-kinematic Rigidbody";

        [HarmonyPatch(typeof(Error_Report_Panel), "RecordError")]
        private static class RecordErrorPatch
        {
            private static bool Prefix(string condition)
            {
                bool isBenign = condition != null &&
                    condition.IndexOf(NonConvexMeshColliderWarning, StringComparison.Ordinal) >= 0;
                return !isBenign;
            }
        }
    }
}
