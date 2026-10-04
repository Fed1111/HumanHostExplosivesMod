using HarmonyLib;

namespace HumanHostExplosives
{
    /// <summary>
    /// Wall pieces from the game's mesh slicer are sometimes slivers; when the game makes one a convex collider
    /// PhysX logs "ConvexHullLib::cleanupVertices: Less than four valid vertices" and the sliver simply gets no
    /// collision. Harmless - but the game's error panel shows it to the player, and a demolition cuts hundreds
    /// of pieces. This keeps that one message out of the panel; every other error still reaches it.
    ///
    /// (Tried first: swapping slivers' colliders before they're made convex. Its check caught ordinary pieces
    /// too - they vanished, and the collapse broke, since the game finds what's still attached by colliding
    /// the pieces. Don't touch the pieces.)
    /// </summary>
    internal static class ShardSanity
    {
        [HarmonyPatch(typeof(Error_Report_Panel), "OnLogMessageThreaded")]
        private static class ErrorPanelFilter
        {
            private static bool Prefix(string condition)
            {
                return condition == null || !condition.Contains("ConvexHullLib::cleanupVertices");
            }
        }
    }
}
