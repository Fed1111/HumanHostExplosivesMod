using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Plays the game's own melee swing whoosh for the throw, borrowed from a vanilla
    /// Melee_Swing_Sound_Set rather than synthesized - so it is bit-identical to the axe rather
    /// than merely similar.
    ///
    /// How vanilla does it (Weapon_Melee.Melee_Start_CallBack, Hand_Tools.dll):
    ///   int n  = _swingSoundSet._MeleeStartAudios.Length;
    ///   int i  = MyStatics.Get_NoneRepeat_Random_Index(n, ref _lastMeleeStartAudioIndex);
    ///   Sound_Mgr.ins.Play_SoundClip_Directly(
    ///       _swingSoundSet._MeleeStartAudios[i], _swingSoundSet._Melee_SA_Volumes[i], pos);
    ///
    /// The sets are registered by ScriptableObject name in
    /// Tool_Interact_Mgr.ins._meleeSwingNameToSets, so we can look one up without owning a weapon.
    /// </summary>
    internal static class SwingSound
    {
        private static Melee_Swing_Sound_Set _set;
        private static bool _resolveAttempted;
        private static int _lastIndex = -1;

        /// <summary>
        /// Finds the configured swing sound set once. Logs every available set name on the first
        /// attempt so the right one can be picked without guessing.
        /// </summary>
        private static Melee_Swing_Sound_Set Resolve()
        {
            if (_resolveAttempted)
            {
                return _set;
            }
            _resolveAttempted = true;

            Tool_Interact_Mgr mgr = Tool_Interact_Mgr.ins;
            if (mgr == null || mgr._meleeSwingNameToSets == null || mgr._meleeSwingNameToSets.Count == 0)
            {
                Plugin.Log.LogWarning("[SwingSound] Tool_Interact_Mgr swing sound sets not available.");
                return null;
            }

            if (Plugin.EnableDiagnostics.Value)
            {
                Plugin.Log.LogInfo(
                    "[SwingSound] available sets: " + string.Join(", ", System.Linq.Enumerable.ToArray(mgr._meleeSwingNameToSets.Keys)));
            }

            string want = Plugin.SwingSoundSetName.Value;

            if (!string.IsNullOrEmpty(want) && mgr._meleeSwingNameToSets.TryGetValue(want, out _set))
            {
                Plugin.Log.LogInfo($"[SwingSound] using set '{want}'.");
                return _set;
            }

            // Fall back to a case-insensitive substring match so a partial name like "axe" works,
            // then to whatever the first registered set is - a swing sound that is close beats none.
            foreach (var kv in mgr._meleeSwingNameToSets)
            {
                if (!string.IsNullOrEmpty(want)
                    && kv.Key.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _set = kv.Value;
                    Plugin.Log.LogInfo($"[SwingSound] '{want}' matched set '{kv.Key}'.");
                    return _set;
                }
            }

            foreach (var kv in mgr._meleeSwingNameToSets)
            {
                _set = kv.Value;
                Plugin.Log.LogWarning(
                    $"[SwingSound] no set matched '{want}'; falling back to '{kv.Key}'. " +
                    "Set SwingSoundSetName to one of the names logged above.");
                return _set;
            }

            return null;
        }

        /// <summary>
        /// Plays one swing whoosh at the given position, picking a clip the same way vanilla does
        /// (non-repeating random, per-clip volume from the set).
        /// </summary>
        internal static void Play(Vector3 position)
        {
            if (!Plugin.EnableSwingSound.Value)
            {
                return;
            }

            Melee_Swing_Sound_Set set = Resolve();
            if (set == null || set._MeleeStartAudios == null || set._MeleeStartAudios.Length == 0)
            {
                return;
            }

            if (Sound_Mgr.ins == null)
            {
                return;
            }

            int count = set._MeleeStartAudios.Length;
            int index = MyStatics.Get_NoneRepeat_Random_Index(count, ref _lastIndex);
            if (index < 0 || index >= count)
            {
                return;
            }

            AudioClip clip = set._MeleeStartAudios[index];
            if (clip == null)
            {
                return;
            }

            // The volumes array is parallel to the clips array, but guard anyway - a mismatched
            // set would otherwise throw inside a coroutine and silently kill the rest of the throw.
            float volume = (set._Melee_SA_Volumes != null && index < set._Melee_SA_Volumes.Length)
                ? set._Melee_SA_Volumes[index]
                : 1f;

            Sound_Mgr.ins.Play_SoundClip_Directly(clip, volume * Plugin.SwingSoundVolume.Value, position);
        }

        /// <summary>Drops the cached set so a config reload can pick a different one.</summary>
        internal static void ResetCache()
        {
            _set = null;
            _resolveAttempted = false;
        }
    }
}
