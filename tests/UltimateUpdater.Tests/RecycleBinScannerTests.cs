using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The one live-run test for the recycle-bin scanner: a scratch file this test creates itself is moved to
/// the bin, found again through the bin's own <c>$I</c> index, and then removed, nothing else on the
/// machine is read for its content or touched. Everything the parser does is unit-tested separately
/// (<see cref="RecycleBinIndexParserTests"/>); what only a real run can prove is that Windows' own files
/// look the way this reads them.
/// </summary>
public class RecycleBinScannerTests
{
    [Fact]
    public void A_recycled_file_is_found_again_with_its_original_path_size_and_date()
    {
        var ownSid = RecycleBinScanner.TryGetOwnSid();
        Assert.NotNull(ownSid);

        var content = new string('x', 1234);
        var file = Path.Combine(Path.GetTempPath(), $"wu-recyclebin-scan-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, content);
        var expectedSize = new FileInfo(file).Length;
        var recycledAt = DateTimeOffset.Now;

        Assert.True(RecycleBinDeleter.DeleteToRecycleBin(file, new LoggingService()));

        var root = Path.GetPathRoot(Path.GetFullPath(file))!;
        Assert.True(RecycleBinScanner.TryEnumerate(root, ownSid, CancellationToken.None, out var items));

        var found = items.FirstOrDefault(i => string.Equals(i.OriginalPath, file, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(found);

        try
        {
            Assert.Equal(expectedSize, found.SizeBytes);
            Assert.True(found.DeletedAt >= recycledAt.AddMinutes(-5), $"deleted at {found.DeletedAt}");
            Assert.True(found.DeletedAt <= DateTimeOffset.Now.AddMinutes(5), $"deleted at {found.DeletedAt}");
            Assert.True(File.Exists(found.ContentPath));

            // The deletion gate must accept exactly these two paths and nothing above them.
            Assert.True(CleanupSafety.IsInsideOwnRecycleBin(found.ContentPath, ownSid));
            Assert.True(CleanupSafety.IsInsideOwnRecycleBin(found.IndexPath, ownSid));
            Assert.Equal(found.IndexPath, RecycleBinScanner.IndexPathFor(found.ContentPath));
            Assert.Equal(found.ContentPath, RecycleBinScanner.ContentPathFor(found.IndexPath));
        }
        finally
        {
            // Only this test's own pair, the rest of the bin is left exactly as it was.
            try { File.Delete(found.ContentPath); } catch (IOException) { }
            try { File.Delete(found.IndexPath); } catch (IOException) { }
        }
    }

    /// <summary>A drive with no bin folder for this user is an empty result, not a failure, the caller
    /// only falls back to the shell's aggregate figure when enumeration itself broke.</summary>
    [Fact]
    public void A_drive_without_a_bin_folder_of_ours_reports_success_and_nothing_found()
    {
        var unusedRoot = DriveInfo.GetDrives()
            .Select(d => d.RootDirectory.FullName)
            .Except(["A:\\", "B:\\"], StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "C:\\";

        Assert.True(RecycleBinScanner.TryEnumerate(
            unusedRoot, "S-1-5-21-1111111111-2222222222-3333333333-4444", CancellationToken.None, out var items));
        Assert.Empty(items);
    }

    [Fact]
    public void An_unusable_drive_or_sid_is_refused_rather_than_guessed_at()
    {
        Assert.False(RecycleBinScanner.TryEnumerate("", "S-1-5-18", CancellationToken.None, out _));
        Assert.False(RecycleBinScanner.TryEnumerate(@"C:\", "", CancellationToken.None, out _));
        Assert.Null(RecycleBinScanner.ContentPathFor(@"C:\$Recycle.Bin\S-1-5-18\notanindex.txt"));
        Assert.Null(RecycleBinScanner.IndexPathFor(@"C:\$Recycle.Bin\S-1-5-18\notacontent.txt"));
    }
}
