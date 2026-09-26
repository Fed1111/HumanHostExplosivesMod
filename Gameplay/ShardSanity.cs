using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Wall pieces from the game's mesh slicer can be slivers - a handful of distinct points, or all in one
    /// plane. When the game turns such a piece into a falling/colliding body it sets MeshCollider.convex, and
    /// PhysX can't build a hull from it: "ConvexHullLib::cleanupVertices: Less than four valid vertices", which
    /// the game's error panel then shows to the player. A demolition makes hundreds of pieces, so it happens.
    ///
    /// Before the two places a wall piece is made convex (Slice_Shard_Connect.BeginCollide, and
    /// Smash_Fallen_Manager.Fall_Shard), a sliver gets a tiny valid box as its collider and is hidden -
    /// too small to see, and nothing is left for PhysX to reject. Everything else passes through untouched.
    /// </summary>
    internal static class ShardSanity
    {
        private static Mesh _tinyBox;
        private static readonly List<Vector3> Verts = new List<Vector3>(256);
        private static readonly HashSet<Vector3Int> Unique = new HashSet<Vector3Int>();

        /// <summary>True when PhysX couldn't make a convex hull from this mesh (too few distinct points, or flat).</summary>
        internal static bool IsSliver(Mesh m)
        {
            if (m == null)
            {
                return true;
            }
            if (!m.isReadable)
            {
                return false;   // can't inspect it; the game makes it readable itself where it matters
            }
            Verts.Clear();
            m.GetVertices(Verts);
            if (Verts.Count < 4)
            {
                return true;
            }
            // Distinct points at 2 mm (PhysX welds points closer than a small fraction of the extents).
            Unique.Clear();
            foreach (Vector3 v in Verts)
            {
                Unique.Add(new Vector3Int(Mathf.RoundToInt(v.x * 500f), Mathf.RoundToInt(v.y * 500f), Mathf.RoundToInt(v.z * 500f)));
                if (Unique.Count > 16)
                {
                    break;
                }
            }
            if (Unique.Count < 4)
            {
                return true;
            }
            // Volume: farthest point from the first, then farthest from that line, then farthest from that plane.
            Vector3 a = Verts[0];
            Vector3 b = a;
            float best = 0f;
            foreach (Vector3 v in Verts)
            {
                float d = (v - a).sqrMagnitude;
                if (d > best)
                {
                    best = d;
                    b = v;
                }
            }
            Vector3 ab = b - a;
            if (ab.sqrMagnitude < 1e-6f)
            {
                return true;
            }
            Vector3 c = a;
            best = 0f;
            foreach (Vector3 v in Verts)
            {
                float d = Vector3.Cross(ab, v - a).sqrMagnitude;
                if (d > best)
                {
                    best = d;
                    c = v;
                }
            }
            Vector3 n = Vector3.Cross(ab, c - a);
            if (n.sqrMagnitude < 1e-10f)
            {
                return true;   // all on one line
            }
            n.Normalize();
            best = 0f;
            foreach (Vector3 v in Verts)
            {
                best = Mathf.Max(best, Mathf.Abs(Vector3.Dot(n, v - a)));
            }
            return best < 0.004f;   // flat
        }

        /// <summary>Gives a sliver a tiny valid box collider and hides it.</summary>
        internal static bool MakeSafe(GameObject go)
        {
            if (go == null || !go.TryGetComponent(out MeshCollider mc) || !IsSliver(mc.sharedMesh))
            {
                return false;
            }
            mc.sharedMesh = TinyBox();
            if (go.TryGetComponent(out MeshRenderer mr))
            {
                mr.enabled = false;
            }
            return true;
        }

        private static Mesh TinyBox()
        {
            if (_tinyBox != null)
            {
                return _tinyBox;
            }
            GameObject prim = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = prim.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(prim);
            Vector3[] v = cube.vertices;
            for (int i = 0; i < v.Length; i++)
            {
                v[i] *= 0.03f;
            }
            _tinyBox = new Mesh { name = "HHE_SliverBox", vertices = v, triangles = cube.triangles };
            _tinyBox.RecalculateBounds();
            return _tinyBox;
        }

        [HarmonyPatch(typeof(Slice_Shard_Connect), nameof(Slice_Shard_Connect.BeginCollide))]
        private static class BeginCollidePatch
        {
            private static void Prefix(Slice_Shard_Connect __instance)
            {
                try
                {
                    MakeSafe(__instance.gameObject);
                }
                catch
                {
                    // never break the game's own collapse over a safety check
                }
            }
        }

        [HarmonyPatch(typeof(Smash_Fallen_Manager), "Fall_Shard")]
        private static class FallShardPatch
        {
            private static void Prefix(Slice_Shard_Connect fallShard)
            {
                try
                {
                    if (fallShard != null)
                    {
                        MakeSafe(fallShard.gameObject);
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Backstop: the game builds convex colliders in other places too (runtime fracturing, placement). This
        /// one PhysX warning - harmless, the sliver just gets no collision - is kept out of the game's error
        /// panel, so players aren't shown an "error" for it. Every other error still reaches the panel.
        /// </summary>
        [HarmonyPatch(typeof(Error_Report_Panel), "OnLogMessageThreaded")]
        private static class ErrorPanelFilter
        {
            private static bool Prefix(string condition)
            {
                return condition == null || !condition.Contains("ConvexHullLib::cleanupVertices");
            }
        }
    }
}
