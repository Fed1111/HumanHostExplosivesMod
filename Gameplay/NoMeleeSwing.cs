using HarmonyLib;
using HumanHostExplosives.Registry;

namespace HumanHostExplosives
{
    /// <summary>
    /// Explosives are clones of a melee tool (ExplosiveItemRegistry), so the held model still carries
    /// Weapon_Melee, whose Updator calls On_Attack on LMB unless the belt "LMB use" prompt is already
    /// active. On the first click that prompt isn't up yet, so the template's swing played - which read
    /// as a throw when placing a mine. Our items never melee: refuse the attack for our tags.
    /// </summary>
    [HarmonyPatch(typeof(Weapon_Melee), nameof(Weapon_Melee.On_Attack))]
    internal static class NoMeleeSwing
    {
        private static bool Prefix(Weapon_Melee __instance)
        {
            Slot_Info slot = __instance._thisToolSlot;
            if (slot == null || slot._iconInfoPrefab == null) return true;
            return ExplosiveItemRegistry.FindByTag(slot._iconInfoPrefab._Tag) == null;
        }
    }
}
