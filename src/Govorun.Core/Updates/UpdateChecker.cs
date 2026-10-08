using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Govorun.Core.Support;
using Serilog;

namespace Govorun.Core.Updates;

/// <summary>A release newer than what is running.</summary>
public sealed record UpdateInfo(Version Version, string Url);

/// <summary>
/// Asks the GitHub releases API whether a newer version exists. This is the only network
/// request Govorun ever makes, it is behind <see cref="Settings.AppSettings.CheckForUpdates"/>,
/// and it fails silently: no network, a firewall rule the user asked the installer to add,
/// or GitHub's unauthenticated rate limit must never surface as an error to a person who
/// just wanted to dictate.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Govorun", CurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public static Version CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);

    /// <summary>
    /// Returns the newer release, or null if up to date or the check failed.
    /// <paramref name="current"/> defaults to the running assembly's version; pass it
    /// explicitly to test the live request (govorun-cli update-check 0.0.1), since the
    /// CLI's own assembly reports 1.0.0.0 and would hide a working response.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(Version? current = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await Http.GetStringAsync(Links.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            return ParseIfNewer(json, current ?? CurrentVersion());
        }
        catch (Exception ex)
        {
            // Debug, not Warning: being offline is normal for this app, not a problem.
            Log.Debug(ex, "Update check failed");
            return null;
        }
    }

    /// <summary>
    /// Separated from the request so the parsing rules are testable without network:
    /// release tags are written "v0.1.2", and a tag that is not newer yields null.
    /// </summary>
    public static UpdateInfo? ParseIfNewer(string json, Version current)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            if (!root.TryGetProperty("tag_name", out var tag)) return null;

            var text = tag.GetString()?.TrimStart('v', 'V');
            if (!Version.TryParse(text, out var released)) return null;

            // Normalize: the assembly version is 4-part ("0.1.2.0"), the tag is 3-part.
            if (Normalize(released) <= Normalize(current)) return null;

            var url = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            return new UpdateInfo(released, string.IsNullOrEmpty(url) ? Links.Repository : url);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not parse the releases response");
            return null;
        }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
