using System.Collections.Generic;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// XP for explosive and fire kills, at the same rate as a weapon kill.
    ///
    /// Blasts, shrapnel and fire damage characters through Smash_Fallen_Manager.Minus_Char_HP, whose own XP
    /// (getEXP) is the trap rate: 30% of the victim's max HP (Build_System:18769). A gun or melee kill gives
    /// 100% (Hand_Tools:2156) - so explosive kills felt like they gave nothing. Callers pass getEXP:false and
    /// Watch() the victim; whoever dies within a moment (Minus_Char_HP can finish the kill a frame later, when
    /// it first has to switch the zombie to its ragdoll) gets the full weapon-kill XP, once.
    /// </summary>
    internal static class KillXp
    {
        private static readonly Dictionary<C_Controller_Base, float> Watching = new Dictionary<C_Controller_Base, float>();
        private static readonly List<C_Controller_Base> Done = new List<C_Controller_Base>();

        /// <summary>Call BEFORE damaging a character; it's credited if this damage kills it.</summary>
        internal static void Watch(C_Controller_Base victim)
        {
            if (victim == null || victim._isPlayer || victim.char_Status == null || victim.char_Status._CurrHP <= 0f)
            {
                return;
            }
            Watching[victim] = Time.time + 1f;
        }

        /// <summary>Called from Plugin.Update, and right after a damage call.</summary>
        internal static void Tick()
        {
            if (Watching.Count == 0)
            {
                return;
            }
            Done.Clear();
            foreach (KeyValuePair<C_Controller_Base, float> kv in Watching)
            {
                C_Controller_Base c = kv.Key;
                if (c == null || c.char_Status == null)
                {
                    Done.Add(c);
                    continue;
                }
                if (c.char_Status._CurrHP <= 0f)
                {
                    Player_Input player = Player_Input.ins;
                    if (player != null && player._charSkills != null)
                    {
                        player._charSkills.GainCharacterExp((int)c.char_Status._MaxHP);
                        Plugin.Log.LogInfo($"[XP] +{(int)c.char_Status._MaxHP} (x{G_Save._config._ExpFactor:F2} game XP rate) for an explosive/fire kill of {c.name}.");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[XP] kill of {c.name} not credited - no player skills found.");
                    }
                    Done.Add(c);
                }
                else if (Time.time > kv.Value)
                {
                    Done.Add(c);
                }
            }
            foreach (C_Controller_Base c in Done)
            {
                Watching.Remove(c);   // a destroyed character is still a valid key (reference)
            }
        }
    }
}
