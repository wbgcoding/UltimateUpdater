using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class ChangelogUrlParsersTests
{
    [Fact]
    public void GitHub_tag_form_extracts_owner_repo_and_tag()
    {
        var page = ChangelogUrlParsers.TryParseGitHubReleaseUrl("https://github.com/owner/repo/releases/tag/v2.4.1");

        Assert.NotNull(page);
        Assert.Equal("owner", page!.Value.Owner);
        Assert.Equal("repo", page.Value.Repo);
        Assert.Equal("v2.4.1", page.Value.Tag);
    }

    /// <summary>The release page URL carries the tag percent-encoded; the API URL builders encode it again,
    /// so the parsed tag must be the decoded one or "+" would reach the API as "%252B".</summary>
    [Fact]
    public void GitHub_tag_is_decoded_so_it_is_not_encoded_twice()
    {
        var page = ChangelogUrlParsers.TryParseGitHubReleaseUrl("https://github.com/owner/repo/releases/tag/v1.2.3%2Bbuild");

        Assert.NotNull(page);
        Assert.Equal("v1.2.3+build", page!.Value.Tag);
    }

    [Fact]
    public void GitHub_latest_form_has_no_tag()
    {
        var page = ChangelogUrlParsers.TryParseGitHubReleaseUrl("https://github.com/owner/repo/releases/latest");

        Assert.NotNull(page);
        Assert.Equal("owner", page!.Value.Owner);
        Assert.Equal("repo", page.Value.Repo);
        Assert.Null(page.Value.Tag);
    }

    [Fact]
    public void GitLab_release_form_extracts_owner_repo_and_tag()
    {
        var page = ChangelogUrlParsers.TryParseGitLabReleaseUrl("https://gitlab.com/owner/repo/-/releases/v1.0.0");

        Assert.NotNull(page);
        Assert.Equal("owner/repo", page!.Value.OwnerAndRepo);
        Assert.Equal("v1.0.0", page.Value.Tag);
    }

    [Fact]
    public void GitLab_subgroup_release_form_keeps_the_whole_project_path()
    {
        var page = ChangelogUrlParsers.TryParseGitLabReleaseUrl("https://gitlab.com/group/sub/project/-/releases/v1.2");

        Assert.NotNull(page);
        Assert.Equal("group/sub/project", page!.Value.OwnerAndRepo);
        Assert.Equal("v1.2", page.Value.Tag);
    }

    [Fact]
    public void GitLab_api_url_escapes_a_subgroup_project_path_as_one_segment()
    {
        Assert.Equal(
            "https://gitlab.com/api/v4/projects/group%2Fsub%2Fproject/releases/v1.2",
            GitLabReleases.ReleaseUrl("group/sub/project", "v1.2"));
        Assert.Throws<ArgumentException>(() => GitLabReleases.ReleaseUrl("group//project", "v1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://github.com/owner/repo/releases/tag/v1")]
    [InlineData("https://evil.com/owner/repo/releases/tag/v1")]
    [InlineData("https://github.com.evil.com/owner/repo/releases/tag/v1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/issues/5")]
    [InlineData("https://github.com/owner/repo/wiki")]
    [InlineData("not a url")]
    public void GitHub_parser_rejects_anything_not_a_release_page(string? url)
    {
        Assert.Null(ChangelogUrlParsers.TryParseGitHubReleaseUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://gitlab.com/owner/repo/-/releases/v1")]
    [InlineData("https://evil.com/owner/repo/-/releases/v1")]
    [InlineData("https://gitlab.com.evil.com/owner/repo/-/releases/v1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo/-/issues/5")]
    [InlineData("not a url")]
    public void GitLab_parser_rejects_anything_not_a_release_page(string? url)
    {
        Assert.Null(ChangelogUrlParsers.TryParseGitLabReleaseUrl(url));
    }
}
