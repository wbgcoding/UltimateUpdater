using System.Text;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The <c>$I</c> index format, both versions Windows writes, plus everything a folder full of files nobody
/// controls can throw at it. The rule the whole feature rests on: a malformed index is answered with false,
/// never an exception, a single odd file must not end a recycle-bin scan.
/// </summary>
public class RecycleBinIndexParserTests
{
    private static readonly DateTimeOffset January2024 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly long January2024AsFileTime = January2024.UtcDateTime.ToFileTimeUtc();

    private static byte[] V1(long size, long fileTime, string path, int pathBufferBytes = 520)
    {
        var buffer = new byte[24 + pathBufferBytes];
        BitConverter.GetBytes(1L).CopyTo(buffer, 0);
        BitConverter.GetBytes(size).CopyTo(buffer, 8);
        BitConverter.GetBytes(fileTime).CopyTo(buffer, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(buffer, 24);
        return buffer;
    }

    private static byte[] V2(long size, long fileTime, string path, int? charCountOverride = null)
    {
        // Windows counts the terminating NUL in the character count, and writes it.
        var pathBytes = Encoding.Unicode.GetBytes(path + "\0");
        var buffer = new byte[28 + pathBytes.Length];
        BitConverter.GetBytes(2L).CopyTo(buffer, 0);
        BitConverter.GetBytes(size).CopyTo(buffer, 8);
        BitConverter.GetBytes(fileTime).CopyTo(buffer, 16);
        BitConverter.GetBytes(charCountOverride ?? (path.Length + 1)).CopyTo(buffer, 24);
        pathBytes.CopyTo(buffer, 28);
        return buffer;
    }

    [Fact]
    public void Reads_a_version_1_index_with_its_fixed_length_path_buffer()
    {
        Assert.True(RecycleBinIndexParser.TryParse(
            V1(4096, January2024AsFileTime, @"C:\Users\someone\Documents\report.docx"), out var entry));

        Assert.NotNull(entry);
        Assert.Equal(4096, entry.SizeBytes);
        Assert.Equal(@"C:\Users\someone\Documents\report.docx", entry.OriginalPath);
        Assert.Equal(January2024, entry.DeletedAt);
    }

    [Fact]
    public void Reads_a_version_2_index_with_its_counted_path()
    {
        Assert.True(RecycleBinIndexParser.TryParse(
            V2(1234567, January2024AsFileTime, @"D:\Spiele\save.dat"), out var entry));

        Assert.NotNull(entry);
        Assert.Equal(1234567, entry.SizeBytes);
        Assert.Equal(@"D:\Spiele\save.dat", entry.OriginalPath);
    }

    /// <summary>A recycled FOLDER is an index like any other, its size is the folder's total.</summary>
    [Fact]
    public void Reads_a_recycled_folder()
    {
        Assert.True(RecycleBinIndexParser.TryParse(V2(0, January2024AsFileTime, @"E:\Projekte\alt"), out var entry));

        Assert.NotNull(entry);
        Assert.Equal(@"E:\Projekte\alt", entry.OriginalPath);
        Assert.Equal(0, entry.SizeBytes);
    }

    [Fact]
    public void A_unicode_path_survives_intact()
    {
        Assert.True(RecycleBinIndexParser.TryParse(
            V2(10, January2024AsFileTime, @"C:\Benutzer\Ordner mit Umlauten äöü\Größe.txt"), out var entry));

        Assert.Equal(@"C:\Benutzer\Ordner mit Umlauten äöü\Größe.txt", entry?.OriginalPath);
    }

    [Fact]
    public void An_unknown_version_is_refused()
    {
        var index = V1(10, January2024AsFileTime, @"C:\x\y.txt");
        BitConverter.GetBytes(3L).CopyTo(index, 0);

        Assert.False(RecycleBinIndexParser.TryParse(index, out var entry));
        Assert.Null(entry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(23)]
    [InlineData(100)]
    public void A_truncated_file_is_refused(int keptBytes)
    {
        var index = V1(10, January2024AsFileTime, @"C:\x\y.txt");

        Assert.False(RecycleBinIndexParser.TryParse(index.AsSpan(0, keptBytes), out var entry));
        Assert.Null(entry);
    }

    /// <summary>The version-1 path buffer is a FIXED 520 bytes, a file that ends inside it is not one.</summary>
    [Fact]
    public void A_version_1_index_with_a_short_path_buffer_is_refused()
    {
        Assert.False(RecycleBinIndexParser.TryParse(
            V1(10, January2024AsFileTime, @"C:\x\y.txt", pathBufferBytes: 100), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    [InlineData(99999)]
    public void A_version_2_character_count_that_does_not_fit_the_file_is_refused(int charCount)
    {
        Assert.False(RecycleBinIndexParser.TryParse(
            V2(10, January2024AsFileTime, @"C:\x\y.txt", charCountOverride: charCount), out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void An_impossible_size_or_timestamp_is_refused()
    {
        Assert.False(RecycleBinIndexParser.TryParse(V2(-1, January2024AsFileTime, @"C:\x\y.txt"), out _));
        Assert.False(RecycleBinIndexParser.TryParse(V2(10, -1, @"C:\x\y.txt"), out _));
        Assert.False(RecycleBinIndexParser.TryParse(V2(10, long.MaxValue, @"C:\x\y.txt"), out _));
    }

    /// <summary>The scan adds every entry's size with a checked sum, so two forged sizes near the limit would
    /// end the whole scan with an overflow. No real file comes near a petabyte.</summary>
    [Fact]
    public void A_size_no_disk_could_hold_is_refused()
    {
        Assert.False(RecycleBinIndexParser.TryParse(V2(long.MaxValue / 2 + 1, January2024AsFileTime, @"C:\x\y.txt"), out _));
        Assert.False(RecycleBinIndexParser.TryParse(V1(1L << 50, January2024AsFileTime, @"C:\x\y.txt"), out _));
        Assert.True(RecycleBinIndexParser.TryParse(V2((1L << 50) - 1, January2024AsFileTime, @"C:\x\y.txt"), out _));
    }

    /// <summary>Random bytes behind a plausible header decode into a string, but not into a path Windows
    /// could ever have written, which is exactly what tells one from a real index file.</summary>
    [Fact]
    public void Garbage_behind_a_valid_header_is_refused()
    {
        var index = V1(10, January2024AsFileTime, string.Empty);
        for (var i = 24; i < index.Length; i++)
            index[i] = 0xFF;

        Assert.False(RecycleBinIndexParser.TryParse(index, out _));

        // An empty path buffer is just as impossible.
        Assert.False(RecycleBinIndexParser.TryParse(V1(10, January2024AsFileTime, string.Empty), out _));

        // A relative path is not what a recycle-bin index holds.
        Assert.False(RecycleBinIndexParser.TryParse(V2(10, January2024AsFileTime, @"..\x\y.txt"), out _));
    }

    /// <summary>Whatever the input, the answer is a bool, this is the promise the scanner relies on.</summary>
    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(4711);
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var bytes = new byte[random.Next(0, 700)];
            random.NextBytes(bytes);
            RecycleBinIndexParser.TryParse(bytes, out _);
        }
    }
}
