using Govorun.Core.Settings;

namespace Govorun.Core.Support;

/// <summary>
/// Decides when it is acceptable to ask for a donation. Kept as pure functions over
/// <see cref="AppSettings"/> so the rules are testable without a UI — the whole point is
/// that the ask is rare and predictable rather than whenever a timer happens to fire.
/// </summary>
public static class DonationPolicy
{
    /// <summary>
    /// Dictations required before each ask. Someone who has dictated 150 times has had
    /// real value out of the app; the later thresholds keep the reminders far apart.
    /// </summary>
    private static readonly int[] DictationThresholds = [150, 600, 2000];

    /// <summary>Never ask a brand-new user, however heavily they started.</summary>
    public static readonly TimeSpan MinAgeBeforeFirstPrompt = TimeSpan.FromDays(7);

    /// <summary>Roughly six months between asks.</summary>
    public static readonly TimeSpan MinIntervalBetweenPrompts = TimeSpan.FromDays(183);

    public static int MaxPrompts => DictationThresholds.Length;

    public static bool ShouldPrompt(AppSettings settings, DateTime utcNow)
    {
        if (!Links.DonationConfigured) return false;
        if (settings.DonationPromptSuppressed) return false;
        if (settings.DonationPromptsShown >= MaxPrompts) return false;
        if (settings.DictationCount < DictationThresholds[settings.DonationPromptsShown]) return false;

        var firstRun = settings.FirstRunUtc ?? utcNow;
        if (utcNow - firstRun < MinAgeBeforeFirstPrompt) return false;

        if (settings.LastDonationPromptUtc is { } last && utcNow - last < MinIntervalBetweenPrompts)
            return false;

        return true;
    }

    /// <summary>Records that the ask was shown. Call it whether or not the user donated.</summary>
    public static void MarkPrompted(AppSettings settings, DateTime utcNow)
    {
        settings.DonationPromptsShown++;
        settings.LastDonationPromptUtc = utcNow;
    }

    /// <summary>
    /// Hours the user plausibly saved, for the ask's text. Dictating runs at roughly
    /// 150 words per minute against 40 typed, so each word saves ~1.8 s; deliberately
    /// conservative, and the UI only shows it once it is worth mentioning.
    /// </summary>
    public static double HoursSaved(long wordsDictated) => wordsDictated * 1.8 / 3600.0;
}
