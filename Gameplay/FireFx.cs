using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.HighDefinition;

namespace HumanHostExplosives
{
    /// <summary>
    /// Fire visuals for the fire system, borrowed from the game's own wall torch rather than built
    /// from scratch (MOD_CONVENTIONS §2/§8). Torch_Build.prefab's hierarchy is
    /// Torch_Build/0/TorchFire -> fx_fire (looping ParticleSystem, 8x4 animated flame sheet, real
    /// HDRP material) and fx_sparks. Those children are COPIED with a plain Object.Instantiate of
    /// the loaded prefab asset's child - they are ordinary scene objects afterwards, not Addressables
    /// instances, so Destroy on them is correct (§9). The prefab asset itself is never instantiated,
    /// destroyed or released; its handle is simply kept for the session.
    ///
    /// Falls back to WB_Campfire's fx_fire (the prefab ExplosionVisual already borrows from), then to
    /// a Sprites/Default particle, so fire is never invisible.
    /// </summary>
    internal static class FireFx
    {
        private const string TorchGuid = "563b55e62c1d0904b93364d5f9429999";    // Torch_Build.prefab
        private const string CampfireGuid = "bc2ff5fe13fe2a54e8a2675e76c2c406"; // WB_Campfire.prefab

        private static GameObject _fireTemplate;
        private static GameObject _sparksTemplate;
        private static bool _loadAttempted;
        private static Material _fallbackMat;

        internal static void Preload()
        {
            if (_loadAttempted)
            {
                return;
            }
            _loadAttempted = true;

            try
            {
                GameObject torch = Addressables.LoadAssetAsync<GameObject>(TorchGuid).WaitForCompletion();
                if (torch != null)
                {
                    _fireTemplate = FindChild(torch.transform, "fx_fire");
                    _sparksTemplate = FindChild(torch.transform, "fx_sparks");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Fire] Torch_Build failed to load: " + ex.Message);
            }

            if (_fireTemplate == null)
            {
                try
                {
                    GameObject campfire = Addressables.LoadAssetAsync<GameObject>(CampfireGuid).WaitForCompletion();
                    if (campfire != null)
                    {
                        _fireTemplate = FindChild(campfire.transform, "fx_fire");
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Fire] WB_Campfire failed to load: " + ex.Message);
                }
            }

            Plugin.Log.LogInfo($"[Fire] fx templates: fire={(_fireTemplate != null ? _fireTemplate.name : "fallback")}, sparks={_sparksTemplate != null}");
        }

        private static GameObject FindChild(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                if (t.name == name && t.GetComponent<ParticleSystem>() != null)
                {
                    return t.gameObject;
                }
            }
            return null;
        }

        /// <summary>The spreading ground fire of a Molotov pool, radius R.</summary>
        internal static ParticleSystem[] SpawnPoolFx(Transform parent, float radius)
        {
            Preload();
            float area = Mathf.Max(1f, radius * radius);

            ParticleSystem fire = CopyOrFallback(_fireTemplate, parent, "HHE_PoolFire");
            Configure(fire, coneRadius: radius * 0.8f, rate: 25f * area, max: Mathf.RoundToInt(40f * area),
                      sizeMin: 0.5f, sizeMax: 1.2f);

            if (_sparksTemplate == null)
            {
                return new[] { fire };
            }

            ParticleSystem sparks = Copy(_sparksTemplate, parent, "HHE_PoolSparks");
            ParticleSystem.EmissionModule em = sparks.emission;
            em.rateOverTimeMultiplier *= 2f;
            ParticleSystem.ShapeModule shape = sparks.shape;
            shape.radius = radius * 0.6f;
            Restart(sparks);
            return new[] { fire, sparks };
        }

        /// <summary>Flames on a burning character. Simulated in world space so they trail.</summary>
        internal static ParticleSystem SpawnBodyFlame(Transform parent)
        {
            Preload();
            ParticleSystem fire = CopyOrFallback(_fireTemplate, parent, "HHE_BodyFire");
            Configure(fire, coneRadius: 0.22f, rate: 14f, max: 30, sizeMin: 0.35f, sizeMax: 0.75f);
            return fire;
        }

        /// <summary>
        /// A warm point light. Built fresh (Light + HDAdditionalLightData, the way the published
        /// Minimap mod does it) rather than copied from the torch, whose lights are authored
        /// disabled and pointing down a wall. Intensity goes through HDRP's own unit conversion
        /// once; the flicker then scales the resulting Light.intensity.
        /// </summary>
        internal static Light SpawnLight(Transform parent, float lumens, float range)
        {
            var go = new GameObject("HHE_FireLight");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.58f, 0.24f);
            light.range = range;
            light.shadows = LightShadows.None;
            try
            {
                HDAdditionalLightData hd = go.AddComponent<HDAdditionalLightData>();
                hd.SetIntensity(lumens, LightUnit.Lumen);
                hd.range = range;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Fire] HDAdditionalLightData setup failed: " + ex.Message);
                light.intensity = lumens / (4f * Mathf.PI);
            }
            return light;
        }

        private static ParticleSystem CopyOrFallback(GameObject template, Transform parent, string name)
        {
            return template != null ? Copy(template, parent, name) : Fallback(parent, name);
        }

        private static ParticleSystem Copy(GameObject template, Transform parent, string name)
        {
            GameObject go = Object.Instantiate(template, parent, worldPositionStays: false);
            go.name = name;
            go.SetActive(true);
            go.transform.localPosition = Vector3.zero;
            // The cone emits along local +Z; -90 about X points it straight up.
            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale = Vector3.one;
            return go.GetComponent<ParticleSystem>();
        }

        private static void Configure(ParticleSystem ps, float coneRadius, float rate, int max, float sizeMin, float sizeMax)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.maxParticles = max;
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = rate;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.radius = Mathf.Max(0.01f, coneRadius);
            shape.angle = 5f;

            ps.Play(true);
        }

        private static void Restart(ParticleSystem ps)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            ps.Play(true);
        }

        /// <summary>No borrowed template: a tinted Sprites/Default particle, like ExplosionVisual's fallback.</summary>
        private static ParticleSystem Fallback(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.55f, 0.12f, 0.85f), new Color(1f, 0.25f, 0.05f, 0.7f));
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.3f, 0.3f, 0.3f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;

            if (_fallbackMat == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    _fallbackMat = new Material(shader) { name = "HHE_FireFallback_Mat" };
                }
            }
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (_fallbackMat != null)
            {
                renderer.sharedMaterial = _fallbackMat;
            }
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            return ps;
        }
    }
}
