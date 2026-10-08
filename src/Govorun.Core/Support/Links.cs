namespace Govorun.Core.Support;

/// <summary>Every outbound URL the app knows about, in one place.</summary>
public static class Links
{
    /// <summary>
    /// CloudTips donation page. Replace the placeholder with the real page created at
    /// cloudtips.ru — until then <see cref="DonationConfigured"/> is false and every
    /// donation entry point stays hidden, so we never show a dead link.
    /// </summary>
    public const string Donation = "https://pay.cloudtips.ru/p/PLACEHOLDER";

    public static bool DonationConfigured => !Donation.EndsWith("PLACEHOLDER", StringComparison.Ordinal);

    public const string Repository = "https://github.com/andreiparhomenco/govorun";

    public const string LatestReleaseApi = "https://api.github.com/repos/andreiparhomenco/govorun/releases/latest";

    public const string Landing = "https://biblio-tech.ru/govorun/";
}
