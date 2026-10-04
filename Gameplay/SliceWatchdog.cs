using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// The game cuts one wall section at a time, gated on Smash_Fallen_Manager._corSlice: Slice_Zone does
    /// `_corSlice = StartCoroutine(delay())`, and delay() sets `_corSlice = null` as its last step. When delay()
    /// finishes WITHIN StartCoroutine (nothing left to wait for - likely with the synchronous cutting a
    /// demolition uses), it clears the flag first and the assignment then sets it to the finished coroutine:
    /// _corSlice stays non-null forever and no wall anywhere can be cut again (all explosives stopped doing
    /// damage). An exception inside delay() leaves it stuck the same way.
    ///
    /// This tracks which delay() coroutines are really still running (their MoveNext returned true and they
    /// haven't finished or thrown since). _corSlice set while none is running = stuck; the zone queue clears it.
    /// </summary>
    [HarmonyPatch]
    internal static class SliceWatchdog
    {
        private static readonly Dictionary<object, float> Running = new Dictionary<object, float>();
        private static int _cleared;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type t in AllNested(typeof(Smash_Fallen_Manager)))
            {
                if (t.Name.Contains("<Slice_Zone>g__delay"))
                {
                    MethodInfo m = AccessTools.Method(t, "MoveNext");
                    if (m != null)
                    {
                        yield return m;
                    }
                }
            }
        }

        private static IEnumerable<Type> AllNested(Type t)
        {
            foreach (Type n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return n;
                foreach (Type d in AllNested(n))
                {
                    yield return d;
                }
            }
        }

        private static void Postfix(object __instance, bool __result)
        {
            if (__result)
            {
                Running[__instance] = Time.time;
            }
            else
            {
                Running.Remove(__instance);
            }
        }

        private static Exception Finalizer(object __instance, Exception __exception)
        {
            if (__exception != null)
            {
                Running.Remove(__instance);
            }
            return __exception;
        }

        /// <summary>Clears _corSlice if it's set but no cut is actually running. True if it did.</summary>
        internal static bool Unstick(Smash_Fallen_Manager smash)
        {
            if (smash == null || smash._corSlice == null)
            {
                return false;
            }
            // A coroutine stopped from outside (StopCoroutine, object disabled) never reports back: forget any
            // that have been silent for a long time. A live one steps at least every few seconds.
            if (Running.Count > 0)
            {
                foreach (object k in Running.Where(kv => Time.time - kv.Value > 30f).Select(kv => kv.Key).ToList())
                {
                    Running.Remove(k);
                }
            }
            if (Running.Count > 0)
            {
                return false;
            }
            smash._corSlice = null;
            if (_cleared++ < 5)
            {
                Plugin.Log.LogInfo("[Demolition] the game's wall-cutting slot was left marked busy with nothing running - released it.");
            }
            return true;
        }
    }
}
