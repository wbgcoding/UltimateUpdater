using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class GitHubReleaseUrlTests
{
    [Fact]
    public void Latest_url_targets_the_api_with_both_halves_escaped()
    {
        Assert.Equal("https://api.github.com/repos/astral-sh/uv/releases/latest",
            GitHubReleases.LatestUrl("astral-sh/uv"));
        Assert.Equal("https://api.github.com/repos/a%20b/c%3Fd/releases/latest",
            GitHubReleases.LatestUrl("a b/c?d"));
    }

    [Fact]
    public void Tag_url_uses_the_plural_tags_segment_and_escapes_the_tag()
    {
        Assert.Equal("https://api.github.com/repos/oven-sh/bun/releases/tags/bun-v1.3.14",
            GitHubReleases.TagUrl("oven-sh/bun", "bun-v1.3.14"));
        // a tag may contain a slash; it must not open a new path segment
        Assert.Equal("https://api.github.com/repos/o/r/releases/tags/rel%2F1.0",
            GitHubReleases.TagUrl("o/r", "rel/1.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("justarepo")]
    [InlineData("a/b/c")]
    public void A_repository_that_is_not_owner_slash_repo_is_rejected(string ownerAndRepo)
    {
        Assert.Throws<ArgumentException>(() => GitHubReleases.LatestUrl(ownerAndRepo));
        Assert.Throws<ArgumentException>(() => GitHubReleases.TagUrl(ownerAndRepo, "v1"));
    }

    [Fact]
    public void Gitlab_release_url_keeps_subgroups_in_one_escaped_project_segment()
    {
        Assert.Equal("https://gitlab.com/api/v4/projects/group%2Fproject/releases/v1",
            GitLabReleases.ReleaseUrl("group/project", "v1"));
        Assert.Equal("https://gitlab.com/api/v4/projects/g%2Fsub%2Fp/releases/v%2F1",
            GitLabReleases.ReleaseUrl("g/sub/p", "v/1"));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("a//b")]
    [InlineData("/a/b")]
    [InlineData("a/b/")]
    public void A_gitlab_project_with_a_missing_part_is_rejected(string project) =>
        Assert.Throws<ArgumentException>(() => GitLabReleases.ReleaseUrl(project, "v1"));
}
