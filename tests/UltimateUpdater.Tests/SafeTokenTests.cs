using UltimateUpdater.Services.Sources.Shared;
using Xunit;

namespace UltimateUpdater.Tests;

public class SafeTokenTests
{
    [Fact]
    public void A_cyrillic_look_alike_is_not_a_plain_token() =>
        Assert.False(SafeToken.IsPlain("ехо"));

    [Fact]
    public void A_fullwidth_digit_is_not_a_plain_token() =>
        Assert.False(SafeToken.IsPlain("a１"));

    [Fact]
    public void An_ordinary_owner_repo_segment_is_plain() =>
        Assert.True(SafeToken.IsPlain("owner-name.repo_1"));

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a%2Fb")]
    public void Separators_and_empty_values_are_refused(string value) =>
        Assert.False(SafeToken.IsPlain(value));

    [Fact]
    public void A_dot_run_is_refused_only_when_asked()
    {
        Assert.True(SafeToken.IsPlain("a..b"));
        Assert.False(SafeToken.IsPlain("a..b", allowDotRuns: false));
        Assert.False(SafeToken.IsPlain(".", allowDotRuns: false));
        Assert.True(SafeToken.IsPlain("llama3.2", allowDotRuns: false));
    }

    [Fact]
    public void The_length_cap_applies()
    {
        Assert.True(SafeToken.IsPlain(new string('a', 128), 128));
        Assert.False(SafeToken.IsPlain(new string('a', 129), 128));
    }
}
