using System.Collections.Generic;
using MLSpace;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Characters that caught fire keep burning after they leave the pool.
    ///
    /// Deliberately a central table, not a component parented onto the creature: zombie bodies are
    /// pooled and swapped between a GPUI crowd render and a live Animancer body (MOD_CONVENTIONS §52),
    /// so anything parented to one can end up riding a recycled body. The flame object is its own
    /// GameObject, re-positioned every LateUpdate onto the character's chest - the ragdoll's chest
    /// collider while that ragdoll still belongs to this controller (a ragdolled zombie's root
    /// stays put, §53), else the capsule.
    ///
    /// Re-igniting refreshes the timer; it never stacks. Capped at MaxBurning.
    /// </summary>
    internal static class BurnManager
    {
        private sealed class Burn
        {
            internal float Until;
            internal float NextTick;
            internal C_Controller_Base Attacker;
            internal GameObject Fx;
            internal ParticleSystem Flame;
        }

        private static readonly Dictionary<C_Controller_Base, Burn> Burning = new Dictionary<C_Controller_Base, Burn>();
        private static readonly List<C_Controller_Base> Keys = new List<C_Controller_Base>();

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
            };
            try
            {
                burn.Fx = new GameObject("HHE_BurningFx");
                burn.Fx.transform.position = ChestPosition(ctrl);
                burn.Flame = FireFx.SpawnBodyFlame(burn.Fx.transform);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Fire] body flame failed: " + ex);
            }
            Burning.Add(ctrl, burn);

            if (ctrl._isPlayer)
            {
                Plugin.Toast("You're on fire!", 2f);
            }
        }

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (Burning.Count == 0)
            {
                return;
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
                    End(ctrl, b);
                    continue;
                }
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

        /// <summary>Called from Plugin.LateUpdate, after animation has placed the bones.</summary>
        internal static void Follow()
        {
            foreach (KeyValuePair<C_Controller_Base, Burn> kv in Burning)
            {
                if (kv.Key == null || kv.Value.Fx == null)
                {
                    continue;
                }
                kv.Value.Fx.transform.position = ChestPosition(kv.Key);
                // In first person the flames would sit inside the camera and fill the screen - the
                // toast and the fire's damage are the cue instead.
                bool hide = kv.Key._isPlayer && kv.Key._InFirstPerson;
                if (kv.Value.Fx.activeSelf == hide)
                {
                    kv.Value.Fx.SetActive(!hide);
                }
            }
        }

        private static void End(C_Controller_Base ctrl, Burn b)
        {
            Burning.Remove(ctrl);
            if (b.Fx == null)
            {
                return;
            }
            if (b.Flame != null && b.Fx.activeSelf)
            {
                b.Flame.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Object.Destroy(b.Fx, 1.2f);
            }
            else
            {
                Object.Destroy(b.Fx);
            }
        }

        internal static void Clear()
        {
            foreach (Burn b in Burning.Values)
            {
                if (b.Fx != null)
                {
                    Object.Destroy(b.Fx);
                }
            }
            Burning.Clear();
        }

        private static Vector3 ChestPosition(C_Controller_Base ctrl)
        {
            RagdollManager rag = ctrl._ragDollMgr;
            if (rag != null && rag._controller == ctrl && rag._ChestBoxCollider != null &&
                rag._ChestBoxCollider.gameObject.activeInHierarchy)
            {
                return rag._ChestBoxCollider.bounds.center;
            }
            if (ctrl.capCol != null)
            {
                return ctrl.capCol.bounds.center;
            }
            return ctrl.transform.position + Vector3.up;
        }
    }
}
