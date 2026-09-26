using System;
using System.Collections;
using HarmonyLib;

namespace HumanHostExplosives
{
    /// <summary>
    /// Rubble from a collapse is placed through the game's build-deploy coroutine
    /// (Shards_Collide_Ground -> Build_System.Directly_Deploy_Object -> To_Deploy_Object), which runs only while
    /// Build_System.cor_Deploy is null and clears it itself when done. During a big demolition collapse,
    /// dozens of pieces land at once and that coroutine can throw (seen: NullReferenceException at
    /// Build_System.cs:2703). An exception ends the coroutine WITHOUT clearing cor_Deploy - after which every
    /// later deploy is silently skipped: no more rubble, and potentially no more building for the player
    /// until a restart. This wraps the coroutine so an exception just ends that one deploy and clears the flag.
    /// Only acts when the game's code throws; normal deploys are passed through untouched.
    /// </summary>
    [HarmonyPatch(typeof(Build_System), "To_Deploy_Object")]
    internal static class DeployGuard
    {
        private static int _logged;

        private static void Postfix(Build_System __instance, ref IEnumerator __result)
        {
            if (__result != null)
            {
                __result = Guard(__instance, __result);
            }
        }

        private static IEnumerator Guard(Build_System bs, IEnumerator inner)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext())
                    {
                        yield break;
                    }
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    if (_logged++ < 5)
                    {
                        Plugin.Log.LogWarning("[DeployGuard] the game's deploy coroutine threw (" + ex.GetType().Name + ": " + ex.Message +
                                              ") - skipped that one placement and released the build lock.");
                    }
                    try
                    {
                        Traverse.Create(bs).Field("cor_Deploy").SetValue(null);
                    }
                    catch
                    {
                        // field missing in a future build: nothing more we can do
                    }
                    yield break;
                }
                yield return current;
            }
        }
    }
}
