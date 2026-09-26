using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Whole-building support check for world buildings after a demolition.
    ///
    /// The game's own check for world buildings (the zone-wall fall check in Smash_Fallen_Manager.Slice_Zone)
    /// is LOCAL: it only looks at the 3x3 patch of sections around the one that just broke, and drops a
    /// column only when most of that patch is gone - a building can float over a missing ground floor.
    ///
    /// A world building ("zone" building, ItemType.ZoneSmashBI) is a grid of 4 m sections: its children, each
    /// a MeshCollider named "x,y,z" (its local grid position - Build_System :19282). This maps the grid and
    /// works out, for every standing section, its SPAN: how many sections sideways it is from the nearest
    /// column of standing sections that goes down to the ground (moving straight up a column is free; every
    /// sideways or downward step costs one). Plain "is it connected to the ground at all" was far too
    /// lenient - one surviving ground-floor wall anywhere held up an entire 775-section building.
    ///
    /// The span is compared with the building's OWN span before the first charge went off (captured then),
    /// so an intact hall with a wide roof is left alone: a section comes down only when the demolition
    /// pushed its span past what it had by more than RemoteCharge.SupportSpan sections, or cut it off
    /// completely. Lowest first, through the same cutting path as the demolition (saved as destroyed), and
    /// repeated a few rounds, since each collapse can strand more.
    /// </summary>
    internal static partial class ExplosionDamage
    {
        private const int Cell = 4;               // grid step of a world building's sections (2 x Smash_Fallen_Manager._halfZone)
        private const int MaxCollapsePerCheck = 800;
        private const int Unreachable = int.MaxValue;

        private static readonly Dictionary<Build_Info, int> SupportPending = new Dictionary<Build_Info, int>();   // building -> rounds done
        private static readonly Dictionary<Build_Info, Dictionary<Vector3Int, int>> SupportBaseline = new Dictionary<Build_Info, Dictionary<Vector3Int, int>>();
        private static float _supportNext;
        private static readonly RaycastHit[] GroundHits = new RaycastHit[16];

        /// <summary>Called as a demolition queues a building's sections - before any are cut.</summary>
        private static void NoteDemolished(Build_Info bi)
        {
            if (bi == null || !Plugin.BuildingSupportCheck.Value || SupportPending.ContainsKey(bi))
            {
                return;
            }
            SupportPending[bi] = 0;
            try
            {
                Smash_Fallen_Manager smash = Smash_Fallen_Manager.ins;
                var standing = MapStanding(smash, bi, out int minY);
                SupportBaseline[bi] = Spans(standing, minY, bi);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Support] baseline of '{bi.name}' threw: {ex.Message}");
            }
        }

        /// <summary>Called every frame (via TickDeferred). Runs once the demolition's own cutting is done.</summary>
        private static void TickSupport()
        {
            if (SupportPending.Count == 0 || ZoneQueue.Count > 0 || Clearances.Count > 0 || Time.time < _supportNext)
            {
                return;
            }
            Smash_Fallen_Manager smash = Smash_Fallen_Manager.ins;
            if (smash == null || smash._corSlice != null)
            {
                return;
            }
            _supportNext = Time.time + 0.75f;   // let the game's own falling settle between rounds
            foreach (Build_Info bi in new List<Build_Info>(SupportPending.Keys))
            {
                int round = SupportPending[bi];
                int queued = 0;
                if (bi != null && round < 5)
                {
                    try
                    {
                        queued = CheckBuildingSupport(smash, bi, round + 1);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Support] check of '{bi.name}' threw: {ex}");
                    }
                }
                if (queued > 0)
                {
                    SupportPending[bi] = round + 1;   // check again once these are down
                }
                else
                {
                    SupportPending.Remove(bi);
                    SupportBaseline.Remove(bi);
                }
            }
        }

        private struct Section
        {
            internal MeshCollider Mc;
            internal int Index;
            internal bool Intact;     // uncut; otherwise cut with pieces still attached
            internal Bounds Bounds;
        }

        /// <summary>Every standing section of the building, by grid position. minY = its lowest level, standing or not.</summary>
        private static Dictionary<Vector3Int, Section> MapStanding(Smash_Fallen_Manager smash, Build_Info bi, out int minY)
        {
            Dictionary<int, Transform> cutTops = null;
            if (smash != null)
            {
                var tops = Traverse.Create(smash).Field("_BIsibling2SilceTop").GetValue<Dictionary<Transform, Dictionary<int, Transform>>>();
                tops?.TryGetValue(bi.transform, out cutTops);
            }
            int battle = Global_Infos.ins != null ? Global_Infos.ins.L_Battle : bi.gameObject.layer;

            var standing = new Dictionary<Vector3Int, Section>();
            minY = int.MaxValue;
            Transform root = bi.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform t = root.GetChild(i);
                if (!TryParseCell(t.name, out Vector3Int key) || !t.TryGetComponent(out MeshCollider mc))
                {
                    continue;
                }
                minY = Mathf.Min(minY, key.y);
                bool intact = t.gameObject.activeInHierarchy && mc.enabled && t.localScale.sqrMagnitude > 1e-6f && !t.CompareTag("ZoneSmashed");
                Bounds b = intact ? mc.bounds : default;
                bool partial = false;
                if (!intact && cutTops != null && cutTops.TryGetValue(i, out Transform top) && top != null)
                {
                    for (int k = 0; k < top.childCount; k++)
                    {
                        Transform s = top.GetChild(k);
                        if (s.gameObject.activeInHierarchy && s.gameObject.layer == battle && s.CompareTag("ZoneSlice") &&
                            s.TryGetComponent(out MeshCollider smc) && smc.enabled && s.GetComponent<Rigidbody>() == null)
                        {
                            if (!partial)
                            {
                                b = smc.bounds;
                                partial = true;
                            }
                            else
                            {
                                b.Encapsulate(smc.bounds);
                            }
                        }
                    }
                }
                if (intact || partial)
                {
                    standing[key] = new Section { Mc = mc, Index = i, Intact = intact, Bounds = b };
                }
            }
            return standing;
        }

        /// <summary>
        /// Span of every standing section: 0 on the ground, same as the section below it when stacked straight
        /// up, +1 for every sideways, diagonal or downward step from the nearest supported section (0-1 BFS).
        /// </summary>
        private static Dictionary<Vector3Int, int> Spans(Dictionary<Vector3Int, Section> standing, int minY, Build_Info bi)
        {
            var span = new Dictionary<Vector3Int, int>(standing.Count);
            var deque = new LinkedList<Vector3Int>();
            foreach (KeyValuePair<Vector3Int, Section> kv in standing)
            {
                if (kv.Key.y == minY || RestsOnGround(kv.Value.Bounds, bi))
                {
                    span[kv.Key] = 0;
                    deque.AddLast(kv.Key);
                }
            }
            while (deque.Count > 0)
            {
                Vector3Int c = deque.First.Value;
                deque.RemoveFirst();
                int here = span[c];
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                    {
                        continue;
                    }
                    Vector3Int nb = new Vector3Int(c.x + dx * Cell, c.y + dy * Cell, c.z + dz * Cell);
                    if (!standing.ContainsKey(nb))
                    {
                        continue;
                    }
                    int step = dx == 0 && dz == 0 && dy == 1 ? 0 : 1;   // straight up a column is free
                    int cost = here + step;
                    if (span.TryGetValue(nb, out int old) && old <= cost)
                    {
                        continue;
                    }
                    span[nb] = cost;
                    if (step == 0)
                    {
                        deque.AddFirst(nb);
                    }
                    else
                    {
                        deque.AddLast(nb);
                    }
                }
            }
            return span;
        }

        private static int CheckBuildingSupport(Smash_Fallen_Manager smash, Build_Info bi, int round)
        {
            var standing = MapStanding(smash, bi, out int minY);
            if (standing.Count == 0)
            {
                return 0;
            }
            Dictionary<Vector3Int, int> now = Spans(standing, minY, bi);
            SupportBaseline.TryGetValue(bi, out Dictionary<Vector3Int, int> before);
            int tolerance = Plugin.SupportSpan.Value;

            var loose = new List<KeyValuePair<Vector3Int, Section>>();
            int cutOff = 0, overSpan = 0;
            foreach (KeyValuePair<Vector3Int, Section> kv in standing)
            {
                int was = Unreachable;
                if (before != null && !before.TryGetValue(kv.Key, out was))
                {
                    // Already unsupported when this demolition started - left floating by an EARLIER one
                    // (its baseline was taken after that). Still floating now: bring it down too.
                    if (!now.ContainsKey(kv.Key))
                    {
                        loose.Add(kv);
                        cutOff++;
                    }
                    continue;
                }
                if (!now.TryGetValue(kv.Key, out int s))
                {
                    loose.Add(kv);
                    cutOff++;
                }
                else if (before != null && s > was + tolerance)   // no baseline: only judge what is cut off
                {
                    loose.Add(kv);
                    overSpan++;
                }
            }
            if (loose.Count == 0)
            {
                Plugin.Log.LogInfo($"[Support] '{bi.name}' round {round}: all {standing.Count} standing section(s) still supported.");
                return 0;
            }

            // Lowest first: the collapse starts where the support went and works up.
            loose.Sort((a, b) => a.Key.y.CompareTo(b.Key.y));
            int n = 0;
            foreach (KeyValuePair<Vector3Int, Section> kv in loose)
            {
                if (n >= MaxCollapsePerCheck)
                {
                    break;
                }
                Section s = kv.Value;
                ZoneQueue.Add(new ZoneJob
                {
                    Bi = bi,
                    Col = s.Intact ? s.Mc : null,
                    Point = s.Bounds.center,
                    Damage = 1000000f,
                    Order = 100000f + kv.Key.y + n * 0.0001f,
                    Expire = Time.time + 180f,
                    Demolish = s.Intact,
                    Stage = s.Intact ? 0 : 2,     // already cut: straight to dropping its pieces
                    ChildIndex = s.Index,
                    Collapse = true,
                });
                n++;
            }
            _zoneSorted = false;
            FastDemolitionSlicing.ActiveUntil = Mathf.Max(FastDemolitionSlicing.ActiveUntil, Time.time + 30f);
            DemolitionWindowUntil = Mathf.Max(DemolitionWindowUntil, Time.time + 60f);
            Plugin.Log.LogInfo($"[Support] '{bi.name}' round {round}: {standing.Count} section(s) standing; {cutOff} cut off from the ground, " +
                               $"{overSpan} left spanning too far - bringing down {n}.");
            return n;
        }

        private static bool TryParseCell(string name, out Vector3Int key)
        {
            key = default;
            string[] p = name.Split(',');
            if (p.Length != 3 ||
                !int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
            {
                return false;
            }
            key = new Vector3Int(x, y, z);
            return true;
        }

        /// <summary>Something that isn't this building directly under the section's bottom.</summary>
        private static bool RestsOnGround(Bounds b, Build_Info self)
        {
            Vector3 from = new Vector3(b.center.x, b.min.y + 0.25f, b.center.z);
            int hits = Physics.RaycastNonAlloc(from, Vector3.down, GroundHits, 1.25f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                Collider c = GroundHits[i].collider;
                if (c == null || c.attachedRigidbody != null)
                {
                    continue;   // falling debris, dropped items, characters
                }
                Build_Info other = c.GetComponentInParent<Build_Info>();
                if (other == self || c.GetComponentInParent<PlacedMine>() != null ||
                    (other != null && other._ItemType == Build_Info.ItemType.GroundDebris))
                {
                    continue;   // rubble from the blast isn't a foundation
                }
                return true;
            }
            return false;
        }
    }
}
