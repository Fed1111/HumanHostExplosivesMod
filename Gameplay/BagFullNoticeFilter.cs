using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// While explosives are breaking things, the game shows "Backpack and belt are both full." once per
    /// resource payout even though the resources land on the ground (ExplosionDrops) and the bag is not
    /// full. Swallow that one notice while a demolition is live, and log once where it came from so the
    /// source can be fixed properly.
    /// </summary>
    [HarmonyPatch]
    internal static class BagFullNoticeFilter
    {
        private const string BagFull = "Backpack and belt are both full.";
        private static bool _traced;

        private static bool Prepare() => AccessTools.TypeByName("NotificationSystem") != null;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            System.Type t = AccessTools.TypeByName("NotificationSystem");   // not in a referenced assembly
            if (t == null)
            {
                return Enumerable.Empty<MethodBase>();
            }
            return AccessTools.GetDeclaredMethods(t)
                .Where(m => m.Name == "Add_Notice" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string));
        }

        private static bool Prefix(object[] __args)
        {
            if (__args == null || __args.Length == 0 || !(__args[0] is string text) || text != BagFull)
            {
                return true;
            }
            Vector3 player = Player_Input.ins != null ? Player_Input.ins.transform.position : Vector3.zero;
            if (ExplosionDrops.Scope <= 0 && Time.time > ExplosionDamage.DemolitionWindowUntil && !ExplosionDrops.NearMark(player, 120f, out _))
            {
                return true;
            }
            if (!_traced)
            {
                _traced = true;
                Plugin.Log.LogInfo("[Drops] suppressed a 'bag full' notice during demolition; raised from:\n" + System.Environment.StackTrace);
            }
            return false;
        }
    }
}
