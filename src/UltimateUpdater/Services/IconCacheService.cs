using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace UltimateUpdater.Services;

/// <summary>
/// Finds the icon an update should be shown with. The app's OWN icon always wins:
/// the Uninstall entry's DisplayIcon first, then the main executable in its install directory, then
/// whatever the source itself keeps (Steam stores an icon per game). Only when none of that exists
/// does the row fall back to a lettered tile.
/// </summary>
public sealed class IconCacheService
{
    private const int MaxCacheAgeDays = 30;
    private const long MaxCacheBytes = 50 * 1024 * 1024;

    /// <summary>
    /// The in-memory cache (unlike the on-disk one above) had no cap at all, including for negative
    /// (null) results, so it grew for the life of the process. A few hundred is comfortably above one
    /// scan's row count, so a hit rate is kept without an unbounded cache.
    /// </summary>
    private const int MaxMemoryCacheEntries = 300;

    /// <summary>Shorter names match far too much ("R", "Go"), so partial matching starts here.</summary>
    private const int MinimumPartialMatchLength = 4;

    /// <summary>
    /// CleanupCache only ran once, from the constructor: so a single long-running session could write
    /// past the 50 MB/30-day cap and not be trimmed back until the next app start. Re-running it every
    /// this many disk writes keeps the cap enforced within a session too, without paying its directory
    /// enumeration cost on every single icon write.
    /// </summary>
    private const int CleanupEveryNWrites = 100;

    private int _writesSinceCleanup;

    /// <summary>Executables that live beside an app but are never the app itself.</summary>
    private static readonly string[] NotTheApplication =
    [
        "unins", "uninstall", "setup", "install", "vcredist", "vc_redist", "dotnet",
        "crashpad", "crashhandler", "crashreporter", "updater", "update", "helper", "notification",
    ];

    /// <summary>Concurrent: icon resolution runs on the thread pool and several batches can overlap.</summary>
    private readonly ConcurrentDictionary<string, BitmapSource?> _memoryCache = new();

    /// <summary>Insertion order for the eviction below: a plain FIFO cap, not a true LRU.</summary>
    private readonly ConcurrentQueue<string> _memoryCacheOrder = new();

    /// <summary>
    /// The Uninstall keys are read once rather than per row. Walking both hives for every one of a
    /// few hundred rows was tens of thousands of registry opens per scan.
    /// </summary>
    private readonly Lazy<IReadOnlyList<InstalledApp>> _installedApps;

    private readonly LoggingService _logging;

    private sealed record InstalledApp(string Name, string? IconValue, string? InstallLocation);

    /// <summary>A file to take an icon from: either an icon resource at an index, or a plain image.</summary>
    private sealed record IconSource(string Path, int Index, bool IsImageFile);

    public IconCacheService(LoggingService logging)
    {
        _logging = logging;
        _installedApps = new Lazy<IReadOnlyList<InstalledApp>>(ReadInstalledApps);
        CleanupCache();
    }

    /// <summary>
    /// Returns null (never throws) if no icon could be resolved; the caller shows a fallback glyph.
    /// <paramref name="executableHint"/> is the row's open target where that is a real executable
    /// (the launcher rows): its icon is used only when nothing better was found.
    /// </summary>
    public BitmapSource? GetIcon(string sourceId, string itemId, string displayName, string? executableHint = null)
    {
        var cacheKey = SanitizeKey($"{sourceId}_{itemId}");
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var diskPath = Path.Combine(AppPaths.IconCacheDirectory, cacheKey + ".png");
        var resolved = TryLoadFromDisk(diskPath) ?? ExtractAndCache(sourceId, itemId, displayName, executableHint, diskPath);

        _memoryCache[cacheKey] = resolved;
        _memoryCacheOrder.Enqueue(cacheKey);
        TrimMemoryCache();
        return resolved;
    }

    /// <summary>Oldest-first eviction back down to the cap. Cheap, and good enough for a soft bound.</summary>
    private void TrimMemoryCache()
    {
        while (_memoryCache.Count > MaxMemoryCacheEntries && _memoryCacheOrder.TryDequeue(out var oldest))
            _memoryCache.TryRemove(oldest, out _);
    }

    private BitmapSource? ExtractAndCache(string sourceId, string itemId, string displayName, string? executableHint, string diskPath)
    {
        var source = FindIconSource(sourceId, itemId, displayName, executableHint);
        if (source is null)
            return null;

        var bitmap = source.IsImageFile ? LoadImageFile(source.Path) : ExtractIconBitmap(source);
        if (bitmap is null)
            return null;

        try
        {
            SaveToDisk(bitmap, diskPath);

            // A separate test-then-reset let two threads crossing the threshold at once (e.g. 100 and
            // 101) both pass and both run CleanupCache concurrently over the same folder. The modulo is
            // exact and atomic in one step: only the single increment that lands exactly on a multiple of
            // CleanupEveryNWrites triggers a cleanup, no separate reset needed.
            if (System.Threading.Interlocked.Increment(ref _writesSinceCleanup) % CleanupEveryNWrites == 0)
                CleanupCache();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Icon cache write failed: {ex.Message}");
        }

        return bitmap;
    }

    /// <summary>The app's own icon first, its own executable second, the source's own store last.</summary>
    private IconSource? FindIconSource(string sourceId, string itemId, string displayName, string? executableHint)
    {
        var app = MatchInstalledApp(displayName);

        if (app is not null)
        {
            if (ParseIconValue(app.IconValue) is { } declared)
                return declared;

            if (MainExecutableIn(app.InstallLocation, displayName) is { } executable)
                return new IconSource(executable, 0, IsImageFile: false);
        }

        // Games are not in the Uninstall registry under their own name. Steam keeps their icons.
        if (sourceId == "steam" && SteamGameIcon(itemId) is { } steamIcon)
            return steamIcon;

        // A launcher row's open target is the client executable itself; its icon is exactly the row's
        // and beats the flat brand-mark tile the row would otherwise fall back to.
        return IconSourceFromExecutable(executableHint);
    }

    /// <summary>An icon straight from an executable (or image) path, when the path is real and rooted.</summary>
    private static IconSource? IconSourceFromExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path))
            return null;

        var isImage = Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".bmp";
        return new IconSource(path, 0, isImage);
    }

    /// <summary>Handles the "C:\path\app.exe,3" form that DisplayIcon commonly takes.</summary>
    private static IconSource? ParseIconValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim().Trim('"');
        var index = 0;

        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out var parsed))
        {
            index = parsed;
            text = text[..comma].Trim().Trim('"');
        }

        if (!File.Exists(text))
            return null;

        var isImage = Path.GetExtension(text) is ".png" or ".jpg" or ".jpeg" or ".bmp";
        return new IconSource(text, index, isImage);
    }

    /// <summary>
    /// The main executable of an installed app, when its Uninstall entry names an install directory
    /// but no icon. Picks by name first, then by size: the main binary is reliably the largest.
    /// </summary>
    private static string? MainExecutableIn(string? installLocation, string displayName)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        try
        {
            var executables = new DirectoryInfo(installLocation)
                .EnumerateFiles("*.exe", SearchOption.TopDirectoryOnly)
                .Where(f => !NotTheApplication.Any(n =>
                    f.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (executables.Count == 0)
                return null;

            var wanted = TextNormalization.AlphanumericLower(displayName);

            var byName = executables.FirstOrDefault(f =>
            {
                var stem = TextNormalization.AlphanumericLower(Path.GetFileNameWithoutExtension(f.Name));
                return stem.Length >= 3 && (wanted.Contains(stem) || stem.Contains(wanted));
            });

            return (byName ?? executables.MaxBy(f => f.Length))?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Steam keeps a per-game icon in its own cache; nothing else on the machine knows about it.</summary>
    private static IconSource? SteamGameIcon(string appId)
    {
        var steamPath = Sources.SteamSource.FindSteamPath();
        if (steamPath is null || !appId.All(char.IsDigit))
            return null;

        var cache = Path.Combine(steamPath, "appcache", "librarycache");

        try
        {
            // Older layout: <appid>_icon.jpg; newer layout: a folder per app id.
            var flat = Path.Combine(cache, $"{appId}_icon.jpg");
            if (File.Exists(flat))
                return new IconSource(flat, 0, IsImageFile: true);

            var folder = Path.Combine(cache, appId);
            if (!Directory.Exists(folder))
                return null;

            var file = new DirectoryInfo(folder)
                .EnumerateFiles("*icon*")
                .FirstOrDefault(f => f.Extension is ".jpg" or ".png");

            return file is null ? null : new IconSource(file.FullName, 0, IsImageFile: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Exact name first, then prefix, then a plain substring. Registry names are usually
    /// vendor-prefixed ("Mozilla Firefox 130.0 (x64 de)") while package ids are the bare product.
    /// </summary>
    private InstalledApp? MatchInstalledApp(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var apps = _installedApps.Value;

        var exact = apps.FirstOrDefault(a => string.Equals(a.Name, displayName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        return Best(apps, displayName, prefixOnly: true) ?? Best(apps, displayName, prefixOnly: false);
    }

    private static InstalledApp? Best(IReadOnlyList<InstalledApp> apps, string displayName, bool prefixOnly) =>
        apps.Where(a => IsPartialMatch(a.Name, displayName, prefixOnly))
            .OrderByDescending(a => a.Name.Length)
            .FirstOrDefault();

    private static bool IsPartialMatch(string candidate, string displayName, bool prefixOnly)
    {
        if (Math.Min(candidate.Length, displayName.Length) < MinimumPartialMatchLength)
            return false;

        if (candidate.StartsWith(displayName, StringComparison.OrdinalIgnoreCase) ||
            displayName.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !prefixOnly &&
               (candidate.Contains(displayName, StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every entry is kept, with or without an icon: the install directory is a second chance.</summary>
    private static List<InstalledApp> ReadInstalledApps()
    {
        var apps = new List<InstalledApp>();
        var registry = Sources.WindowsRegistryView.Instance;

        // The shared list of uninstall roots, so the icon lookup sees exactly the programs every other
        // registry walk in the app sees. The view never throws: a hive we may not read is simply one
        // fewer source of icons.
        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var subKeyName in registry.SubKeyNames(root))
            {
                var entry = $@"{root}\{subKeyName}";
                if (registry.Value(entry, "DisplayName") is not { } name || string.IsNullOrWhiteSpace(name))
                    continue;

                apps.Add(new InstalledApp(
                    name,
                    registry.Value(entry, "DisplayIcon"),
                    registry.Value(entry, "InstallLocation")));
            }
        }

        return apps;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, int count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// ExtractIconEx rather than ExtractAssociatedIcon: it honours the index in "app.exe,3", and it
    /// reads .ico and .dll files, which DisplayIcon points at just as often as it points at an .exe.
    /// </summary>
    private static BitmapSource? ExtractIconBitmap(IconSource source)
    {
        var large = new IntPtr[1];

        try
        {
            if (ExtractIconEx(source.Path, source.Index, large, null, 1) <= 0 || large[0] == IntPtr.Zero)
                return FromAssociatedIcon(source.Path);

            try
            {
                var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    large[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DestroyIcon(large[0]);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or ExternalException)
        {
            return null;
        }
    }

    private static BitmapSource? FromAssociatedIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;

            var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }

    internal static BitmapSource? LoadImageFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            // The image belongs to the app being shown, not to us: no icon, never a delete.
            return null;
        }
    }

    internal static BitmapSource? TryLoadFromDisk(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            TrySetLastAccess(path);
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException)
        {
            // Our own cache file, but not a decodable image (truncated write, disk damage). Dropping it
            // lets the next lookup re-resolve the icon instead of failing on the same bytes forever.
            TryDeletePath(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Only feeds the age-based cleanup, so failing to stamp it is not worth reporting.</summary>
    private static void TrySetLastAccess(string path)
    {
        try
        {
            File.SetLastAccessTime(path, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort housekeeping only.
        }
    }

    /// <summary>
    /// Written beside the target and moved into place: encoding a PNG straight into the cache
    /// path leaves a partially written file for as long as it takes, and another row asking for the same
    /// icon at that moment loaded exactly that: a truncated image, cached in memory for the rest of the
    /// session. A move is atomic enough that a reader sees either the old file or the complete new one.
    /// </summary>
    private static void SaveToDisk(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        AtomicFile.Write(path, encoder.Save);
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort housekeeping only.
        }
    }

    /// <summary>Runs from the constructor and periodically from a write (see
    /// <see cref="CleanupEveryNWrites"/>), so it must not throw whatever the cache directory looks
    /// like, and must tolerate running concurrently with itself (TryDelete already swallows a file
    /// that is already gone).</summary>
    private void CleanupCache()
    {
        try
        {
            var directory = new DirectoryInfo(AppPaths.IconCacheDirectory);
            var cutoff = DateTime.Now.AddDays(-MaxCacheAgeDays);

            foreach (var file in FilesOlderThan(directory.GetFiles("*.png"), cutoff))
                TryDelete(file);

            foreach (var file in FilesOverByteBudget(directory.GetFiles("*.png"), MaxCacheBytes))
                TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logging.Warn($"Icon cache cleanup failed: {ex.Message}");
        }
    }

    /// <summary>Pure selection logic, split out so the disk-cap eviction order can be unit-tested without
    /// touching the real cache directory.</summary>
    internal static IEnumerable<FileInfo> FilesOlderThan(IEnumerable<FileInfo> files, DateTime cutoff) =>
        files.Where(f => f.LastAccessTime < cutoff);

    /// <summary>Oldest-accessed first, until the running total (starting from ALL files, not just the
    /// ones returned) is back under <paramref name="maxBytes"/>: a soft LRU-by-access-time bound.</summary>
    internal static IEnumerable<FileInfo> FilesOverByteBudget(IEnumerable<FileInfo> files, long maxBytes)
    {
        var ordered = files.OrderBy(f => f.LastAccessTime).ToList();
        var totalBytes = ordered.Sum(f => f.Length);

        foreach (var file in ordered)
        {
            if (totalBytes <= maxBytes)
                yield break;
            totalBytes -= file.Length;
            yield return file;
        }
    }

    /// <summary>One file we may not delete must not abort the whole sweep.</summary>
    private static void TryDelete(FileInfo file) => FileDeletion.TryDelete(file.FullName);

    private static string SanitizeKey(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = key.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
