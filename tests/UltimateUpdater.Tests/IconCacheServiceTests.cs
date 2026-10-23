using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The on-disk icon cache's 50 MB/30-day cap (<see cref="IconCacheService"/>'s eviction selection,
/// split out as pure static helpers precisely so this does not need to touch the real, shared cache
/// directory under <see cref="AppPaths.IconCacheDirectory"/>).
/// </summary>
public class IconCacheServiceTests
{
    private static FileInfo TempFile(string directory, string name, long bytes, DateTime lastAccess)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastAccessTime(path, lastAccess);
        return new FileInfo(path);
    }

    [Fact]
    public void Files_older_than_the_cutoff_are_selected_and_newer_ones_are_not()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var now = DateTime.Now;
            var old = TempFile(dir, "old.png", 100, now.AddDays(-31));
            var fresh = TempFile(dir, "fresh.png", 100, now.AddDays(-1));

            var selected = IconCacheService.FilesOlderThan([old, fresh], now.AddDays(-30)).ToList();

            Assert.Contains(old, selected);
            Assert.DoesNotContain(fresh, selected);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Byte_budget_eviction_removes_oldest_accessed_files_first_until_under_budget()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var now = DateTime.Now;
            // Three 20-byte files, 50-byte budget: only the single oldest must be evicted to get to 40.
            var oldest = TempFile(dir, "oldest.png", 20, now.AddMinutes(-30));
            var middle = TempFile(dir, "middle.png", 20, now.AddMinutes(-20));
            var newest = TempFile(dir, "newest.png", 20, now.AddMinutes(-10));

            var evicted = IconCacheService.FilesOverByteBudget([middle, newest, oldest], maxBytes: 50).ToList();

            Assert.Equal([oldest], evicted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Byte_budget_eviction_can_remove_several_files_when_needed_and_stops_once_under_budget()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var now = DateTime.Now;
            var a = TempFile(dir, "a.png", 20, now.AddMinutes(-30));
            var b = TempFile(dir, "b.png", 20, now.AddMinutes(-20));
            var c = TempFile(dir, "c.png", 20, now.AddMinutes(-10));

            // Total 60, budget 25: evicting "a" alone leaves 40 (still over), evicting "b" too leaves 20
            // (under), "c" must survive.
            var evicted = IconCacheService.FilesOverByteBudget([a, b, c], maxBytes: 25).ToList();

            Assert.Equal([a, b], evicted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Byte_budget_eviction_evicts_nothing_when_already_under_budget()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var file = TempFile(dir, "small.png", 10, DateTime.Now);

            var evicted = IconCacheService.FilesOverByteBudget([file], maxBytes: 1024).ToList();

            Assert.Empty(evicted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] ValidPng()
    {
        var pixels = new byte[32 * 32 * 4];
        Array.Fill(pixels, (byte)0x80);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(
            32, 32, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void A_garbage_cache_file_is_deleted_and_reported_as_no_icon()
    {
        WpfHarness.OnUiThread(() =>
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Path.Combine(dir, "winget_garbage.png");
                File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

                var loaded = IconCacheService.TryLoadFromDisk(path);

                Assert.Null(loaded);
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
    }

    [Fact]
    public void A_truncated_png_in_the_cache_is_deleted_and_reported_as_no_icon()
    {
        WpfHarness.OnUiThread(() =>
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var png = ValidPng();
                var path = Path.Combine(dir, "winget_truncated.png");
                File.WriteAllBytes(path, png[..(png.Length / 2)]);

                var loaded = IconCacheService.TryLoadFromDisk(path);

                Assert.Null(loaded);
                Assert.False(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
    }

    [Fact]
    public void A_valid_cache_file_still_loads_and_stays()
    {
        WpfHarness.OnUiThread(() =>
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var path = Path.Combine(dir, "winget_ok.png");
                File.WriteAllBytes(path, ValidPng());

                var loaded = IconCacheService.TryLoadFromDisk(path);

                Assert.NotNull(loaded);
                Assert.True(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
    }

    /// <summary>The source image of a row is not ours: a damaged one is "no icon", never deleted.</summary>
    [Fact]
    public void A_damaged_source_image_is_not_deleted()
    {
        WpfHarness.OnUiThread(() =>
        {
            var dir = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var png = ValidPng();
                var path = Path.Combine(dir, "app-own-icon.png");
                File.WriteAllBytes(path, png[..(png.Length / 2)]);

                var loaded = IconCacheService.LoadImageFile(path);

                Assert.Null(loaded);
                Assert.True(File.Exists(path));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
    }
}
