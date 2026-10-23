using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class RecycleBinSkippedIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-recyclebin-skip-" + Guid.NewGuid());
    private readonly string _ownSid = RecycleBinScanner.TryGetOwnSid()!;
    private readonly string _binFolder;

    public RecycleBinSkippedIndexTests()
    {
        Assert.NotNull(_ownSid);
        _binFolder = RecycleBinScanner.OwnBinFolder(_root, _ownSid);
        Directory.CreateDirectory(_binFolder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] ValidIndex(string originalPath)
    {
        var pathBytes = System.Text.Encoding.Unicode.GetBytes(originalPath + "\0");
        var buffer = new byte[28 + pathBytes.Length];
        BitConverter.GetBytes(2L).CopyTo(buffer, 0);
        BitConverter.GetBytes(1L).CopyTo(buffer, 8);
        BitConverter.GetBytes(DateTimeOffset.UtcNow.UtcDateTime.ToFileTimeUtc()).CopyTo(buffer, 16);
        BitConverter.GetBytes(originalPath.Length + 1).CopyTo(buffer, 24);
        pathBytes.CopyTo(buffer, 28);
        return buffer;
    }

    [Fact]
    public void An_unparseable_index_is_counted_but_an_orphan_without_content_is_not()
    {
        File.WriteAllText(Path.Combine(_binFolder, "$RGOOD01.txt"), "x");
        File.WriteAllBytes(Path.Combine(_binFolder, "$IGOOD01.txt"), ValidIndex(@"C:\a.txt"));

        File.WriteAllText(Path.Combine(_binFolder, "$RBAD0001.txt"), "x");
        File.WriteAllBytes(Path.Combine(_binFolder, "$IBAD0001.txt"), [1, 2, 3]);

        File.WriteAllBytes(Path.Combine(_binFolder, "$IORPH001.txt"), ValidIndex(@"C:\orphan.txt"));

        Assert.True(RecycleBinScanner.TryEnumerate(_root, _ownSid, CancellationToken.None, out var items, out var skipped));

        Assert.Single(items);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void A_clean_bin_reports_nothing_skipped()
    {
        File.WriteAllText(Path.Combine(_binFolder, "$RGOOD01.txt"), "x");
        File.WriteAllBytes(Path.Combine(_binFolder, "$IGOOD01.txt"), ValidIndex(@"C:\a.txt"));

        Assert.True(RecycleBinScanner.TryEnumerate(_root, _ownSid, CancellationToken.None, out _, out var skipped));
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void A_drive_with_a_skipped_index_never_takes_the_shell_path_even_when_every_row_is_selected()
    {
        CleanupEntry[] entries =
        [
            new(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAAA.txt", "a.txt", 100, 1,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, CleanupEntryType.Other, @"C:\Users\x\a.txt"),
        ];
        RecycleBinDriveSummary[] drives =
            [new(@"C:\", @"C:\", 1, 1, 100, 100, DetailsAvailable: true, SkippedIndexCount: 1)];

        var plan = Assert.Single(CleanupService.PlanRecycleBinEmpty(entries, drives, null));

        Assert.False(plan.UseShellEmpty);
        Assert.Equal(entries.Select(e => e.Path), plan.Entries.Select(e => e.Path));
    }
}
