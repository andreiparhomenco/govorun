using Govorun.Core.Asr;
using Govorun.Core.Audio;

namespace Govorun.Core.Tests;

public class AudioTests
{
    [Fact]
    public void ResampleHalvesSampleCount()
    {
        var input = Enumerable.Range(0, 32000).Select(i => (float)Math.Sin(i * 0.01)).ToList();
        var output = AudioRecorder.Resample(input, 32000, 16000);
        Assert.Equal(16000, output.Length);
    }

    [Fact]
    public void ResamplePassthroughWhenRatesMatch()
    {
        var input = new List<float> { 0.1f, 0.2f, 0.3f };
        var output = AudioRecorder.Resample(input, 16000, 16000);
        Assert.Equal(input, output);
    }

    [Fact]
    public void ChunkerKeepsShortAudioWhole()
    {
        var samples = new float[16000 * 10];
        var chunks = AudioChunker.Split(samples, 16000, 60).ToList();
        Assert.Single(chunks);
        Assert.Equal(samples.Length, chunks[0].Length);
    }

    [Fact]
    public void ChunkerSplitsLongAudioWithoutLosingSamples()
    {
        var rng = new Random(1);
        var samples = new float[16000 * 130]; // 2m10s
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(rng.NextDouble() - 0.5);
        // Quiet gap around 55 s — the chunker should prefer to cut there.
        Array.Clear(samples, 16000 * 55, 16000);

        var chunks = AudioChunker.Split(samples, 16000, 60).ToList();
        Assert.True(chunks.Count >= 2);
        Assert.Equal(samples.Length, chunks.Sum(c => c.Length));
        Assert.All(chunks, c => Assert.True(c.Length <= 16000 * 60));
    }
}
