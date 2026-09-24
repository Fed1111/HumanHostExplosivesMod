using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Physical thrown grenade: real Rigidbody flight, fuse timer, then AoE damage on detonation.
    /// </summary>
    internal class GrenadeProjectile : ExplosiveProjectile
    {
        public float FuseSeconds = 3f;
        public float MaxDamage = 120f;

        private bool _detonated;

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

            float radius = Plugin.ExplosionRadius.Value;
            float effectRadius = Plugin.GrenadeEffectRadius.Value;
            int hits = 0;
            try
            {
                hits = ExplosionDamage.Apply(transform.position, radius, MaxDamage, Thrower, requireLineOfSight: true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Grenade] ExplosionDamage.Apply threw: {ex}");
            }

            int buildableHits = 0;
            try
            {
                buildableHits = ExplosionDamage.ApplyToBuildables(transform.position, effectRadius, Plugin.BuildableDamage.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Grenade] ExplosionDamage.ApplyToBuildables threw: {ex}");
            }

            try
            {
                NoiseAttractor.Emit(transform.position, Plugin.GrenadeNoiseRadius.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Noise] Emit threw: {ex}");
            }

            try
            {
                ExplosionVisual.Spawn(transform.position, effectRadius, Plugin.GrenadeFlashScale.Value, Plugin.GrenadeParticulateScale.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Grenade] ExplosionVisual.Spawn threw: {ex}");
            }

            try
            {
                ExplosionSound.Play(transform.position, Plugin.ExplosionVolume.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Grenade] ExplosionSound.Play threw: {ex}");
            }

            Plugin.Log.LogInfo($"[Grenade] Detonated at {transform.position}, damageRadius={radius}, effectRadius={effectRadius}, creatureHits={hits}, buildableHits={buildableHits}.");
            Destroy(gameObject);
        }
    }
}
