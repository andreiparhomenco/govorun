using Govorun.Core.Text;

namespace Govorun.Core.Tests;

public class TextCleanerTests
{
    [Fact]
    public void RemovesCjkHallucinations()
    {
        Assert.Equal("Привет мир", TextCleaner.Clean("Привет 你好世界 мир"));
        Assert.Equal("Тест", TextCleaner.Clean("Тест こんにちは"));
        Assert.Equal("Ок", TextCleaner.Clean("Ок 안녕하세요"));
    }

    [Fact]
    public void CollapsesWordRepeats()
    {
        Assert.Equal("да", TextCleaner.Clean("да да да да да да"));
    }

    [Fact]
    public void CollapsesPhraseRepeats()
    {
        Assert.Equal("как дела", TextCleaner.Clean("как дела как дела как дела как дела"));
    }

    [Fact]
    public void KeepsLegitimateRepetition()
    {
        // Up to 3 repeats are legitimate speech ("очень, очень, очень").
        Assert.Equal("очень очень очень хорошо", TextCleaner.Clean("очень очень очень хорошо"));
    }

    [Fact]
    public void NormalizesPunctuationSpacing()
    {
        Assert.Equal("Привет, как дела?", TextCleaner.Clean("Привет ,  как дела ?"));
    }

    [Fact]
    public void HandlesEmptyAndWhitespace()
    {
        Assert.Equal("", TextCleaner.Clean(""));
        Assert.Equal("", TextCleaner.Clean("   "));
        Assert.Equal("", TextCleaner.Clean("你好"));
    }

    [Fact]
    public void PreservesNormalRussianText()
    {
        const string text = "Он сказал: «Сделаем это сегодня — пока есть время». Конечно, не всё так просто!";
        Assert.Equal(text, TextCleaner.Clean(text));
    }
}
