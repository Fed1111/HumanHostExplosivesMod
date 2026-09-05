using System.Collections.Generic;
using MLSpace;
using UnityEngine;

namespace HumanHostExplosives
{
    internal static class ExplosionDamage
    {
        /// <summary>
        /// Applies falloff damage to every living creature within radius of center, using the
        /// game's own trap/fall damage pipeline (Creature_Mgr + Smash_Fallen_Manager.Minus_Char_HP).
        /// Returns the number of creatures hit.
        /// </summary>
        internal static int Apply(
            Vector3 center,
            float radius,
            float maxDamage,
            C_Controller_Base attacker,
            float hitFlyForce = 1f,
            float hitReact = 0.8f)
        {
            Creature_Mgr creatureMgr = Creature_Mgr.ins;
            Smash_Fallen_Manager smashMgr = Smash_Fallen_Manager.ins;
            if (creatureMgr == null || smashMgr == null || creatureMgr.capCol_To_Controller == null)
            {
                Plugin.Log.LogWarning("[Explosion] Creature_Mgr or Smash_Fallen_Manager not available (no world loaded?). Skipping AoE damage.");
                return 0;
            }

            Global_Infos globalInfos = Global_Infos.ins;
            int mask = globalInfos != null ? globalInfos.Mask_Creature.value : -1;

            Collider[] hitColliders = Physics.OverlapSphere(center, radius, mask, QueryTriggerInteraction.Ignore);
            var alreadyHit = new HashSet<C_Controller_Base>();
            int hits = 0;

            foreach (Collider col in hitColliders)
            {
                if (!creatureMgr.capCol_To_Controller.TryGetValue(col, out C_Controller_Base ctrl) || ctrl == null)
                {
                    continue;
                }
                if (!alreadyHit.Add(ctrl))
                {
                    continue;
                }
                if (attacker != null && ctrl == attacker)
                {
                    continue;
                }
                if (ctrl.char_Status == null || ctrl.char_Status._CurrHP <= 0f)
                {
                    continue;
                }

                Vector3 targetPos = ctrl.capCol != null ? ctrl.capCol.bounds.center : ctrl.transform.position;
                float dist = Vector3.Distance(center, targetPos);
                float falloff = Mathf.Clamp01(1f - dist / radius);
                if (falloff <= 0f)
                {
                    continue;
                }

                Vector3 hitDirect = targetPos - center;
                hitDirect = hitDirect.sqrMagnitude > 0.0001f ? hitDirect.normalized : Vector3.up;

                float damage = maxDamage * falloff;
                BodyColliderScript bodyScript = ctrl._ragDollMgr ? ctrl._ragDollMgr._headBodyScript : null;

                smashMgr.Minus_Char_HP(
                    ctrl,
                    bodyScript,
                    targetPos,
                    switchToAnimancer: true,
                    damage: damage,
                    damageInterval: 0.05f,
                    hitDirect: hitDirect,
                    hitFlyForce: hitFlyForce,
                    hitReact: hitReact,
                    mustHitDown: false,
                    bloodPos: targetPos,
                    bloodParticle: null,
                    useDefaultBloodPar: true,
                    getEXP: true);

                hits++;
                Plugin.Log.LogInfo($"[Explosion] Hit {ctrl.name} at {dist:F1}m (falloff={falloff:F2}) for {damage:F0} dmg.");
            }

            return hits;
        }
    }
}
