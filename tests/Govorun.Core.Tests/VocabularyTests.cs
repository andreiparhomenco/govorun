using System.IO;
using Govorun.Core.Asr;

namespace Govorun.Core.Tests;

public class VocabularyTests
{
    private static Vocabulary Create(params string[] lines)
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, lines);
        try
        {
            return new Vocabulary(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParsesTokenIdPairsAndFindsBlank()
    {
        var vocab = Create("▁при 0", "вет 1", "▁мир 2", "<blk> 3");
        Assert.Equal(4, vocab.Count);
        Assert.Equal(3, vocab.BlankIndex);
    }

    [Fact]
    public void DecodesSentencePieceTokensIntoText()
    {
        var vocab = Create("▁при 0", "вет 1", "▁мир 2", "<blk> 3");
        Assert.Equal("привет мир", vocab.Decode(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void DecodeStripsLeadingSpace()
    {
        var vocab = Create("▁а 0", "<blk> 1");
        Assert.Equal("а", vocab.Decode(new[] { 0 }));
    }
}
