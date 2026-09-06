using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Makes explosions draw zombies the way gunfire does.
    ///
    /// The game has no "make noise here" call - attraction is a UnityEvent carrying a sound
    /// descriptor. Smash_Fallen_Manager._attctZombies takes a Creature_Mgr.OnSoundPlayed_Param,
    /// and AI_Agent.Begin_Move_To_Target consumes it. Falling trees use exactly this
    /// (TopOnHit.Attract_Zombies_Around_Hit_Point), so an explosion emitting the same event is
    /// consistent with how the game already handles loud events rather than a bolted-on system.
    ///
    /// Note vanilla's tree version reports the sound at the PLAYER's chest rather than the tree,
    /// so zombies path to the player. For a thrown explosive that would be wrong - the whole point
    /// of a distraction throw is that they go to where it went off - so this reports the blast
    /// position instead.
    /// </summary>
    internal static class NoiseAttractor
    {
        internal static void Emit(Vector3 position, float hearDistance)
        {
            if (!Plugin.ExplosionsAttractZombies.Value)
            {
                return;
            }

            Smash_Fallen_Manager mgr = Smash_Fallen_Manager.ins;
            Player_Input player = Player_Input.ins;
            if (mgr == null || mgr._attctZombies == null || player == null)
            {
                return;
            }

            try
            {
                var param = default(Creature_Mgr.OnSoundPlayed_Param);
                param.soundSourcePos = position;
                param.hearDis = hearDistance;
                // soundSource/charController are the fields the AI uses to resolve who made the
                // noise. A thrown projectile has neither, and the player is the one who caused it,
                // so attribute it to them - but keep soundSourcePos at the blast so they walk there.
                param.soundSource = player._ragDollMgr != null ? player._ragDollMgr._ChestBoxCollider : null;
                param.sourceRadius = player.capCol != null ? player.capCol.radius : 0.3f;
                param.charController = player;

                mgr._attctZombies.Invoke(param);

                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo($"[Noise] explosion at {position} heard to {hearDistance}m.");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Noise] attract failed: {ex.Message}");
            }
        }
    }
}
