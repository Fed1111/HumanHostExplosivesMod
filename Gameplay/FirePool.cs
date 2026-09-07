using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// A lingering AoE fire effect left behind by a detonated Molotov: ticks falloff damage on
    /// a timer for its duration, then removes itself. A runtime-built ParticleSystem stands in
    /// for real fire VFX since no reusable fire/campfire effect was found to borrow.
    /// </summary>
    internal class FirePool : MonoBehaviour
    {
        public float Radius = 4f;
        public float TickDamage = 8f;
        public float TickInterval = 0.5f;
        public float Duration = 6f;
        public C_Controller_Base Thrower;

        private float _elapsed;
        private float _sinceLastTick;

        private void Start()
        {
            BuildFireVisual();
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            _sinceLastTick += Time.deltaTime;

            if (_sinceLastTick >= TickInterval)
            {
                _sinceLastTick = 0f;
                try
                {
                    ExplosionDamage.Apply(transform.position, Radius, TickDamage, Thrower, hitFlyForce: 0f, hitReact: 0f);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"[FirePool] ExplosionDamage.Apply threw: {ex}");
                }
            }

            if (_elapsed >= Duration)
            {
                Destroy(gameObject);
            }
        }

        private void BuildFireVisual()
        {
            var ps = gameObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(1f, 0.45f, 0.1f, 0.9f);
            main.startSpeed = 1.5f;
            main.startSize = 0.6f;
            main.startLifetime = 0.8f;
            main.maxParticles = 80;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 40f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = Mathf.Max(0.1f, Radius * 0.5f);

            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                renderer.material = new Material(shader);
            }
        }
    }
}
