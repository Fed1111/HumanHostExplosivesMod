using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// A short-lived, layered particle effect at a detonation point: a fast bright flash, a
    /// bigger fireball burst, and a lingering smoke plume. GrenadeProjectile previously applied
    /// damage and despawned with no visual feedback at all - this is the same runtime-
    /// ParticleSystem technique FirePool uses for its lingering fire effect, just three of them
    /// layered together instead of one.
    /// </summary>
    internal static class ExplosionVisual
    {
        private static Mesh _cubeMesh;
        private static Mesh _sphereMesh;

        internal static void Spawn(Vector3 position, float radius)
        {
            var root = new GameObject("HumanHostExplosives_ExplosionVisual");
            root.transform.position = position;

            SpawnFlash(root.transform, radius);
            SpawnFireball(root.transform, radius);
            SpawnDebris(root.transform, radius);
            SpawnSmoke(root.transform, radius);

            Object.Destroy(root, 4f);
        }

        // Billboard particles (Flash/Fireball/Smoke below) are flat camera-facing quads - more of
        // them is still just more flat quads, no actual dimensionality. Debris uses real 3D mesh
        // particles instead (small tumbling cubes) for actual faceted/chunky depth.
        private static void SpawnDebris(Transform parent, float radius)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Debris", 35);

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(0.35f, 0.28f, 0.2f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.25f);
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

        private static void SpawnFlash(Transform parent, float radius)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Flash", 45);

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(1f, 0.98f, 0.85f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startLifetime = 0.15f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.1f, radius * 0.15f);

            ApplyUnlitMaterial(ps);
            ps.Play();
        }

        private static void SpawnFireball(Transform parent, float radius)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Fireball", 160);

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(1f, 0.5f, 0.1f, 1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 12f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 1.5f);
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

            // Was pure Billboard (flat camera-facing quads) - fine for smoke/flash but read as
            // flat for a "fireball" specifically. Mesh-mode spheres give it real volume/faceting.
            ApplyUnlitMaterial(ps);
            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = GetSphereMesh();

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

        private static void SpawnSmoke(Transform parent, float radius)
        {
            ParticleSystem ps = CreateBurstSystem(parent, "Smoke", 50);

            ParticleSystem.MainModule main = ps.main;
            main.startColor = new Color(0.25f, 0.24f, 0.22f, 0.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(1f, 2.5f);
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

            ApplyUnlitMaterial(ps);
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
