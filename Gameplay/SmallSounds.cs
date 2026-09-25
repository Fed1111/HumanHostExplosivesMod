using System;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// The quieter sounds the 0.3.0 items need: Molotov shatter, mine arm/trigger click, improvised
    /// mine dud fizz, and the looping fire crackle.
    ///
    /// No vanilla glass-break or click clip exists in the shipped assets (only UI button clicks).
    /// The fire loop prefers the vanilla "Campfire" clip - the only fire sound the game has, loaded
    /// with level0 - found by exact name among loaded AudioClips, and falls back to a synthesized
    /// crackle. Everything plays 2D with a manual distance falloff, like ExplosionSound: 3D rolloff
    /// under this game's mixer has failed silently before.
    /// </summary>
    internal static class SmallSounds
    {
        private const int Rate = 44100;

        private static AudioClip _shatter;
        private static AudioClip _click;
        private static AudioClip _fizz;
        private static AudioClip _beep;
        private static AudioClip _rattle;
        private static AudioClip _crackle;
        private static AudioClip _campfire;
        private static bool _campfireSearched;

        internal static void PlayShatter(Vector3 pos, float volume) => PlayAt(_shatter ?? (_shatter = BuildShatter()), pos, volume, 40f);
        internal static void PlayClick(Vector3 pos, float volume) => PlayAt(_click ?? (_click = BuildClick()), pos, volume, 18f);
        internal static void PlayFizz(Vector3 pos, float volume) => PlayAt(_fizz ?? (_fizz = BuildFizz()), pos, volume, 25f);
        internal static void PlayBeep(Vector3 pos, float volume) => PlayAt(_beep ?? (_beep = BuildBeep()), pos, volume, 20f);
        internal static void PlayRattle(Vector3 pos, float volume) => PlayAt(_rattle ?? (_rattle = BuildRattle()), pos, volume, 20f);

        /// <summary>Volume multiplier for a sound at pos heard from the camera, 0 beyond maxDistance.</summary>
        internal static float Falloff(Vector3 pos, float maxDistance)
        {
            Transform cam = CamController.ins != null ? CamController.ins._mainCamTrans : null;
            if (cam == null)
            {
                return 1f;
            }
            float t = Mathf.Clamp01(1f - Vector3.Distance(cam.position, pos) / maxDistance);
            return t * t;
        }

        private static void PlayAt(AudioClip clip, Vector3 pos, float volume, float maxDistance)
        {
            if (clip == null)
            {
                return;
            }
            float v = volume * Falloff(pos, maxDistance);
            if (v <= 0.001f)
            {
                return;
            }
            var go = new GameObject("HHE_Sound");
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 0f;
            src.volume = v;
            src.playOnAwake = false;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.3f);
        }

        /// <summary>A looping fire source on parent. The caller drives its volume with Falloff each frame.</summary>
        internal static AudioSource StartFireLoop(Transform parent)
        {
            AudioClip clip = FireLoopClip();
            if (clip == null)
            {
                return null;
            }
            var go = new GameObject("HHE_FireLoop");
            go.transform.SetParent(parent, worldPositionStays: false);
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.spatialBlend = 0f;
            src.volume = 0f;
            src.playOnAwake = false;
            // Start at a random point so two pools lit together don't phase.
            src.time = UnityEngine.Random.Range(0f, clip.length * 0.9f);
            src.Play();
            return src;
        }

        private static AudioClip FireLoopClip()
        {
            string wanted = Plugin.FireLoopClipName.Value;
            if (!_campfireSearched && !string.IsNullOrEmpty(wanted))
            {
                _campfireSearched = true;
                try
                {
                    foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    {
                        if (c != null && c.name == wanted)
                        {
                            _campfire = c;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[Fire] clip search failed: " + ex.Message);
                }
                Plugin.Log.LogInfo(_campfire != null
                    ? $"[Fire] using vanilla '{wanted}' clip for the fire loop ({_campfire.length:F1}s)."
                    : $"[Fire] vanilla '{wanted}' clip not loaded - using the synthesized crackle.");
            }
            if (_campfire != null)
            {
                return _campfire;
            }
            return _crackle ?? (_crackle = BuildCrackle());
        }

        // ---------------------------------------------------------------- synthesis

        private static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void FadeTail(float[] d, float seconds)
        {
            int n = Mathf.Min(d.Length, Mathf.RoundToInt(Rate * seconds));
            for (int i = 0; i < n; i++)
            {
                d[d.Length - 1 - i] *= i / (float)n;
            }
        }

        /// <summary>Bright filtered noise burst with a few ringing glass partials, over a low "whoomp" of catching fuel.</summary>
        private static AudioClip BuildShatter()
        {
            try
            {
                var rng = new System.Random(4242);
                var d = new float[(int)(Rate * 1.1f)];
                float hp = 0f, prev = 0f, whoomp = 0f;
                double[] partials = { 3150, 4420, 5870, 7310 };
                for (int i = 0; i < d.Length; i++)
                {
                    float t = i / (float)Rate;
                    float w = (float)(rng.NextDouble() * 2 - 1);
                    hp = 0.85f * (hp + w - prev);
                    prev = w;
                    float crash = hp * Mathf.Exp(-t * 14f) * 0.9f;
                    float ring = 0f;
                    for (int k = 0; k < partials.Length; k++)
                    {
                        ring += (float)Math.Sin(2 * Math.PI * partials[k] * t + k) * Mathf.Exp(-t * (18f + 6f * k));
                    }
                    // Fuel catching: a soft low swell ~80 ms in.
                    whoomp = (whoomp + 0.03f * w) / 1.03f;
                    float env = t < 0.08f ? 0f : Mathf.Sin(Mathf.Clamp01((t - 0.08f) / 0.5f) * Mathf.PI) * Mathf.Exp(-(t - 0.08f) * 2.5f);
                    d[i] = Mathf.Clamp(crash + ring * 0.12f + whoomp * 6f * env, -1f, 1f);
                }
                FadeTail(d, 0.03f);
                return Make("HHE_Shatter", d);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Sound] shatter synth failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Two sharp mechanical ticks, 40 ms apart.</summary>
        private static AudioClip BuildClick()
        {
            var d = new float[(int)(Rate * 0.12f)];
            var rng = new System.Random(99);
            foreach (float start in new[] { 0f, 0.04f })
            {
                int s0 = (int)(start * Rate);
                for (int i = 0; i < Rate * 0.012f && s0 + i < d.Length; i++)
                {
                    float t = i / (float)Rate;
                    float tone = (float)Math.Sin(2 * Math.PI * 2600 * t) * 0.6f + (float)(rng.NextDouble() * 2 - 1) * 0.5f;
                    d[s0 + i] += tone * Mathf.Exp(-t * 500f);
                }
            }
            FadeTail(d, 0.01f);
            return Make("HHE_Click", d);
        }

        /// <summary>Mine placed: two short electronic beeps, the second higher.</summary>
        private static AudioClip BuildBeep()
        {
            var d = new float[(int)(Rate * 0.34f)];
            foreach (var (start, freq) in new[] { (0f, 2100.0), (0.16f, 2800.0) })
            {
                int s0 = (int)(start * Rate);
                int len = (int)(Rate * 0.09f);
                for (int i = 0; i < len && s0 + i < d.Length; i++)
                {
                    float t = i / (float)Rate;
                    // Square-ish tone (a piezo buzzer), soft 4 ms edges so it doesn't click.
                    float tone = Mathf.Sign((float)Math.Sin(2 * Math.PI * freq * t)) * 0.35f
                               + (float)Math.Sin(2 * Math.PI * freq * t) * 0.25f;
                    float edge = Mathf.Clamp01(Mathf.Min(i, len - i) / (Rate * 0.004f));
                    d[s0 + i] += tone * edge;
                }
            }
            return Make("HHE_Beep", d);
        }

        /// <summary>
        /// Grenade thrown: the pin's ring tink, the spoon (safety lever) flipping off with a bright
        /// "ping", then its short metallic rattle as it clatters away.
        /// </summary>
        private static AudioClip BuildRattle()
        {
            var rng = new System.Random(515);
            var d = new float[(int)(Rate * 0.75f)];
            void Strike(float at, double[] partials, float amp, float decay)
            {
                int s0 = (int)(at * Rate);
                for (int i = 0; s0 + i < d.Length; i++)
                {
                    float t = i / (float)Rate;
                    float env = Mathf.Exp(-t * decay);
                    if (env < 0.002f)
                    {
                        break;
                    }
                    float v = (float)(rng.NextDouble() * 2 - 1) * 0.25f * Mathf.Exp(-t * 400f);
                    for (int k = 0; k < partials.Length; k++)
                    {
                        v += (float)Math.Sin(2 * Math.PI * partials[k] * t + k) * (1f / (k + 1));
                    }
                    d[s0 + i] += v * amp * env;
                }
            }
            Strike(0f, new[] { 4200.0, 6900.0 }, 0.25f, 60f);           // pin ring
            Strike(0.06f, new[] { 3100.0, 5230.0, 7750.0 }, 0.5f, 28f);  // spoon ping
            float at = 0.2f;
            for (int j = 0; j < 7; j++)                                  // spoon rattling to a stop
            {
                Strike(at, new[] { 2600.0 + rng.Next(0, 900), 5100.0 + rng.Next(0, 1500) }, 0.32f * Mathf.Pow(0.78f, j), 55f);
                at += 0.045f + (float)rng.NextDouble() * 0.05f;
            }
            float peak = 0f;
            foreach (float v in d)
            {
                peak = Mathf.Max(peak, Mathf.Abs(v));
            }
            if (peak > 0.95f)
            {
                for (int i = 0; i < d.Length; i++)
                {
                    d[i] *= 0.95f / peak;
                }
            }
            FadeTail(d, 0.03f);
            return Make("HHE_GrenadeRattle", d);
        }

        /// <summary>A dud: a short hiss that sputters out.</summary>
        private static AudioClip BuildFizz()
        {
            var rng = new System.Random(7);
            var d = new float[(int)(Rate * 1.4f)];
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float w = (float)(rng.NextDouble() * 2 - 1);
                lp += (w - lp) * 0.6f;
                float sputter = 0.6f + 0.4f * Mathf.Sin(t * 37f) * Mathf.Sin(t * 11f);
                d[i] = lp * 0.45f * sputter * Mathf.Exp(-t * 2.2f);
            }
            FadeTail(d, 0.05f);
            return Make("HHE_Fizz", d);
        }

        /// <summary>
        /// A 3 s seamless crackle: brown-noise roar plus random pops. Built one second longer than the
        /// loop and the tail crossfaded into the head, so the loop point has no click.
        /// </summary>
        private static AudioClip BuildCrackle()
        {
            try
            {
                var rng = new System.Random(1337);
                int loop = Rate * 3;
                int extra = Rate / 2;
                var raw = new float[loop + extra];
                float brown = 0f;
                float pop = 0f, popDecay = 0f;
                for (int i = 0; i < raw.Length; i++)
                {
                    float w = (float)(rng.NextDouble() * 2 - 1);
                    brown = (brown + 0.02f * w) / 1.02f;
                    if (rng.NextDouble() < 0.0009)
                    {
                        pop = (float)(0.4 + rng.NextDouble() * 0.6);
                        popDecay = (float)(0.9 + rng.NextDouble() * 0.08);
                    }
                    pop *= popDecay;
                    raw[i] = brown * 2.2f + w * pop * 0.8f;
                }
                var d = new float[loop];
                for (int i = 0; i < loop; i++)
                {
                    d[i] = raw[i];
                    if (i < extra)
                    {
                        float a = i / (float)extra;
                        d[i] = raw[i] * a + raw[loop + i] * (1f - a);
                    }
                    d[i] = Mathf.Clamp(d[i], -1f, 1f);
                }
                return Make("HHE_FireCrackle", d);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Sound] crackle synth failed: " + ex.Message);
                return null;
            }
        }
    }
}
