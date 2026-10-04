using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Why a demolished structure came down so slowly: the game cuts every wall section that breaks or
    /// falls (the blast's own cells AND every cell of the collapsing column above - Build_System
    /// Slice_Zone / the fall check at :20417) with BzKovSoft's mesh slicer in ASYNC mode - each plane's
    /// cut on a worker thread, awaited frame by frame, one section at a time. A pillar is dozens of
    /// sections, so the collapse crawled for tens of seconds.
    ///
    /// While a demolition is running, the slicer works synchronously instead (its own
    /// `asynchronously` switch - the same code path, just not on a thread): each section is cut within
    /// the frame it is reached, so the structure collapses in about a second, at the price of a few
    /// heavier frames. Every other slice in the game (melee, bullets) is untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class FastDemolitionSlicing
    {
        /// <summary>Set by the demolition zone queue; slicing is synchronous until then.</summary>
        internal static float ActiveUntil;

        private static FieldInfo _async;

        private static bool Prepare() => AccessTools.TypeByName("BzKovSoft.ObjectSlicer.BzSliceableBase") != null;

        private static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("BzKovSoft.ObjectSlicer.BzSliceableBase"), "SliceAsync");

        private static void Prefix(object __instance)
        {
            if (Time.time > ActiveUntil || !Plugin.FastDemolitionCollapse.Value)
            {
                return;
            }
            if (_async == null)
            {
                _async = AccessTools.Field(__instance.GetType(), "asynchronously");
            }
            _async?.SetValue(__instance, false);
        }
    }
}
