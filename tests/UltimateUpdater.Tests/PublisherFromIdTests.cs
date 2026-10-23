using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>The pure "Publisher.Product" id parser the widened search and the row tooltip both
/// rely on.</summary>
public class PublisherFromIdTests
{
    [Fact]
    public void A_winget_shaped_id_returns_the_part_before_the_first_dot()
    {
        Assert.Equal("Mozilla", PublisherFromId.Derive("Mozilla.Firefox"));
        Assert.Equal("Microsoft", PublisherFromId.Derive("Microsoft.VisualStudioCode"));
    }

    [Fact]
    public void Only_the_first_dot_counts()
    {
        Assert.Equal("Python", PublisherFromId.Derive("Python.Python.3.12"));
    }

    [Fact]
    public void A_plain_id_with_no_dot_returns_null()
    {
        Assert.Null(PublisherFromId.Derive("eslint"));
        Assert.Null(PublisherFromId.Derive("KB5041585"));
        Assert.Null(PublisherFromId.Derive("steam"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".hidden")]
    [InlineData("trailingdot.")]
    public void Empty_or_malformed_input_returns_null(string id)
    {
        Assert.Null(PublisherFromId.Derive(id));
    }
}
