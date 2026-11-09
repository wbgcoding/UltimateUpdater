using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class NodeEngineRangeTests
{
    [Theory]
    [InlineData("^20.17 || >=22.9", "18.19.0", false)]
    [InlineData("^20.17 || >=22.9", "20.17.0", true)]
    [InlineData("^20.17 || >=22.9", "21.0.0", false)]
    [InlineData("^20.17 || >=22.9", "22.9.0", true)]
    [InlineData(">=18", "18.0.0", true)]
    [InlineData(">=18 <22", "22.1.0", false)]
    [InlineData("~20.1.0", "20.1.9", true)]
    [InlineData("~20.1.0", "20.2.0", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    public void Satisfies_follows_the_npm_range_rules(string range, string node, bool expected)
    {
        Assert.Equal(expected, NodeEngineRange.Satisfies(range, node));
    }

    [Theory]
    [InlineData("1.x", "20.0.0")]
    [InlineData("*", "20.0.0")]
    [InlineData(">=18", "not a version")]
    [InlineData(null, "20.0.0")]
    [InlineData(">=18", null)]
    public void An_unreadable_side_is_unknown_never_unsatisfied(string? range, string? node)
    {
        Assert.Null(NodeEngineRange.Satisfies(range, node));
    }

    [Fact]
    public void Engine_is_read_from_the_manifest_or_null()
    {
        Assert.Equal(">=18", NodeEngineRange.ParseNodeEngine("""{"engines":{"node":">=18"}}"""));
        Assert.Null(NodeEngineRange.ParseNodeEngine("""{"version":"1.0.0"}"""));
        Assert.Null(NodeEngineRange.ParseNodeEngine("""{"engines":["node"]}"""));
        Assert.Null(NodeEngineRange.ParseNodeEngine("not json"));
    }
}
