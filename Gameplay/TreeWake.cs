using System.Collections.Generic;
using HarmonyLib;
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
            TreePasses.Add((center, radius, Time.time + 0.2f, 0));
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
                // The game gives physics only to the first 10 pieces of each smash batch
                // (Delay_AddRigid_To_Smashed_Shards: ProcessedCount < 11 - the rest jump away and despawn), and a
                // tree only falls if it gets that treatment. Wait for any running batch (the blast's own) to end,
                // then smash at most 8 trees per batch.
                if (Traverse.Create(topOnHit).Field("cor").GetValue() != null)
                {
                    TreePasses.Add((p.Center, p.Radius, Time.time + 0.1f, p.Pass));
                    continue;
                }
                bool more = false;
                var done = new HashSet<Build_Info>();
                var hitPieces = new HashSet<Battle_Info>();
                ExplosionDrops.Scope++;
                try
                {
                    foreach (Collider c in Physics.OverlapSphere(p.Center, p.Radius, StructureMask(), QueryTriggerInteraction.Ignore))
                    {
                        Build_Info bi = c != null ? c.GetComponentInParent<Build_Info>() : null;
                        if (bi == null || bi._Type != Build_Info.Type.TerrainTreeBI || !done.Add(bi) || !HasStandingPiece(bi))
                        {
                            continue;
                        }
                        if (broken >= 8)
                        {
                            more = true;
                            break;
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
                    Plugin.Diag($"[Demolition] tree pass {p.Pass + 1}: {broken} tree(s) that loaded after the blast brought down.");
                }
                if (more)
                {
                    TreePasses.Add((p.Center, p.Radius, Time.time + 0.1f, p.Pass));   // the rest, next batch
                }
                else if (p.Pass + 1 < TreePassDelays.Length)
                {
                    TreePasses.Add((p.Center, p.Radius, Time.time + TreePassDelays[p.Pass + 1] - TreePassDelays[p.Pass], p.Pass + 1));
                }
            }
        }

        private static bool HasStandingPiece(Build_Info bi)
        {
            foreach (Battle_Info b in bi.Spawned_BaIs)
            {
                if (b != null && !b.Smashed && !b.Is_Fallen)
                {
                    return true;
                }
            }
            return bi.Spawned_BaIs.Count == 0;   // not split into pieces yet: SmashWholeBlock spawns them
        }

        private static readonly RaycastHit[] WakeHits = new RaycastHit[16];

        private static bool WakeAt(TerrainTreeManager trees, Global_Infos g, Vector3 p)
        {
            // All hits, nearest first, and take the first that is actual terrain. A single raycast stopped on the
            // charge itself (its shootable collider is on the Scene layer) or on a tree/prop above the ground, and
            // handed the game that instead of the terrain - so the tree the charge was stuck to never woke.
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 3f, Vector3.down, WakeHits, 15f, g.Mask_Scene.value, QueryTriggerInteraction.Ignore);
            System.Array.Sort(WakeHits, 0, n, HitDistance.Instance);
            for (int i = 0; i < n; i++)
            {
                Collider c = WakeHits[i].collider;
                if (c == null)
                {
                    continue;
                }
                // Same terrain-object lookup the game's own hits use (dug terrain is nested 4 levels down).
                GameObject terrainObj = c.gameObject;
                if (c.CompareTag("DiggerMesh"))
                {
                    Transform t = c.transform;
                    for (int k = 0; k < 4 && t != null; k++)
                    {
                        t = t.parent;
                    }
                    if (t == null)
                    {
                        continue;
                    }
                    terrainObj = t.gameObject;
                }
                if (terrainObj.GetComponent<TerrainTreeSpawner>() == null)
                {
                    continue;   // not terrain: the charge, a tree, a prop - keep looking down
                }
                try
                {
                    return trees.Spawn_Terrain_Tree_Around_HitPoint(terrainObj, WakeHits[i].point);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Demolition] waking trees threw: " + ex.Message);
                    return false;
                }
            }
            return false;
        }

        private sealed class HitDistance : IComparer<RaycastHit>
        {
            internal static readonly HitDistance Instance = new HitDistance();

            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }    }
}
