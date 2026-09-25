using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Line-of-sight shrapnel damage, shared by the nail bomb and the improvised mine. Moved out of
    /// NailbombProjectile unchanged (0.3.0) - only the per-blast bleed flag became a local.
    /// </summary>
    internal static class Shrapnel
    {
        /// <summary>
        /// Damages every character in range that shrapnel can actually reach.
        ///
        /// The first version sprayed N rays evenly over a sphere and damaged whatever each one hit.
        /// That is physically honest and useless in practice: a human-sized target only subtends a
        /// tiny fraction of a sphere, so at any real distance almost no rays find it. Measured in
        /// game, 48 fragments landed 1-2 hits for ~4 damage out of a possible 200.
        ///
        /// So this inverts the test - for each candidate target, sample a few points on its body
        /// and see how many are in line of sight. Cover still works exactly as before (that is the
        /// whole point of the weapon), and partial cover now gives partial damage, but the damage
        /// a fully exposed target takes is deterministic instead of a lottery.
        /// </summary>
        internal static int Fire(Vector3 origin, float radius, float maxDamage, C_Controller_Base thrower, string logTag)
        {
            Creature_Mgr creatureMgr = Creature_Mgr.ins;
            if (creatureMgr == null || creatureMgr.capCol_To_Controller == null)
            {
                Plugin.Log.LogWarning($"[{logTag}] Creature_Mgr not available; no fragment damage.");
                return 0;
            }

            // Sampled up the body: a target crouched behind a wall should catch the head shots only.
            float[] heights = { 0.4f, 1.0f, 1.6f };
            int connected = 0;
            bool bledThisBlast = false;
            float radiusSqr = radius * radius;

            foreach (C_Controller_Base victim in creatureMgr.capCol_To_Controller.Values)
            {
                if (victim == null)
                {
                    continue;
                }
                if (!Plugin.AllowSelfDamage.Value && victim == thrower)
                {
                    continue;
                }

                Vector3 basePos = victim.transform.position;
                float distSqr = (basePos - origin).sqrMagnitude;
                if (distSqr > radiusSqr)
                {
                    continue;
                }

                int clear = 0;
                for (int i = 0; i < heights.Length; i++)
                {
                    Vector3 target = basePos + Vector3.up * heights[i];
                    Vector3 delta = target - origin;
                    float dist = delta.magnitude;
                    if (dist < 0.01f)
                    {
                        clear++;
                        continue;
                    }

                    // Anything that is not this victim's own collider counts as cover.
                    if (Physics.Raycast(origin, delta / dist, out RaycastHit hit, dist,
                                        ~0, QueryTriggerInteraction.Ignore))
                    {
                        C_Controller_Base blocker = ResolveCharacter(creatureMgr, hit.collider);
                        if (blocker != victim)
                        {
                            continue;   // cover did its job for this sample point
                        }
                    }
                    clear++;
                }

                if (clear == 0)
                {
                    continue;
                }

                // Fragment density falls off with distance as the spray spreads out.
                float dist2 = Mathf.Sqrt(distSqr);
                float falloff = Mathf.Clamp01(1f - dist2 / radius);
                float exposure = clear / (float)heights.Length;
                float dmg = maxDamage * exposure * falloff * Plugin.NailbombFragmentDamage.Value;

                if (victim == thrower)
                {
                    dmg *= Plugin.SelfDamageMultiplier.Value;
                }
                if (dmg <= 0.5f)
                {
                    continue;
                }

                connected++;

                if (Plugin.NailbombCausesBleed.Value && !bledThisBlast && victim._isPlayer)
                {
                    bledThisBlast = true;
                    try
                    {
                        if (Skill_Mgr.ins != null)
                        {
                            Skill_Mgr.ins.Start_Bleeding();
                            Plugin.Log.LogInfo($"[{logTag}] applied Bleeding to the player.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[{logTag}] Start_Bleeding threw: {ex.Message}");
                    }
                }

                try
                {
                    ExplosionDamage.Apply(basePos + Vector3.up, 0.6f, dmg, thrower,
                                          hitFlyForce: 0.35f, hitReact: 0.5f);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[{logTag}] per-target Apply threw: {ex}");
                }

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo(
                        $"[{logTag}] target at {dist2:F1}m: {clear}/{heights.Length} exposed, " +
                        $"falloff {falloff:F2} -> {dmg:F0} dmg");
                }
            }

            return connected;
        }

        /// <summary>
        /// Maps a hit collider back to the character that owns it. Creature_Mgr keys its lookup by
        /// capsule collider, but a fragment usually lands on a ragdoll bone collider instead, so
        /// walk up the hierarchy before giving up.
        /// </summary>
        private static C_Controller_Base ResolveCharacter(Creature_Mgr mgr, Collider col)
        {
            if (col == null)
            {
                return null;
            }

            if (mgr.capCol_To_Controller.TryGetValue(col, out C_Controller_Base direct) && direct != null)
            {
                return direct;
            }

            return col.GetComponentInParent<C_Controller_Base>();
        }
    }
}
