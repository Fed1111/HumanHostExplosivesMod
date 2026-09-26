using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Terrain trees aren't objects until something hits the ground near them: they're drawn as terrain tree
    /// instances, and the game swaps the ones within 5 m of a hit for real, breakable trees
    /// (TerrainTreeManager.Spawn_Terrain_Tree_Around_HitPoint - what a melee swing or a bullet into the
    /// ground calls). An explosion never hit the ground that way, so a demolition charge found no trees to
    /// break. Before its sweep, the charge does the same wake-up over its whole radius: at the centre and on a
    /// ring around it (points more than 5 m apart - the game ignores a second wake-up within 5 m of the last).
    /// The woken trees are then broken like any other block (Process_Smashed_Shard -> the game's own tree fall).
    /// </summary>
    internal static partial class ExplosionDamage
    {
        private static void WakeTrees(Vector3 center, float radius)
        {
            TerrainTreeManager trees = Init.ins != null ? Init.ins.terrainTreeManager : null;
            Global_Infos g = Global_Infos.ins;
            if (trees == null || g == null)
            {
                return;
            }
            int woken = 0;
            woken += WakeAt(trees, g, center) ? 1 : 0;
            if (radius > 4f)
            {
                const int ring = 6;
                float r = Mathf.Min(radius, 5.5f);
                for (int i = 0; i < ring; i++)
                {
                    float a = i * Mathf.PI * 2f / ring;
                    woken += WakeAt(trees, g, center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r)) ? 1 : 0;
                }
            }
            if (woken > 0)
            {
                Physics.SyncTransforms();   // any woken tree that is already there, for the sweep that follows
            }
            // Woken trees load in as objects a moment later - the sweep this frame misses them (including
            // the tree the charge is stuck to). Come back for them.
            TreePasses.Add((center, radius, Time.time + 0.35f, 0));
        }

        private static readonly List<(Vector3 Center, float Radius, float At, int Pass)> TreePasses = new List<(Vector3, float, float, int)>();
        private static readonly float[] TreePassDelays = { 0.35f, 0.9f, 2f };

        /// <summary>Called every frame (via TickDeferred): breaks trees that finished loading after the blast.</summary>
        private static void TickTreePasses()
        {
            for (int i = TreePasses.Count - 1; i >= 0; i--)
            {
                var p = TreePasses[i];
                if (Time.time < p.At)
                {
                    continue;
                }
                TreePasses.RemoveAt(i);
                int broken = 0;
                TopOnHit topOnHit = Object.FindObjectOfType<TopOnHit>();
                if (topOnHit == null)
                {
                    continue;
                }
                var done = new HashSet<Build_Info>();
                var hitPieces = new HashSet<Battle_Info>();
                ExplosionDrops.Scope++;
                try
                {
                    foreach (Collider c in Physics.OverlapSphere(p.Center, p.Radius, StructureMask(), QueryTriggerInteraction.Ignore))
                    {
                        Build_Info bi = c != null ? c.GetComponentInParent<Build_Info>() : null;
                        if (bi == null || bi._Type != Build_Info.Type.TerrainTreeBI || !done.Add(bi))
                        {
                            continue;
                        }
                        broken += SmashWholeBlock(bi, topOnHit, hitPieces);
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Demolition] tree pass threw: " + ex.Message);
                }
                finally
                {
                    ExplosionDrops.Scope--;
                }
                FlushFallChecks(topOnHit);
                if (broken > 0)
                {
                    Plugin.Log.LogInfo($"[Demolition] tree pass {p.Pass + 1}: {broken} tree(s) that loaded after the blast brought down.");
                }
                if (p.Pass + 1 < TreePassDelays.Length)
                {
                    TreePasses.Add((p.Center, p.Radius, Time.time + TreePassDelays[p.Pass + 1] - TreePassDelays[p.Pass], p.Pass + 1));
                }
            }
        }

        private static bool WakeAt(TerrainTreeManager trees, Global_Infos g, Vector3 p)
        {
            if (!Physics.Raycast(p + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 12f, g.Mask_Scene.value, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            Collider c = hit.collider;
            // Same terrain-object lookup the game's own hits use (dug terrain is nested 4 levels down).
            GameObject terrainObj = c.CompareTag("DiggerMesh") && c.transform.parent != null && c.transform.parent.parent != null &&
                                    c.transform.parent.parent.parent != null && c.transform.parent.parent.parent.parent != null
                ? c.transform.parent.parent.parent.parent.gameObject
                : c.gameObject;
            try
            {
                return trees.Spawn_Terrain_Tree_Around_HitPoint(terrainObj, hit.point);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Demolition] waking trees threw: " + ex.Message);
                return false;
            }
        }
    }
}
