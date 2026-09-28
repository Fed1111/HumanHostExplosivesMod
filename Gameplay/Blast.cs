using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>Everything that distinguishes one high-explosive blast from another.</summary>
    internal struct BlastParams
    {
        internal string LogTag;
        internal float Damage;
        internal float DamageRadius;
        internal float EffectRadius;
        internal float BuildableDamage;
        internal float NoiseRadius;
        internal float FlashScale;
        internal float ParticulateScale;
        internal float HitFlyForce;
        internal float Volume;
        internal bool WholeBlocks;   // demolition: break every block it touches completely (ApplyToBuildables)
        internal bool Heavy;         // sound bank: the deep, long "heavy" explosions
        internal bool Metallic;      // synthesized-sound fallback: the metallic variant
        internal string Sound;       // sound bank category override (e.g. "ap"); null = from Heavy
        internal int Debris;         // chunks thrown off a nearby structure (DebrisBurst)
        internal float DebrisForce;
    }

    /// <summary>
    /// A high-explosive detonation: creature damage (respecting cover), structure damage, zombie
    /// noise, visual, sound, then sympathetic detonation of nearby mines. This was the body of
    /// GrenadeProjectile.Detonate; 0.3.0 moved it here so the contact grenade and the mine blow up
    /// exactly the same way. Each step is isolated so one failing never skips the rest.
    /// </summary>
    internal static class Blast
    {
        internal static void Detonate(BlastParams p, Vector3 center, C_Controller_Base attacker)
        {
            int hits = 0;
            try
            {
                hits = ExplosionDamage.Apply(center, p.DamageRadius, p.Damage, attacker,
                                             hitFlyForce: p.HitFlyForce, requireLineOfSight: true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[{p.LogTag}] ExplosionDamage.Apply threw: {ex}");
            }

            int buildableHits = 0;
            try
            {
                buildableHits = ExplosionDamage.ApplyToBuildables(center, p.EffectRadius, p.BuildableDamage, p.WholeBlocks);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[{p.LogTag}] ExplosionDamage.ApplyToBuildables threw: {ex}");
            }

            try
            {
                NoiseAttractor.Emit(center, p.NoiseRadius);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Noise] Emit threw: {ex}");
            }

            try
            {
                ExplosionVisual.Spawn(center, p.EffectRadius, p.FlashScale, p.ParticulateScale);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[{p.LogTag}] ExplosionVisual.Spawn threw: {ex}");
            }

            try
            {
                ExplosionSound.Play(center, p.Volume, p.Metallic, p.Heavy, p.Sound);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[{p.LogTag}] ExplosionSound.Play threw: {ex}");
            }

            try
            {
                if (buildableHits > 0)
                {
                    DebrisBurst.Spawn(center, p.Debris, p.DebrisForce);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[{p.LogTag}] debris threw: {ex}");
            }

            MineManager.OnBlast(center, p.EffectRadius);

            Plugin.Log.LogInfo($"[{p.LogTag}] Detonated at {center}, damageRadius={p.DamageRadius}, " +
                               $"effectRadius={p.EffectRadius}, creatureHits={hits}, buildableHits={buildableHits}.");
        }

        internal static BlastParams Grenade()
        {
            return new BlastParams
            {
                LogTag = "Grenade",
                Damage = Plugin.ExplosionDamage.Value,
                DamageRadius = Plugin.ExplosionRadius.Value,
                EffectRadius = Plugin.GrenadeEffectRadius.Value,
                BuildableDamage = Plugin.BuildableDamage.Value,
                NoiseRadius = Plugin.GrenadeNoiseRadius.Value,
                FlashScale = Plugin.GrenadeFlashScale.Value,
                ParticulateScale = Plugin.GrenadeParticulateScale.Value,
                HitFlyForce = 1f,
                Volume = Plugin.ExplosionVolume.Value,
                Debris = 8,
                DebrisForce = 9f,
            };
        }

        internal static BlastParams ContactGrenade()
        {
            BlastParams p = Grenade();
            p.LogTag = "ContactGrenade";
            p.Damage = Plugin.ContactDamage.Value;
            p.DamageRadius = Plugin.ContactRadius.Value;
            p.EffectRadius = Plugin.ContactEffectRadius.Value;
            p.BuildableDamage = Plugin.ContactBuildableDamage.Value;
            return p;
        }

        /// <summary>Demolition charge: modest radius against creatures, heavy against structures.</summary>
        internal static BlastParams Demo()
        {
            BlastParams p = Grenade();
            p.LogTag = "DemoCharge";
            p.Damage = Plugin.DemoDamage.Value;
            p.DamageRadius = Plugin.DemoRadius.Value;
            p.EffectRadius = Plugin.DemoEffectRadius.Value;
            p.BuildableDamage = Plugin.DemoBuildableDamage.Value;
            p.NoiseRadius = Plugin.DemoNoiseRadius.Value;
            p.FlashScale = Plugin.GrenadeFlashScale.Value * 1.2f;
            p.ParticulateScale = Plugin.GrenadeParticulateScale.Value * 1.4f;
            p.HitFlyForce = 1.6f;
            p.Volume = Plugin.ExplosionVolume.Value * 1.25f;
            p.WholeBlocks = Plugin.DemoDestroysBlocks.Value;   // off: plain block damage (DemoBuildableDamage) - user 2026-09-28
            p.Heavy = true;
            p.Debris = 26;
            p.DebrisForce = 13f;
            return p;
        }

        internal static BlastParams Mine()
        {
            BlastParams p = Grenade();
            p.LogTag = "Mine";
            p.Damage = Plugin.MineDamage.Value;
            p.DamageRadius = Plugin.MineRadius.Value;
            p.EffectRadius = Plugin.MineEffectRadius.Value;
            p.BuildableDamage = Plugin.MineBuildableDamage.Value;
            p.NoiseRadius = Plugin.MineNoiseRadius.Value;
            // A buried charge throws more dirt than flash.
            p.FlashScale = Plugin.GrenadeFlashScale.Value * 0.8f;
            p.ParticulateScale = Plugin.GrenadeParticulateScale.Value * 1.25f;
            p.HitFlyForce = 1.5f;
            p.Volume = Plugin.ExplosionVolume.Value * 1.1f;
            p.Heavy = true;
            p.Debris = 12;
            p.DebrisForce = 11f;
            return p;
        }
    }
}
