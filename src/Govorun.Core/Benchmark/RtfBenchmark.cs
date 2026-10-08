using Govorun.Core.Asr;

namespace Govorun.Core.Benchmark;

/// <summary>
/// Measures the real-time factor of the ASR engine on synthetic speech-band audio.
/// Encoder cost does not depend on audio content, so a generated signal gives the
/// same timing as real speech without shipping a test wav.
/// </summary>
public static class RtfBenchmark
{
    public static TranscriptionResult Run(AsrEngine engine, int seconds = 10)
    {
        var samples = GenerateTestSignal(seconds);
        engine.Transcribe(samples.AsMemory(0, AsrEngine.SampleRate)); // warm-up, 1 s
        return engine.Transcribe(samples);
    }

    public static float[] GenerateTestSignal(int seconds)
    {
        var rng = new Random(42);
        int n = seconds * AsrEngine.SampleRate;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / AsrEngine.SampleRate;
            // Modulated tones in the speech band plus a little noise.
            double envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 2.5 * t);
            samples[i] = (float)(envelope * (0.2 * Math.Sin(2 * Math.PI * 220 * t)
                                           + 0.1 * Math.Sin(2 * Math.PI * 880 * t))
                                 + 0.02 * (rng.NextDouble() * 2 - 1));
        }
        return samples;
    }
}
