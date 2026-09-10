namespace Govorun.Core.Asr;

/// <summary>
/// Splits long recordings into chunks the encoder handles comfortably,
/// cutting at the quietest point near each chunk boundary to avoid
/// slicing through a word.
/// </summary>
public static class AudioChunker
{
    public static IEnumerable<ReadOnlyMemory<float>> Split(ReadOnlyMemory<float> samples, int sampleRate, int maxChunkSeconds)
    {
        int maxChunk = maxChunkSeconds * sampleRate;
        if (samples.Length <= maxChunk)
        {
            yield return samples;
            yield break;
        }

        int searchWindow = 5 * sampleRate;
        int offset = 0;
        while (samples.Length - offset > maxChunk)
        {
            int idealEnd = offset + maxChunk;
            int cut = FindQuietestPoint(samples.Span, Math.Max(offset + sampleRate, idealEnd - searchWindow), idealEnd, sampleRate);
            yield return samples[offset..cut];
            offset = cut;
        }
        if (samples.Length - offset > 0)
            yield return samples[offset..];
    }

    private static int FindQuietestPoint(ReadOnlySpan<float> samples, int from, int to, int sampleRate)
    {
        int window = sampleRate / 50; // 20 ms energy windows
        int bestPos = to;
        double bestEnergy = double.MaxValue;
        for (int pos = from; pos + window <= to; pos += window)
        {
            double energy = 0;
            for (int i = pos; i < pos + window; i++) energy += samples[i] * samples[i];
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestPos = pos + window / 2;
            }
        }
        return bestPos;
    }
}
