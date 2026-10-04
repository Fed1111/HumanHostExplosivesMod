using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Physical thrown grenade: real Rigidbody flight, then a Blast. Either a timed fuse (the
    /// grenade) or an impact fuse (the contact grenade): the first collision after ArmSeconds
    /// detonates it, earlier ones are ignored so a point-blank drop bounces once instead of going
    /// off in the thrower's hand. An impact-fused grenade that never hits anything still goes off
    /// on its FuseSeconds failsafe.
    /// </summary>
    internal class GrenadeProjectile : ExplosiveProjectile
    {
        public float FuseSeconds = 3f;
        public bool ImpactFuse;
        public float ArmSeconds = 0.25f;
        internal BlastParams Blast;

        private bool _detonated;

        private void Update()
        {
            if (!_detonated && Time.time - SpawnTime >= FuseSeconds)
            {
                Detonate();
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!ImpactFuse || _detonated || Time.time - SpawnTime < ArmSeconds)
            {
                return;
            }
            Detonate();
        }

        private void Detonate()
        {
            _detonated = true;
            HumanHostExplosives.Blast.Detonate(Blast, transform.position, Thrower);
            Destroy(gameObject);
        }
    }
}
