using System;
using LabApi.Features.Audio;

namespace Examples.VoiceAudioSnippets
{
    /// <summary>
    /// PCM helpers for <c>AudioTransmitter</c>. The contract: mono, 48 kHz, float in -1..1, length a whole
    /// number of 480-sample frames. Nothing downstream resamples, downmixes or pads.
    /// </summary>
    internal static class AudioClips
    {
        /// <summary>Samples per 10 ms frame (480) and sample rate (48 000), straight from LabAPI.</summary>
        public const int FrameSize = AudioTransmitter.FrameSize;
        public const int SampleRate = AudioTransmitter.SampleRate;

        /// <summary>
        /// Peak level used by <see cref="Normalize"/>. The transmitter multiplies by sqrt(2) to offset a
        /// client-side loss, so a peak near 0.7 is the conservative choice. Whether louder input clips is
        /// not verified: listen-test before raising it.
        /// </summary>
        public const float SafePeak = 0.7f;

        /// <summary>
        /// Returns <paramref name="samples"/> itself when it is already a whole number of frames,
        /// otherwise a zero-padded copy. A tail that is not a multiple of 480 can leave the transmitter
        /// playing an internal silent buffer that never ends, so every clip goes through this.
        /// </summary>
        public static float[] PadToFrames(float[] samples)
        {
            if (samples == null || samples.Length == 0)
                throw new ArgumentException("clip must not be empty", "samples");

            int padded = (samples.Length + FrameSize - 1) / FrameSize * FrameSize;
            if (padded == samples.Length)
                return samples;

            float[] result = new float[padded];
            Array.Copy(samples, result, samples.Length);
            return result;
        }

        /// <summary>Length of the clip in seconds, for the stop-after guard.</summary>
        public static double Seconds(float[] samples) => samples.Length / (double)SampleRate;

        /// <summary>Scales in place so the loudest sample is <paramref name="peak"/>. Silence stays silence.</summary>
        public static void Normalize(float[] samples, float peak = SafePeak)
        {
            float max = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                float abs = Math.Abs(samples[i]);
                if (abs > max)
                    max = abs;
            }

            if (max < 1e-6f)
                return;

            float gain = peak / max;
            for (int i = 0; i < samples.Length; i++)
                samples[i] *= gain;
        }

        /// <summary>
        /// A sine tone with short linear fades (so it does not click), already padded to whole frames.
        /// Handy as a chime, and as a known signal for listen tests.
        /// </summary>
        public static float[] Tone(float frequencyHz, float seconds, float peak = 0.5f, float fadeSeconds = 0.01f)
        {
            int count = Math.Max(1, (int)(seconds * SampleRate));
            int fade = Math.Max(1, Math.Min(count / 2, (int)(fadeSeconds * SampleRate)));
            float[] samples = new float[count];

            double step = 2.0 * Math.PI * frequencyHz / SampleRate;
            for (int i = 0; i < count; i++)
            {
                float envelope = 1f;
                if (i < fade)
                    envelope = i / (float)fade;
                else if (i >= count - fade)
                    envelope = (count - 1 - i) / (float)fade;

                samples[i] = (float)Math.Sin(step * i) * peak * envelope;
            }

            return PadToFrames(samples);
        }

        /// <summary>
        /// Decodes a 16-bit PCM WAV that is already mono and 48 kHz. Anything else is REJECTED with a
        /// reason, because converting is not done anywhere downstream and a wrong rate or channel count
        /// plays at the wrong speed or garbled. Pure managed work with no game types, so it may run on
        /// the plugin's own <c>SlKit.Background.ForOwner</c> worker; hand the result back with <c>MainThread.Post</c>.
        /// </summary>
        public static bool TryDecodeWav(byte[] wav, out float[] samples, out string error)
        {
            samples = null;
            error = null;

            if (wav == null || wav.Length < 44 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE"))
            {
                error = "not a RIFF/WAVE file";
                return false;
            }

            int channels = 0, rate = 0, bits = 0, format = 0;
            int position = 12;
            while (position + 8 <= wav.Length)
            {
                int size = BitConverter.ToInt32(wav, position + 4);
                int body = position + 8;
                if (size < 0 || body + size > wav.Length)
                    size = wav.Length - body; // tolerate a truncated final chunk

                if (Tag(wav, position, "fmt ") && size >= 16)
                {
                    format = BitConverter.ToInt16(wav, body);
                    channels = BitConverter.ToInt16(wav, body + 2);
                    rate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToInt16(wav, body + 14);
                }
                else if (Tag(wav, position, "data"))
                {
                    if (format != 1 || bits != 16)
                    {
                        error = "need 16-bit integer PCM, got format " + format + " / " + bits + " bit";
                        return false;
                    }

                    if (channels != 1 || rate != SampleRate)
                    {
                        error = "need mono 48000 Hz, got " + channels + " ch / " + rate + " Hz (convert first)";
                        return false;
                    }

                    int count = size / 2;
                    if (count == 0)
                    {
                        error = "empty data chunk";
                        return false;
                    }

                    float[] decoded = new float[count];
                    for (int i = 0; i < count; i++)
                        decoded[i] = BitConverter.ToInt16(wav, body + i * 2) / 32768f;

                    samples = decoded;
                    return true;
                }

                position = body + size + (size & 1); // chunks are word-aligned
            }

            error = "no data chunk";
            return false;
        }

        private static bool Tag(byte[] data, int offset, string tag)
        {
            for (int i = 0; i < 4; i++)
                if (data[offset + i] != tag[i])
                    return false;
            return true;
        }
    }
}
