using System.Globalization;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class GitHubReleaseParsersTests
{
    /// <summary>A machine whose current culture defaults to a non-Gregorian calendar (Thai here, Buddhist
    /// era) must still read GitHub's ISO-8601 "published_at" as the correct Gregorian year, the suite
    /// runs serially (AssemblyInfo.cs), so mutating CurrentCulture for the duration is safe.</summary>
    [Fact]
    public void A_non_gregorian_current_culture_still_parses_the_correct_gregorian_year()
    {
        const string json = """
            {
              "tag_name": "v1.0.0",
              "published_at": "2026-06-01T12:00:00Z"
            }
            """;

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");

            var release = GitHubReleaseParsers.ParseLatestRelease(json);

            Assert.NotNull(release.PublishDate);
            Assert.Equal(2026, release.PublishDate!.Value.Year);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }


    [Theory]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("https://github.com/owner/repo/", "owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "owner/repo")]
    [InlineData("http://github.com/Owner/Repo", "Owner/Repo")]
    public void A_plain_repo_url_extracts_owner_and_repo(string url, string expected)
    {
        Assert.Equal(expected, GitHubReleaseParsers.TryExtractOwnerRepo(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/issues/5")]
    [InlineData("https://github.com/owner/repo/wiki")]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("not a url")]
    [InlineData("ftp://github.com/owner/repo")]
    public void Anything_that_is_not_exactly_owner_and_repo_is_refused(string? url)
    {
        Assert.Null(GitHubReleaseParsers.TryExtractOwnerRepo(url));
    }

    [Fact]
    public void The_latest_release_endpoint_gives_version_date_page_and_changelog()
    {
        const string json = """
            {
              "tag_name": "v2.4.1",
              "published_at": "2026-06-01T12:00:00Z",
              "html_url": "https://github.com/owner/repo/releases/tag/v2.4.1",
              "body": "### Fixed\n- a bug",
              "assets": [
                { "name": "app-setup.exe", "browser_download_url": "https://example.com/app-setup.exe", "size": 12345 },
                { "name": "app-linux.tar.gz", "browser_download_url": "https://example.com/app-linux.tar.gz", "size": 999 }
              ]
            }
            """;

        var release = GitHubReleaseParsers.ParseLatestRelease(json);

        Assert.Equal("2.4.1", release.Version);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), release.PublishDate);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v2.4.1", release.HtmlUrl);
        Assert.Contains("a bug", release.Body);
        Assert.Equal(2, release.Assets.Count);
    }

    [Fact]
    public void Malformed_json_never_throws()
    {
        Assert.Equal(default, GitHubReleaseParsers.ParseLatestRelease("{ not json"));
        Assert.Equal(default, GitHubReleaseParsers.ParseLatestRelease(null));
        Assert.Equal(default, GitHubReleaseParsers.ParseLatestRelease("[]"));
    }

    [Fact]
    public void A_single_windows_installer_asset_is_found()
    {
        GitHubReleaseAsset[] assets =
        [
            new("app-setup.exe", "https://example.com/app-setup.exe", 100),
            new("app-linux.tar.gz", "https://example.com/app-linux.tar.gz", 200),
            new("app-macos.dmg", "https://example.com/app-macos.dmg", 300),
        ];

        var found = GitHubReleaseParsers.TryFindUnambiguousWindowsInstaller(assets);

        Assert.NotNull(found);
        Assert.Equal("app-setup.exe", found.Value.Name);
    }

    [Fact]
    public void Two_windows_candidates_refuse_to_guess()
    {
        GitHubReleaseAsset[] assets =
        [
            new("app-x64-setup.exe", "https://example.com/x64.exe", 100),
            new("app-x86-setup.exe", "https://example.com/x86.exe", 100),
        ];

        Assert.Null(GitHubReleaseParsers.TryFindUnambiguousWindowsInstaller(assets));
    }

    [Fact]
    public void No_windows_asset_at_all_is_null_not_a_fallback_guess()
    {
        GitHubReleaseAsset[] assets = [new("app-linux.tar.gz", "https://example.com/x.tar.gz", 100)];

        Assert.Null(GitHubReleaseParsers.TryFindUnambiguousWindowsInstaller(assets));
    }
}
