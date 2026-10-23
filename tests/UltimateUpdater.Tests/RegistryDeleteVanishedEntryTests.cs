using Microsoft.Win32;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>An entry that disappears between the last check and the delete call was not deleted by this run
/// and must not be counted as freed.</summary>
public class RegistryDeleteVanishedEntryTests : IDisposable
{
    private const string Broken = @"C:\wu-no-such-folder\app.exe";
    private readonly string _relativeRoot = @"Software\WuVanish" + Guid.NewGuid().ToString("N");

    public RegistryDeleteVanishedEntryTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose()
    {
        RegistryCleanupScanner.BeforeDeleteForTest.Value = null;
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
    }

    [Fact]
    public void A_key_that_vanishes_before_the_delete_is_not_counted()
    {
        var keyPath = @"HKCU\" + _relativeRoot + @"\app.exe";
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe"))
            key.SetValue(string.Empty, Broken);

        RegistryCleanupScanner.BeforeDeleteForTest.Value = () =>
            Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot + @"\app.exe");

        var finding = new RegistryFinding(RegistryCleanupCategory.AppPaths, keyPath, null, "app.exe", Broken);

        Assert.Equal(0, RegistryCleanupScanner.Delete([finding], new LoggingService()));
    }

    [Fact]
    public void A_value_that_vanishes_before_the_delete_is_not_counted()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_relativeRoot, writable: true)!)
            key.SetValue("entry", Broken);

        RegistryCleanupScanner.BeforeDeleteForTest.Value = () =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(_relativeRoot, writable: true)!;
            key.DeleteValue("entry");
        };

        var finding = new RegistryFinding(
            RegistryCleanupCategory.Autostart, @"HKCU\" + _relativeRoot, "entry", "entry", Broken);

        Assert.Equal(0, RegistryCleanupScanner.Delete([finding], new LoggingService()));
    }

    [Fact]
    public void An_entry_that_is_still_there_is_deleted_and_counted()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(_relativeRoot, writable: true)!)
            key.SetValue("entry", Broken);

        var finding = new RegistryFinding(
            RegistryCleanupCategory.Autostart, @"HKCU\" + _relativeRoot, "entry", "entry", Broken);

        Assert.Equal(1, RegistryCleanupScanner.Delete([finding], new LoggingService()));
        using var check = Registry.CurrentUser.OpenSubKey(_relativeRoot)!;
        Assert.Null(check.GetValue("entry"));
    }
}
