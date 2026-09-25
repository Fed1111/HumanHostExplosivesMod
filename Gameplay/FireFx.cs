using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering.HighDefinition;

namespace HumanHostExplosives
{
    /// <summary>One burning patch of a pool: its particle system, full emission rate, and when its fuel runs out.</summary>
    internal sealed class FirePatch
    {
        internal ParticleSystem Ps;
        internal float BaseRate;
        internal float DieAt;      // seconds after ignition
        internal float Seed;       // per-patch flicker phase
    }

    /// <summary>
    /// Fire visuals for the fire system, borrowed from the game's own torch and campfire rather than built
    /// from scratch (MOD_CONVENTIONS §2/§8). Torch_Build.prefab: Torch_Build/0/TorchFire -> fx_fire (looping,
    /// 8x4 animated flame sheet, real HDRP material) and fx_sparks; WB_Campfire -> fx_fire, fx_smoke. The
    /// children are COPIED with a plain Object.Instantiate of the loaded prefab asset's child - ordinary scene
    /// objects afterwards, so Destroy on them is correct (§9). The prefab assets themselves are never
    /// instantiated, destroyed or released.
    ///
    /// A Molotov pool is NOT one emitter stretched to the pool's radius - that is what read as "a ring of
    /// fire": the torch's cone emits from its RIM (radiusThickness 0), so scaling it up drew a hollow circle.
    /// Instead: several irregular patches of different size scattered over the pool (spilled fuel), each
    /// filling its own area, plus a taller core, rising smoke, embers and an ignition flare. Each patch has its
    /// own fuel (DieAt), so the fire shrinks and gutters out unevenly (FirePool drives that).
    /// </summary>
    internal static class FireFx
    {
        private const string TorchGuid = "563b55e62c1d0904b93364d5f9429999";    // Torch_Build.prefab
        private const string CampfireGuid = "bc2ff5fe13fe2a54e8a2675e76c2c406"; // WB_Campfire.prefab

        private static GameObject _fireTemplate;
        private static GameObject _sparksTemplate;
        private static GameObject _smokeTemplate;
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

            try
            {
                GameObject campfire = Addressables.LoadAssetAsync<GameObject>(CampfireGuid).WaitForCompletion();
                if (campfire != null)
                {
                    _smokeTemplate = FindChild(campfire.transform, "fx_smoke");
                    if (_fireTemplate == null)
                    {
                        _fireTemplate = FindChild(campfire.transform, "fx_fire");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Fire] WB_Campfire failed to load: " + ex.Message);
            }

            Plugin.Log.LogInfo($"[Fire] fx templates: fire={(_fireTemplate != null ? _fireTemplate.name : "fallback")}, " +
                               $"sparks={_sparksTemplate != null}, smoke={_smokeTemplate != null}");
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

        /// <summary>
        /// Builds a Molotov pool of radius R that burns for `duration` seconds. Returns every patch (flames,
        /// core, smoke, embers) with its own burn-out time; FirePool scales their emission as they run dry.
        /// </summary>
        internal static List<FirePatch> SpawnPoolFx(Transform parent, float radius, float duration)
        {
            Preload();
            var patches = new List<FirePatch>();
            var rng = new System.Random(unchecked((int)(parent.position.x * 73856093f) ^ (int)(parent.position.z * 19349663f)));
            float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);

            // Spilled fuel: a few irregular patches, the first near the centre (where most fuel landed),
            // the rest scattered, of mixed size so the outline is ragged rather than a circle.
            int count = Mathf.Clamp(Mathf.RoundToInt(radius * 1.8f), 3, 8);
            for (int i = 0; i < count; i++)
            {
                float ang = Rand(0f, Mathf.PI * 2f);
                float dist = i == 0 ? Rand(0f, 0.15f) * radius : Mathf.Sqrt(Rand(0.1f, 1f)) * radius * 0.78f;
                float patchR = i == 0 ? radius * 0.45f : radius * Rand(0.18f, 0.38f);
                var offset = new Vector3(Mathf.Cos(ang) * dist, 0.02f, Mathf.Sin(ang) * dist);
                float area = Mathf.Max(0.15f, patchR * patchR);

                // Taper: full height at the centre, down to about a third at the rim - flame height is
                // size x speed x lifetime, so all three shrink with distance from the middle.
                float taper = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(dist / (radius * 0.85f)));

                ParticleSystem ps = CopyOrFallback(_fireTemplate, parent, "HHE_PoolFire" + i);
                ps.transform.localPosition = offset;
                float rate = 34f * area + 6f;
                Configure(ps, coneRadius: patchR, rate: rate, max: Mathf.RoundToInt(rate * 2.2f),
                          sizeMin: (0.35f + patchR * 0.35f) * taper, sizeMax: (0.7f + patchR * 0.9f) * taper,
                          lifeScale: Rand(0.85f, 1.2f) * Mathf.Lerp(0.6f, 1f, taper), speedScale: Mathf.Lerp(0.55f, 1f, taper));
                patches.Add(new FirePatch
                {
                    Ps = ps, BaseRate = rate, Seed = Rand(0f, 100f),
                    // Thin edges burn off first; the central pool lasts the longest.
                    DieAt = duration * (i == 0 ? 1f : Rand(0.45f, 0.95f)),
                });
            }

            // The fringe: a thin band of small, low flames around the edge, so the fire tapers away into
            // licks of flame instead of ending at a hard line.
            ParticleSystem fringe = CopyOrFallback(_fireTemplate, parent, "HHE_PoolFringe");
            float fringeRate = 10f + radius * 9f;
            Configure(fringe, coneRadius: radius * 0.92f, rate: fringeRate, max: Mathf.RoundToInt(fringeRate * 2f),
                      sizeMin: 0.12f, sizeMax: 0.32f, lifeScale: 0.55f, speedScale: 0.45f);
            ParticleSystem.ShapeModule fringeShape = fringe.shape;
            fringeShape.radiusThickness = 0.3f;   // outer 30% of the disc only
            patches.Add(new FirePatch { Ps = fringe, BaseRate = fringeRate, Seed = Rand(0f, 100f), DieAt = duration * 0.6f });

            // A taller core where the fuel is deepest.
            ParticleSystem core = CopyOrFallback(_fireTemplate, parent, "HHE_PoolCore");
            core.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            Configure(core, coneRadius: radius * 0.22f, rate: 10f + radius * 3f, max: 60,
                      sizeMin: 0.9f + radius * 0.2f, sizeMax: 1.6f + radius * 0.35f, lifeScale: 1.35f, speedScale: 1.6f);
            patches.Add(new FirePatch { Ps = core, BaseRate = 10f + radius * 3f, Seed = Rand(0f, 100f), DieAt = duration * 0.85f });

            // Smoke rising off the fire and drifting with a slight breeze.
            ParticleSystem smoke = SpawnSmoke(parent, radius);
            if (smoke != null)
            {
                float smokeRate = 3f + radius * 2f;
                patches.Add(new FirePatch { Ps = smoke, BaseRate = smokeRate, Seed = 0f, DieAt = duration + 1.5f });
            }

            // Embers.
            if (_sparksTemplate != null)
            {
                ParticleSystem sparks = Copy(_sparksTemplate, parent, "HHE_PoolSparks");
                ParticleSystem.EmissionModule em = sparks.emission;
                float sparkRate = em.rateOverTimeMultiplier * 1.5f;
                ParticleSystem.ShapeModule shape = sparks.shape;
                shape.radius = radius * 0.7f;
                shape.radiusThickness = 1f;
                Restart(sparks);
                patches.Add(new FirePatch { Ps = sparks, BaseRate = sparkRate, Seed = 0f, DieAt = duration * 0.8f });
            }

            // Ignition: the fuel catching - one short fireball, the only non-looping part.
            try
            {
                ExplosionVisual.Spawn(parent.position + Vector3.up * 0.4f, radius * 0.55f, 0.55f, 0.15f);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Fire] ignition flare failed: " + ex.Message);
            }
            return patches;
        }

        /// <summary>
        /// One flame on one body part of a burning character (BurnManager places one per limb). World-space
        /// simulation, so flames trail behind a moving body and lick upward off it.
        /// </summary>
        internal static ParticleSystem SpawnBodyFlame(Transform parent, string name, float coneRadius, float rate, float sizeMin, float sizeMax)
        {
            Preload();
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, worldPositionStays: false);
            ParticleSystem fire = CopyOrFallback(_fireTemplate, holder.transform, name + "_fx");
            Configure(fire, coneRadius: coneRadius, rate: rate, max: Mathf.RoundToInt(rate * 2.5f) + 4,
                      sizeMin: sizeMin, sizeMax: sizeMax, lifeScale: 0.8f, speedScale: 0.9f);
            return fire;
        }

        /// <summary>Smoke streaming off a burning character (campfire fx_smoke, scaled down). Null if unavailable.</summary>
        internal static ParticleSystem SpawnSmokeTrail(Transform parent)
        {
            Preload();
            if (_smokeTemplate == null)
            {
                return null;
            }
            ParticleSystem ps = Copy(_smokeTemplate, parent, "HHE_BodySmoke");
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 40;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.14f, 0.13f, 0.12f, 0.5f), new Color(0.28f, 0.27f, 0.25f, 0.35f));
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 6f;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.25f;
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2f));
            ParticleSystem.VelocityOverLifetimeModule vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
            vel.y = new ParticleSystem.MinMaxCurve(0.5f, 1f);
            vel.z = new ParticleSystem.MinMaxCurve(0.05f, 0.3f);
            ps.Play(true);
            return ps;
        }

        /// <summary>
        /// Dark smoke column. The campfire's own fx_smoke when available (real smoke material), reshaped
        /// for a bigger fire; nothing if it is missing - a flat-coloured fallback smoke looks worse than none.
        /// </summary>
        private static ParticleSystem SpawnSmoke(Transform parent, float radius)
        {
            if (_smokeTemplate == null)
            {
                return null;
            }
            ParticleSystem ps = Copy(_smokeTemplate, parent, "HHE_PoolSmoke");
            ps.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.8f + radius * 0.3f, 1.6f + radius * 0.5f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.16f, 0.15f, 0.14f, 0.55f), new Color(0.3f, 0.29f, 0.27f, 0.4f));

            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 3f + radius * 2f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = radius * 0.55f;
            shape.radiusThickness = 1f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // Grow as it rises, drift downwind.
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));
            ParticleSystem.VelocityOverLifetimeModule vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0.3f, 0.7f);
            vel.y = new ParticleSystem.MinMaxCurve(0.6f, 1.2f);
            vel.z = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);

            ps.Play(true);
            return ps;
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

        private static void Configure(ParticleSystem ps, float coneRadius, float rate, int max, float sizeMin, float sizeMax,
                                      float lifeScale = 1f, float speedScale = 1f)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.maxParticles = max;
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            main.startLifetimeMultiplier *= lifeScale;
            main.startSpeedMultiplier *= speedScale;
            // Random spin so neighbouring flames don't share the same sprite orientation.
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = rate;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.radius = Mathf.Max(0.01f, coneRadius);
            // THE ring fix: emit from the whole disc, not just its rim (the torch's cone uses the rim).
            shape.radiusThickness = 1f;
            shape.angle = 6f;

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
