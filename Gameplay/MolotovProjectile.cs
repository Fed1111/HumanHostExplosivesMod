using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Physical thrown Molotov: detonates on first impact (no fuse) and leaves behind a
    /// FirePool that ticks damage over time. The game has no burn/fire-DoT system of its own
    /// (confirmed absent from every decompiled managed assembly), so the whole fire effect is
    /// mod-side.
    /// </summary>
    internal class MolotovProjectile : ExplosiveProjectile
    {
        public float FireRadius = 4f;
        public float TickDamage = 8f;
        public float TickInterval = 0.5f;
        public float BurnDuration = 6f;

        // Avoids detonating instantly against the thrower's own collider a frame after spawn.
        private const float ArmDelay = 0.1f;

        private bool _detonated;

        private void OnCollisionEnter(Collision collision)
        {
            if (_detonated || Time.time - SpawnTime < ArmDelay)
            {
                return;
            }
            Detonate(collision.GetContact(0).point);
        }

        private void Detonate(Vector3 impactPoint)
        {
            _detonated = true;

            var poolObj = new GameObject("HumanHostExplosives_FirePool");
            poolObj.transform.position = impactPoint;
            FirePool pool = poolObj.AddComponent<FirePool>();
            pool.Radius = FireRadius;
            pool.TickDamage = TickDamage;
            pool.TickInterval = TickInterval;
            pool.Duration = BurnDuration;
            pool.Thrower = Thrower;

            Plugin.Log.LogInfo($"[Molotov] Detonated at {impactPoint}, fire radius={FireRadius}, duration={BurnDuration}s.");
            Destroy(gameObject);
        }
    }
}
