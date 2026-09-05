using System.Collections.Generic;
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
        /// </summary>
        internal static int Apply(
            Vector3 center,
            float radius,
            float maxDamage,
            C_Controller_Base attacker,
            float hitFlyForce = 1f,
            float hitReact = 0.8f)
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

                Vector3 hitDirect = targetPos - center;
                hitDirect = hitDirect.sqrMagnitude > 0.0001f ? hitDirect.normalized : Vector3.up;

                // Grenades are risky to use at close range in real life too - self-damage uses
                // the same falloff as everyone else, just scaled down separately so it can be
                // tuned (or turned off) without affecting damage to others.
                float damage = maxDamage * falloff * (isSelf ? Plugin.SelfDamageMultiplier.Value : 1f);
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
                Plugin.Log.LogInfo($"[Explosion] Hit {ctrl.name} at {dist:F1}m (falloff={falloff:F2}) for {damage:F0} dmg.");
            }

            if (hits == 0 && hitColliders.Length > 0)
            {
                Plugin.Log.LogInfo($"[Explosion] {hitColliders.Length} collider(s) in range, {unmappedColliders} didn't map to a creature controller, 0 hits.");
            }

            return hits;
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
        internal static int ApplyToBuildables(Vector3 center, float radius, float damage)
        {
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
                    bool isAlreadySlicedShard = col.CompareTag("ZoneSlice");
                    try
                    {
                        if (isAlreadySlicedShard)
                        {
                            smashMgr.ZoneSmash_Shard_MinusHP(buildInfo, center, col, damage, isFromPlayer: true);
                        }
                        else
                        {
                            smashMgr.ZoneSmash_BI_MinusHP(buildInfo, center, col, damage, isFromPlayer: true);
                        }
                        hits++;
                        if (Plugin.EnableDiagnostics.Value)
                        {
                            string method = isAlreadySlicedShard ? "ZoneSmash_Shard_MinusHP" : "ZoneSmash_BI_MinusHP";
                            Plugin.Log.LogInfo($"[Explosion] ZoneSmashBI '{buildInfo.name}' via collider '{col.name}' ({method}): -{damage:F0} HP.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Explosion] ZoneSmash MinusHP threw for '{buildInfo.name}' via '{col.name}': {ex.Message}");
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
                        smashMgr.SysHouse_BigWall_MinusHP(buildInfo, damage, center);
                        hits++;
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
