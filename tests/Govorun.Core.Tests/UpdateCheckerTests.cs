using Govorun.Core.Updates;

namespace Govorun.Core.Tests;

/// <summary>
/// Parsing only — no network. The shape of the JSON is what the GitHub releases API
/// returns for /releases/latest.
/// </summary>
public class UpdateCheckerTests
{
    private static string Release(string tag, bool draft = false, bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/andreiparhomenco/govorun/releases/tag/{{tag}}"
        }
        """;

    [Fact]
    public void FindsANewerRelease()
    {
        var update = UpdateChecker.ParseIfNewer(Release("v0.2.0"), new Version(0, 1, 2));
        Assert.NotNull(update);
        Assert.Equal(new Version(0, 2, 0), update!.Version);
        Assert.Contains("releases/tag/v0.2.0", update.Url);
    }

    [Theory]
    [InlineData("v0.1.2")]
    [InlineData("v0.1.1")]
    [InlineData("v0.0.9")]
    public void IgnoresSameOrOlderReleases(string tag) =>
        Assert.Null(UpdateChecker.ParseIfNewer(Release(tag), new Version(0, 1, 2)));

    [Fact]
    public void TreatsFourPartAssemblyVersionAsThreePart()
    {
        // The exe reports 0.1.2.0; the tag says v0.1.2. Same version, no prompt.
        Assert.Null(UpdateChecker.ParseIfNewer(Release("v0.1.2"), new Version(0, 1, 2, 0)));
    }

    [Fact]
    public void AcceptsTagWithoutVPrefix() =>
        Assert.NotNull(UpdateChecker.ParseIfNewer(Release("0.3.0"), new Version(0, 1, 2)));

    [Fact]
    public void SkipsDraftsAndPrereleases()
    {
        Assert.Null(UpdateChecker.ParseIfNewer(Release("v0.9.0", draft: true), new Version(0, 1, 2)));
        Assert.Null(UpdateChecker.ParseIfNewer(Release("v0.9.0", prerelease: true), new Version(0, 1, 2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": \"latest\"}")]
    [InlineData("{\"message\": \"API rate limit exceeded\"}")]
    public void SurvivesJunkResponses(string json) =>
        Assert.Null(UpdateChecker.ParseIfNewer(json, new Version(0, 1, 2)));

    [Fact]
    public void FallsBackToTheRepositoryWhenUrlIsMissing()
    {
        var update = UpdateChecker.ParseIfNewer("{\"tag_name\": \"v1.0.0\"}", new Version(0, 1, 2));
        Assert.NotNull(update);
        Assert.Equal("https://github.com/andreiparhomenco/govorun", update!.Url);
    }
}
