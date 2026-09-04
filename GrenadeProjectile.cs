using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Physical thrown grenade: real Rigidbody flight, fuse timer, then detonates.
    /// AoE damage isn't wired up yet - this validates the model/physics first.
    /// </summary>
    internal class GrenadeProjectile : MonoBehaviour
    {
        public float FuseSeconds = 3f;

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
            Plugin.Log.LogInfo($"[Grenade] Detonated at {transform.position}. (AoE damage not wired up yet)");
            Destroy(gameObject);
        }
    }
}
