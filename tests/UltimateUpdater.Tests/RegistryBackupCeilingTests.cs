using System.IO;
using Microsoft.Win32;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The registry backup runs elevated over keys the unprivileged user writes, so neither one value
/// nor the whole export may grow without a ceiling; past either the backup fails and nothing is deleted.</summary>
public class RegistryBackupCeilingTests : IDisposable
{
    private readonly string _relativeRoot = @"Software\WuBackupCeiling" + Guid.NewGuid().ToString("N");
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "wu-regceiling-" + Guid.NewGuid());

    public RegistryBackupCeilingTests() => Registry.CurrentUser.CreateSubKey(_relativeRoot).Dispose();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_relativeRoot, throwOnMissingSubKey: false);
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string RootPath => @"HKCU\" + _relativeRoot;

    [Fact]
    public void A_value_exactly_at_the_ceiling_fits_and_one_byte_more_does_not()
    {
        RegistryBackupService.EnsureValueFits(RegistryBackupService.MaxValueBytes, "v", @"HKCU\k");

        var ex = Assert.Throws<IOException>(
            () => RegistryBackupService.EnsureValueFits(RegistryBackupService.MaxValueBytes + 1L, "v", @"HKCU\k"));
        Assert.Contains(@"HKCU\k", ex.Message);
    }

    [Fact]
    public void An_export_total_over_the_ceiling_throws_and_one_at_it_does_not()
    {
        RegistryBackupService.EnsureExportFits(RegistryBackupService.MaxExportBytes, @"HKCU\k", RegistryBackupService.MaxExportBytes);

        var ex = Assert.Throws<IOException>(() => RegistryBackupService.EnsureExportFits(
            RegistryBackupService.MaxExportBytes + 1, @"HKCU\k", RegistryBackupService.MaxExportBytes));
        Assert.Contains(@"HKCU\k", ex.Message);
    }

    [Fact]
    public async Task A_real_oversized_value_fails_the_backup_and_writes_no_backup_file()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot))
        {
            key.SetValue("small", "fine");
            key.SetValue("huge", new byte[RegistryBackupService.MaxValueBytes + 1], RegistryValueKind.Binary);
        }

        var ex = Assert.Throws<IOException>(() => RegistryBackupService.ExportKeyTreeTextOrNull(RootPath));
        Assert.Contains(_relativeRoot, ex.Message);

        var result = await RegistryBackupService.BackupRootsBeforeWriteAsync(
            [RootPath], _folder, 5, new LoggingService(), CancellationToken.None);
        Assert.Null(result);
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
    }

    [Fact]
    public void A_running_export_total_over_its_ceiling_fails_while_a_normal_export_is_unchanged()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_relativeRoot))
        {
            for (var i = 0; i < 20; i++)
                key.SetValue("value" + i, new string('x', 100));
        }

        var text = RegistryBackupService.ExportKeyTreeTextOrNull(RootPath);
        Assert.NotNull(text);
        Assert.Contains("value19", text);

        var ex = Assert.Throws<IOException>(
            () => RegistryBackupService.ExportKeyTreeTextOrNull(RootPath, maxExportBytes: 1000));
        Assert.Contains(_relativeRoot, ex.Message);
    }
}
