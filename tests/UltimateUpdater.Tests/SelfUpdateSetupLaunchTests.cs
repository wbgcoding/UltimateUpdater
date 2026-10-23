using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class SelfUpdateSetupLaunchTests
{
    [Fact]
    public void An_all_users_install_starts_the_verified_setup_elevated()
    {
        var info = SelfUpdateService.BuildSetupStartInfo(@"C:\Temp\setup.exe", machineScope: true);

        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Temp\setup.exe", info.FileName);
        Assert.Contains("/VERYSILENT", info.Arguments);
    }

    [Fact]
    public void A_per_user_install_starts_the_verified_setup_without_elevation()
    {
        var info = SelfUpdateService.BuildSetupStartInfo(@"C:\Temp\setup.exe", machineScope: false);

        Assert.True(string.IsNullOrEmpty(info.Verb));
        Assert.True(info.UseShellExecute);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Ultimate Updater\", true)]
    [InlineData(@"c:\program files\Ultimate Updater", true)]
    [InlineData(@"C:\Program Files (x86)\Ultimate Updater\", true)]
    [InlineData(@"C:\Users\Someone\AppData\Local\Programs\Ultimate Updater\", false)]
    [InlineData(@"C:\Program FilesEvil\Ultimate Updater\", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Machine_scope_means_the_install_folder_is_below_a_Program_Files_root(string? baseDirectory, bool expected)
    {
        string[] roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

        Assert.Equal(expected, SelfUpdateService.IsMachineScopeInstall(baseDirectory, roots));
    }
}
