using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// An app started with administrator rights had no helper running yet, the analysis refused to start
/// one, and the component store card stayed empty for the whole session. Only an unelevated app must
/// leave the helper alone, since starting it there would raise a prompt.
/// </summary>
public class ComponentStoreElevatedStartTests
{
    [Fact]
    public void An_elevated_app_starts_the_helper_for_the_analysis() =>
        Assert.True(CleanupViewModel.MayStartHelperQuietly(processElevated: true));

    [Fact]
    public void An_unelevated_app_never_starts_the_helper_for_the_analysis() =>
        Assert.False(CleanupViewModel.MayStartHelperQuietly(processElevated: false));
}
