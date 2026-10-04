using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Shared bits of a thrown explosive: who threw it and when it was spawned. Detonation
    /// timing/trigger and the actual damage shape are per-subclass (GrenadeProjectile: timed
    /// fuse, uniform AoE; MolotovProjectile: detonate on impact, then a lingering fire pool).
    /// </summary>
    internal abstract class ExplosiveProjectile : MonoBehaviour
    {
        public C_Controller_Base Thrower;

        protected float SpawnTime;

        protected virtual void Awake()
        {
            SpawnTime = Time.time;
        }
    }
}
