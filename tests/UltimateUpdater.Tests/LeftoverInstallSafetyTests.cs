using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The core judgment behind the leftover-folder scanner: a folder is only a "plausible" leftover, never
/// a proven one, the one real failure mode (a genuine, still-used portable program that was never
/// registered) cannot be told apart from a true leftover with certainty, so this gate is deliberately
/// conservative on both signals it has.
/// </summary>
public class LeftoverInstallSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromDays(180);

    [Fact]
    public void An_old_untouched_folder_with_no_registered_owner_is_a_plausible_leftover()
    {
        var untouched = Now.AddDays(-400);

        Assert.True(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\SomeOldApp", [], untouched, Now, MinimumAge));
    }

    [Fact]
    public void A_folder_touched_recently_is_never_a_leftover()
    {
        // exactly the signal that tells a real, still-used portable app apart from a true leftover
        var recentlyTouched = Now.AddDays(-10);

        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\PortableApp", [], recentlyTouched, Now, MinimumAge));
    }

    [Fact]
    public void A_folder_a_registered_uninstall_entry_still_claims_is_never_a_leftover()
    {
        var untouched = Now.AddDays(-400);

        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\RealApp", [@"C:\Program Files\RealApp"], untouched, Now, MinimumAge));
    }

    [Fact]
    public void A_real_app_installed_INSIDE_the_candidate_folder_still_protects_it()
    {
        var untouched = Now.AddDays(-400);

        // the registered path is a subfolder of the candidate, something real still lives in there
        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\Vendor", [@"C:\Program Files\Vendor\RealApp\bin"], untouched, Now, MinimumAge));
    }

    [Fact]
    public void The_candidate_being_a_subfolder_of_a_registered_path_also_protects_it()
    {
        var untouched = Now.AddDays(-400);

        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\Vendor\SubTool", [@"C:\Program Files\Vendor"], untouched, Now, MinimumAge));
    }

    [Theory]
    [InlineData(@"C:\Program Files\ModifiableWindowsApps")]
    [InlineData(@"C:\Program Files (x86)\Windows Sidebar")]
    [InlineData(@"C:\Program Files\Microsoft.NET")]
    [InlineData(@"C:\ProgramData\SoftwareDistribution")]
    public void Known_Windows_system_folders_are_never_a_leftover_even_if_old_and_unregistered(string path)
    {
        // Known Windows-owned folders look exactly like a leftover, old, untouched, no ARP entry, but
        // deleting any of them would damage the OS rather than free disk space.
        var untouched = Now.AddYears(-2);

        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(path, [], untouched, Now, MinimumAge));
    }

    [Fact]
    public void This_apps_own_directory_is_never_a_leftover_of_itself() =>
        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            AppContext.BaseDirectory, [], Now.AddYears(-1), Now, MinimumAge));

    [Fact]
    public void Right_at_the_age_threshold_is_not_yet_old_enough()
    {
        var justUnderThreshold = Now.Subtract(MinimumAge).AddSeconds(1);

        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            @"C:\Program Files\BorderlineApp", [], justUnderThreshold, Now, MinimumAge));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_never_a_leftover(string? path) =>
        Assert.False(LeftoverInstallSafety.IsPlausibleLeftover(
            path!, [], Now.AddYears(-1), Now, MinimumAge));
}
