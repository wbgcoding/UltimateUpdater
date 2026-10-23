using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>HKCU backup roots are opened one segment at a time; a linked root is skipped and nothing under
/// it is ever deleted. The link verdict comes from the detector seam (a live link needs NtCreateKey).</summary>
public class RegistryRootSafetyTests : IDisposable
{
    private const uint RegLinkType = 6;
    private readonly string _relativeRoot = @"Software\WuRootSafety" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wu-regroot-" + Guid.NewGuid());

    public RegistryRootSafetyTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose()
    {
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(
        SafeRegistryHandle hKey, string lpValueName, int reserved, uint dwType, byte[] lpData, int cbData);

    private static void PlantMarker(RegistryKey key)
    {
        var data = Encoding.Unicode.GetBytes(@"\Registry\Machine" + "\0");
        Assert.Equal(0, RegSetValueExW(key.Handle, "SymbolicLinkValue", 0, RegLinkType, data, data.Length));
    }

    private string HkcuPath(string relative) => @"HKCU\" + _relativeRoot + @"\" + relative;

    [Fact]
    public void Open_returns_null_when_the_link_check_itself_fails_mid_scan()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Fragile"))
            PlantMarker(key);
        RegistryBackupService.RealLinkDetectorForTest.Value = (_, _, _) => throw new IOException("key deleted");

        Assert.Null(RegistryCleanupScanner.Open(HkcuPath("Fragile"), writable: false));
        Assert.False(RegistryCleanupScanner.KeyExists(HkcuPath("Fragile")));
    }

    [Fact]
    public async Task A_linked_HKCU_root_is_skipped_the_backup_succeeds_without_it_and_it_is_not_in_the_exported_set()
    {
        using (var linked = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Linked"))
        {
            PlantMarker(linked);
            linked.SetValue("secret", "must-not-be-exported");
        }

        using (var good = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Good"))
            good.SetValue("kept", "exported-value");

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Linked";

        var result = await RegistryBackupService.BackupRootsBeforeWriteAsync(
            [HkcuPath("Linked"), HkcuPath("Good")], _folder, 5, new LoggingService(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal([HkcuPath("Good")], result!.ExportedRoots);
        var text = await File.ReadAllTextAsync(result.Path);
        Assert.Contains("exported-value", text);
        Assert.DoesNotContain("must-not-be-exported", text);
    }

    [Fact]
    public async Task A_root_that_does_not_exist_at_backup_time_is_not_in_the_exported_set()
    {
        using (var good = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Good"))
            good.SetValue("kept", "x");

        var result = await RegistryBackupService.BackupRootsBeforeWriteAsync(
            [HkcuPath("Missing"), HkcuPath("Good")], _folder, 5, new LoggingService(), CancellationToken.None);

        Assert.Equal([HkcuPath("Good")], result!.ExportedRoots);
    }

    [Fact]
    public void A_link_in_the_middle_of_a_multi_segment_HKCU_root_stops_the_export_of_everything_below_it()
    {
        using (var mid = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Mid"))
        {
            PlantMarker(mid);
            using var leaf = mid.CreateSubKey("Leaf");
            leaf.SetValue("behind-link", "must-not-be-exported");
        }

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Mid";

        var text = RegistryBackupService.ExportKeyTreeText(HkcuPath(@"Mid\Leaf"));

        Assert.DoesNotContain("must-not-be-exported", text);
    }

    [Fact]
    public void A_control_character_in_a_value_name_names_the_key_in_the_error()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Bad"))
            key.SetValue("name\u0001", "x");

        var ex = Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeText(HkcuPath("Bad")));

        Assert.Contains(_relativeRoot + @"\Bad", ex.Message);
    }

    [Fact]
    public void Findings_outside_every_exported_root_are_dropped_and_the_rest_kept()
    {
        var inside = new RegistryFinding(RegistryCleanupCategory.AppPaths, @"HKCU\Software\A\sub", null, "a", "x");
        var onRoot = new RegistryFinding(RegistryCleanupCategory.Autostart, @"HKCU\Software\A", "v", "v", "x");
        var outside = new RegistryFinding(RegistryCleanupCategory.AppPaths, @"HKCU\Software\B\sub", null, "b", "x");
        var lookalike = new RegistryFinding(RegistryCleanupCategory.AppPaths, @"HKCU\Software\AB\sub", null, "c", "x");

        var (kept, dropped) = CleanupService.SplitByExportedRoots(
            [inside, onRoot, outside, lookalike], [@"hkcu\software\a"]);

        Assert.Equal([inside, onRoot], kept);
        Assert.Equal(2, dropped);
    }
}
