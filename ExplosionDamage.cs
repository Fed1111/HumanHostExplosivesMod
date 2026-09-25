using System.Collections.Generic;
using HarmonyLib;
using MLSpace;
using UnityEngine;

namespace HumanHostExplosives
{
    internal static class ExplosionDamage
    {
        /// <summary>
        /// Applies falloff damage to every living creature within radius of center, using the
        /// game's own trap/fall damage pipeline (Creature_Mgr + Smash_Fallen_Manager.Minus_Char_HP).
        /// Returns the number of creatures hit.
        ///
        /// requireLineOfSight (default off, so nothing calling this without it changes behavior):
        /// when on, each victim's damage is additionally scaled by an exposure fraction from the
        /// same 3-sample-height raycast technique NailbombProjectile.FireShrapnel uses - a fully
        /// covered target (0/3 clear) takes nothing, partial cover gives partial damage. This is
        /// what makes the grenade respect cover the same way the nail bomb does.
        /// </summary>
        internal static int Apply(
            Vector3 center,
            float radius,
            float maxDamage,
            C_Controller_Base attacker,
            float hitFlyForce = 1f,
            float hitReact = 0.8f,
            bool requireLineOfSight = false)
        {
            Creature_Mgr creatureMgr = Creature_Mgr.ins;
            Smash_Fallen_Manager smashMgr = Smash_Fallen_Manager.ins;
            if (creatureMgr == null || smashMgr == null || creatureMgr.capCol_To_Controller == null)
            {
                Plugin.Log.LogWarning("[Explosion] Creature_Mgr or Smash_Fallen_Manager not available (no world loaded?). Skipping AoE damage.");
                return 0;
            }

            Global_Infos globalInfos = Global_Infos.ins;
            int mask = globalInfos != null ? globalInfos.Mask_Creature.value : -1;

            Collider[] hitColliders = Physics.OverlapSphere(center, radius, mask, QueryTriggerInteraction.Ignore);
            var alreadyHit = new HashSet<C_Controller_Base>();
            int hits = 0;
            int unmappedColliders = 0;

            foreach (Collider col in hitColliders)
            {
                if (!creatureMgr.capCol_To_Controller.TryGetValue(col, out C_Controller_Base ctrl) || ctrl == null)
                {
                    // A collider was found in range but doesn't map to a creature controller -
                    // if this fires a lot with a creature genuinely nearby, the mask/lookup
                    // itself is the problem, not the damage call below.
                    unmappedColliders++;
                    continue;
                }
                if (!alreadyHit.Add(ctrl))
                {
                    continue;
                }
                bool isSelf = attacker != null && ctrl == attacker;
                if (isSelf && !Plugin.AllowSelfDamage.Value)
                {
                    continue;
                }
                if (ctrl.char_Status == null || ctrl.char_Status._CurrHP <= 0f)
                {
                    continue;
                }

                Vector3 targetPos = ctrl.capCol != null ? ctrl.capCol.bounds.center : ctrl.transform.position;
                float dist = Vector3.Distance(center, targetPos);
                float falloff = Mathf.Clamp01(1f - dist / radius);
                if (falloff <= 0f)
                {
                    continue;
                }

                float exposure = 1f;
                if (requireLineOfSight)
                {
                    exposure = ComputeExposure(creatureMgr, ctrl, center);
                    if (exposure <= 0f)
                    {
                        continue;   // fully behind cover - nothing reaches this target
                    }
                }

                Vector3 hitDirect = targetPos - center;
                hitDirect = hitDirect.sqrMagnitude > 0.0001f ? hitDirect.normalized : Vector3.up;

                // Grenades are risky to use at close range in real life too - self-damage uses
                // the same falloff as everyone else, just scaled down separately so it can be
                // tuned (or turned off) without affecting damage to others.
                float damage = maxDamage * falloff * exposure * (isSelf ? Plugin.SelfDamageMultiplier.Value : 1f);
                BodyColliderScript bodyScript = ctrl._ragDollMgr ? ctrl._ragDollMgr._headBodyScript : null;

                smashMgr.Minus_Char_HP(
                    ctrl,
                    bodyScript,
                    targetPos,
                    switchToAnimancer: true,
                    damage: damage,
                    damageInterval: 0.05f,
                    hitDirect: hitDirect,
                    hitFlyForce: hitFlyForce,
                    hitReact: hitReact,
                    mustHitDown: false,
                    bloodPos: targetPos,
                    bloodParticle: null,
                    useDefaultBloodPar: true,
                    getEXP: true);

                hits++;
                // MaxHP logged deliberately (not diagnostics-gated) - zombie/creature HP is data,
                // not something visible in decompiled code, and varies unknown amounts by zone/tier.
                // This is the fastest way to get real numbers to tune ExplosionDamage/NailbombDamage
                // against: play a few zones, grep LogOutput.log for "[Explosion] Hit" afterward.
                Plugin.Log.LogInfo(
                    $"[Explosion] Hit {ctrl.name} (MaxHP={ctrl.char_Status._MaxHP:F0}) at {dist:F1}m (falloff={falloff:F2}" +
                    (requireLineOfSight ? $", exposure={exposure:F2}" : "") + $") for {damage:F0} dmg.");
            }

            if (hits == 0 && hitColliders.Length > 0)
            {
                Plugin.Log.LogInfo($"[Explosion] {hitColliders.Length} collider(s) in range, {unmappedColliders} didn't map to a creature controller, 0 hits.");
            }

            return hits;
        }

        // Same 3-height sampling NailbombProjectile.FireShrapnel uses - feet/torso/head - so a
        // target crouched behind a wall only catches what's actually exposed rather than an
        // all-or-nothing check.
        private static readonly float[] ExposureSampleHeights = { 0.4f, 1.0f, 1.6f };

        /// <summary>
        /// Fraction (0..1) of ExposureSampleHeights that has a clear line from origin to the
        /// victim - i.e. how much of their body the blast can actually reach.
        /// </summary>
        private static float ComputeExposure(Creature_Mgr creatureMgr, C_Controller_Base victim, Vector3 origin)
        {
            Vector3 basePos = victim.transform.position;
            int clear = 0;
            for (int i = 0; i < ExposureSampleHeights.Length; i++)
            {
                Vector3 target = basePos + Vector3.up * ExposureSampleHeights[i];
                Vector3 delta = target - origin;
                float dist = delta.magnitude;
                if (dist < 0.01f)
                {
                    clear++;
                    continue;
                }

                // Anything that isn't this victim's own collider counts as cover.
                if (Physics.Raycast(origin, delta / dist, out RaycastHit hit, dist,
                                     ~0, QueryTriggerInteraction.Ignore))
                {
                    C_Controller_Base blocker = ResolveCharacter(creatureMgr, hit.collider);
                    if (blocker != victim)
                    {
                        continue;   // cover did its job for this sample point
                    }
                }
                clear++;
            }
            return clear / (float)ExposureSampleHeights.Length;
        }

        /// <summary>
        /// Maps a raycast-hit collider back to the character that owns it - a hit usually lands on
        /// a ragdoll bone collider rather than the capsule Creature_Mgr keys its lookup by, so walk
        /// up the hierarchy before giving up.
        /// </summary>
        private static C_Controller_Base ResolveCharacter(Creature_Mgr mgr, Collider col)
        {
            if (col == null)
            {
                return null;
            }
            if (mgr.capCol_To_Controller.TryGetValue(col, out C_Controller_Base direct) && direct != null)
            {
                return direct;
            }
            return col.GetComponentInParent<C_Controller_Base>();
        }

        /// <summary>
        /// Applies flat (non-falloff) chip damage to nearby buildable/structural things,
        /// regardless of their specific type or which physics layer they happen to use - a
        /// grenade shouldn't need to know a structure's name or category to damage it. Combines
        /// every damage path the vanilla game itself uses (see Tool_Interacter's hit-resolution
        /// code): the generic per-shard Battle_Info.MinusHP() path most buildables use (shards
        /// lazily spawned via Smash_Fallen_Manager.Spawn_BaIs_Under_BI), the separate single-HP-pool
        /// SysHouse_BigWall_MinusHP() path for Build_Info.ItemType.SysHouseBigWall structures, and
        /// a direct Battle_Info fallback for anything with a spawned shard collider but no
        /// Build_Info in its parent chain. Searches the union of the Build/Battle/Scene layers
        /// (Mask_Build alone missed at least one real structure entirely, at point-blank range,
        /// across many throws). The one vanilla path NOT replicated is ZoneSmash_BI_MinusHP - an
        /// earlier version of this method used it and it was a silent no-op for every wall tested,
        /// including real player-built ones (confirmed via reflection into the private
        /// Get_RealZonePosRound check it relies on internally); everything ZoneSmashBI-tagged that
        /// still has spawnable shards is covered by the generic path instead.
        /// Returns the number of pieces/structures actually damaged.
        /// </summary>
        /// <param name="wholeBlocks">
        /// Demolition: every generic block the blast touches is broken COMPLETELY - all of its shards,
        /// not just the ones in range, and regardless of their HP. Blocks otherwise go in two stages:
        /// the blast breaks what it can reach, and the game's support check (TopOnHit.Begin_Check_Fall ->
        /// Check_Fallen_For_Smash, fed only by shards that were actually smashed) collapses what is left
        /// hanging - so a partly-damaged block would stand until hit again. Breaking the whole block lets
        /// that same vanilla check bring down whatever it was holding up. Goes through
        /// Process_Smashed_Shard with its guards ON (forceRun false), so nothing is broken mid-load or
        /// mid-save.
        /// </param>
        /// <summary>Per-blast demolition tally, logged once per demolition blast (not diagnostics-gated).</summary>
        private struct ZoneJob
        {
            internal Build_Info Bi;
            internal Collider Col;
            internal Vector3 Point;
            internal float Damage;
            internal float Order;    // blast batch, then distance from that blast
            internal float Expire;
            internal bool Sweep;     // no collider: find the shard pieces left at Point and hit those
            internal bool Demolish;  // demolition: after cutting a cell, sweep its shards too
        }

        private static readonly List<ZoneJob> ZoneQueue = new List<ZoneJob>();
        private static int _zoneBatch;
        private static bool _zoneSorted = true;

        /// <summary>
        /// Called from Plugin.Update: hands queued zone-wall hits to the game one at a time, each as soon as
        /// the previous slice has finished (_corSlice null; the shard path also waits out furniture
        /// spawning). Walls come apart outward from the blast over a moment instead of only the first cell.
        /// </summary>
        internal static void TickZoneQueue()
        {
            if (ZoneQueue.Count == 0)
            {
                return;
            }
            Smash_Fallen_Manager smash = Smash_Fallen_Manager.ins;
            if (smash == null)
            {
                return;
            }
            if (!_zoneSorted)
            {
                ZoneQueue.Sort((a, b) => a.Order.CompareTo(b.Order));
                _zoneSorted = true;
            }
            int budget = 6;
            ExplosionDrops.Scope++;
            try
            {
                while (ZoneQueue.Count > 0 && budget-- > 0 && smash._corSlice == null)
                {
                    ZoneJob job = ZoneQueue[0];
                    ZoneQueue.RemoveAt(0);
                    if (job.Bi == null || Time.time > job.Expire)
                    {
                        continue;
                    }
                    try
                    {
                        RunZoneJob(smash, job);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Explosion] zone hit on '{job.Bi.name}' threw: {ex.Message}");
                    }
                }
            }
            finally
            {
                ExplosionDrops.Scope--;
            }
        }

        private static readonly Collider[] SweepBuffer = new Collider[64];

        /// <summary>
        /// One queued zone-wall hit, checked against the wall as it is NOW - a queued collider can have
        /// been sliced by an earlier hit in the meantime.
        ///  - A shard piece (tag ZoneSlice, parent named with its cell index - the game int.Parse()s it):
        ///    the shard path.
        ///  - A ZoneSlice collider whose parent is NOT a number: its cell was cut up since it was queued;
        ///    that collider is no longer something the game can hit. Sweep for the real shards instead.
        ///  - A whole cell: the cell path, and for demolition a follow-up sweep of whatever shards the cut
        ///    leaves standing (they would otherwise hold the structure up - "the pillar took several blasts").
        /// </summary>
        private static void RunZoneJob(Smash_Fallen_Manager smash, ZoneJob job)
        {
            ExplosionDrops.MarkRecent(job.Bi.gameObject, job.Point);
            if (job.Sweep)
            {
                int n = Physics.OverlapSphereNonAlloc(job.Point, 0.9f, SweepBuffer, ~0, QueryTriggerInteraction.Ignore);
                int added = 0;
                for (int i = 0; i < n && added < 24; i++)
                {
                    Collider c = SweepBuffer[i];
                    if (c == null || !c.CompareTag("ZoneSlice") || !IsShardPiece(c) || c.GetComponentInParent<Build_Info>() != job.Bi)
                    {
                        continue;
                    }
                    ZoneQueue.Insert(0, new ZoneJob
                    {
                        Bi = job.Bi, Col = c, Point = c.ClosestPoint(job.Point), Damage = job.Damage,
                        Order = job.Order, Expire = job.Expire,
                    });
                    added++;
                }
                return;
            }

            Collider col = job.Col;
            if (col == null || !col.gameObject.activeInHierarchy)
            {
                return;
            }
            if (col.CompareTag("ZoneSlice"))
            {
                if (!IsShardPiece(col))
                {
                    ZoneQueue.Insert(0, new ZoneJob { Bi = job.Bi, Point = job.Point, Damage = job.Damage, Order = job.Order, Expire = job.Expire, Sweep = true });
                    return;
                }
                if (Traverse.Create(smash).Field("_sysHouseMgr").Field("_inSpawningFurni").GetValue<bool>())
                {
                    ZoneQueue.Insert(0, job);   // the shard path waits for furniture spawning too
                    return;
                }
                smash.ZoneSmash_Shard_MinusHP(job.Bi, job.Point, col, job.Damage, isFromPlayer: true);
                return;
            }

            smash.ZoneSmash_BI_MinusHP(job.Bi, job.Point, col, job.Damage, isFromPlayer: true);
            if (job.Demolish)
            {
                // After this cell's slice runs, clear what it leaves standing.
                ZoneQueue.Add(new ZoneJob
                {
                    Bi = job.Bi, Point = job.Point, Damage = job.Damage, Order = job.Order + 0.5f,
                    Expire = job.Expire, Sweep = true,
                });
                _zoneSorted = false;
            }
        }

        /// <summary>A real shard piece: ZoneSmash_Shard_MinusHP int.Parse()s its parent's name as the cell index.</summary>
        private static bool IsShardPiece(Collider c)
        {
            Transform parent = c.transform.parent;
            return parent != null && parent.parent != null && parent.parent.parent != null &&
                   int.TryParse(parent.name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        private static readonly Dictionary<string, int> _demoReasons = new Dictionary<string, int>();
        private static readonly List<(Battle_Info Piece, float GiveUpAt)> Deferred = new List<(Battle_Info, float)>();
        private static float _nextDeferredTry;
        private static int _deferredDone;

        private static int _demoHouseBlocks, _demoBlocks, _demoShardsBroken, _demoShardsRefused, _demoWalls, _demoZoneCells;

        internal static int ApplyToBuildables(Vector3 center, float radius, float damage, bool wholeBlocks = false)
        {
            _demoReasons.Clear();
            _demoHouseBlocks = _demoBlocks = _demoShardsBroken = _demoShardsRefused = _demoWalls = _demoZoneCells = 0;
            int result;
            ExplosionDrops.Scope++;   // resources knocked loose land on the ground
            try
            {
                result = ApplyToBuildablesCore(center, radius, damage, wholeBlocks);
            }
            finally
            {
                ExplosionDrops.Scope--;
            }
            if (wholeBlocks)
            {
                Plugin.Log.LogInfo($"[Demolition] {_demoBlocks} block(s) ({_demoHouseBlocks} of them world-building): {_demoShardsBroken} shard(s) broken, " +
                                   $"{_demoShardsRefused} refused by the game (loading/saving/furniture guard); " +
                                   $"{_demoWalls} big wall(s) destroyed, {_demoZoneCells} zone cell(s) queued to break." +
                                   (_demoReasons.Count > 0 ? " Refused because: " + string.Join(", ", ReasonList()) + "." : "") +
                                   (Deferred.Count > 0 ? $" {Deferred.Count} queued to retry." : ""));
            }
            return result;
        }

        private static int ApplyToBuildablesCore(Vector3 center, float radius, float damage, bool wholeBlocks)
        {
            _zoneBatch++;
            _zoneSorted = false;
            Smash_Fallen_Manager smashMgr = Smash_Fallen_Manager.ins;
            Global_Infos globalInfos = Global_Infos.ins;
            if (smashMgr == null || globalInfos == null)
            {
                return 0;
            }

            // A grenade shouldn't care what a structure is named or which specific layer its
            // collider happens to sit on (Build for most buildables, Battle for already-spawned
            // shard pieces, Scene for some static/pre-built structures) - damage needs to reach
            // anything destructible nearby, so the sweep uses the union of all three instead of
            // Mask_Build alone.
            int combinedMask = globalInfos.Mask_Build.value | globalInfos.Mask_Battle.value | globalInfos.Mask_Scene.value;
            Collider[] hitColliders = Physics.OverlapSphere(center, radius, combinedMask, QueryTriggerInteraction.Ignore);
            var directlyFoundColliders = new HashSet<Collider>(hitColliders);
            var processedBuildInfos = new HashSet<Build_Info>();
            var alreadyHitPieces = new HashSet<Battle_Info>();
            int hits = 0;

            // Battle_Info.MinusHP() ONLY decrements an internal HP counter - it never checks
            // whether that reached zero, so calling it alone (as this method always did before)
            // left every hit piece fully intact and visually/functionally unchanged no matter how
            // many times it was "hit". Vanilla TopOnHit.One_Shot_Through checks BEFORE calling
            // MinusHP: if the incoming damage would meet or exceed the piece's remaining HP
            // (FatherBI.BaIs_HP_Lefts[nameIndex]), it calls TopOnHit.Process_Smashed_Shard instead
            // - the actual method that breaks/pools/removes the piece. Process_Smashed_Shard is a
            // public instance method (not static) with no obvious per-weapon binding of its own
            // (its dependencies - Init.ins, layer masks - are all global singletons cached at
            // Awake), so any live TopOnHit in the scene works identically for this purpose.
            TopOnHit topOnHit = UnityEngine.Object.FindObjectOfType<TopOnHit>();
            if (topOnHit == null && Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogWarning("[Explosion] No TopOnHit instance found in scene - lethal hits will only decrement HP, not actually destroy pieces.");
            }

            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo($"[Explosion] buildable sweep: {hitColliders.Length} collider(s) on Build|Battle|Scene layers within {radius}m.");
                foreach (Collider col in hitColliders)
                {
                    Build_Info bi = col.GetComponentInParent<Build_Info>();
                    Battle_Info piece = col.GetComponentInParent<Battle_Info>();
                    string biDetail = bi != null ? $"{bi.name} (Type={bi._Type}, ItemType={bi._ItemType})" : "NONE";
                    Plugin.Log.LogInfo($"[Explosion]   collider '{col.name}' (layer={LayerMask.LayerToName(col.gameObject.layer)}) -> Build_Info={biDetail}, Battle_Info(parent-search)={(piece != null ? piece.name : "NONE")}");

                    // AB_Pool's numbered colliders find a Build_Info via parent-search but not a
                    // Battle_Info, unlike every other structure tested (Cardboard/Ivy/MetalBoxes) -
                    // print the exact hierarchy so we can see structurally where the disconnect is,
                    // rather than guessing again.
                    if (bi != null && piece == null)
                    {
                        var pathParts = new System.Collections.Generic.List<string>();
                        Transform t = col.transform;
                        int depth = 0;
                        while (t != null && depth < 10)
                        {
                            bool hasBattleInfo = t.GetComponent<Battle_Info>() != null;
                            bool hasBuildInfo = t.GetComponent<Build_Info>() != null;
                            pathParts.Add($"{t.name}[BattleInfo={hasBattleInfo},BuildInfo={hasBuildInfo},layer={LayerMask.LayerToName(t.gameObject.layer)}]");
                            t = t.parent;
                            depth++;
                        }
                        Plugin.Log.LogInfo($"[Explosion]     hierarchy (self->up): {string.Join(" -> ", pathParts)}");
                    }
                }

                // ALL colliders regardless of layer/mask - a one-off wide sweep purely to find out
                // what a stubborn structure (never once appearing in the Mask_Build-only sweep
                // above even at point-blank range) actually looks like to the physics system: what
                // layer it's really on, and whether it even has a Build_Info at all vs. some other
                // door/structure component entirely.
                Collider[] everyCollider = Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore);
                Plugin.Log.LogInfo($"[Explosion] wide sweep (ALL layers): {everyCollider.Length} collider(s) within {radius}m.");
                foreach (Collider col in everyCollider)
                {
                    Build_Info bi = col.GetComponentInParent<Build_Info>();
                    string biDetail = bi != null ? $"{bi.name} (Type={bi._Type}, ItemType={bi._ItemType})" : "none";
                    Plugin.Log.LogInfo($"[Explosion]   [wide] '{col.name}' (layer={LayerMask.LayerToName(col.gameObject.layer)}) Build_Info={biDetail}");
                }
            }

            foreach (Collider col in hitColliders)
            {
                // Prefer a direct Battle_Info hit over deriving the owner via
                // GetComponentInParent<Build_Info>(): the collider intersecting our OverlapSphere
                // is itself proof of being in range (no need to recheck via a piece's
                // bounds-center distance, which can wrongly reject a large/oddly-shaped piece),
                // and Battle_Info.FatherBI is the authoritative owner set directly at spawn time.
                // This matters because for at least one real structure (a ZoneSmashBI-type "wall"
                // internally named AB_Pool) GetComponentInParent<Build_Info> on an
                // already-spawned piece's collider returned a Build_Info whose OWN Spawned_BaIs
                // list was empty - a mismatched ancestor, not the true owner - so every piece was
                // silently skipped despite clearly existing, being active, and being in range.
                Battle_Info directPiece = col.GetComponentInParent<Battle_Info>();
                if (wholeBlocks && directPiece != null && directPiece.FatherBI != null &&
                    directPiece.FatherBI._ItemType != Build_Info.ItemType.ZoneSmashBI &&
                    directPiece.FatherBI._ItemType != Build_Info.ItemType.SysHouseBigWall)
                {
                    if (processedBuildInfos.Add(directPiece.FatherBI))
                    {
                        hits += SmashWholeBlock(directPiece.FatherBI, topOnHit, alreadyHitPieces);
                    }
                    continue;
                }
                if (directPiece != null)
                {
                    if (!directPiece.Is_Fallen && !directPiece.Smashed && alreadyHitPieces.Add(directPiece))
                    {
                        if (DamagePiece(directPiece, damage, topOnHit, "direct"))
                        {
                            hits++;
                        }
                    }
                    continue;
                }

                // No already-spawned piece on this collider - it's either a structure that hasn't
                // had Spawn_BaIs_Under_BI run yet (its own outer Build_Info collider still active),
                // a SysHouseBigWall (which never spawns per-piece shards at all), or a ZoneSmashBI
                // cell (HP tracked per-COLLIDER, not per-Build_Info - see below).
                Build_Info buildInfo = col.GetComponentInParent<Build_Info>();
                if (buildInfo == null)
                {
                    continue;
                }

                // ZoneSmashBI is a fourth damage path, distinct from the two below and from the
                // generic Battle_Info system - confirmed real via a live axe test (a ZoneSmashBI
                // wall's "80/80" HP bar, driven by this exact method's
                // UI_Control.ins.Show_Target_Block_HP_Bar call, visibly went to 0 and the structure
                // collapsed) after Battle_Info.MinusHP/Process_Smashed_Shard NEVER fired for it
                // despite clean hit feedback. HP here is tracked per-CELL in a private dictionary
                // (_zoneData._zoneHPs) keyed off a registered child collider (Get_RealZonePosRound
                // looks the collider up in a private _child2ZoneLoPos map) - so unlike the other
                // paths this is deliberately NOT deduped by Build_Info (processedBuildInfos): a
                // multi-cell structure like a big wall has many independently-damageable cells, and
                // an earlier attempt at this exact method was judged "broken" from a reflection
                // probe that returned false, almost certainly because it was called with the wrong
                // collider rather than because the method itself doesn't work - THIS collider
                // (`col`) is the literal one our own OverlapSphere sweep found intersecting the
                // structure, so it should be the one the registered mapping actually expects.
                if (buildInfo._ItemType == Build_Info.ItemType.ZoneSmashBI)
                {
                    // Confirmed via a live axe test + a stack-trace probe on the HP bar UI method:
                    // a ZoneSmashBI cell that's ALREADY been partially sliced from earlier damage
                    // is tracked as a separate fragment shard (its own HP in a different private
                    // dictionary, _shard_HP, keyed by collider) and needs ZoneSmash_Shard_MinusHP
                    // instead - calling ZoneSmash_BI_MinusHP on it is a silent no-op (its internal
                    // Get_RealZonePosRound lookup doesn't recognize a shard collider). Vanilla
                    // (TopOnHit.Hit_ZoneSmash_House) picks between the two with exactly this tag
                    // check.
                    // Queued, not called here: the game only actually slices a cell when no slice is already
                    // running (Smash_Fallen_Manager._corSlice == null, Build_System ZoneSmash_*_MinusHP). Dozens of
                    // calls in one frame zeroed every cell's HP but sliced ONE - the wall stood until hammered.
                    // TickZoneQueue feeds them one at a time, nearest first. Hit point = the closest point on the
                    // cell's own collider, as a real hit would report, not the blast centre.
                    Vector3 hitPoint = col.ClosestPoint(center);
                    // Demolition cuts only the cells close to the charge: each cut is a multi-frame slice,
                    // run one at a time, so the whole 6 m radius (~200 cells) took many seconds. The rest of
                    // the structure comes down through the game's own fall check once the supports go.
                    if (wholeBlocks && Vector3.Distance(center, hitPoint) > Plugin.DemoCoreRadius.Value)
                    {
                        continue;
                    }
                    ExplosionDrops.MarkRecent(buildInfo.gameObject, center);
                    ZoneQueue.Add(new ZoneJob
                    {
                        Bi = buildInfo,
                        Col = col,
                        Point = hitPoint,
                        Damage = wholeBlocks ? 1000000f : damage,
                        // Nearest to ITS charge first, across all charges - not one blast's cells after another's.
                        Order = Vector3.Distance(center, hitPoint) + _zoneBatch * 0.001f,
                        Expire = Time.time + 20f,
                        Demolish = wholeBlocks,
                    });
                    hits++;
                    if (wholeBlocks)
                    {
                        _demoZoneCells++;
                    }
                    continue;
                }

                if (!processedBuildInfos.Add(buildInfo))
                {
                    continue;
                }

                // SysHouseBigWall is a third damage path, distinct from the generic Battle_Info
                // sub-piece system below - a big pre-built structural wall tracked as ONE HP pool
                // directly on the Build_Info itself (Shards_HP_Left), never split into individually
                // collidable shards, so Spawn_BaIs_Under_BI/Battle_Info.MinusHP never applies to it
                // (confirmed via decompiled TopOnHit.One_Shot_Through, which takes this exact
                // branch for this exact ItemType instead of the generic one).
                if (buildInfo._ItemType == Build_Info.ItemType.SysHouseBigWall)
                {
                    try
                    {
                        // Demolition: whatever the wall has left - one charge brings a big wall down.
                        float wallDamage = wholeBlocks ? Mathf.Max(damage, buildInfo.Shards_HP_Left + 1f) : damage;
                        float before = buildInfo.Shards_HP_Left;
                        smashMgr.SysHouse_BigWall_MinusHP(buildInfo, wallDamage, center);
                        hits++;
                        if (wholeBlocks)
                        {
                            _demoWalls++;
                            Plugin.Log.LogInfo($"[Demolition] big wall '{buildInfo.name}': HP {before:F0} -> {buildInfo.Shards_HP_Left:F0}.");
                        }
                        if (Plugin.EnableDiagnostics.Value)
                        {
                            Plugin.Log.LogInfo($"[Explosion] SysHouseBigWall '{buildInfo.name}': -{damage:F0} HP (Shards_HP_Left now {buildInfo.Shards_HP_Left:F0}).");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Explosion] SysHouse_BigWall_MinusHP threw for '{buildInfo.name}': {ex.Message}");
                    }
                    continue;
                }

                if (wholeBlocks)
                {
                    hits += SmashWholeBlock(buildInfo, topOnHit, alreadyHitPieces);
                    continue;
                }

                try
                {
                    smashMgr.Spawn_BaIs_Under_BI(buildInfo);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Explosion] Spawn_BaIs_Under_BI threw for '{buildInfo.name}': {ex.Message}");
                    continue;
                }

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo($"[Explosion] '{buildInfo.name}' has {buildInfo.Spawned_BaIs.Count} Spawned_BaIs pieces total.");
                }

                int skippedNull = 0, skippedDead = 0, skippedDup = 0, skippedFar = 0;
                foreach (Battle_Info piece in buildInfo.Spawned_BaIs)
                {
                    if (piece == null)
                    {
                        skippedNull++;
                        continue;
                    }
                    if (piece.Is_Fallen || piece.Smashed)
                    {
                        skippedDead++;
                        continue;
                    }
                    if (!alreadyHitPieces.Add(piece))
                    {
                        skippedDup++;
                        continue;
                    }

                    // A piece whose OWN collider was one of the colliders Physics.OverlapSphere
                    // itself found is confirmed in-range by the physics engine's actual geometry
                    // test - no need to recheck via a bounds-center distance heuristic, which is
                    // unreliable for large/sprawling meshes (a spread-out ivy vine's collision
                    // point can be well within the blast while its overall bounds center sits
                    // meters away, wrongly failing the recheck and silently skipping a piece the
                    // explosion demonstrably touched). Only fall back to the heuristic for OTHER
                    // pieces of a multi-piece structure that weren't directly found (a real
                    // multi-piece wall can have Spawn_BaIs_Under_BI populate pieces scattered
                    // across the whole structure, most of which genuinely aren't near this blast).
                    bool directHit = piece.selfMeshCollider != null && directlyFoundColliders.Contains(piece.selfMeshCollider);
                    float dist;
                    if (directHit)
                    {
                        dist = Vector3.Distance(center, piece.selfMeshCollider.ClosestPoint(center));
                    }
                    else
                    {
                        Vector3 piecePos = piece.selfMeshRender != null ? piece.selfMeshRender.bounds.center : piece.transform.position;
                        dist = Vector3.Distance(center, piecePos);
                        if (dist > radius)
                        {
                            skippedFar++;
                            if (Plugin.EnableDiagnostics.Value)
                            {
                                Plugin.Log.LogInfo($"[Explosion]   piece '{piece.name}' on '{buildInfo.name}' too far: {dist:F1}m (radius {radius:F1}m), pos={piecePos}.");
                            }
                            continue;
                        }
                    }

                    if (DamagePiece(piece, damage, topOnHit, $"on '{buildInfo.name}' at {dist:F1}m"))
                    {
                        hits++;
                    }
                }

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo($"[Explosion] '{buildInfo.name}' piece loop done: {skippedNull} null, {skippedDead} already fallen/smashed, {skippedDup} duplicate, {skippedFar} out of radius.");
                }
            }

            return hits;
        }

        private static IEnumerable<string> ReasonList()
        {
            foreach (KeyValuePair<string, int> kv in _demoReasons)
            {
                yield return $"{kv.Key} x{kv.Value}";
            }
        }

        private static bool IsTransient(string reason) =>
            reason.StartsWith("loading") || reason.StartsWith("saving") || reason.StartsWith("detecting");

        /// <summary>
        /// Which of Process_Smashed_Shard's guards (Build_System:22198-22276) turned a smash away - the
        /// same checks in the same order. Private fields are read through Traverse; a missing field reads as
        /// "not blocking" rather than throwing.
        /// </summary>
        private static string RefusalReason(Battle_Info piece, TopOnHit t)
        {
            try
            {
                Build_Info bi = piece.FatherBI;
                if (bi == null) return "no owning block";
                Traverse tr = Traverse.Create(t);
                if (piece.gameObject.layer == tr.Field("layer_Bullet").GetValue<int>()) return "bullet layer";
                if (piece.Belong_Group == null) return "no shard group";
                if (bi.top_Info != null && bi.top_Info.Is_Detecting_Battles) return "detecting (block being placed)";
                if (bi._IsFurniBI && !bi.IsCoding) return "loading (furniture data)";
                if (bi._IsFurniBI && piece.Belong_Group != null)
                {
                    var groups = new List<Shards_Group> { piece.Belong_Group };
                    var seenG = new HashSet<Shards_Group> { piece.Belong_Group };
                    for (int g = 0; g < groups.Count && g < 3000; g++)
                    {
                        foreach (Shards_Group c in groups[g].Contacts)
                        {
                            Build_Info o = c != null ? c.BI : null;
                            if (o != null && !o.IsCoding && o._Type == Build_Info.Type.SystemHouseBI && o._ItemType != Build_Info.ItemType.SysHouseBigWall)
                            {
                                return o._IsFurniBI ? "loading (connected furniture data)" : "loading (connected world-building piece)";
                            }
                            if (c != null && seenG.Add(c))
                            {
                                groups.Add(c);
                            }
                        }
                    }
                }
                if (bi.top_Info != null && bi.top_Info.IsInLoading) return "loading (this structure)";
                Traverse init = tr.Field("_buildInit");
                if (init.Field("systemHouseManager").Field("InLoadingSystemHouse").GetValue<int>() > 0) return "loading (a world building nearby)";
                if (init.Field("chunkMgr").Field("_inSavingData").GetValue<bool>()) return "saving";
                if (tr.Field("_terraTreeMgr").Field("In_LoadTreeSpawners").GetValue<int>() > 0) return "loading (trees)";
                return "unknown";
            }
            catch (System.Exception ex)
            {
                return "unknown (" + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// Called from Plugin.Update: retries demolition smashes the game refused while something was
        /// loading or saving, four times a second, for up to 15 s each.
        /// </summary>
        internal static void TickDeferred()
        {
            if (Deferred.Count == 0 || Time.time < _nextDeferredTry)
            {
                return;
            }
            _nextDeferredTry = Time.time + 0.25f;
            TopOnHit t = UnityEngine.Object.FindObjectOfType<TopOnHit>();
            if (t == null)
            {
                return;
            }
            for (int i = Deferred.Count - 1; i >= 0; i--)
            {
                Battle_Info piece = Deferred[i].Piece;
                if (piece == null || piece.Smashed || piece.Is_Fallen)
                {
                    Deferred.RemoveAt(i);
                    continue;
                }
                string why = RefusalReason(piece, t);
                if (why.Contains("furniture"))
                {
                    EnsureFurnitureCoded(piece.FatherBI);
                    why = RefusalReason(piece, t);
                }
                if (why == "unknown")
                {
                    ExplosionDrops.Scope++;
                    try
                    {
                        t.Process_Smashed_Shard(piece, fromPlayer: false, entityBulletHit: true);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning("[Demolition] retry threw: " + ex.Message);
                    }
                    finally
                    {
                        ExplosionDrops.Scope--;
                    }
                    if (piece == null || piece.Smashed || piece.Is_Fallen)
                    {
                        _deferredDone++;
                        Deferred.RemoveAt(i);
                        continue;
                    }
                }
                if (Time.time > Deferred[i].GiveUpAt || !IsTransient(why) && why != "unknown")
                {
                    Plugin.Log.LogInfo($"[Demolition] gave up on a shard of '{piece.FatherBI?.name}': {why}.");
                    Deferred.RemoveAt(i);
                }
            }
            if (Deferred.Count == 0 && _deferredDone > 0)
            {
                Plugin.Log.LogInfo($"[Demolition] {_deferredDone} queued shard(s) broken on retry.");
                _deferredDone = 0;
            }
        }

        private static readonly Collider[] FurniBuffer = new Collider[100];
        private static System.Reflection.MethodInfo _furniHit;
        private static SystemHouseManager _houseMgr;

        /// <summary>
        /// Mirrors SystemHouseManager's furniture contact pass (Check_Dis_For_Furnitures, Build_System:13704-13730):
        /// overlap the piece's box on Mask_8_10, register each overlap through the game's own
        /// Furni_Hit_BaI (which links the shard groups both ways and marks ground contact), then set
        /// IsCoding. Done for the piece and for every uncoded furniture piece connected to it, because
        /// Process_Smashed_Shard refuses a furniture smash while ANY connected world-building piece is
        /// uncoded (:22202-22244).
        /// </summary>
        private static void EnsureFurnitureCoded(Build_Info start)
        {
            if (start == null || !start._IsFurniBI)
            {
                return;
            }
            try
            {
                if (_houseMgr == null)
                {
                    _houseMgr = UnityEngine.Object.FindObjectOfType<SystemHouseManager>();
                }
                if (_furniHit == null)
                {
                    _furniHit = AccessTools.Method(typeof(SystemHouseManager), "Furni_Hit_BaI");
                }
                if (_houseMgr == null || _furniHit == null || Global_Infos.ins == null)
                {
                    return;
                }
                int mask = Global_Infos.ins.Mask_8_10.value;
                var waiting = Traverse.Create(_houseMgr).Field("_furniWaitContact").GetValue<HashSet<Build_Info>>();

                // Walk the WHOLE connected graph, exactly like the guard does (Build_System:22213-22234): it
                // refuses if any world-building piece anywhere in it is uncoded - including ones behind an
                // already-coded neighbour, which the first version never reached.
                var queue = new Queue<Build_Info>();
                var seen = new HashSet<Build_Info> { start };
                queue.Enqueue(start);
                int codedFurni = 0, codedOther = 0, visited = 0;
                while (queue.Count > 0 && visited < 3000)
                {
                    Build_Info bi = queue.Dequeue();
                    visited++;
                    if (bi == null)
                    {
                        continue;
                    }
                    if (!bi.IsCoding)
                    {
                        if (bi._IsFurniBI)
                        {
                            if (CodeFurniture(bi, mask))
                            {
                                waiting?.Remove(bi);
                                codedFurni++;
                            }
                        }
                        else if (bi._Type == Build_Info.Type.SystemHouseBI && bi._ItemType != Build_Info.ItemType.SysHouseBigWall)
                        {
                            // Not furniture, so there is no contact data to build; IsCoding only gates
                            // this smash guard and the furniture pass (the only readers).
                            bi.IsCoding = true;
                            codedOther++;
                        }
                    }
                    if (bi.shards_Groups.Count == 0)
                    {
                        continue;
                    }
                    foreach (Shards_Group contact in bi.shards_Groups[0].Contacts)
                    {
                        Build_Info other = contact != null ? contact.BI : null;
                        if (other != null && seen.Add(other))
                        {
                            queue.Enqueue(other);
                        }
                    }
                }
                if (codedFurni + codedOther > 0)
                {
                    Plugin.Log.LogInfo($"[Demolition] prepared {codedFurni} furniture + {codedOther} other world-building piece(s) " +
                                       $"for breaking ({visited} connected piece(s) checked).");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("[Demolition] furniture contact pass failed: " + ex.Message);
            }
        }

        /// <summary>The game's own contact pass for one furniture piece, then IsCoding.</summary>
        private static bool CodeFurniture(Build_Info bi, int mask)
        {
            if (bi.BaIs_All.Count == 0 || bi.shards_Groups.Count == 0)
            {
                return false;
            }
            Battle_Info first = bi.BaIs_All[0];
            if (first == null || first.selfMeshRender == null)
            {
                return false;
            }
            Vector3 center = first.selfMeshRender.bounds.center;
            Vector3 sc = first.transform.lossyScale;
            Vector3 half = new Vector3(bi.Size.x * sc.x, bi.Size.y * sc.y, bi.Size.z * sc.z) * 0.5f + Vector3.one * 0.02f;
            int n = Physics.OverlapBoxNonAlloc(center, half, FurniBuffer, first.transform.rotation, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                _furniHit.Invoke(_houseMgr, new object[] { FurniBuffer[i], bi });
            }
            bi.IsCoding = true;
            return true;
        }

        /// <summary>Breaks every shard of one block (demolition) - see ApplyToBuildables' wholeBlocks.</summary>
        private static int SmashWholeBlock(Build_Info buildInfo, TopOnHit topOnHit, HashSet<Battle_Info> alreadyHit)
        {
            if (topOnHit == null || Smash_Fallen_Manager.ins == null)
            {
                return 0;
            }
            try
            {
                Smash_Fallen_Manager.ins.Spawn_BaIs_Under_BI(buildInfo);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Explosion] Spawn_BaIs_Under_BI threw for '{buildInfo.name}': {ex.Message}");
                return 0;
            }
            // World-building furniture pieces can't be broken until the game has worked out what they touch
            // ("coded"), which it only does for pieces in its spawn queue near the player - support pieces
            // left out of that queue refuse every hit ("Loading data... Please wait") and hold a building
            // up forever. Do the game's own contact pass for this piece and its connected cluster first.
            EnsureFurnitureCoded(buildInfo);

            int n = 0;
            // Copy first: smashing a shard can change the block's piece list.
            var pieces = new List<Battle_Info>(buildInfo.Spawned_BaIs);
            foreach (Battle_Info piece in pieces)
            {
                if (piece == null || piece.Is_Fallen || piece.Smashed || !alreadyHit.Add(piece))
                {
                    continue;
                }
                try
                {
                    topOnHit.Process_Smashed_Shard(piece, fromPlayer: true, entityBulletHit: true);
                    // The game can turn a smash away silently (loading, saving, furniture data not ready).
                    if (piece == null || piece.Smashed || piece.Is_Fallen)
                    {
                        n++;
                        _demoShardsBroken++;
                    }
                    else
                    {
                        _demoShardsRefused++;
                        string why = RefusalReason(piece, topOnHit);
                        _demoReasons[why] = (_demoReasons.TryGetValue(why, out int c) ? c : 0) + 1;
                        // Loading/saving refusals are temporary: queue it and try again shortly
                        // (TickDeferred), rather than forcing past a guard that protects world loading.
                        if (IsTransient(why))
                        {
                            Deferred.Add((piece, Time.time + 15f));
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[Explosion] demolishing '{piece.name}' threw: {ex.Message}");
                }
            }
            _demoBlocks++;
            if (buildInfo._Type == Build_Info.Type.SystemHouseBI)
            {
                _demoHouseBlocks++;
            }
            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo($"[Explosion] demolished '{buildInfo.name}' ({buildInfo._Type}/{buildInfo._ItemType}): {n} of {pieces.Count} shard(s) broken.");
            }
            return n > 0 ? 1 : 0;
        }

        /// <summary>
        /// Mirrors the decision TopOnHit.One_Shot_Through makes before touching a piece: a lethal
        /// hit (damage >= remaining HP on that piece) goes through Process_Smashed_Shard (the
        /// method that actually breaks/pools/removes it), everything else is a plain MinusHP
        /// partial-damage tick. Returns true if a hit was actually applied (for the caller's hit
        /// counter).
        /// </summary>
        private static bool DamagePiece(Battle_Info piece, float damage, TopOnHit topOnHit, string context)
        {
            try
            {
                bool lethal = piece.FatherBI != null
                    && piece.nameIndex >= 0
                    && piece.nameIndex < piece.FatherBI.BaIs_HP_Lefts.Count
                    && piece.FatherBI.BaIs_HP_Lefts[piece.nameIndex] <= damage;

                if (lethal && topOnHit != null)
                {
                    topOnHit.Process_Smashed_Shard(piece, fromPlayer: true, entityBulletHit: true);
                    if (Plugin.EnableDiagnostics.Value)
                    {
                        Plugin.Log.LogInfo($"[Explosion] piece '{piece.name}' {context}: LETHAL -{damage:F0} HP, Process_Smashed_Shard called.");
                    }
                }
                else
                {
                    piece.MinusHP(damage);
                    if (Plugin.EnableDiagnostics.Value)
                    {
                        string note = topOnHit == null ? " (no TopOnHit instance - can never be lethal via this path)" : "";
                        Plugin.Log.LogInfo($"[Explosion] piece '{piece.name}' {context}: -{damage:F0} HP{note}.");
                    }
                }
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Explosion] damaging piece '{piece.name}' threw: {ex.Message}");
                return false;
            }
        }
    }
}
