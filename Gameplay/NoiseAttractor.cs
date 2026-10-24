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
    /// Where zombies go: AI_Agent.Begin_Move_To_Target ignores soundSourcePos and locks onto the
    /// soundSource COLLIDER (its transform becomes the zombie's target). Vanilla passes the player's
    /// chest collider, so zombies walked to the thrower, not the boom (KangarooCaptain, 2026-10-23).
    /// So the sound comes from a small trigger collider left at the blast (BlastDecoy): zombies walk
    /// there, and when it goes away they lose the target and wander from that spot. charController
    /// stays the player: On_Sound_Played reads its height and stealth skills without a null check.
    /// </summary>
    internal static class NoiseAttractor
    {
        internal static void Emit(Vector3 position, float hearDistance)
        {
            if (hearDistance <= 0f || !Plugin.ExplosionsAttractZombies.Value)
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
                param.soundSource = BlastDecoy.Make(position);
                param.sourceRadius = BlastDecoy.Radius;
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

    /// <summary>
    /// The spot zombies walk to after an explosion: an invisible trigger (Ignore Raycast layer, so nothing
    /// hits or stands on it) that follows the game's world shift and removes itself after a while.
    /// </summary>
    internal sealed class BlastDecoy : MonoBehaviour
    {
        internal const float Radius = 0.3f;
        private const float Seconds = 15f;   // long enough to walk over; then they wander from there

        private Vector3 _real;
        private float _until;

        private static Vector3 Shift =>
            Terrain_Loader_Manager.ins != null ? Terrain_Loader_Manager.ins.neutralizedPlayerMove : Vector3.zero;

        internal static Collider Make(Vector3 world)
        {
            var go = new GameObject("HHE_BlastDecoy") { layer = 2 };
            go.transform.position = world;
            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = Radius;
            var d = go.AddComponent<BlastDecoy>();
            d._real = world - Shift;
            d._until = Time.time + Seconds;
            return col;
        }

        private void Update()
        {
            if (Time.time > _until)
            {
                Destroy(gameObject);
                return;
            }
            transform.position = _real + Shift;
        }
    }
}
