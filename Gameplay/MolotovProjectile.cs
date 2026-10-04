using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Thrown Molotov: shatters on the first thing it hits (after a 0.1 s arm, so it can't break on
    /// the thrower) and spills a FirePool onto the ground below the impact. A direct hit on a
    /// character sets them alight straight away. The shatter is a one-off noise - zombies come to
    /// look and walk into the fire; the fire itself makes no further noise (a steady lure would
    /// spam the game's attract event every tick).
    /// </summary>
    internal class MolotovProjectile : ExplosiveProjectile
    {
        private const float ArmDelay = 0.1f;
        private const float FailsafeSeconds = 10f;

        private bool _detonated;

        private void Update()
        {
            // Never hit anything (fell out of the world, landed in an unloaded chunk) - just go.
            if (!_detonated && Time.time - SpawnTime > FailsafeSeconds)
            {
                _detonated = true;
                Destroy(gameObject);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (_detonated || Time.time - SpawnTime < ArmDelay)
            {
                return;
            }
            _detonated = true;

            ContactPoint contact = collision.GetContact(0);
            Vector3 impact = contact.point;

            try
            {
                SmallSounds.PlayShatter(impact, Plugin.ExplosionVolume.Value * 0.35f);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Molotov] shatter sound threw: " + ex);
            }

            try
            {
                NoiseAttractor.Emit(impact, Plugin.MolotovNoiseRadius.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Noise] Emit threw: " + ex);
            }

            C_Controller_Base direct = FireDamage.Resolve(collision.collider);
            Vector3 poolAt = GroundBelow(impact + contact.normal * 0.3f, impact);
            try
            {
                FirePool.Spawn(poolAt, Plugin.MolotovRadius.Value, Plugin.MolotovDuration.Value, Thrower);
                if (direct != null)
                {
                    BurnManager.Ignite(direct, Thrower);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Molotov] fire spawn threw: " + ex);
            }

            Plugin.Log.LogInfo($"[Molotov] shattered at {impact}, pool at {poolAt}, radius={Plugin.MolotovRadius.Value}, " +
                               $"duration={Plugin.MolotovDuration.Value}s{(direct != null ? ", direct hit on " + direct.name : "")}.");
            Destroy(gameObject);
        }

        /// <summary>
        /// Where the fuel lands: straight down from just off the impact surface, onto the first solid
        /// non-character surface. A bottle smashed against a wall pools at its foot, not on the wall.
        /// </summary>
        internal static Vector3 GroundBelow(Vector3 from, Vector3 fallback)
        {
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4f, WorldMask(), QueryTriggerInteraction.Ignore))
            {
                return hit.point;
            }
            return fallback;
        }

        /// <summary>Everything solid except characters, ragdolls, traps and UI.</summary>
        internal static int WorldMask()
        {
            Global_Infos g = Global_Infos.ins;
            if (g == null)
            {
                return ~0;
            }
            return ~(g.Mask_Creature.value | g.Mask_Ragdoll.value | g.Mask_Trap.value | g.Mask_UI.value);
        }
    }
}
