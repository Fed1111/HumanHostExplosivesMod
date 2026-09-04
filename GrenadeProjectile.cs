using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Physical thrown grenade: real Rigidbody flight, fuse timer, then AoE damage on detonation.
    /// </summary>
    internal class GrenadeProjectile : MonoBehaviour
    {
        public float FuseSeconds = 3f;
        public float MaxDamage = 120f;
        public C_Controller_Base Thrower;

        private float _spawnTime;
        private bool _detonated;

        private void Awake()
        {
            _spawnTime = Time.time;
        }

        private void Update()
        {
            if (!_detonated && Time.time - _spawnTime >= FuseSeconds)
            {
                Detonate();
            }
        }

        private void Detonate()
        {
            _detonated = true;

            float radius = Plugin.ExplosionRadius.Value;
            int hits = 0;
            try
            {
                hits = ExplosionDamage.Apply(transform.position, radius, MaxDamage, Thrower);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"[Grenade] ExplosionDamage.Apply threw: {ex}");
            }

            Plugin.Log.LogInfo($"[Grenade] Detonated at {transform.position}, radius={radius}, hits={hits}.");
            Destroy(gameObject);
        }
    }
}
