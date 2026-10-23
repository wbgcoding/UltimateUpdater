using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// A live registry link cannot be created from a test without NtCreateKey, so these plant the link marker
/// on ordinary scratch keys and let the detector seam decide whether the marker counts as a real link.
/// What is proven is the decision around a link, not the kernel's link detection itself.
/// </summary>
public class RegistryLinkSafetyTests : IDisposable
{
    private const uint RegLinkType = 6;
    private readonly string _relativeRoot = @"Software\WuLinkSafety" + Guid.NewGuid().ToString("N");

    public RegistryLinkSafetyTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose()
    {
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
    }

    private static void PlantMarker(RegistryKey key)
    {
        var data = Encoding.Unicode.GetBytes(@"\Registry\Machine" + "\0");
        Assert.Equal(0, RegSetValueExW(key.Handle, "SymbolicLinkValue", 0, RegLinkType, data, data.Length));
    }

    private static void AlwaysReal() =>
        RegistryBackupService.RealLinkDetectorForTest.Value = (_, _, _) => true;

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(
        SafeRegistryHandle hKey, string lpValueName, int reserved, uint dwType, byte[] lpData, int cbData);

    [Fact]
    public void An_HKCU_backup_root_that_is_a_link_is_skipped_and_nothing_under_it_is_deleted()
    {
        const string broken = @"C:\wu-no-such-folder\app.exe";
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Root"))
        {
            PlantMarker(key);
            using var app = key.CreateSubKey("app.exe");
            app.SetValue(string.Empty, broken);
        }

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Root";

        var text = RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot + @"\Root");
        Assert.DoesNotContain("app.exe", text);

        var finding = new RegistryFinding(
            RegistryCleanupCategory.AppPaths, @"HKCU\" + _relativeRoot + @"\Root\app.exe", null, "app.exe", broken);
        Assert.Equal(0, RegistryCleanupScanner.Delete([finding], new LoggingService()));
        using var still = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\Root\app.exe");
        Assert.NotNull(still);
    }

    [Fact]
    public void An_HKLM_root_that_is_a_link_is_followed_an_HKCU_root_is_not()
    {
        Assert.True(RegistryBackupService.FollowsRootLink(@"HKLM\SOFTWARE\WOW6432Node\Microsoft"));
        Assert.False(RegistryBackupService.FollowsRootLink(@"HKCU\Software\Microsoft"));

        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Root"))
            PlantMarker(key);
        AlwaysReal();

        using var followed = RegistryBackupService.OpenKeyForExport(
            Registry.CurrentUser.Handle, _relativeRoot + @"\Root", "x", depth: 0, logging: null, followRootLink: true);
        Assert.NotNull(followed);
        Assert.Throws<IOException>(() => RegistryBackupService.OpenKeyForExport(
            Registry.CurrentUser.Handle, _relativeRoot + @"\Root", "x", depth: 0, logging: null, followRootLink: false));
    }

    [Fact]
    public void A_link_in_the_middle_of_an_HKCU_path_makes_the_open_return_null()
    {
        using (var middle = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Mid"))
        {
            PlantMarker(middle);
            middle.CreateSubKey("Leaf").Dispose();
        }

        var path = @"HKCU\" + _relativeRoot + @"\Mid\Leaf";

        using (var plain = RegistryCleanupScanner.Open(path, writable: false))
            Assert.NotNull(plain); // a marker on an ordinary key is a decoy: nothing links anywhere

        AlwaysReal();
        Assert.Null(RegistryCleanupScanner.Open(path, writable: false));
        Assert.Null(RegistryCleanupScanner.Open(@"HKCU\" + _relativeRoot + @"\Mid", writable: true));
    }

    [Fact]
    public void An_ordinary_HKCU_path_still_opens_for_reading_and_writing()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\A\B"))
            key.SetValue("v", "1");

        var path = @"HKCU\" + _relativeRoot + @"\A\B";
        using (var read = RegistryCleanupScanner.Open(path, writable: false))
            Assert.Equal("1", read!.GetValue("v"));
        using (var write = RegistryCleanupScanner.Open(path, writable: true))
        {
            write!.DeleteValue("v");
            Assert.Null(write.GetValue("v"));
        }

        Assert.Null(RegistryCleanupScanner.Open(path + @"\Missing", writable: false));
    }

    [Fact]
    public void A_key_finding_with_a_link_below_it_is_not_deleted_nor_counted()
    {
        var keyPath = @"HKCU\" + _relativeRoot + @"\app.exe";
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\app.exe"))
        {
            key.SetValue(string.Empty, @"C:\wu-no-such-folder\app.exe");
            using var sub = key.CreateSubKey("Sub");
            PlantMarker(sub);
        }

        var finding = new RegistryFinding(
            RegistryCleanupCategory.AppPaths, keyPath, null, "app.exe", @"C:\wu-no-such-folder\app.exe");
        var logging = new LoggingService();

        AlwaysReal();
        Assert.Equal(0, RegistryCleanupScanner.Delete([finding], logging));
        using (var still = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe"))
            Assert.NotNull(still);

        // Without the link verdict the same marker is a decoy and the broken key is removed as before.
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        Assert.Equal(1, RegistryCleanupScanner.Delete([finding], logging));
        using (var gone = Registry.CurrentUser.OpenSubKey(_relativeRoot + @"\app.exe"))
            Assert.Null(gone);
    }

    /// <summary>Windows mounts the per-user classes hive under HKCU\Software\Classes through a real link, and
    /// the CLSID cleanup roots live below it: that one mount must stay reachable.</summary>
    [Fact]
    public void The_users_own_classes_mount_is_followed_other_links_are_not()
    {
        var name = "WuLinkSafetyClasses" + Guid.NewGuid().ToString("N");
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + name))
            key.SetValue("v", "1");
        try
        {
            using var opened = RegistryCleanupScanner.Open(@"HKCU\Software\Classes\" + name, writable: true);
            Assert.NotNull(opened);
            Assert.Equal("1", opened!.GetValue("v"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + name, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void OpenBelowLinkSafe_returns_null_when_a_middle_segment_is_a_real_link()
    {
        using (var middle = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Mid"))
        {
            PlantMarker(middle);
            middle.CreateSubKey("Leaf").Dispose();
        }

        using var start = Registry.CurrentUser.OpenSubKey(_relativeRoot)!;

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Mid";
        Assert.Null(RegistryBackupService.OpenBelowLinkSafe(start.Handle, @"Mid\Leaf", writable: true, logging: null));

        // The same walk without the verdict: the marker alone is a decoy.
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        using var opened = RegistryBackupService.OpenBelowLinkSafe(start.Handle, @"Mid\Leaf", writable: true, logging: null);
        Assert.NotNull(opened);
    }

    [Fact]
    public void An_HKLM_path_is_split_at_its_longest_root_on_a_segment_boundary()
    {
        const string run = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        Assert.Equal((run, string.Empty), RegistryCleanupScanner.SplitAtHklmRoot(run));
        Assert.Equal((run, "Updater"), RegistryCleanupScanner.SplitAtHklmRoot(run + @"\Updater"));
        Assert.Equal((run, @"A\B"), RegistryCleanupScanner.SplitAtHklmRoot(run.ToUpperInvariant() + @"\A\B"));

        // RunOnce is its own root, never a child of Run.
        Assert.Equal((run + "Once", "X"), RegistryCleanupScanner.SplitAtHklmRoot(run + @"Once\X"));

        // Outside every root, or merely sharing a prefix with one.
        Assert.Null(RegistryCleanupScanner.SplitAtHklmRoot(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion"));
        Assert.Null(RegistryCleanupScanner.SplitAtHklmRoot(run + "X"));
        Assert.Null(RegistryCleanupScanner.SplitAtHklmRoot(@"HKLM\SYSTEM\CurrentControlSet"));
        Assert.Null(RegistryCleanupScanner.SplitAtHklmRoot(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\X"));
    }

    [Fact]
    public void A_refused_link_is_logged_through_the_scanner_open_and_the_walk()
    {
        using (var middle = Registry.CurrentUser.CreateSubKey(_relativeRoot + @"\Mid"))
        {
            PlantMarker(middle);
            middle.CreateSubKey("Leaf").Dispose();
        }

        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) => name == "Mid";

        var lines = new List<string>();
        Assert.Null(RegistryCleanupScanner.Open(
            @"HKCU\" + _relativeRoot + @"\Mid\Leaf", writable: true, new LoggingService(lines.Add)));
        Assert.Contains(lines, line => line.Contains("is a registry link; not following it", StringComparison.Ordinal));

        // The HKLM display root of the same walk; the unelevated test cannot plant a marker under HKLM.
        var hklmLines = new List<string>();
        using var start = Registry.CurrentUser.OpenSubKey(_relativeRoot)!;
        Assert.Null(RegistryBackupService.OpenBelowLinkSafe(
            start.Handle, @"Mid\Leaf", writable: true, logging: new LoggingService(hklmLines.Add), displayRoot: "HKLM"));
        Assert.Contains(hklmLines, line => line.Contains("Registry cleanup: 'HKLM\\Mid'", StringComparison.Ordinal));
    }

    /// <summary>The WOW6432Node App Paths root is a real registry link on 64-bit Windows; the walk below it,
    /// started from the plainly opened root, must still reach its children.</summary>
    [Fact]
    public void The_link_safe_walk_reaches_a_child_below_the_WOW6432Node_App_Paths_root()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths");
        var child = root?.GetSubKeyNames().FirstOrDefault();
        if (child is null)
            return; // nothing below the root on this machine to walk to

        using var opened = RegistryBackupService.OpenBelowLinkSafe(root!.Handle, child, writable: false, logging: null, displayRoot: "HKLM");
        Assert.NotNull(opened);
    }

    /// <summary>Every HKCU root the scanners work on, where it exists, must open through the link-safe walk,
    /// or its category would silently scan nothing and its backup would fail closed.</summary>
    [Fact]
    public void Every_existing_HKCU_cleanup_root_opens_link_safe()
    {
        foreach (var category in Enum.GetValues<RegistryCleanupCategory>())
        {
            foreach (var root in RegistryCleanupScanner.RootKeys(category).Where(r => r.StartsWith(@"HKCU\", StringComparison.Ordinal)))
            {
                using var plain = Registry.CurrentUser.OpenSubKey(root[5..]);
                if (plain is null)
                    continue;

                using var safe = RegistryCleanupScanner.Open(root, writable: false);
                Assert.True(safe is not null, $"{root} exists but did not open link-safe");
            }
        }
    }
}
