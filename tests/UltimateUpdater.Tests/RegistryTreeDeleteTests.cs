using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The key-tree delete opens everything first and deletes through the held handles; these prove
/// the two refusals (a link below, a child that appeared since) and the depth rule for the classes mount.</summary>
public class RegistryTreeDeleteTests : IDisposable
{
    private const uint RegLinkType = 6;
    private const string Broken = @"C:\wu-no-such-folder\app.exe";
    private readonly string _relativeRoot = @"Software\WuTreeDelete" + Guid.NewGuid().ToString("N");

    public RegistryTreeDeleteTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose()
    {
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        RegistryCleanupScanner.BeforeDeleteForTest.Value = null;
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(
        SafeRegistryHandle hKey, string lpValueName, int reserved, uint dwType, byte[] lpData, int cbData);

    private static void PlantMarker(RegistryKey key)
    {
        var data = Encoding.Unicode.GetBytes(@"\Registry\Machine" + "\0");
        Assert.Equal(0, RegSetValueExW(key.Handle, "SymbolicLinkValue", 0, RegLinkType, data, data.Length));
    }

    private RegistryFinding BrokenKeyFinding() =>
        new(RegistryCleanupCategory.AppPaths, @"HKCU\" + _relativeRoot + @"\app.exe", null, "app.exe", Broken);

    private void CreateBrokenKeyWithChild()
    {
        using var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe");
        key.SetValue(string.Empty, Broken);
        key.CreateSubKey("Child").Dispose();
    }

    [Fact]
    public void A_link_below_the_key_refuses_the_whole_finding_and_a_sentinel_below_it_survives()
    {
        CreateBrokenKeyWithChild();
        using (var linked = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe\Linked"))
        {
            PlantMarker(linked);
            linked.CreateSubKey("Sentinel").Dispose();
        }

        // Only the planted child counts as a link; the siblings must stay too (nothing is deleted at all).
        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Linked";

        Assert.Equal(0, RegistryCleanupScanner.Delete([BrokenKeyFinding()], new LoggingService()));

        using var sentinel = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe\Linked\Sentinel");
        Assert.NotNull(sentinel);
        using var sibling = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe\Child");
        Assert.NotNull(sibling);
    }

    [Fact]
    public void A_child_that_appears_after_the_keys_were_opened_stops_the_delete_and_survives()
    {
        CreateBrokenKeyWithChild();
        RegistryCleanupScanner.BeforeDeleteForTest.Value = () =>
            Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe\Child\Late").Dispose();

        Assert.Equal(0, RegistryCleanupScanner.Delete([BrokenKeyFinding()], new LoggingService()));

        using var late = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe\Child\Late");
        Assert.NotNull(late);
    }

    [Fact]
    public void An_untouched_tree_is_deleted_bottom_up_and_counted_once()
    {
        CreateBrokenKeyWithChild();
        using (var deep = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe\Child\Deep\Deeper"))
            deep.SetValue("v", "1");

        Assert.Equal(1, RegistryCleanupScanner.Delete([BrokenKeyFinding()], new LoggingService()));

        using var gone = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe");
        Assert.Null(gone);
    }

    [Fact]
    public void A_missing_top_key_reports_not_found_rather_than_a_delete()
    {
        var status = RegistryBackupService.DeleteKeyTreeLinkSafe(
            Registry.CurrentUser.Handle, _relativeRoot + @"\does-not-exist", out _);

        Assert.Equal(RegistryBackupService.KeyTreeDeleteStatus.NotFound, status);
    }

    [Fact]
    public void A_tree_deeper_than_the_cap_is_refused_untouched()
    {
        var path = _relativeRoot + @"\deep";
        // Registry key names cap the total path at 255 chars per segment but not overall: chain 70 levels.
        var current = Registry.CurrentUser.CreateSubKey(path);
        for (var i = 0; i < 70; i++)
        {
            var next = current.CreateSubKey("k");
            current.Dispose();
            current = next;
        }

        current.Dispose();

        var status = RegistryBackupService.DeleteKeyTreeLinkSafe(
            Registry.CurrentUser.Handle, path, out _);

        Assert.Equal(RegistryBackupService.KeyTreeDeleteStatus.RefusedLink, status);
        using var still = Registry.CurrentUser.OpenSubKey(path);
        Assert.NotNull(still);
    }

    [Fact]
    public void The_classes_mount_exception_applies_only_to_the_second_segment_of_software_classes()
    {
        Assert.True(RegistryBackupService.IsClassesMountPosition(["Software", "Classes", "CLSID"], 1));
        Assert.True(RegistryBackupService.IsClassesMountPosition(["software", "Classes"], 1));
        Assert.False(RegistryBackupService.IsClassesMountPosition(["Software", "Vendor", "Classes"], 2));
        Assert.False(RegistryBackupService.IsClassesMountPosition(["Other", "Classes"], 1));
        Assert.False(RegistryBackupService.IsClassesMountPosition(["Classes"], 0));
    }

    [Fact]
    public void A_link_named_classes_at_depth_two_is_not_followed()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Classes"))
        {
            PlantMarker(key);
            key.CreateSubKey("Inner").Dispose();
        }

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Classes";

        Assert.Null(RegistryCleanupScanner.Open(@"HKCU\" + _relativeRoot + @"\Classes\Inner", writable: false));
    }
}
