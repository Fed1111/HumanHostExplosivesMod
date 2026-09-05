using System.Collections;
using System.IO;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Plays a throw animation on the player when an explosive is released.
    ///
    /// The clip ships in an AssetBundle built from Unity 2022.3.62f3 (the game's own version) and
    /// is loaded lazily on first throw. Everything here is best-effort: if the bundle is missing,
    /// the wrong Unity version, or the clip is not humanoid, Play() returns 0 and the caller
    /// throws instantly exactly as it did before.
    ///
    /// The animation goes on the character's UPPER BODY layer (Animancer layer 1) so the player
    /// can keep running while throwing. Three things about that layer are load-bearing and each
    /// one silently eats the animation if you skip it - see GAME_SYSTEMS_REFERENCE.md section 1:
    ///   - Play_Anim_UpLayer early-outs unless the clip CHANGED, so forceUpdateAnim_UpLayer must
    ///     be set before every single call or a second throw plays nothing.
    ///   - The layer's weight is faded to 0 whenever the game leaves aiming mode, so it has to be
    ///     faded back in or the clip plays on a zero-weight layer and is invisible.
    ///   - The right arm is driven by IK on top of the clip, so the hand IK has to be released or
    ///     the arm barely moves.
    /// Note the layer's AvatarMask (UpperBodyMask) enables Head/Arms/Fingers/HandIK but NOT Body
    /// or Root, so the torso windup of a full-body throw clip is discarded. That is expected.
    /// </summary>
    internal static class ThrowAnimation
    {
        private const string BundleRelPath = @"Assets\Anim\throwanim.bundle";

        private static AnimationClip _clip;
        private static bool _loadAttempted;
        private static Coroutine _restoreRoutine;

        /// <summary>
        /// Loads the clip on first use. Returns null (once, loudly) if it is unavailable, and
        /// never retries - a missing bundle should not spam the log on every throw.
        /// </summary>
        private static AnimationClip GetClip()
        {
            if (_loadAttempted)
            {
                return _clip;
            }
            _loadAttempted = true;

            string pluginDir = Path.GetDirectoryName(typeof(ThrowAnimation).Assembly.Location);
            string bundlePath = Path.Combine(pluginDir, BundleRelPath);

            if (!File.Exists(bundlePath))
            {
                Plugin.Log.LogInfo(
                    $"[ThrowAnim] no bundle at '{bundlePath}' - throwing without an animation.");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    // Almost always a Unity version mismatch between the build machine and the game.
                    Plugin.Log.LogWarning(
                        "[ThrowAnim] AssetBundle.LoadFromFile returned null. Rebuild the bundle " +
                        "with Unity 2022.3.62f3 (the game's version).");
                    return null;
                }

                AnimationClip[] clips = bundle.LoadAllAssets<AnimationClip>();
                if (clips == null || clips.Length == 0)
                {
                    Plugin.Log.LogWarning("[ThrowAnim] bundle contains no AnimationClip.");
                    return null;
                }

                // An FBX exports a "__preview__" helper clip alongside the real one; skip it.
                foreach (AnimationClip candidate in clips)
                {
                    if (candidate == null || candidate.name.StartsWith("__preview__"))
                    {
                        continue;
                    }
                    _clip = candidate;
                    break;
                }

                if (_clip == null)
                {
                    Plugin.Log.LogWarning("[ThrowAnim] no usable AnimationClip in bundle.");
                    return null;
                }

                if (!_clip.isHumanMotion)
                {
                    // A generic clip's curves are bound to 'mixamorig:*' paths that do not exist
                    // on the player rig (it uses 'mixamorig_*'), so it would bind to nothing.
                    Plugin.Log.LogWarning(
                        $"[ThrowAnim] clip '{_clip.name}' is not humanoid - it will not retarget " +
                        "onto the player. Re-import the FBX with Rig > Animation Type = Humanoid.");
                    _clip = null;
                    return null;
                }

                Plugin.Log.LogInfo(
                    $"[ThrowAnim] loaded '{_clip.name}' ({_clip.length:F3}s, humanoid).");
                return _clip;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[ThrowAnim] failed to load bundle: {ex.Message}");
                _clip = null;
                return null;
            }
            finally
            {
                // Unload the container but keep the loaded clip alive (false).
                if (bundle != null)
                {
                    bundle.Unload(false);
                }
            }
        }

        /// <summary>
        /// Plays the throw animation on the local player's upper body.
        /// Returns the real duration in seconds (clip length / speed), or 0 if no animation was
        /// played - in which case the caller should carry on and throw immediately.
        /// </summary>
        internal static float Play(float speed)
        {
            if (!Plugin.EnableThrowAnimation.Value)
            {
                return 0f;
            }

            AnimationClip clip = GetClip();
            if (clip == null)
            {
                return 0f;
            }

            Player_Input player = Player_Input.ins;
            if (player == null)
            {
                return 0f;
            }

            if (speed <= 0f)
            {
                speed = 1f;
            }

            // The controller refuses to play anything at all while this is set (no log, no error).
            if (player._NotAllowPlay)
            {
                if (Plugin.EnableDiagnostics.Value)
                {
                    Plugin.Log.LogInfo("[ThrowAnim] skipped: _NotAllowPlay is set.");
                }
                return 0f;
            }

            // Let go of the right-hand IK, or it overrides the clip and the arm barely moves.
            if (player._UseIK && player._IK != null)
            {
                player._IK._allowRightHandIK = false;
            }

            // Fade the upper-body layer in - it sits at weight 0 outside aiming mode.
            if (player._UpperBodyLayer != null)
            {
                player._UpperBodyLayer.StartFade(1f, 0.15f);
            }

            // Mandatory: without this a repeat throw re-plays the same clip and is a silent no-op.
            player.forceUpdateAnim_UpLayer = true;
            player.Play_Anim_UpLayer(clip, null, 0.1f, speed);

            float duration = clip.length / speed;

            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo(
                    $"[ThrowAnim] playing '{clip.name}' speed={speed:F2} duration={duration:F3}s");
            }

            if (Plugin.Instance != null)
            {
                if (_restoreRoutine != null)
                {
                    Plugin.Instance.StopCoroutine(_restoreRoutine);
                }
                _restoreRoutine = Plugin.Instance.StartCoroutine(RestoreAfter(duration));

                // Vanilla fires the swing whoosh from an animation event at the start of the
                // swing (Weapon_Melee.Melee_Start_CallBack). We have no events on this clip, so
                // schedule it at a configured point instead.
                Plugin.Instance.StartCoroutine(PlaySwingSound(
                    player, duration * Mathf.Clamp01(Plugin.SwingSoundNormalized.Value)));

                if (Plugin.TraceHandBone.Value)
                {
                    Plugin.Instance.StartCoroutine(TraceHand(player, duration));
                }
            }

            return duration;
        }

        /// <summary>
        /// Fires the borrowed melee swing whoosh partway through the throw, from the hand so it is
        /// positioned like the vanilla one (which plays at the weapon's transform).
        /// </summary>
        private static IEnumerator PlaySwingSound(Player_Input player, float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            if (player == null)
            {
                yield break;
            }

            Transform hand = player._EquipBones.rightHand;
            SwingSound.Play(hand != null ? hand.position : player.transform.position);
        }

        /// <summary>
        /// Diagnostic: samples the right hand bone through the throw, in the player's own space,
        /// so we can see where the hand actually is at each point of the clip rather than guessing.
        /// right = +X (out from the body), up = +Y, fwd = +Z. Pick the frame where fwd/up peak and
        /// set ReleaseNormalized to that t/duration.
        /// </summary>
        private static IEnumerator TraceHand(Player_Input player, float duration)
        {
            Transform hand = player._EquipBones.rightHand;
            if (hand == null)
            {
                Plugin.Log.LogWarning("[ThrowAnim] _EquipBones.rightHand is null - cannot trace.");
                yield break;
            }

            Plugin.Log.LogInfo($"[HandTrace] bone='{hand.name}' parent='{(hand.parent ? hand.parent.name : "?")}'");

            float t = 0f;
            while (t <= duration + 0.001f)
            {
                Transform root = player.transform;
                Vector3 local = root.InverseTransformPoint(hand.position);
                Plugin.Log.LogInfo(
                    $"[HandTrace] t={t:F2}s n={(duration > 0f ? t / duration : 0f):F2} " +
                    $"right={local.x:F2} up={local.y:F2} fwd={local.z:F2}");
                yield return new WaitForSeconds(0.08f);
                t += 0.08f;
            }
        }

        /// <summary>
        /// Hands the upper body back to the game once the throw has played out. Nothing else does
        /// this for us - the vanilla melee path restores its own state explicitly.
        /// </summary>
        private static IEnumerator RestoreAfter(float duration)
        {
            yield return new WaitForSeconds(duration);
            _restoreRoutine = null;

            Player_Input player = Player_Input.ins;
            if (player == null)
            {
                yield break;
            }

            // Leave the layer up if the player is aiming - the game keeps it at weight 1 there and
            // fading it out would drop them out of the aim pose.
            if (!player._InAimingMode && player._UpperBodyLayer != null)
            {
                player._UpperBodyLayer.StartFade(0f, 0.15f);
            }

            if (player._UseIK && player._IK != null)
            {
                player._IK._allowRightHandIK = true;
            }
        }
    }
}
