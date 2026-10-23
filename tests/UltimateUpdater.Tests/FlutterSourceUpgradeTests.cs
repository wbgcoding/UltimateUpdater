using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>The Flutter row appears only for a strictly newer feed version, and the install never forces.</summary>
public class FlutterSourceUpgradeTests
{
    [Theory]
    [InlineData("3.24.0", "3.22.3")]
    [InlineData("3.24.0", "3.24.0")]
    [InlineData("3.24.0-0.2.pre", "3.24.0-0.2.pre")]
    [InlineData("3.24.0", null)]
    public void No_row_when_the_feed_is_not_newer(string installed, string? latest) =>
        Assert.False(FlutterSource.ShouldOffer(installed, latest));

    [Theory]
    [InlineData("3.22.3", "3.24.0")]
    [InlineData("3.24.0-0.2.pre", "3.24.0")]
    public void Row_when_the_feed_is_newer(string installed, string latest) =>
        Assert.True(FlutterSource.ShouldOffer(installed, latest));

    [Fact]
    public void Upgrade_arguments_never_force() =>
        Assert.DoesNotContain("--force", FlutterSource.UpgradeArguments);
}
