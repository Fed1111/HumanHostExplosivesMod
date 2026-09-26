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
                Physics.SyncTransforms();   // the woken trees' colliders, for the sweep that follows this frame
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
