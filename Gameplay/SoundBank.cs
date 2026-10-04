using System;
using System.Collections.Generic;
using System.IO;
using HumanHostExplosives.Registry;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// Recorded/generated sound effects shipped as .wav files in Sounds/&lt;category&gt;/ next to the DLL
    /// (grenade, heavy, shrapnel, spoon, beep, molotov). Every file in a category is a take; each play
    /// picks one at random (never the same one twice in a row) with a little pitch variation, so a chain
    /// of blasts doesn't sound like one clip looping. An empty or missing category returns false and
    /// the caller falls back to its synthesized sound.
    ///
    /// Explosions are heard with a delay and falloff by distance (sound travels ~343 m/s), which is what
    /// makes several charges going off at different distances read as separate blasts.
    /// </summary>
    internal static class SoundBank
    {
        private static readonly Dictionary<string, List<AudioClip>> Banks = new Dictionary<string, List<AudioClip>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> LastPick = new Dictionary<string, int>();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            int total = 0;
            foreach (string root in AssetPaths.Roots)
            {
                string dir = Path.Combine(root, "Sounds");
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                foreach (string catDir in Directory.GetDirectories(dir))
                {
                    string cat = Path.GetFileName(catDir);
                    if (Banks.ContainsKey(cat))
                    {
                        continue; // first root wins, same as art
                    }
                    var clips = new List<AudioClip>();
                    foreach (string file in Directory.GetFiles(catDir, "*.wav"))
                    {
                        try
                        {
                            AudioClip clip = LoadWav(file);
                            if (clip != null)
                            {
                                clips.Add(clip);
                            }
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[Sound] could not read '{file}': {ex.Message}");
                        }
                    }
                    if (clips.Count > 0)
                    {
                        Banks[cat] = clips;
                        total += clips.Count;
                    }
                }
            }
            var summary = new List<string>();
            foreach (KeyValuePair<string, List<AudioClip>> kv in Banks)
            {
                summary.Add($"{kv.Key}={kv.Value.Count}");
            }
            Plugin.Log.LogInfo(total > 0
                ? $"[Sound] loaded {total} sound take(s): {string.Join(", ", summary.ToArray())}."
                : "[Sound] no Sounds/ folder - using the synthesized sounds.");
        }

        internal static bool Has(string category)
        {
            EnsureLoaded();
            return Banks.TryGetValue(category, out List<AudioClip> l) && l.Count > 0;
        }

        /// <summary>
        /// Plays a random take from the category. distanceDelay: explosions arrive after the flash.
        /// Returns false if the category has no takes.
        /// </summary>
        internal static bool Play(string category, Vector3 pos, float volume, float maxDistance, float pitchJitter = 0.06f, bool distanceDelay = false)
        {
            EnsureLoaded();
            if (!Banks.TryGetValue(category, out List<AudioClip> clips) || clips.Count == 0)
            {
                return false;
            }
            int last = LastPick.TryGetValue(category, out int l) ? l : -1;
            int pick = UnityEngine.Random.Range(0, clips.Count);
            if (clips.Count > 1 && pick == last)
            {
                pick = (pick + 1 + UnityEngine.Random.Range(0, clips.Count - 1)) % clips.Count;
            }
            LastPick[category] = pick;
            AudioClip clip = clips[pick];

            float dist = 0f;
            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (cam != null)
            {
                dist = Vector3.Distance(cam.position, pos);
            }
            float fall = Mathf.Clamp01(1f - dist / maxDistance);
            float v = volume * (distanceDelay ? Mathf.Lerp(0.3f, 1f, fall) : fall * fall);
            if (v <= 0.001f)
            {
                return true;
            }

            var go = new GameObject("HHE_Sound_" + category);
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0f;
            src.volume = v;
            src.pitch = 1f + UnityEngine.Random.Range(-pitchJitter, pitchJitter);
            src.playOnAwake = false;
            float delay = distanceDelay ? Mathf.Min(0.9f, dist / 343f) : 0f;
            if (delay > 0.01f)
            {
                src.PlayDelayed(delay);
            }
            else
            {
                src.Play();
            }
            UnityEngine.Object.Destroy(go, delay + clip.length / Mathf.Max(0.5f, src.pitch) + 0.3f);
            return true;
        }

        /// <summary>Minimal RIFF/WAVE reader: PCM 16/24-bit or 32-bit float, mono or stereo (mixed to mono).</summary>
        private static AudioClip LoadWav(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
            {
                throw new InvalidDataException("not a RIFF file");
            }
            int channels = 0, rate = 0, bits = 0, format = 0;
            int pos = 12;
            while (pos + 8 <= b.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
                int size = BitConverter.ToInt32(b, pos + 4);
                int body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, body);
                    channels = BitConverter.ToInt16(b, body + 2);
                    rate = BitConverter.ToInt32(b, body + 4);
                    bits = BitConverter.ToInt16(b, body + 14);
                }
                else if (id == "data")
                {
                    if (channels <= 0 || rate <= 0)
                    {
                        throw new InvalidDataException("data before fmt");
                    }
                    int bytesPer = bits / 8;
                    int frames = Math.Min(size, b.Length - body) / (bytesPer * channels);
                    var data = new float[frames];
                    for (int f = 0; f < frames; f++)
                    {
                        float sum = 0f;
                        for (int c = 0; c < channels; c++)
                        {
                            int o = body + (f * channels + c) * bytesPer;
                            if (format == 3 && bits == 32)
                            {
                                sum += BitConverter.ToSingle(b, o);
                            }
                            else if (bits == 16)
                            {
                                sum += BitConverter.ToInt16(b, o) / 32768f;
                            }
                            else if (bits == 24)
                            {
                                int v = (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16));
                                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                                sum += v / 8388608f;
                            }
                            else
                            {
                                throw new InvalidDataException($"unsupported {bits}-bit format {format}");
                            }
                        }
                        data[f] = sum / channels;
                    }
                    var clip = AudioClip.Create("HHE_" + Path.GetFileNameWithoutExtension(path), frames, 1, rate, false);
                    clip.SetData(data, 0);
                    return clip;
                }
                pos = body + size + (size & 1);
            }
            throw new InvalidDataException("no data chunk");
        }
    }
}
