using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Chunks blown off a structure at the instant of detonation. The game's own destruction of a wall
    /// happens cell by cell over the following seconds (one slice at a time - see ExplosionDamage's zone
    /// queue), which on its own reads as the wall slowly crumbling rather than being blasted. This adds
    /// the blast: irregular rigidbody chunks wearing the ACTUAL material of the nearest wall/block (a
    /// real game material, shared - nothing built from scratch, MOD_CONVENTIONS §2), thrown outward by an
    /// explosion force. Purely visual: no damage, no loot, no save. They shrink away after a few seconds.
    /// </summary>
    internal static class DebrisBurst
    {
        private static readonly List<GameObject> Live = new List<GameObject>();
        private static readonly Collider[] Buffer = new Collider[64];
        private static Mesh[] _meshes;

        internal static void Spawn(Vector3 center, int count, float force)
        {
            if (count <= 0 || !Plugin.DebrisChunks.Value)
            {
                return;
            }
            Material mat = FindSurfaceMaterial(center, out Vector3 surface);
            if (mat == null)
            {
                return; // nothing built nearby - an open-ground blast throws dirt, not chunks
            }
            Live.RemoveAll(g => g == null);
            int room = Plugin.MaxDebrisChunks.Value - Live.Count;
            count = Mathf.Min(count, room);
            if (count <= 0)
            {
                return;
            }
            EnsureMeshes();
            int layer = Global_Infos.ins != null ? Global_Infos.ins.L_Smashed : 0;
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("HHE_Debris");
                go.layer = layer;
                float size = Random.Range(0.12f, 0.38f);
                go.transform.position = surface + Random.insideUnitSphere * 0.5f;
                go.transform.rotation = Random.rotation;
                go.transform.localScale = new Vector3(size * Random.Range(0.7f, 1.3f), size * Random.Range(0.5f, 1.1f), size * Random.Range(0.7f, 1.3f));
                Mesh mesh = _meshes[Random.Range(0, _meshes.Length)];
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.AddComponent<BoxCollider>();
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.mass = 2f + size * 10f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.AddExplosionForce(force * Random.Range(0.6f, 1.2f), center, 6f, 0.6f, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 8f, ForceMode.VelocityChange);
                go.AddComponent<DebrisFade>().Life = Random.Range(5f, 8f);
                Live.Add(go);
            }
        }

        /// <summary>The material of the nearest built/structure surface within 2.5 m, and a point on it.</summary>
        private static Material FindSurfaceMaterial(Vector3 center, out Vector3 surface)
        {
            surface = center;
            Global_Infos g = Global_Infos.ins;
            if (g == null)
            {
                return null;
            }
            int mask = g.Mask_Build.value | g.Mask_Battle.value | g.Mask_Scene.value;
            int n = Physics.OverlapSphereNonAlloc(center, 2.5f, Buffer, mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Material found = null;
            for (int i = 0; i < n; i++)
            {
                Collider c = Buffer[i];
                if (c == null || c.GetComponentInParent<Build_Info>() == null)
                {
                    continue; // terrain and loose props: not a structure
                }
                Renderer r = c.GetComponent<Renderer>() ?? c.GetComponentInParent<Renderer>();
                if (r == null || r.sharedMaterial == null || r.sharedMaterial.name.StartsWith("HHE_"))
                {
                    continue;
                }
                Vector3 p = c.ClosestPoint(center);
                float d = (p - center).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    found = r.sharedMaterial;
                    surface = p;
                }
            }
            return found;
        }

        /// <summary>
        /// A few irregular chunk shapes: Unity's own cube mesh (so the winding and UVs are right) with
        /// each corner nudged - vertices sharing a position get the same nudge, so faces stay closed.
        /// </summary>
        private static void EnsureMeshes()
        {
            if (_meshes != null)
            {
                return;
            }
            GameObject prim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = prim.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(prim);
            var rng = new System.Random(4711);
            _meshes = new Mesh[4];
            for (int m = 0; m < _meshes.Length; m++)
            {
                Vector3[] v = cube.vertices;
                var nudge = new Dictionary<Vector3, Vector3>();
                for (int k = 0; k < v.Length; k++)
                {
                    if (!nudge.TryGetValue(v[k], out Vector3 d))
                    {
                        d = new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.35f;
                        nudge[v[k]] = d;
                    }
                    v[k] += d;
                }
                var mesh = new Mesh { name = "HHE_DebrisChunk" + m };
                mesh.vertices = v;
                mesh.uv = cube.uv;
                mesh.triangles = cube.triangles;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                _meshes[m] = mesh;
            }
        }
    }

    /// <summary>
    /// The wall pieces the game drops when an explosive cuts a zone wall (Smash_Fallen_Manager.Fall_Shard)
    /// get only a small random nudge (5 m/s from a random point up to 4 m away). Near a blast, add a
    /// real push away from the charge, so the structure's own pieces are blown out, not just let go.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Smash_Fallen_Manager), "Fall_Shard")]
    internal static class BlastPushFallingShards
    {
        private static void Postfix(Slice_Shard_Connect fallShard)
        {
            if (fallShard == null || !Plugin.DebrisChunks.Value)
            {
                return;
            }
            Vector3 pos = fallShard.transform.position;
            if (!ExplosionDrops.NearMark(pos, 4f, out Vector3 blast))
            {
                return;
            }
            Rigidbody rb = fallShard.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Only what sat next to the charge is thrown; the collapse above just falls.
                rb.AddExplosionForce(Random.Range(8f, 13f), blast, 4.5f, 0.4f, ForceMode.VelocityChange);
                rb.AddTorque(Random.insideUnitSphere * 3f, ForceMode.VelocityChange);
            }
        }
    }

    /// <summary>Shrinks a debris chunk away at the end of its life, then removes it.</summary>
    internal class DebrisFade : MonoBehaviour
    {
        internal float Life = 6f;
        private float _age;
        private Vector3 _scale;

        private void Start()
        {
            _scale = transform.localScale;
        }

        private void Update()
        {
            _age += Time.deltaTime;
            float left = Life - _age;
            if (left < 1f)
            {
                transform.localScale = _scale * Mathf.Max(0f, left);
            }
            if (left <= 0f)
            {
                Destroy(gameObject);
            }
        }
    }
}
