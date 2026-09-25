using System.Collections.Generic;
using MLSpace;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Characters that caught fire keep burning after they leave the pool.
    ///
    /// A central table, never components parented onto the creature: zombie bodies are pooled and swapped
    /// between a GPUI crowd render and a live Animancer body (MOD_CONVENTIONS §52), so anything parented to
    /// one can end up riding a recycled body. Every burning character gets its own effect root, moved each
    /// LateUpdate:
    ///
    ///  - FLAMES ON EACH BODY PART (hips, chest, head, forearms, shins). On a live body they follow the
    ///    ragdoll bones (RagdollManager.RagdollBones, matched by name, only while that ragdoll still belongs
    ///    to this controller). A GPUI crowd zombie has no bones, so its flames sit at the same heights along
    ///    its capsule instead. The nearest BurnDetailedCount burners are switched to live bodies
    ///    (Char_GPUI_Render.Switch_To_Animancer - vanilla does the same when you shoot one; calling it again
    ///    just keeps it live) so their flames follow the limbs.
    ///  - SMOKE streaming off the body.
    ///  - A LIGHT on the nearest BurnLights burners only (lights are the expensive part in HDRP).
    ///  - SCORCH: a live body darkens toward charred the longer it burns, via a MaterialPropertyBlock on
    ///    its SkinnedMeshRenderers (per renderer, never the shared material, which the whole crowd uses).
    ///    The original property block is saved and put back when the body leaves this zombie (pool reuse),
    ///    or a while after death.
    ///  - PANIC: a burning zombie breaks into a run (NPC_Fast_Run); when the fire ends it is walked again
    ///    only if the fire started the run and the world isn't set to "always run".
    ///
    /// Re-igniting refreshes the timer; it never stacks. Capped at MaxBurning.
    /// </summary>
    internal static class BurnManager
    {
        private struct Part
        {
            internal string Name;
            internal string[] BoneKeys;     // lowercase substrings of the ragdoll bone name
            internal float Height;          // capsule fallback: metres above the feet
            internal float Side;            // capsule fallback: metres to the right
            internal float Radius, Rate, SizeMin, SizeMax;
        }

        private static readonly Part[] Parts =
        {
            new Part { Name = "hips", BoneKeys = new[] { "hips", "pelvis" }, Height = 0.95f, Radius = 0.14f, Rate = 9f, SizeMin = 0.3f, SizeMax = 0.6f },
            new Part { Name = "chest", BoneKeys = new[] { "spine2", "chest", "spine1", "spine" }, Height = 1.3f, Radius = 0.16f, Rate = 11f, SizeMin = 0.35f, SizeMax = 0.7f },
            new Part { Name = "head", BoneKeys = new[] { "head" }, Height = 1.65f, Radius = 0.08f, Rate = 6f, SizeMin = 0.25f, SizeMax = 0.45f },
            new Part { Name = "armL", BoneKeys = new[] { "leftforearm", "l_forearm", "forearm_l", "lowerarm_l", "leftlowerarm" }, Height = 1.15f, Side = -0.3f, Radius = 0.06f, Rate = 5f, SizeMin = 0.18f, SizeMax = 0.38f },
            new Part { Name = "armR", BoneKeys = new[] { "rightforearm", "r_forearm", "forearm_r", "lowerarm_r", "rightlowerarm" }, Height = 1.15f, Side = 0.3f, Radius = 0.06f, Rate = 5f, SizeMin = 0.18f, SizeMax = 0.38f },
            new Part { Name = "legL", BoneKeys = new[] { "leftleg", "l_calf", "calf_l", "lowerleg_l", "leftlowerleg" }, Height = 0.45f, Side = -0.12f, Radius = 0.07f, Rate = 5f, SizeMin = 0.18f, SizeMax = 0.35f },
            new Part { Name = "legR", BoneKeys = new[] { "rightleg", "r_calf", "calf_r", "lowerleg_r", "rightlowerleg" }, Height = 0.45f, Side = 0.12f, Radius = 0.07f, Rate = 5f, SizeMin = 0.18f, SizeMax = 0.35f },
        };

        private sealed class Burn
        {
            internal float Until;
            internal float NextTick;
            internal float BurnedSeconds;
            internal C_Controller_Base Attacker;
            internal GameObject Root;
            internal Transform[] PartFx;
            internal ParticleSystem[] PartPs;
            internal float[] PartRate;
            internal ParticleSystem Smoke;
            internal Light Light;
            internal float LightBase;
            internal bool Detailed;
            internal bool StartedRun;
            internal RagdollManager BonesFor;   // which ragdoll the cached bones belong to
            internal Transform[] Bones;
            internal float Seed;
        }

        /// <summary>A body darkened by fire, and what to put back.</summary>
        private sealed class Scorch
        {
            internal C_Controller_Base Owner;
            internal RagdollManager Rag;
            internal MaterialPropertyBlock Original;
            internal Color Base;
            internal float Amount;
            internal float DeadSince = -1f;
        }

        private static readonly Dictionary<C_Controller_Base, Burn> Burning = new Dictionary<C_Controller_Base, Burn>();
        private static readonly List<C_Controller_Base> Keys = new List<C_Controller_Base>();
        private static readonly Dictionary<SkinnedMeshRenderer, Scorch> Scorched = new Dictionary<SkinnedMeshRenderer, Scorch>();
        private static readonly List<SkinnedMeshRenderer> ScorchKeys = new List<SkinnedMeshRenderer>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Color Charred = new Color(0.2f, 0.17f, 0.15f, 1f);
        private static float _nextRank;

        internal static int Count => Burning.Count;

        internal static bool IsBurning(C_Controller_Base ctrl) => ctrl != null && Burning.ContainsKey(ctrl);

        internal static void Ignite(C_Controller_Base ctrl, C_Controller_Base attacker)
        {
            if (ctrl == null || ctrl.char_Status == null || ctrl.char_Status._CurrHP <= 0f)
            {
                return;
            }

            float duration;
            if (ctrl._isPlayer)
            {
                if (!Plugin.PlayerCanCatchFire.Value || !Plugin.AllowSelfDamage.Value)
                {
                    return;
                }
                duration = Plugin.PlayerBurnSeconds.Value;
            }
            else
            {
                duration = Plugin.BurnSeconds.Value;
            }
            if (duration <= 0f)
            {
                return;
            }

            if (Burning.TryGetValue(ctrl, out Burn existing))
            {
                existing.Until = Mathf.Max(existing.Until, Time.time + duration);
                return;
            }
            if (Burning.Count >= Plugin.MaxBurning.Value)
            {
                return;
            }

            var burn = new Burn
            {
                Until = Time.time + duration,
                // First tick a moment later: the pool that lit them already hit them this tick.
                NextTick = Time.time + Mathf.Max(0.25f, Plugin.FireTickSeconds.Value),
                Attacker = attacker,
                Seed = Random.value * 100f,
            };
            try
            {
                BuildFx(ctrl, burn);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Fire] body flames failed: " + ex);
            }
            Burning.Add(ctrl, burn);

            if (ctrl._isPlayer)
            {
                Plugin.Toast("You're on fire!", 2f);
            }
            else if (Plugin.BurnPanic.Value && ctrl is NPC_Input npc)
            {
                try
                {
                    burn.StartedRun = !npc._inRunning;
                    npc.NPC_Fast_Run();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Fire] panic run failed: " + ex.Message);
                }
            }
            _nextRank = 0f; // re-rank detail/lights now that someone new is burning
        }

        private static void BuildFx(C_Controller_Base ctrl, Burn burn)
        {
            burn.Root = new GameObject("HHE_BurningFx");
            burn.Root.transform.position = ctrl.transform.position;
            burn.PartFx = new Transform[Parts.Length];
            burn.PartPs = new ParticleSystem[Parts.Length];
            burn.PartRate = new float[Parts.Length];
            for (int i = 0; i < Parts.Length; i++)
            {
                Part p = Parts[i];
                ParticleSystem ps = FireFx.SpawnBodyFlame(burn.Root.transform, "HHE_Burn_" + p.Name, p.Radius, p.Rate, p.SizeMin, p.SizeMax);
                burn.PartPs[i] = ps;
                burn.PartFx[i] = ps.transform.parent;
                burn.PartRate[i] = p.Rate;
            }
            burn.Smoke = FireFx.SpawnSmokeTrail(burn.Root.transform);
        }

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (Burning.Count > 0)
            {
                if (Time.unscaledTime >= _nextRank)
                {
                    _nextRank = Time.unscaledTime + 0.5f;
                    Rank();
                }

                Keys.Clear();
                Keys.AddRange(Burning.Keys);
                foreach (C_Controller_Base ctrl in Keys)
                {
                    Burn b = Burning[ctrl];
                    bool alive = ctrl != null && ctrl.gameObject.activeInHierarchy &&
                                 ctrl.char_Status != null && ctrl.char_Status._CurrHP > 0f;
                    if (!alive || Time.time >= b.Until)
                    {
                        End(ctrl, b, alive);
                        continue;
                    }
                    b.BurnedSeconds += Time.deltaTime;
                    if (Time.time >= b.NextTick)
                    {
                        b.NextTick = Time.time + Mathf.Max(0.25f, Plugin.FireTickSeconds.Value);
                        try
                        {
                            FireDamage.Tick(ctrl, Plugin.BurnDamage.Value, b.Attacker);
                        }
                        catch (System.Exception ex)
                        {
                            Plugin.Log.LogError("[Fire] burn tick threw: " + ex);
                        }
                    }
                }
            }
            TickScorch();
        }

        /// <summary>
        /// The nearest BurnDetailedCount burners get live bodies (bone-tracked flames, scorch); the nearest
        /// BurnLights get a light. Re-ranked twice a second.
        /// </summary>
        private static void Rank()
        {
            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            Vector3 eye = cam != null ? cam.position : Vector3.zero;
            var order = new List<KeyValuePair<C_Controller_Base, Burn>>();
            foreach (KeyValuePair<C_Controller_Base, Burn> kv in Burning)
            {
                if (kv.Key != null)
                {
                    order.Add(kv);
                }
            }
            order.Sort((a, c) => (a.Key.transform.position - eye).sqrMagnitude.CompareTo((c.Key.transform.position - eye).sqrMagnitude));
            for (int i = 0; i < order.Count; i++)
            {
                Burn b = order[i].Value;
                C_Controller_Base ctrl = order[i].Key;
                b.Detailed = i < Plugin.BurnDetailedCount.Value;
                if (b.Detailed && !ctrl._isPlayer && ctrl._CharGPUIRender != null)
                {
                    try
                    {
                        // Keep it a live body while it burns, so the flames follow its limbs. Twice a second
                        // is plenty: each call just refreshes the timer that would send it back to the crowd.
                        ctrl._CharGPUIRender.Switch_To_Animancer();
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning("[Fire] switch to live body failed: " + ex.Message);
                        b.Detailed = false;
                    }
                }
                bool wantLight = i < Plugin.BurnLights.Value && b.Root != null;
                if (wantLight && b.Light == null)
                {
                    try
                    {
                        b.Light = FireFx.SpawnLight(b.Root.transform, 1800f, 7f);
                        b.Light.transform.localPosition = new Vector3(0f, 1.3f, 0f);
                        b.LightBase = b.Light.intensity;
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning("[Fire] body light failed: " + ex.Message);
                    }
                }
                else if (!wantLight && b.Light != null)
                {
                    Object.Destroy(b.Light.gameObject);
                    b.Light = null;
                }
            }
        }

        /// <summary>Called from Plugin.LateUpdate, after animation has placed the bones.</summary>
        internal static void Follow()
        {
            foreach (KeyValuePair<C_Controller_Base, Burn> kv in Burning)
            {
                C_Controller_Base ctrl = kv.Key;
                Burn b = kv.Value;
                if (ctrl == null || b.Root == null)
                {
                    continue;
                }
                // In first person the flames would sit inside the camera and fill the screen - the toast
                // and the fire's damage are the cue instead.
                bool hide = ctrl._isPlayer && ctrl._InFirstPerson;
                if (b.Root.activeSelf == hide)
                {
                    b.Root.SetActive(!hide);
                }
                if (hide)
                {
                    continue;
                }

                RagdollManager rag = LiveRagdoll(ctrl);
                if (rag != null && b.BonesFor != rag)
                {
                    b.Bones = MatchBones(rag);
                    b.BonesFor = rag;
                }
                Transform t = ctrl.transform;
                Vector3 feet = t.position;
                float pulse = 0.7f + 0.6f * Mathf.PerlinNoise(b.Seed, Time.time * 1.9f);
                // The last second of a burn gutters out instead of snapping off.
                float fade = Mathf.Clamp01(b.Until - Time.time);
                for (int i = 0; i < Parts.Length; i++)
                {
                    Transform bone = rag != null && b.Bones != null ? b.Bones[i] : null;
                    b.PartFx[i].position = bone != null
                        ? bone.position
                        : feet + Vector3.up * Parts[i].Height + t.right * Parts[i].Side;
                    if (b.PartPs[i] != null)
                    {
                        ParticleSystem.EmissionModule em = b.PartPs[i].emission;
                        em.rateOverTimeMultiplier = b.PartRate[i] * pulse * fade;
                    }
                }
                if (b.Smoke != null)
                {
                    b.Smoke.transform.position = b.PartFx[1].position + Vector3.up * 0.3f;
                }
                if (b.Light != null)
                {
                    b.Light.transform.position = b.PartFx[1].position;
                    b.Light.intensity = b.LightBase * (0.75f + 0.4f * Mathf.PerlinNoise(b.Seed + 7f, Time.time * 8f)) * fade;
                }

                if (rag != null && Plugin.BurnScorch.Value && !ctrl._isPlayer)
                {
                    ApplyScorch(ctrl, rag, Mathf.Clamp01(b.BurnedSeconds / 8f));
                }
            }
        }

        /// <summary>The ragdoll currently rendering this controller, or null (GPUI crowd, or a recycled body).</summary>
        private static RagdollManager LiveRagdoll(C_Controller_Base ctrl)
        {
            RagdollManager rag = ctrl._ragDollMgr;
            if (rag == null || rag._controller != ctrl || !rag.gameObject.activeInHierarchy)
            {
                return null;
            }
            if (ctrl._CharGPUIRender != null && !ctrl._CharGPUIRender._switchToAnimancerAlready)
            {
                return null;
            }
            return rag;
        }

        private static Transform[] MatchBones(RagdollManager rag)
        {
            var found = new Transform[Parts.Length];
            Transform[] bones = rag.RagdollBones;
            if (bones == null)
            {
                return found;
            }
            for (int i = 0; i < Parts.Length; i++)
            {
                foreach (string key in Parts[i].BoneKeys)
                {
                    foreach (Transform bone in bones)
                    {
                        if (bone != null && bone.name.ToLowerInvariant().Replace(":", "").Replace("mixamorig", "").Contains(key))
                        {
                            found[i] = bone;
                            break;
                        }
                    }
                    if (found[i] != null)
                    {
                        break;
                    }
                }
            }
            // The chest emitter must never borrow the hips' bone ("spine" matches both on some rigs).
            if (found[1] != null && found[1] == found[0])
            {
                found[1] = null;
            }
            return found;
        }

        // ---------------------------------------------------------------- scorch

        private static void ApplyScorch(C_Controller_Base ctrl, RagdollManager rag, float amount)
        {
            SkinnedMeshRenderer[] renders = rag.SkinedRenders;
            if (renders == null)
            {
                return;
            }
            foreach (SkinnedMeshRenderer r in renders)
            {
                if (r == null)
                {
                    continue;
                }
                if (!Scorched.TryGetValue(r, out Scorch s) || s.Owner != ctrl)
                {
                    if (s != null)
                    {
                        Restore(r, s);
                    }
                    s = new Scorch { Owner = ctrl, Rag = rag, Original = new MaterialPropertyBlock() };
                    r.GetPropertyBlock(s.Original);
                    Material m = r.sharedMaterial;
                    s.Base = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
                    Scorched[r] = s;
                }
                if (amount <= s.Amount)
                {
                    continue;
                }
                s.Amount = amount;
                r.GetPropertyBlock(Block);
                Block.SetColor(BaseColorId, Color.Lerp(s.Base, s.Base * Charred, amount));
                r.SetPropertyBlock(Block);
            }
        }

        /// <summary>
        /// Puts a body's own look back once it no longer belongs to the zombie that burned (the pool handed
        /// it to another), or 30 s after that zombie died.
        /// </summary>
        private static void TickScorch()
        {
            if (Scorched.Count == 0)
            {
                return;
            }
            ScorchKeys.Clear();
            ScorchKeys.AddRange(Scorched.Keys);
            foreach (SkinnedMeshRenderer r in ScorchKeys)
            {
                Scorch s = Scorched[r];
                bool stillOurs = r != null && s.Rag != null && s.Owner != null && s.Rag._controller == s.Owner &&
                                 s.Rag.gameObject.activeInHierarchy;
                if (stillOurs && (s.Owner.char_Status == null || s.Owner.char_Status._CurrHP <= 0f))
                {
                    if (s.DeadSince < 0f)
                    {
                        s.DeadSince = Time.time;
                    }
                    stillOurs = Time.time - s.DeadSince < 30f;
                }
                if (!stillOurs)
                {
                    Restore(r, s);
                    Scorched.Remove(r);
                }
            }
        }

        private static void Restore(SkinnedMeshRenderer r, Scorch s)
        {
            if (r == null)
            {
                return;
            }
            r.SetPropertyBlock(s.Original != null && !s.Original.isEmpty ? s.Original : null);
        }

        // ---------------------------------------------------------------- end

        private static void End(C_Controller_Base ctrl, Burn b, bool alive)
        {
            Burning.Remove(ctrl);
            if (alive && b.StartedRun && ctrl is NPC_Input npc && G_Save._config._Z_MoveType != 1)
            {
                try
                {
                    npc.NPC_Walk();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("[Fire] ending panic run failed: " + ex.Message);
                }
            }
            if (b.Root == null)
            {
                return;
            }
            if (b.Light != null)
            {
                Object.Destroy(b.Light.gameObject);
            }
            if (b.Root.activeSelf)
            {
                foreach (ParticleSystem ps in b.Root.GetComponentsInChildren<ParticleSystem>())
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                Object.Destroy(b.Root, 3.5f);
            }
            else
            {
                Object.Destroy(b.Root);
            }
        }

        internal static void Clear()
        {
            foreach (Burn b in Burning.Values)
            {
                if (b.Root != null)
                {
                    Object.Destroy(b.Root);
                }
            }
            Burning.Clear();
            foreach (KeyValuePair<SkinnedMeshRenderer, Scorch> kv in Scorched)
            {
                Restore(kv.Key, kv.Value);
            }
            Scorched.Clear();
        }
    }
}
