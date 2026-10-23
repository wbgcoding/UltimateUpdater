using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The Android source updates ONE package per row. `sdkmanager --update` (and the forwarder in the newest
/// command-line tools) updates every package at once and aborts on a single downgrade offer, which failed
/// every selected row. The argument lists are built by pure functions so they are tested without the SDK.
/// </summary>
public class AndroidSdkArgumentTests
{
    private const string Root = @"D:\AndroidStudio";

    [Fact]
    public void The_android_cli_updates_exactly_the_one_package() =>
        Assert.Equal(
            [$"--sdk={Root}", "sdk", "update", "emulator"],
            AndroidSdkSource.BuildInstallArguments(true, Root, "emulator"));

    [Fact]
    public void The_android_cli_lists_and_removes_through_its_sdk_verbs()
    {
        Assert.Equal([$"--sdk={Root}", "sdk", "list"], AndroidSdkSource.BuildScanArguments(true, Root));
        Assert.Equal([$"--sdk={Root}", "sdk", "remove", "emulator"], AndroidSdkSource.BuildUninstallArguments(true, Root, "emulator"));
    }

    /// <summary>Installing a package path installs its newest version; `--update` is never used.</summary>
    [Fact]
    public void Sdkmanager_installs_the_package_path_instead_of_updating_everything()
    {
        Assert.Equal(
            [$"--sdk_root={Root}", "platforms;android-34"],
            AndroidSdkSource.BuildInstallArguments(false, Root, "platforms;android-34"));
        Assert.Equal([$"--sdk_root={Root}", "--list"], AndroidSdkSource.BuildScanArguments(false, Root));
        Assert.Equal(
            [$"--sdk_root={Root}", "--uninstall", "platforms;android-34"],
            AndroidSdkSource.BuildUninstallArguments(false, Root, "platforms;android-34"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_command_ever_forces_a_downgrade_or_a_channel(bool useAndroidCli)
    {
        var all = AndroidSdkSource.BuildInstallArguments(useAndroidCli, Root, "emulator")
            .Concat(AndroidSdkSource.BuildScanArguments(useAndroidCli, Root))
            .Concat(AndroidSdkSource.BuildUninstallArguments(useAndroidCli, Root, "emulator"));

        Assert.DoesNotContain(all, a => a is "--force" or "--beta" or "--canary" or "--update");
    }

    [Fact]
    public void Without_a_known_sdk_root_no_root_argument_is_passed()
    {
        Assert.Equal(["sdk", "update", "emulator"], AndroidSdkSource.BuildInstallArguments(true, null, "emulator"));
        Assert.Equal(["emulator"], AndroidSdkSource.BuildInstallArguments(false, null, "emulator"));
    }
}
