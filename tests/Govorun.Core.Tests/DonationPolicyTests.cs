using Govorun.Core.Settings;
using Govorun.Core.Support;

namespace Govorun.Core.Tests;

public class DonationPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static AppSettings Eligible() => new()
    {
        FirstRunUtc = Now - TimeSpan.FromDays(30),
        DictationCount = 150,
        WordsDictated = 20_000,
    };

    [Fact]
    public void DonationLinkIsConfigured() =>
        Assert.True(Links.DonationConfigured, "A placeholder link hides every donation entry point.");

    [Fact]
    public void PromptsOnceThresholdsAreMet() =>
        Assert.True(DonationPolicy.ShouldPrompt(Eligible(), Now));

    [Fact]
    public void DoesNotPromptBeforeEnoughDictations()
    {
        var settings = Eligible();
        settings.DictationCount = 149;
        Assert.False(DonationPolicy.ShouldPrompt(settings, Now));
    }

    [Fact]
    public void DoesNotPromptInTheFirstWeek()
    {
        var settings = Eligible();
        settings.FirstRunUtc = Now - TimeSpan.FromDays(3);
        Assert.False(DonationPolicy.ShouldPrompt(settings, Now));
    }

    [Fact]
    public void DoesNotPromptTwiceInSixMonths()
    {
        var settings = Eligible();
        settings.DictationCount = 600;
        DonationPolicy.MarkPrompted(settings, Now);

        Assert.False(DonationPolicy.ShouldPrompt(settings, Now + TimeSpan.FromDays(100)));
        Assert.True(DonationPolicy.ShouldPrompt(settings, Now + TimeSpan.FromDays(200)));
    }

    [Fact]
    public void SecondPromptNeedsAHigherThreshold()
    {
        var settings = Eligible();
        DonationPolicy.MarkPrompted(settings, Now);
        var later = Now + TimeSpan.FromDays(200);

        // Still at 150 dictations: the next ask waits for 600.
        Assert.False(DonationPolicy.ShouldPrompt(settings, later));
        settings.DictationCount = 600;
        Assert.True(DonationPolicy.ShouldPrompt(settings, later));
    }

    [Fact]
    public void StopsAfterMaxPrompts()
    {
        var settings = Eligible();
        settings.DictationCount = 100_000;
        var when = Now;
        for (int i = 0; i < DonationPolicy.MaxPrompts; i++)
        {
            DonationPolicy.MarkPrompted(settings, when);
            when += TimeSpan.FromDays(200);
        }
        Assert.False(DonationPolicy.ShouldPrompt(settings, when));
    }

    [Fact]
    public void NeverPromptsOnceSuppressed()
    {
        var settings = Eligible();
        settings.DonationPromptSuppressed = true;
        Assert.False(DonationPolicy.ShouldPrompt(settings, Now));
    }

    [Fact]
    public void HoursSavedIsConservative()
    {
        // 10 000 words at 1.8 s saved each = 5 hours.
        Assert.Equal(5.0, DonationPolicy.HoursSaved(10_000), precision: 1);
        Assert.Equal(0, DonationPolicy.HoursSaved(0));
    }
}
