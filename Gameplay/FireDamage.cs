using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// One tick of fire damage on one character, through the same Minus_Char_HP entry point every
    /// other explosive uses - but called the way a vanilla TRAP calls it, not the way a blast does:
    ///
    ///  - switchToAnimancer false: no ragdoll hit reaction and no GPUI->Animancer body swap every
    ///    tick. Minus_Char_HP still forces the swap itself on the lethal tick, so deaths look normal.
    ///  - With the swap off, a zombie plays Play_OnHit_Anim_For_GPUI_Char - the vanilla trap
    ///    stagger (_zombieTrapAnims) - and the player's base implementation is empty, so burning
    ///    costs the player HP without knocking them about. (Build_System:18713, Creature:4295/4710)
    ///  - No blood: bloodParticle null and useDefaultBloodPar false.
    ///  - damageInterval -1: ticks are paced by the caller, not by the per-character hit timer.
    /// </summary>
    internal static class FireDamage
    {
        private static readonly HashSet<C_Controller_Base> Logged = new HashSet<C_Controller_Base>();

        internal static bool Tick(C_Controller_Base ctrl, float damage, C_Controller_Base attacker)
        {
            if (ctrl == null || damage <= 0f || ctrl.char_Status == null || ctrl.char_Status._CurrHP <= 0f)
            {
                return false;
            }

            if (ctrl._isPlayer)
            {
                if (!Plugin.AllowSelfDamage.Value)
                {
                    return false;
                }
                damage *= Plugin.SelfDamageMultiplier.Value * Plugin.PlayerFireDamageMultiplier.Value;
                if (damage <= 0f)
                {
                    return false;
                }
            }

            Smash_Fallen_Manager smash = Smash_Fallen_Manager.ins;
            if (smash == null)
            {
                return false;
            }

            Vector3 pos = ctrl.capCol != null ? ctrl.capCol.bounds.center : ctrl.transform.position;
            KillXp.Watch(ctrl);   // full weapon-kill XP if this kills it (see KillXp)
            smash.Minus_Char_HP(
                ctrl,
                null,
                pos,
                switchToAnimancer: false,
                damage: damage,
                damageInterval: -1f,
                hitDirect: Vector3.up,
                hitFlyForce: 0f,
                hitReact: 0f,
                mustHitDown: false,
                bloodPos: pos,
                bloodParticle: null,
                useDefaultBloodPar: false,
                getEXP: false);

            if (Logged.Add(ctrl))
            {
                if (Logged.Count > 256)
                {
                    Logged.Clear();
                }
                Plugin.Log.LogInfo($"[Fire] burning {ctrl.name} (MaxHP={ctrl.char_Status._MaxHP:F0}) for {damage:F0}/tick.");
            }
            return true;
        }

        /// <summary>
        /// A collider back to its character: Creature_Mgr's capsule map first, then the hierarchy
        /// (ragdoll bone colliders sit under the controller).
        /// </summary>
        internal static C_Controller_Base Resolve(Collider col)
        {
            if (col == null)
            {
                return null;
            }
            Creature_Mgr mgr = Creature_Mgr.ins;
            if (mgr != null && mgr.capCol_To_Controller != null &&
                mgr.capCol_To_Controller.TryGetValue(col, out C_Controller_Base direct) && direct != null)
            {
                return direct;
            }
            return col.GetComponentInParent<C_Controller_Base>();
        }
    }
}
