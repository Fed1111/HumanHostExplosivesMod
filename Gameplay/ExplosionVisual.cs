using UnityEngine;
using UnityEngine.AddressableAssets;

namespace HumanHostExplosives
{
    /// <summary>
    /// A short-lived, layered particle effect at a detonation point: a fast bright flash, a
    /// bigger fireball burst, and a lingering smoke plume. GrenadeProjectile previously applied
    /// damage and despawned with no visual feedback at all - this is the same runtime-
    /// ParticleSystem technique FirePool uses for its lingering fire effect, just three of them
    /// layered together instead of one.
    ///
    /// Fireball/smoke use real textures borrowed the same way SwingSound borrows a vanilla sound
    /// set and ExplosiveDef borrows a template's material - live references to the base game's own
    /// already-loaded assets, never extracted/shipped as files. Source is WB_Campfire.prefab (the
    /// player-placeable campfire), whose fx_fire/fx_smoke children use real artist-authored HDRP
    /// materials (Fire_Mat with an 8x4 animated flame sprite sheet, CampFire_Black_Smoke). Per
    /// MOD_CONVENTIONS.md #2, cloning a real material this way is the only reliable way to get a
    /// correctly-configured HDRP material from script - building one from scratch renders grey/
    /// garbled even with every property set by hand.
    /// </summary>
    internal static class ExplosionVisual
    {
        private const string CampfireGuid = "bc2ff5fe13fe2a54e8a2675e76c2c406"; // WB_Campfire.prefab

        private static Material _realFireMaterial;
        private static Material _realSmokeMaterial;
        private static bool _fxMaterialsLoadAttempted;

        private static Mesh _cubeMesh;
        private static Mesh _sphereMesh;

        /// <summary>
        /// Loads WB_Campfire once and clones its fire/smoke ParticleSystemRenderer materials -
        /// never Instantiate()'d into the scene (that would also spawn its full crafting UI/
        /// Special_Workbench), just read directly off the loaded prefab asset, which is enough to
        /// get at sharedMaterial. Best-effort: on any failure, both stay null and the callers fall
        /// back to the original flat-colored Sprites/Default look.
        /// </summary>
        private static void EnsureRealFxMaterials()
        {
            if (_fxMaterialsLoadAttempted)
            {
                return;
            }
            _fxMaterialsLoadAttempted = true;

            try
            {
                GameObject campfire = Addressables.LoadAssetAsync<GameObject>(CampfireGuid).WaitForCompletion();
                if (campfire == null)
                {
                    Plugin.Log.LogWarning("[Explosion] WB_Campfire failed to load; fireball/smoke will use the flat-colored fallback.");
                    return;
                }

                foreach (ParticleSystemRenderer renderer in campfire.GetComponentsInChildren<ParticleSystemRenderer>(includeInactive: true))
                {
                    if (renderer.sharedMaterial == null)
                    {
                        continue;
                    }
                    if (_realFireMaterial == null && renderer.gameObject.name == "fx_fire")
                    {
                        _realFireMaterial = new Material(renderer.sharedMaterial);
                    }
                    else if (_realSmokeMaterial == null && renderer.gameObject.name == "fx_smoke")
                    {
                        _realSmokeMaterial = new Material(renderer.sharedMaterial);
                    }
                }

                if (_realFireMaterial == null || _realSmokeMaterial == null)
                {
                    Plugin.Log.LogWarning($"[Explosion] borrowed fire/smoke materials incomplete (fire={_realFireMaterial != null}, smoke={_realSmokeMaterial != null}) - falling back to flat color for whichever is missing.");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Explosion] failed to borrow WB_Campfire's fire/smoke materials: " + ex.Message);
            }
        }

        /// <summary>
        /// flashScale/particulateScale let callers weight the same four-layer effect differently -
        /// the Nailbomb wants a smaller but sharper "crack" (Flash+Fireball emphasized, small
        /// radius), the Grenade wants a bigger, dirtier "boom" (Debris+Smoke emphasized). 1f each
        /// reproduces the original, unweighted look.
        /// </summary>
        internal static void Spawn(Vector3 position, float radius, float flashScale = 1f, float particulateScale = 1f)
        {
            EnsureRealFxMaterials();

            var root = new GameObject("HumanHostExplosives_ExplosionVisual");
            root.transform.position = position;

            SpawnFlash(root.transform, radius, flashScale);
            SpawnFireball(root.transform, radius, flashScale);
            SpawnDebris(root.transform, radius, particulateScale);
            SpawnSmoke(root.transform, radius, particulateScale);

            Object.Destroy(root, 4f);
        }

        // Billboard particles (Flash/Fireball/Smoke below) are flat camera-facing quads - more of
        // them is still just more flat quads, no actual dimensionality. Debris uses real 3D mesh
        // particles instead (small tumbling cubes) for actual faceted/chunky depth.
        private static void SpawnDebris(Transform parent, float radius, float particulateScale)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Debris", Mathf.RoundToInt(35 * particulateScale));

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(0.35f, 0.28f, 0.2f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.25f * Mathf.Sqrt(particulateScale));
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
            main.gravityModifier = 1.2f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.15f, radius * 0.2f);

            ParticleSystem.RotationOverLifetimeModule rotationOverLifetime = ps.rotationOverLifetime;
            rotationOverLifetime.enabled = true;
            rotationOverLifetime.x = new ParticleSystem.MinMaxCurve(-4f, 4f);
            rotationOverLifetime.y = new ParticleSystem.MinMaxCurve(-4f, 4f);
            rotationOverLifetime.z = new ParticleSystem.MinMaxCurve(-4f, 4f);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.35f, 0.28f, 0.2f), 0f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = gradient;

            ApplyUnlitMaterial(ps);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = GetCubeMesh();

            ps.Play();
        }

        private static Mesh GetCubeMesh()
        {
            if (_cubeMesh != null)
            {
                return _cubeMesh;
            }
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cubeMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return _cubeMesh;
        }

        private static void SpawnFlash(Transform parent, float radius, float flashScale)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Flash", Mathf.RoundToInt(45 * flashScale));

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(1f, 0.98f, 0.85f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.6f * Mathf.Sqrt(flashScale));
            main.startLifetime = 0.15f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.1f, radius * 0.15f);

            ApplyUnlitMaterial(ps);
            ps.Play();
        }

        private static void SpawnFireball(Transform parent, float radius, float flashScale)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Fireball", Mathf.RoundToInt(160 * flashScale));

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(1f, 0.5f, 0.1f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 1.5f * Mathf.Sqrt(flashScale));
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.7f);

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.9f, 0.4f), 0f),
                    new GradientColorKey(new Color(1f, 0.35f, 0.05f), 0.4f),
                    new GradientColorKey(new Color(0.2f, 0.15f, 0.1f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.6f),
                    new GradientAlphaKey(0f, 1f),
                });
            colorOverLifetime.color = gradient;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.2f, radius * 0.3f);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (_realFireMaterial != null)
            {
                // The real flame sprite sheet is a flat silhouette shape (see WB_Campfire's own
                // fx_fire) - it wants Billboard, not the Mesh-sphere mode below, which was only
                // there to fake volume/faceting in place of an actual flame shape.
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                renderer.material = _realFireMaterial;

                ParticleSystem.TextureSheetAnimationModule sheet = ps.textureSheetAnimation;
                sheet.enabled = true;
                sheet.numTilesX = 8;
                sheet.numTilesY = 4;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1f);
                sheet.cycleCount = 1;
            }
            else
            {
                // Was pure Billboard (flat camera-facing quads) - fine for smoke/flash but read as
                // flat for a "fireball" specifically without a real texture. Mesh-mode spheres give
                // it fake volume/faceting instead, as a fallback if the real material didn't load.
                ApplyUnlitMaterial(ps);
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = GetSphereMesh();
            }

            ps.Play();
        }

        private static Mesh GetSphereMesh()
        {
            if (_sphereMesh != null)
            {
                return _sphereMesh;
            }
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(temp);
            return _sphereMesh;
        }

        private static void SpawnSmoke(Transform parent, float radius, float particulateScale)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Smoke", Mathf.RoundToInt(50 * particulateScale));

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(0.25f, 0.24f, 0.22f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(1f, 2.5f * Mathf.Sqrt(particulateScale));
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3f);
            main.gravityModifier = -0.05f; // drifts gently upward

            ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.gray, 0f) },
                new[]
                {
                    new GradientAlphaKey(0.6f, 0f),
                    new GradientAlphaKey(0.3f, 0.5f),
                    new GradientAlphaKey(0f, 1f),
                });
            colorOverLifetime.color = gradient;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.2f, radius * 0.25f);

            if (_realSmokeMaterial != null)
            {
                var smokeRenderer = ps.GetComponent<ParticleSystemRenderer>();
                smokeRenderer.renderMode = ParticleSystemRenderMode.Billboard;
                smokeRenderer.material = _realSmokeMaterial;
            }
            else
            {
                ApplyUnlitMaterial(ps);
            }
            ps.Play();
        }

        private static ParticleSystem CreateBurstSystem(Transform parent, string name, int burstCount)
        {
            var go = new GameObject("HHX_Explosion_" + name);
            // worldPositionStays MUST be false here: go is freshly created at world (0,0,0), and
            // worldPositionStays: true would preserve that (wrong) world position instead of
            // moving it to the parent's - every particle system was spawning at world origin
            // instead of the detonation point, nowhere near visible.
            go.transform.SetParent(parent, worldPositionStays: false);

            var ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.maxParticles = burstCount * 2;
            main.duration = 0.2f;
            main.loop = false;
            main.playOnAwake = false;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, burstCount) });

            return ps;
        }

        private static void ApplyUnlitMaterial(ParticleSystem ps)
        {
            // Sprites/Default first, not last: it's one of the most universally-included shaders
            // regardless of render pipeline (built for legacy UI/2D, still shipped under HDRP) and
            // reliably multiplies its output by the particle's own vertex color (startColor /
            // colorOverLifetime) with zero extra setup. HDRP/Unlit was tried first originally and
            // produced no visible effect with no exception thrown - the likely cause is that a
            // freshly-created `new Material(shader)` doesn't have vertex-color modulation enabled
            // by default under HDRP, so the particle system's own color settings had no way to
            // reach the screen even though nothing failed loudly. Setting the tint color explicitly
            // below covers that gap for whichever shader actually gets used.
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Standard Unlit") ?? Shader.Find("HDRP/Unlit");
            if (shader == null)
            {
                Plugin.Log.LogWarning("[Explosion] no usable particle shader found (tried Sprites/Default, Particles/Standard Unlit, HDRP/Unlit) - particles will render with Unity's default/missing material.");
                return;
            }

            var material = new Material(shader);
            // Cover every common tint-property name across shader families so whichever one this
            // resolved to actually gets a visible, opaque-enough tint instead of defaulting to
            // black/transparent.
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", Color.white);
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }
            if (material.HasProperty("_UnlitColor"))
            {
                material.SetColor("_UnlitColor", Color.white);
            }

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.material = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }
    }
}
