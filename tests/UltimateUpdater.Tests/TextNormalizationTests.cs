using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Shared by <c>BrandIcons</c>, <c>IconCacheService</c> and <c>SecurityRelevance</c>,
/// all three had byte-identical copies of this before this consolidation.</summary>
public class TextNormalizationTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", "nvidiageforcertx4090")]
    [InlineData("Node.js v20.11", "nodejsv2011")]
    [InlineData("", "")]
    public void Letters_and_digits_are_kept_lowercase_everything_else_is_dropped(string input, string expected)
    {
        Assert.Equal(expected, TextNormalization.AlphanumericLower(input));
    }
}
