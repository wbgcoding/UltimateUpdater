using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Value findings under one key (every SharedDLLs entry, every Run entry) must each have their own row path,
/// or unticking one row excludes its siblings while they still show as ticked and are reported as cleaned.
/// </summary>
public class RegistryEntryPathTests
{
    private const string SharedDlls = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";

    [Fact]
    public void Two_values_under_one_key_get_different_row_paths()
    {
        var first = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls, @"C:\gone\a.dll", "a.dll", @"C:\gone\a.dll");
        var second = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls, @"C:\gone\b.dll", "b.dll", @"C:\gone\b.dll");

        Assert.NotEqual(CleanupService.RegistryEntryPath(first), CleanupService.RegistryEntryPath(second));
    }

    /// <summary>An exclusion stored before value rows got their own paths is the bare key path; it must
    /// keep excluding every value finding under that key.</summary>
    [Fact]
    public void A_bare_key_path_exclusion_still_covers_the_value_findings_under_that_key()
    {
        var first = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls, @"C:\gone\a.dll", "a.dll", @"C:\gone\a.dll");
        var other = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls + "2", @"C:\gone\b.dll", "b.dll", @"C:\gone\b.dll");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SharedDlls.ToLowerInvariant() };

        Assert.True(CleanupService.IsRegistryFindingExcluded(first, excluded));
        Assert.False(CleanupService.IsRegistryFindingExcluded(other, excluded));
    }

    [Fact]
    public void A_row_path_exclusion_covers_only_that_value()
    {
        var first = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls, @"C:\gone\a.dll", "a.dll", @"C:\gone\a.dll");
        var second = new RegistryFinding(RegistryCleanupCategory.SharedDlls, SharedDlls, @"C:\gone\b.dll", "b.dll", @"C:\gone\b.dll");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CleanupService.RegistryEntryPath(first) };

        Assert.True(CleanupService.IsRegistryFindingExcluded(first, excluded));
        Assert.False(CleanupService.IsRegistryFindingExcluded(second, excluded));
    }

    [Fact]
    public void A_key_finding_keeps_its_key_path_as_the_row_path()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\gone.exe";
        var finding = new RegistryFinding(RegistryCleanupCategory.AppPaths, key, null, "gone.exe", @"C:\gone.exe");

        Assert.Equal(key, CleanupService.RegistryEntryPath(finding));
    }
}
