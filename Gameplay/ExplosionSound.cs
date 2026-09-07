using System;
using UnityEngine;

namespace HumanHostExplosives
{
    /// <summary>
    /// No existing explosion/blast sound was found anywhere in the game's own asset set
    /// (confirmed by searching every decompiled managed assembly - the only "explosion" hits
    /// were Rigidbody.AddExplosionForce and a ragdoll gib effect, nothing usable as a grenade
    /// boom) - so this synthesizes one at runtime instead of depending on a borrowed asset: a
    /// short sharp crackle transient over a low brown-noise rumble with an exponential decay.
    /// Generated once and cached; playing it just spawns a temporary AudioSource via the
    /// standard Unity helper, safe to call during gameplay (this does no rendering, unlike the
    /// icon-snapshot attempt that hung the game at boot - it's just a PCM buffer).
    /// </summary>
    internal static class ExplosionSound
    {
        private static AudioClip _cachedClip;
        private static AudioClip _cachedMetallicClip;

        // The clip is synthesized once and reused, so the cache has to be invalidated when the
        // echo settings change - otherwise tuning them via the F10 config reload would appear to
        // do nothing until the next game start.
        private static float _cachedEchoDelay = float.NaN;
        private static float _cachedEchoVolume = float.NaN;

        /// <summary>
        /// Plays the blast. <paramref name="metallic"/> selects the nail-bomb variant: a shorter,
        /// drier blast whose tail is a scatter of sharp high-frequency taps - fragments striking
        /// hard surfaces - instead of the grenade's low concussive echo.
        /// </summary>
        internal static void Play(Vector3 position, float volume = 1f, bool metallic = false)
        {
            AudioClip clip = GetOrCreateClip(metallic);
            if (clip == null)
            {
                return;
            }

            // AudioSource.PlayClipAtPoint's auto-created source uses Unity's default 3D rolloff
            // settings (spatialBlend=1, default min/max distance) with no visibility into whether
            // those match this game's own scale/mixer setup - if they don't, it plays with no
            // audible/logged failure at all. Building the AudioSource explicitly and forcing 2D
            // playback (spatialBlend=0) removes that whole category of silent failure - it won't
            // have real positional falloff, but for confirming the sound itself works that's the
            // right tradeoff.
            var go = new GameObject("HumanHostExplosives_ExplosionSound");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.spatialBlend = 0f;
            source.volume = volume;
            source.pitch = 1f;
            source.loop = false;
            source.playOnAwake = false;
            source.Play();

            UnityEngine.Object.Destroy(go, clip.length + 0.5f);
        }

        private static AudioClip GetOrCreateClip(bool metallic = false)
        {
            if (metallic)
            {
                return _cachedMetallicClip ?? (_cachedMetallicClip = BuildMetallic());
            }

            if (_cachedClip != null
                && _cachedEchoDelay == Plugin.EchoDelay.Value
                && _cachedEchoVolume == Plugin.EchoVolume.Value)
            {
                return _cachedClip;
            }

            _cachedEchoDelay = Plugin.EchoDelay.Value;
            _cachedEchoVolume = Plugin.EchoVolume.Value;

            try
            {
                const int sampleRate = 44100;
                float echoDelay = Mathf.Max(0f, Plugin.EchoDelay.Value);
                float echoLevel = Mathf.Max(0f, Plugin.EchoVolume.Value);
                // Long enough to hold the main blast plus the delayed crack and its own tail.
                float duration = 1.0f + echoDelay + 0.6f;
                int sampleCount = Mathf.CeilToInt(sampleRate * duration);
                var samples = new float[sampleCount];

                var rng = new System.Random(12345);
                float brown = 0f;

                // The crack returning off distant geometry. Two closely-spaced taps read as a real
                // slap-back off hard surfaces rather than one flat repeat, and it is low-passed so
                // it sits behind the blast instead of competing with it.
                int echoStart = Mathf.RoundToInt(sampleRate * echoDelay);
                int echoStart2 = echoStart + Mathf.RoundToInt(sampleRate * 0.055f);
                float lowpass = 0f;

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;

                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    brown = (brown + 0.02f * white) / 1.02f;

                    float rumbleEnvelope = Mathf.Exp(-t * 5f);
                    float crackle = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 40f);

                    float sample = brown * 3.5f * rumbleEnvelope + crackle * 0.6f;

                    if (echoLevel > 0f)
                    {
                        // Decays faster than the main blast - a crack, not a second boom.
                        float raw = 0f;
                        if (i >= echoStart)
                        {
                            float te = (i - echoStart) / (float)sampleRate;
                            raw += (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-te * 22f);
                        }
                        if (i >= echoStart2)
                        {
                            float te2 = (i - echoStart2) / (float)sampleRate;
                            raw += (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-te2 * 30f) * 0.55f;
                        }
                        lowpass += (raw - lowpass) * 0.35f;
                        sample += lowpass * echoLevel;
                    }

                    samples[i] = Mathf.Clamp(sample, -1f, 1f);
                }

                var clip = AudioClip.Create("HHX_ExplosionSound", sampleCount, 1, sampleRate, false);
                clip.SetData(samples, 0);
                _cachedClip = clip;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Explosion] failed to synthesize explosion sound: " + ex);
                return null;
            }

            return _cachedClip;
        }

        /// <summary>
        /// Nail-bomb blast: the same waveform as the grenade, but with a louder and slightly later
        /// echo. An earlier attempt used bright metallic pings to suggest nails striking surfaces;
        /// in practice that read as breaking pottery rather than shrapnel, so this reuses the
        /// grenade's blast and just leans on the echo instead.
        /// </summary>
        private static AudioClip BuildMetallic()
        {
            try
            {
                const int sampleRate = 44100;
                float echoDelay = Mathf.Max(0f, Plugin.EchoDelay.Value) + Plugin.NailbombEchoExtraDelay.Value;
                float echoLevel = Mathf.Max(0f, Plugin.EchoVolume.Value) * Plugin.NailbombEchoGain.Value;
                float duration = 1.0f + echoDelay + 0.6f;
                int sampleCount = Mathf.CeilToInt(sampleRate * duration);
                var samples = new float[sampleCount];

                var rng = new System.Random(12345);
                float brown = 0f;
                int echoStart = Mathf.RoundToInt(sampleRate * echoDelay);
                int echoStart2 = echoStart + Mathf.RoundToInt(sampleRate * 0.055f);
                float lowpass = 0f;

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;
                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    brown = (brown + 0.02f * white) / 1.02f;
                    float sample = brown * 3.5f * Mathf.Exp(-t * 5f)
                                 + (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 40f) * 0.6f;

                    if (echoLevel > 0f)
                    {
                        float raw = 0f;
                        if (i >= echoStart)
                        {
                            float te = (i - echoStart) / (float)sampleRate;
                            raw += (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-te * 22f);
                        }
                        if (i >= echoStart2)
                        {
                            float te2 = (i - echoStart2) / (float)sampleRate;
                            raw += (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-te2 * 30f) * 0.55f;
                        }
                        lowpass += (raw - lowpass) * 0.35f;
                        sample += lowpass * echoLevel;
                    }
                    samples[i] = Mathf.Clamp(sample, -1f, 1f);
                }

                var clip = AudioClip.Create("HHX_NailbombSound", sampleCount, 1, sampleRate, false);
                clip.SetData(samples, 0);
                return clip;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Nailbomb] failed to synthesize sound: " + ex);
                return null;
            }
        }
    }
}
