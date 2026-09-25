using System.Collections.Generic;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// A mine sitting on the ground. Arming -> Armed -> Triggered -> boom (or, improvised only,
    /// a dud). All timing and polling is driven by MineManager from Plugin.Update, so a mine costs
    /// nothing per frame of its own.
    ///
    /// Arming only completes once the player who placed it has stepped away (1.5x the trigger
    /// radius), so a mine can never go off under the feet of whoever just put it down.
    ///
    /// Not saved: mines are lost on save/quit or scene change (no cheap safe persistence exists).
    /// </summary>
    internal class PlacedMine : MonoBehaviour
    {
        internal enum State { Arming, Armed, Triggered, Done }

        internal ExplosiveDef Def;
        internal C_Controller_Base Owner;
        internal State Phase;
        internal float ArmAt;
        internal float NextPoll;
        internal float DetonateAt;
        internal bool Dud;

        private ParticleSystem _led;
        private float _nextBlink;

        internal bool Improvised => Def.Kind == ExplosiveKind.ImprovisedMine;
        internal float TriggerRadius => Improvised ? Plugin.ImprovisedMineTriggerRadius.Value : Plugin.MineTriggerRadius.Value;

        internal static PlacedMine Create(ExplosiveDef def, Vector3 point, Quaternion rot, C_Controller_Base owner)
        {
            var go = new GameObject("HHE_PlacedMine_" + def.Tag);
            go.transform.SetPositionAndRotation(point, rot);
            go.AddComponent<MeshFilter>().sharedMesh = def.RuntimeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = def.RuntimeMaterial;

            PlacedMine mine = go.AddComponent<PlacedMine>();
            mine.Def = def;
            mine.Owner = owner;
            mine.Phase = State.Arming;
            float arm = mine.Improvised ? Plugin.ImprovisedMineArmSeconds.Value : Plugin.MineArmSeconds.Value;
            if (Plugin.EnableDiagnostics.Value && Plugin.InstantArmMines.Value)
            {
                arm = 0.5f;
            }
            mine.ArmAt = Time.time + arm;
            if (!mine.Improvised)
            {
                mine._led = BuildLed(go.transform);
            }
            MineManager.Add(mine);
            SmallSounds.PlayClick(point, 0.35f);
            Plugin.Log.LogInfo($"[Mine] '{def.Tag}' placed at {point}, arms in {arm:F1}s.");
            return mine;
        }

        private void Update()
        {
            // The LED is the only per-frame work, and only on an armed manufactured mine.
            if (_led != null && Phase == State.Armed && Time.time >= _nextBlink)
            {
                _nextBlink = Time.time + 1f;
                _led.Emit(1);
            }
        }

        /// <summary>A red blip every second once armed - tells the player (and only the player) it's live.</summary>
        private static ParticleSystem BuildLed(Transform parent)
        {
            var go = new GameObject("HHE_MineLed");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0.065f, 0f);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = 0.18f;
            main.startSpeed = 0f;
            main.startSize = 0.035f;
            main.startColor = new Color(1f, 0.08f, 0.05f, 1f);
            main.maxParticles = 4;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = MineManager.LedMaterial();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            return ps;
        }

        internal void Explode()
        {
            Phase = State.Done;
            Vector3 pos = transform.position;
            if (Dud)
            {
                SmallSounds.PlayFizz(pos, 0.6f);
                try
                {
                    ExplosionVisual.Spawn(pos, 0.6f, 0f, 0.35f);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("[Mine] dud puff threw: " + ex);
                }
                Plugin.Log.LogInfo("[Mine] improvised mine was a dud.");
                Destroy(gameObject);
                return;
            }

            if (!Improvised)
            {
                Blast.Detonate(Blast.Mine(), pos + Vector3.up * 0.15f, Owner);
                Destroy(gameObject);
                return;
            }

            float range = Plugin.ImprovisedMineRange.Value;
            Vector3 origin = pos + Vector3.up * 0.3f;
            int hits = 0;
            try
            {
                hits = Shrapnel.Fire(origin, range, Plugin.ImprovisedMineDamage.Value, Owner, "ImprovisedMine");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[ImprovisedMine] shrapnel threw: " + ex);
            }
            try
            {
                ExplosionDamage.ApplyToBuildables(pos, range * 0.5f, Plugin.ImprovisedMineBlockDamage.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[ImprovisedMine] ApplyToBuildables threw: " + ex);
            }
            try
            {
                NoiseAttractor.Emit(pos, Plugin.ImprovisedMineNoiseRadius.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[Noise] Emit threw: " + ex);
            }
            try
            {
                ExplosionVisual.Spawn(pos, range * Plugin.NailbombVisualRadiusMultiplier.Value,
                                      Plugin.NailbombFlashScale.Value, Plugin.NailbombParticulateScale.Value);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[ImprovisedMine] visual threw: " + ex);
            }
            try
            {
                ExplosionSound.Play(pos, Plugin.ExplosionVolume.Value * 0.8f, metallic: true);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError("[ImprovisedMine] sound threw: " + ex);
            }
            MineManager.OnBlast(pos, range * 0.3f);
            Plugin.Log.LogInfo($"[ImprovisedMine] Detonated at {pos}, range={range}, fragmentHits={hits}.");
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            MineManager.Remove(this);
        }
    }

    /// <summary>Polls placed mines (staggered, NonAlloc), handles arming, triggering and chain detonation.</summary>
    internal static class MineManager
    {
        private static readonly List<PlacedMine> Mines = new List<PlacedMine>();
        private static readonly List<PlacedMine> Snapshot = new List<PlacedMine>();
        private static readonly Collider[] Buffer = new Collider[32];
        private static Material _ledMat;

        internal static int Count => Mines.Count;

        internal static void Add(PlacedMine m) => Mines.Add(m);
        internal static void Remove(PlacedMine m) => Mines.Remove(m);
        internal static void Prune() => Mines.RemoveAll(m => m == null);

        internal static Material LedMaterial()
        {
            if (_ledMat == null)
            {
                Shader s = Shader.Find("Sprites/Default");
                if (s != null)
                {
                    _ledMat = new Material(s) { name = "HHE_MineLed_Mat" };
                }
            }
            return _ledMat;
        }

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (Mines.Count == 0)
            {
                return;
            }
            Prune();
            Snapshot.Clear();
            Snapshot.AddRange(Mines);
            Player_Input player = Player_Input.ins;
            float now = Time.time;

            foreach (PlacedMine m in Snapshot)
            {
                if (m == null || m.Phase == PlacedMine.State.Done)
                {
                    continue;
                }
                try
                {
                    Step(m, player, now);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError("[Mine] tick threw: " + ex);
                }
            }
        }

        private static void Step(PlacedMine m, Player_Input player, float now)
        {
            if (m.Phase == PlacedMine.State.Triggered)
            {
                if (now >= m.DetonateAt)
                {
                    m.Explode();
                }
                return;
            }
            if (now < m.NextPoll)
            {
                return;
            }

            Vector3 pos = m.transform.position;
            float playerDist = player != null ? Vector3.Distance(player.transform.position, pos) : 999f;
            // Far from the player nothing is watching - poll 5x less often.
            m.NextPoll = now + (playerDist > 80f ? 1f : 0.2f) + Random.Range(0f, 0.05f);

            if (m.Phase == PlacedMine.State.Arming)
            {
                bool ownerClear = m.Owner == null || !m.Owner.gameObject.activeInHierarchy ||
                                  Vector3.Distance(m.Owner.transform.position, pos) > m.TriggerRadius * 1.5f;
                if (now >= m.ArmAt && ownerClear)
                {
                    m.Phase = PlacedMine.State.Armed;
                    SmallSounds.PlayClick(pos, 0.5f);
                    Plugin.Log.LogInfo($"[Mine] '{m.Def.Tag}' armed.");
                }
                return;
            }

            int mask = Global_Infos.ins != null ? Global_Infos.ins.Mask_Creature.value : -1;
            int n = Physics.OverlapSphereNonAlloc(pos + Vector3.up * 0.3f, m.TriggerRadius, Buffer, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                C_Controller_Base c = FireDamage.Resolve(Buffer[i]);
                if (c == null || c.char_Status == null || c.char_Status._CurrHP <= 0f)
                {
                    continue;
                }
                bool friendly = c._isPlayer || c._CharFaction == C_Controller_Base.CharFaction.NpcAlly;
                if (friendly && !Plugin.MinesTriggerOnPlayer.Value)
                {
                    continue;
                }
                Trigger(m, now, sympathetic: false);
                Plugin.Log.LogInfo($"[Mine] '{m.Def.Tag}' triggered by {c.name}.");
                return;
            }
        }

        private static void Trigger(PlacedMine m, float now, bool sympathetic)
        {
            m.Phase = PlacedMine.State.Triggered;
            if (sympathetic)
            {
                m.DetonateAt = now + Random.Range(0.15f, 0.3f);
                return;
            }
            float delay = m.Improvised ? Plugin.ImprovisedMineTriggerDelay.Value : Plugin.MineTriggerDelay.Value;
            m.DetonateAt = now + delay;
            m.Dud = m.Improvised && Random.value < Plugin.ImprovisedMineDudChance.Value;
            SmallSounds.PlayClick(m.transform.position, 0.7f);
        }

        /// <summary>
        /// Any blast sets off mines close to it (within min(radius, 4 m)), a beat later - they
        /// chain rather than all going up on the same frame. Never a dud: it was blown up, not
        /// stepped on.
        /// </summary>
        internal static void OnBlast(Vector3 center, float radius)
        {
            if (Mines.Count == 0)
            {
                return;
            }
            float r = Mathf.Min(radius, 4f);
            float now = Time.time;
            foreach (PlacedMine m in Mines)
            {
                if (m == null || m.Phase == PlacedMine.State.Triggered || m.Phase == PlacedMine.State.Done)
                {
                    continue;
                }
                if ((m.transform.position - center).sqrMagnitude <= r * r)
                {
                    Trigger(m, now, sympathetic: true);
                }
            }
        }

        internal static void Clear()
        {
            foreach (PlacedMine m in Mines)
            {
                if (m != null)
                {
                    Object.Destroy(m.gameObject);
                }
            }
            Mines.Clear();
        }
    }
}
