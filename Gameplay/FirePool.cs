using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// A patch of burning fuel left by a Molotov. The game has no fire system of its own, so this
    /// is the mod's: every FireTickSeconds it damages and ignites (BurnManager) every character
    /// standing in it, shows borrowed torch flames (FireFx) with a flickering light, and plays a
    /// crackle loop. It dies down over its last 1.5 s instead of vanishing.
    ///
    /// Pools are capped (MaxFirePools): lighting one more puts the oldest out early, which also
    /// bounds the number of point lights.
    /// </summary>
    internal class FirePool : MonoBehaviour
    {
        private const float FadeSeconds = 1.5f;

        internal static readonly List<FirePool> Active = new List<FirePool>();
        private static readonly Collider[] Buffer = new Collider[64];
        private static readonly HashSet<C_Controller_Base> Seen = new HashSet<C_Controller_Base>();

        public float Radius = 3f;
        public float Duration = 10f;
        public C_Controller_Base Thrower;

        private float _elapsed;
        private float _nextTick;
        private ParticleSystem[] _fx;
        private Light _light;
        private float _lightBase;
        private AudioSource _loop;
        private bool _fading;
        private float _noiseSeed;

        internal static FirePool Spawn(Vector3 position, float radius, float duration, C_Controller_Base thrower)
        {
            Active.RemoveAll(p => p == null);
            while (Active.Count >= Mathf.Max(1, Plugin.MaxFirePools.Value))
            {
                FirePool oldest = Active[0];
                Active.RemoveAt(0);
                oldest.BeginFade();
            }

            var go = new GameObject("HHE_FirePool");
            go.transform.position = position;
            FirePool pool = go.AddComponent<FirePool>();
            pool.Radius = radius;
            pool.Duration = duration;
            pool.Thrower = thrower;
            Active.Add(pool);
            return pool;
        }

        private void Start()
        {
            _noiseSeed = Random.value * 100f;
            try
            {
                _fx = FireFx.SpawnPoolFx(transform, Radius);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Fire] pool fx failed: " + ex);
            }
            try
            {
                if (Plugin.FireLightLumens.Value > 0f)
                {
                    _light = FireFx.SpawnLight(transform, Plugin.FireLightLumens.Value, Radius * 3f + 4f);
                    _lightBase = _light.intensity;
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Fire] pool light failed: " + ex);
            }
            try
            {
                _loop = SmallSounds.StartFireLoop(transform);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Fire] pool sound failed: " + ex);
            }
        }

        /// <summary>Puts the pool out early (cap reached) - it dies down over FadeSeconds.</summary>
        internal void BeginFade()
        {
            if (_elapsed < Duration - FadeSeconds)
            {
                _elapsed = Duration - FadeSeconds;
            }
        }

        private void Update()
        {
            _elapsed += Time.deltaTime;
            float remaining = Duration - _elapsed;
            float strength = Mathf.Clamp01(remaining / FadeSeconds);

            if (!_fading && remaining <= FadeSeconds)
            {
                _fading = true;
                if (_fx != null)
                {
                    foreach (ParticleSystem ps in _fx)
                    {
                        if (ps != null)
                        {
                            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                        }
                    }
                }
            }

            if (_light != null)
            {
                float flicker = 0.8f + 0.35f * Mathf.PerlinNoise(_noiseSeed, Time.time * 7f);
                _light.intensity = _lightBase * flicker * strength;
            }
            if (_loop != null)
            {
                _loop.volume = Plugin.FireVolume.Value * strength * SmallSounds.Falloff(transform.position, 30f);
            }

            if (!_fading && Time.time >= _nextTick)
            {
                _nextTick = Time.time + Mathf.Max(0.25f, Plugin.FireTickSeconds.Value);
                try
                {
                    Burn();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("[Fire] pool tick threw: " + ex);
                }
            }

            // Let the last flames finish their lifetime before the object goes.
            if (_elapsed >= Duration + 1.2f)
            {
                Destroy(gameObject);
            }
        }

        private void Burn()
        {
            Vector3 p = transform.position;
            int mask = Global_Infos.ins != null ? Global_Infos.ins.Mask_Creature.value : -1;
            int n = Physics.OverlapCapsuleNonAlloc(p - Vector3.up * 0.3f, p + Vector3.up * 1.8f, Radius, Buffer, mask,
                                                    QueryTriggerInteraction.Ignore);
            Seen.Clear();
            for (int i = 0; i < n; i++)
            {
                C_Controller_Base ctrl = FireDamage.Resolve(Buffer[i]);
                if (ctrl == null || !Seen.Add(ctrl))
                {
                    continue;
                }
                // Horizontal distance: the capsule is round at the ends, the fire is a flat disc.
                Vector3 d = ctrl.transform.position - p;
                d.y = 0f;
                if (d.sqrMagnitude > Radius * Radius)
                {
                    continue;
                }
                FireDamage.Tick(ctrl, Plugin.FireGroundDamage.Value, Thrower);
                BurnManager.Ignite(ctrl, Thrower);
            }

            if (Plugin.FireBuildableDamage.Value > 0f)
            {
                ExplosionDamage.ApplyToBuildables(p, Radius, Plugin.FireBuildableDamage.Value);
            }
        }

        private void OnDestroy()
        {
            Active.Remove(this);
        }
    }
}
