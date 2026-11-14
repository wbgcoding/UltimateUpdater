using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class NpmChangelogLookupTests
{
    // Real captured `npm view react homepage repository --json` output, 2026-09-17: repository is an
    // object whose url carries the "git+" prefix npm's own packument convention adds.
    private const string ReactView = """
        {
          "homepage": "https://react.dev/",
          "repository": {
            "url": "git+https://github.com/react/react.git",
            "type": "git",
            "directory": "packages/react"
          }
        }
        """;

    // Real captured `npm view left-pad homepage repository --json` output, 2026-09-17: the repository's
    // url is an SSH form GitHubReleaseParsers.TryExtractOwnerRepo does not accept, and homepage is the
    // package's own GitHub Pages-style readme link rather than a plain project root.
    // Split so the ssh user and host do not read as an e-mail address in the source.
    private const string LeftPadSshUrl = "git+ssh://git" + "@github.com/stevemao/left-pad.git";

    private const string LeftPadView = $$"""
        {
          "homepage": "https://github.com/stevemao/left-pad#readme",
          "repository": {
            "url": "{{LeftPadSshUrl}}",
            "type": "git"
          }
        }
        """;

    [Fact]
    public void ParseView_reads_an_object_shaped_repository_and_the_homepage()
    {
        var result = NpmChangelogLookup.ParseView(ReactView);

        Assert.Equal("git+https://github.com/react/react.git", result.RepositoryUrl);
        Assert.Equal("https://react.dev/", result.Homepage);
    }

    [Fact]
    public void ParseView_reads_an_ssh_form_repository_too_leaving_recognition_to_the_caller()
    {
        var result = NpmChangelogLookup.ParseView(LeftPadView);

        Assert.Equal(LeftPadSshUrl, result.RepositoryUrl);
        Assert.Equal("https://github.com/stevemao/left-pad#readme", result.Homepage);
    }

    [Fact]
    public void ParseView_reads_a_plain_string_shaped_repository()
    {
        const string json = """{"homepage":"https://example.com","repository":"git+https://github.com/owner/repo.git"}""";

        var result = NpmChangelogLookup.ParseView(json);

        Assert.Equal("git+https://github.com/owner/repo.git", result.RepositoryUrl);
        Assert.Equal("https://example.com", result.Homepage);
    }

    /// <summary>With a single field asked for, npm prints the bare value instead of a keyed object.</summary>
    [Fact]
    public void ParseView_reads_a_bare_homepage_string_of_a_single_field_query()
    {
        var result = NpmChangelogLookup.ParseView("\"https://react.dev/\"", "homepage");

        Assert.Null(result.RepositoryUrl);
        Assert.Equal("https://react.dev/", result.Homepage);
    }

    [Fact]
    public void ParseView_reads_a_bare_repository_object_and_a_bare_repository_string_of_a_single_field_query()
    {
        var asObject = NpmChangelogLookup.ParseView(
            """{"url":"git+https://github.com/react/react.git","type":"git","directory":"packages/react"}""", "repository");
        var asString = NpmChangelogLookup.ParseView("\"git+https://github.com/owner/repo.git\"", "repository");

        Assert.Equal("git+https://github.com/react/react.git", asObject.RepositoryUrl);
        Assert.Null(asObject.Homepage);
        Assert.Equal("git+https://github.com/owner/repo.git", asString.RepositoryUrl);
        Assert.Null(asString.Homepage);
    }

    [Fact]
    public void ParseView_without_a_single_field_still_ignores_a_bare_value()
    {
        Assert.Equal(default, NpmChangelogLookup.ParseView("\"https://react.dev/\""));
    }

    /// <summary>An unpublished/removed package's `npm view --json` prints an error object with neither
    /// expected key; the parser must read that as "nothing found", not throw or fabricate a value.</summary>
    [Fact]
    public void ParseView_treats_an_npm_error_object_as_nothing_found()
    {
        const string json = """{"error":{"code":"E404","summary":"Not Found"}}""";

        var result = NpmChangelogLookup.ParseView(json);

        Assert.Null(result.RepositoryUrl);
        Assert.Null(result.Homepage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{"repository": 42}""")]
    [InlineData("""{"repository": {"url": 42}}""")]
    public void ParseView_never_throws_on_malformed_or_unexpected_input(string input)
    {
        var result = NpmChangelogLookup.ParseView(input);

        Assert.Null(result.RepositoryUrl);
        Assert.Null(result.Homepage);
    }

    [Fact]
    public void TryBuildGitHubReleasesUrl_strips_the_git_plus_prefix_and_builds_the_releases_page()
    {
        var url = NpmChangelogLookup.TryBuildGitHubReleasesUrl("git+https://github.com/react/react.git");

        Assert.Equal("https://github.com/react/react/releases/latest", url);
    }

    /// <summary>Real captured shape (left-pad): an ssh-form repository is a GitHub location but not a
    /// GitHub URL the shared recogniser accepts, and is deliberately left unrecognised rather than guessed at.</summary>
    [Fact]
    public void TryBuildGitHubReleasesUrl_returns_null_for_an_ssh_form_repository()
    {
        var url = NpmChangelogLookup.TryBuildGitHubReleasesUrl(LeftPadSshUrl);

        Assert.Null(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/owner/repo.git")]
    public void TryBuildGitHubReleasesUrl_returns_null_for_anything_not_a_github_url(string? repositoryUrl)
    {
        Assert.Null(NpmChangelogLookup.TryBuildGitHubReleasesUrl(repositoryUrl));
    }
}
