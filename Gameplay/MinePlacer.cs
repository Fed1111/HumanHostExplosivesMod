using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Puts a mine on the ground where the player is looking. A single LMB tap with a mine
    /// equipped - no charge bar, no throw animation.
    ///
    /// The spot is the first solid, roughly flat surface along the camera ray, within
    /// MinePlaceDistance of the player; failing that, the ground straight in front of their feet.
    /// If neither works (steep slope, looking at the sky from a ledge) nothing is placed and nothing
    /// is consumed. The placed mine has no collider or rigidbody: zombies can't kick it about,
    /// nothing re-paths around it, and it can't fall through terrain that hasn't streamed in.
    /// </summary>
    internal static class MinePlacer
    {
        private const float MinFlatness = 0.6f;   // normal.y - about 53 degrees of slope
        private const float MinHorizontal = 0.6f;

        internal static bool TryPlace(ExplosiveDef def, C_Controller_Base player)
        {
            if (!TryFindPlacement(def, player, out Vector3 point, out Quaternion rot, out _))
            {
                return false;
            }
            PlacedMine.Create(def, point, rot, player);
            return true;
        }

        /// <summary>
        /// The checks and the spot, without placing - so the kneel can play first. onWall: a
        /// WallPlaceable item (the demolition charge) stuck to a vertical surface - no kneel for that.
        /// </summary>
        internal static bool TryFindPlacement(ExplosiveDef def, C_Controller_Base player, out Vector3 point, out Quaternion rot, out bool onWall)
        {
            point = Vector3.zero;
            rot = Quaternion.identity;
            onWall = false;
            if (def == null || player == null || def.RuntimeMesh == null || def.RuntimeMaterial == null)
            {
                return false;
            }
            MineManager.Prune();
            if (MineManager.Count >= Plugin.MaxActiveMines.Value)
            {
                Plugin.Toast($"Too many mines placed ({MineManager.Count}/{Plugin.MaxActiveMines.Value})");
                return false;
            }

            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (cam == null)
            {
                return false;
            }

            if (!FindSpot(cam, player, def.WallPlaceable, out point, out Vector3 normal, out onWall))
            {
                Plugin.Toast("No room to place a mine here");
                return false;
            }

            if (onWall)
            {
                // Flat against the wall (model up = wall normal), long side horizontal (model forward
                // points up the wall).
                Vector3 upWall = Vector3.ProjectOnPlane(Vector3.up, normal);
                rot = Quaternion.LookRotation(upWall.sqrMagnitude > 0.001f ? upWall.normalized : Vector3.forward, normal);
                return true;
            }

            // Face where the player is LOOKING (camera), not where the body happens to point - the
            // anti-personnel charge fires out of its front.
            Vector3 flatFwd = cam.forward;
            flatFwd.y = 0f;
            float yaw = flatFwd.sqrMagnitude > 0.001f ? Quaternion.LookRotation(flatFwd).eulerAngles.y : 0f;
            rot = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, yaw, 0f);
            return true;
        }

        private static bool FindSpot(Transform cam, C_Controller_Base player, bool allowWall,
                                     out Vector3 point, out Vector3 normal, out bool onWall)
        {
            onWall = false;
            int mask = MolotovProjectile.WorldMask();
            Vector3 feet = player.transform.position;
            float maxRange = Vector3.Distance(cam.position, feet) + Plugin.MinePlaceDistance.Value + 1f;

            RaycastHit[] hits = Physics.RaycastAll(cam.position, cam.forward, maxRange, mask, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit hit in hits)
            {
                if (IsOwn(hit.collider, player))
                {
                    continue;
                }
                // First real surface decides it - never place "through" a wall onto the floor behind.
                if (Acceptable(hit, feet))
                {
                    point = hit.point;
                    normal = hit.normal;
                    return true;
                }
                if (allowWall && AcceptableWall(hit, feet))
                {
                    point = hit.point;
                    normal = hit.normal;
                    onWall = true;
                    return true;
                }
                break;
            }

            Vector3 fwd = player.transform.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            Vector3 from = feet + fwd * 1.5f + Vector3.up * 1.5f;
            if (Physics.Raycast(from, Vector3.down, out RaycastHit down, 4f, mask, QueryTriggerInteraction.Ignore)
                && !IsOwn(down.collider, player) && down.normal.y >= MinFlatness)
            {
                point = down.point;
                normal = down.normal;
                return true;
            }

            point = normal = Vector3.zero;
            return false;
        }

        private static bool Acceptable(RaycastHit hit, Vector3 feet)
        {
            if (hit.normal.y < MinFlatness)
            {
                return false;
            }
            Vector3 d = hit.point - feet;
            float vertical = d.y;
            d.y = 0f;
            float horizontal = d.magnitude;
            return horizontal >= MinHorizontal && horizontal <= Plugin.MinePlaceDistance.Value + 0.5f && Mathf.Abs(vertical) < 2.5f;
        }

        /// <summary>A roughly vertical surface within reach, between the ankles and just over head height.</summary>
        private static bool AcceptableWall(RaycastHit hit, Vector3 feet)
        {
            if (Mathf.Abs(hit.normal.y) > 0.35f)
            {
                return false;
            }
            Vector3 d = hit.point - feet;
            float vertical = d.y;
            d.y = 0f;
            float horizontal = d.magnitude;
            return horizontal >= 0.25f && horizontal <= Plugin.MinePlaceDistance.Value + 0.5f && vertical > 0.05f && vertical < 2.4f;
        }

        private static bool IsOwn(Collider c, C_Controller_Base player)
        {
            if (c == null)
            {
                return true;
            }
            if (c.GetComponentInParent<C_Controller_Base>() == player)
            {
                return true;
            }
            Tool_Interacter tool = c.GetComponentInParent<Tool_Interacter>();
            return tool != null && tool._CharBase == player;
        }
    }
}
