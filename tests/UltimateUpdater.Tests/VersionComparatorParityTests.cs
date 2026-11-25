using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The generic comparator for tool output and the strict one for the self-update must give the same answer
/// for any input both accept. They are allowed to differ only on input that is not strict SemVer.
/// </summary>
public class VersionComparatorParityTests
{
    [Theory]
    [InlineData("1.2.0-rc1", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0-rc1", false)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("1.10.0", "1.9.0", false)]
    [InlineData("v1.2.0", "1.2.0", false)]
    [InlineData("1.2.0", "v1.2.0", false)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.2.0-rc1", "1.2.0-rc2", true)]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.10", true)]
    [InlineData("1.2.0-rc.10", "1.2.0-rc.2", false)]
    [InlineData("1.2.0+build1", "1.2.0+build2", false)]
    public void Both_comparators_agree_on_strict_SemVer(string installed, string available, bool expected)
    {
        Assert.Equal(expected, PackageManagerParsers.IsNewer(installed, available));
        Assert.Equal(expected, UpdateManifest.IsNewer(installed, available));
    }

    [Fact]
    public void Only_the_self_update_comparator_refuses_a_version_that_is_not_strict_SemVer()
    {
        Assert.True(PackageManagerParsers.IsNewer("1.2", "1.3"));
        Assert.False(UpdateManifest.IsNewer("1.2", "1.3"));
    }
}
