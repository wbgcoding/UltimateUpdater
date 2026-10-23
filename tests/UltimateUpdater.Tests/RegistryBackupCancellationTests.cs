using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class RegistryBackupCancellationTests : IDisposable
{
    private readonly string _relativeRoot = @"Software\WuBackupCancel" + Guid.NewGuid().ToString("N");

    public RegistryBackupCancellationTests()
    {
        for (var i = 0; i < 3; i++)
            Registry.CurrentUser.CreateSubKey($@"{_relativeRoot}\k{i}\inner").Dispose();
    }

    public void Dispose()
    {
        RegistryBackupService.RealLinkDetectorForTest.Value = null;
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(
        SafeRegistryHandle hKey, string lpValueName, int reserved, uint dwType, byte[] lpData, int cbData);

    [Fact]
    public void A_cancelled_token_stops_the_export_of_a_key_tree()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot, null, cts.Token));
    }

    /// <summary>The cancel arrives while the walk is already inside the tree (the link detector runs when
    /// k1 is opened, after the root's own check passed), so only a check per key can stop it: a single
    /// check at the top of the export would let the whole tree through.</summary>
    [Fact]
    public void A_cancel_that_arrives_mid_walk_stops_the_export_at_the_next_key()
    {
        using (var k1 = Registry.CurrentUser.OpenSubKey($@"{_relativeRoot}\k1", writable: true)!)
        {
            var data = Encoding.Unicode.GetBytes(@"\Registry\Machine" + "\0");
            Assert.Equal(0, RegSetValueExW(k1.Handle, "SymbolicLinkValue", 0, 6, data, data.Length));
        }

        using var cts = new CancellationTokenSource();
        var detectorRanFor = new List<string>();
        RegistryBackupService.RealLinkDetectorForTest.Value = (_, name, _) =>
        {
            detectorRanFor.Add(name);
            cts.Cancel();
            return false; // a decoy: the key is exported as an ordinary one, only the cancel matters
        };

        Assert.Throws<OperationCanceledException>(
            () => RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot, null, cts.Token));
        Assert.Equal(["k1"], detectorRanFor);
    }

    [Fact]
    public void Without_a_cancellation_the_whole_tree_is_exported()
    {
        var text = RegistryBackupService.ExportKeyTreeText(@"HKCU\" + _relativeRoot);

        Assert.Contains(@"\k2\inner]", text);
    }
}
