using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Improvised nail bomb: a pipe packed with powder and nails.
    ///
    /// Deliberately NOT a reskinned grenade. A grenade applies uniform area damage that ignores
    /// geometry; a nail bomb throws discrete fragments, so this fires a spread of shrapnel rays
    /// from the blast point and only damages the FIRST thing each ray reaches. The consequences
    /// are what make it play differently:
    ///
    ///   - Cover works. Anything behind a wall, a crate or a tree takes nothing from that ray.
    ///   - Damage concentrates at close range, where more rays intersect the same target.
    ///   - It is vicious against soft targets and poor against structures, which is the opposite
    ///     of the grenade - so the two have a reason to coexist rather than one superseding
    ///     the other.
    ///
    /// Structural damage is intentionally a fraction of the grenade's: nails do not bring down
    /// walls.
    /// </summary>
    internal class NailbombProjectile : ExplosiveProjectile
    {
        public float FuseSeconds = 3f;
        public float MaxDamage = 60f;

        private bool _detonated;
        private bool _bledThisBlast;

        private void Update()
        {
            if (!_detonated && Time.time - SpawnTime >= FuseSeconds)
            {
                Detonate();
            }
        }

        private void Detonate()
        {
            _detonated = true;
            Vector3 origin = transform.position;
            float radius = Plugin.NailbombRadius.Value;

            int fragmentHits = 0;
            try
            {
                fragmentHits = FireShrapnel(origin, radius);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Nailbomb] FireShrapnel threw: {ex}");
            }

            // A small structural component: the pipe itself still goes off. Scaled well down from
            // the grenade so nail bombs are an anti-personnel tool, not a demolition one.
            int buildableHits = 0;
            try
            {
                buildableHits = ExplosionDamage.ApplyToBuildables(
                    origin, radius * 0.5f, Plugin.NailbombBlockDamage.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Nailbomb] ApplyToBuildables threw: {ex}");
            }

            try
            {
                NoiseAttractor.Emit(origin, Plugin.NailbombNoiseRadius.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Noise] Emit threw: {ex}");
            }

            try
            {
                ExplosionVisual.Spawn(origin, radius * 0.7f);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Nailbomb] ExplosionVisual.Spawn threw: {ex}");
            }

            try
            {
                // Quieter than a grenade, with a sharper metallic tail - fragments striking hard
                // surfaces rather than a second concussive boom.
                ExplosionSound.Play(origin, Plugin.ExplosionVolume.Value * 0.8f, metallic: true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Nailbomb] ExplosionSound.Play threw: {ex}");
            }

            Plugin.Log.LogInfo(
                $"[Nailbomb] Detonated at {origin}, radius={radius}, " +
                $"fragmentHits={fragmentHits}, buildableHits={buildableHits}.");
            Destroy(gameObject);
        }

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
        private int FireShrapnel(Vector3 origin, float radius)
        {
            Creature_Mgr creatureMgr = Creature_Mgr.ins;
            if (creatureMgr == null || creatureMgr.capCol_To_Controller == null)
            {
                Plugin.Log.LogWarning("[Nailbomb] Creature_Mgr not available; no fragment damage.");
                return 0;
            }

            // Sampled up the body: a target crouched behind a wall should catch the head shots only.
            float[] heights = { 0.4f, 1.0f, 1.6f };
            int connected = 0;
            float radiusSqr = radius * radius;

            foreach (C_Controller_Base victim in creatureMgr.capCol_To_Controller.Values)
            {
                if (victim == null)
                {
                    continue;
                }
                if (!Plugin.AllowSelfDamage.Value && victim == Thrower)
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
                float dmg = MaxDamage * exposure * falloff * Plugin.NailbombFragmentDamage.Value;

                if (victim == Thrower)
                {
                    dmg *= Plugin.SelfDamageMultiplier.Value;
                }
                if (dmg <= 0.5f)
                {
                    continue;
                }

                connected++;

                if (Plugin.NailbombCausesBleed.Value && !_bledThisBlast && victim._isPlayer)
                {
                    _bledThisBlast = true;
                    try
                    {
                        if (Skill_Mgr.ins != null)
                        {
                            Skill_Mgr.ins.Start_Bleeding();
                            Plugin.Log.LogInfo("[Nailbomb] applied Bleeding to the player.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Nailbomb] Start_Bleeding threw: {ex.Message}");
                    }
                }

                try
                {
                    ExplosionDamage.Apply(basePos + Vector3.up, 0.6f, dmg, Thrower,
                                          hitFlyForce: 0.35f, hitReact: 0.5f);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[Nailbomb] per-target Apply threw: {ex}");
                }

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo(
                        $"[Nailbomb] target at {dist2:F1}m: {clear}/{heights.Length} exposed, " +
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
