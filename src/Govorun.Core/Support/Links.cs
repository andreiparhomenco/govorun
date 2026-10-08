namespace Govorun.Core.Support;

/// <summary>Every outbound URL the app knows about, in one place.</summary>
public static class Links
{
    /// <summary>
    /// CloudTips donation page. <see cref="DonationConfigured"/> guards every entry point,
    /// so a placeholder here hides them all rather than showing a dead link.
    /// </summary>
    public const string Donation = "https://pay.cloudtips.ru/p/8417cafd";

    public static bool DonationConfigured => !Donation.EndsWith("PLACEHOLDER", StringComparison.Ordinal);

    public const string Repository = "https://github.com/andreiparhomenco/govorun";

    public const string LatestReleaseApi = "https://api.github.com/repos/andreiparhomenco/govorun/releases/latest";

    public const string Landing = "https://biblio-tech.ru/govorun/";
}
