using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class GitLabReleaseParsersTests
{
    [Fact]
    public void The_description_is_returned_verbatim()
    {
        const string json = """{"tag_name":"v1.2.0","description":"## Fixed\n- a thing\n"}""";

        Assert.Equal("## Fixed\n- a thing\n", GitLabReleaseParsers.ParseReleaseDescription(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"description\": ")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("{}")]
    [InlineData("{\"description\":null}")]
    [InlineData("{\"description\":42}")]
    [InlineData("{\"description\":{\"a\":1}}")]
    public void Anything_unexpected_is_null_and_never_throws(string? json) =>
        Assert.Null(GitLabReleaseParsers.ParseReleaseDescription(json));

    [Fact]
    public void An_empty_description_stays_an_empty_string()
    {
        Assert.Equal(string.Empty, GitLabReleaseParsers.ParseReleaseDescription("""{"description":""}"""));
    }
}
