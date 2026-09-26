using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Test key (RemoteCharge.SupportProbeKey): look at a part of a world building and press it - shows on screen
    /// (and logs) what the building support check thinks of that section: standing or not, its span, whether
    /// it counts as on the ground and what the ground test hit, and the chain of sections holding it up.
    /// For "why is this floor still floating?".
    /// </summary>
    internal static partial class ExplosionDamage
    {
        private static string _probeText;
        private static float _probeUntil;

        internal static void TickSupportProbe()
        {
            KeyCode key = Plugin.SupportProbeKey.Value;
            if (key == KeyCode.None || !Input.GetKeyDown(key))
            {
                return;
            }
            try
            {
                _probeText = Probe();
            }
            catch (System.Exception ex)
            {
                _probeText = "Support probe failed: " + ex.Message;
            }
            _probeUntil = Time.time + 20f;
            Plugin.Log.LogInfo("[SupportProbe] " + _probeText.Replace("\n", " | "));
        }

        internal static void DrawSupportProbe()
        {
            if (_probeText == null || Time.time > _probeUntil)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true };
            style.normal.textColor = Color.white;
            GUI.Box(new Rect(Screen.width * 0.5f + 40f, Screen.height * 0.25f, 620f, 330f), _probeText, style);
        }

        private static string Probe()
        {
            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (cam == null)
            {
                return "No camera.";
            }
            if (!Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 80f, ~0, QueryTriggerInteraction.Ignore))
            {
                return "Nothing under the crosshair.";
            }
            Collider col = hit.collider;
            Build_Info bi = col.GetComponentInParent<Build_Info>();
            if (bi == null || bi._ItemType != Build_Info.ItemType.ZoneSmashBI)
            {
                return $"'{col.name}' is not part of a world building's section grid" +
                       (bi != null ? $" - it belongs to '{bi.name}' ({bi._Type}/{bi._ItemType}), which the building check doesn't cover." : ".");
            }

            // Which section: a section collider is named "x,y,z"; a cut piece sits under a parent named by its
            // section's child index.
            Vector3Int key;
            if (!TryParseCell(col.name, out key))
            {
                Transform p = col.transform.parent;
                if (p == null || !int.TryParse(p.name, out int idx) || idx < 0 || idx >= bi.transform.childCount ||
                    !TryParseCell(bi.transform.GetChild(idx).name, out key))
                {
                    return $"Hit '{col.name}' of '{bi.name}' but couldn't work out which section it belongs to.";
                }
            }

            Smash_Fallen_Manager smash = Smash_Fallen_Manager.ins;
            var standing = MapStanding(smash, bi, out int minY);
            var sb = new StringBuilder();
            sb.Append($"Building '{bi.name}': {standing.Count} section(s) standing, lowest level y={minY}.\n");
            sb.Append($"Looking at section {key} (hit '{col.name}').\n");
            if (!standing.TryGetValue(key, out Section sec))
            {
                sb.Append("The check sees this section as GONE (not standing) - what you see is a leftover piece it doesn't track.");
                return sb.ToString();
            }
            sb.Append(sec.Intact ? "State: intact.\n" : "State: cut, with pieces still attached.\n");
            bool onGround = key.y == minY;
            string groundHit = null;
            if (!onGround)
            {
                onGround = RestsOnGroundWhy(sec.Bounds, bi, out groundHit);
            }
            sb.Append(key.y == minY ? "On the ground: yes (lowest level).\n"
                : onGround ? $"On the ground: yes - the ground test hit '{groundHit}' under it.\n"
                : "On the ground: no.\n");

            // Path back to the ground.
            Dictionary<Vector3Int, int> span = Spans(standing, minY, bi);
            if (!span.TryGetValue(key, out int s))
            {
                sb.Append("Support: NONE - it should come down on the next demolition check.");
                return sb.ToString();
            }
            sb.Append($"Span: {s} (sideways/down steps from a column reaching the ground).\n");
            int was = -1;
            if (SupportBaseline.TryGetValue(bi, out var before) && before.TryGetValue(key, out int b))
            {
                was = b;
                sb.Append($"Span before the current demolition: {b}.\n");
            }
            sb.Append("Held up by: ");
            Vector3Int cur = key;
            for (int step = 0; step < 14; step++)
            {
                int here = span[cur];
                if (here == 0 && (cur.y == minY || RestsOnGroundWhy(standing[cur].Bounds, bi, out _)))
                {
                    sb.Append($"{cur} [ground]");
                    break;
                }
                sb.Append($"{cur} -> ");
                // Step to the neighbour this one's span came from.
                Vector3Int next = cur;
                int bestCost = int.MaxValue;
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    Vector3Int nb = new Vector3Int(cur.x + dx * Cell, cur.y + dy * Cell, cur.z + dz * Cell);
                    if (nb == cur || !span.TryGetValue(nb, out int ns))
                    {
                        continue;
                    }
                    // cur came from nb if nb + step(nb->cur) == span[cur]; going from nb to cur is "up" when dy == -1 here.
                    int stepCost = dx == 0 && dz == 0 && dy == -1 ? 0 : 1;
                    if (ns + stepCost == here && ns < bestCost)
                    {
                        bestCost = ns;
                        next = nb;
                    }
                }
                if (next == cur)
                {
                    sb.Append("?");
                    break;
                }
                cur = next;
            }
            return sb.ToString();
        }

        private static bool RestsOnGroundWhy(Bounds b, Build_Info self, out string what)
        {
            what = null;
            Vector3 from = new Vector3(b.center.x, b.min.y + 0.25f, b.center.z);
            int hits = Physics.RaycastNonAlloc(from, Vector3.down, GroundHits, 1.25f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider c = GroundHits[i].collider;
                if (!IsGround(c))
                {
                    continue;
                }
                Build_Info other = c.GetComponentInParent<Build_Info>();
                what = other != null ? $"{c.name} of {other.name} ({other._Type}/{other._ItemType})" : $"{c.name} (layer {LayerMask.LayerToName(c.gameObject.layer)})";
                return true;
            }
            return false;
        }
    }
}
