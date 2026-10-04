using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Improvised nail bomb: a pipe packed with powder and nails.
    ///
    /// Deliberately NOT a reskinned grenade. Both now respect cover (ExplosionDamage.Apply's
    /// requireLineOfSight, shared by GrenadeProjectile), but this fires a spread of shrapnel from
    /// the blast point rather than a uniform-falloff sphere, and only damages the FIRST thing each
    /// ray reaches. The consequences are what make it play differently from the grenade:
    ///
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
                fragmentHits = Shrapnel.Fire(origin, radius, MaxDamage, Thrower, "Nailbomb");
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
                ExplosionVisual.Spawn(
                    origin,
                    radius * Plugin.NailbombVisualRadiusMultiplier.Value,
                    Plugin.NailbombFlashScale.Value,
                    Plugin.NailbombParticulateScale.Value);
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

            MineManager.OnBlast(origin, radius * Plugin.NailbombVisualRadiusMultiplier.Value);

            Plugin.Log.LogInfo(
                $"[Nailbomb] Detonated at {origin}, radius={radius}, " +
                $"fragmentHits={fragmentHits}, buildableHits={buildableHits}.");
            Destroy(gameObject);
        }
    }
}
