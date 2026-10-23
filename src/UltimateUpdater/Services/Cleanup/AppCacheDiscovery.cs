using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Finds cache/log-shaped folders belonging to programs this app has no catalogue entry for, no fixed
/// folder paths, only detection via environment placeholders like LOCALAPPDATA. Unlike
/// <see cref="CleanupCatalog"/>'s fixed table, nothing here is a
/// literal path: every root comes from <see cref="Environment.GetFolderPath"/>, and every candidate folder
/// underneath it is discovered by walking the filesystem and judged the exact same way the fixed catalogue
/// already is, <see cref="CleanupSafety.IsSafeToEmpty"/> is the one gate, run again here AND again inside
/// <see cref="CleanupService"/> right before anything is touched, so a discovery bug can narrow what gets
/// offered but never widen what gets deleted.
/// Precedent for dynamic discovery already exists in this file's sibling, <c>CleanupCatalog.
/// OtherUsersTempDirectories</c>, which walks the Users folder the same way, this is that pattern applied
/// more broadly, not a new one.
/// </summary>
public static class AppCacheDiscovery
{
    /// <summary>Vendor\App\CacheFolder is the deepest shape expected; deeper than that risks
    /// wandering into a program's real data by accident.</summary>
    private const int MaxDepth = 3;

    private static IReadOnlyList<string> Roots => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    }.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every folder under a %LOCALAPPDATA%/%APPDATA%/%PROGRAMDATA%-style root that both looks
    /// like a cache/log location by name AND passes the full safety gate.</summary>
    public static IReadOnlyList<string> DiscoverCacheDirectories(CancellationToken cancellationToken = default)
    {
        var found = new List<string>();

        foreach (var root in Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(root, root, depth: 0, found, cancellationToken);
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Leaf names a folder directly under a root may have to count as a cache or log location.
    /// A vendor or app folder there only ENDS in "cache" by chance ("Geocache", "SmartCache") and holds the
    /// program's real data, so the loose suffix rule is not applied at that level.</summary>
    private static readonly string[] ExactTopLevelNames = ["Cache", "Caches", "Temp", "Tmp", "Logs", "CrashDumps"];

    /// <summary>Whether the folder at <paramref name="depth"/> below the root (1 = directly under it) looks like
    /// a cache or log location by name. Directly under the root only the exact names count; deeper, the
    /// looser rule applies, but not beneath a top-level folder that itself only matched that loose rule.</summary>
    internal static bool NameQualifies(string relativePath, int depth)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        if (depth <= 1)
            return segments.Length == 1 && ExactTopLevelNames.Contains(segments[0], StringComparer.OrdinalIgnoreCase);

        // Without this, a path like Geocache plus any child would match through its first segment and offer the
        // whole program folder one level down.
        if (!ExactTopLevelNames.Contains(segments[0], StringComparer.OrdinalIgnoreCase)
            && CleanupSafety.LooksLikeTempOrCacheLocation(segments[0]))
            return false;

        return CleanupSafety.LooksLikeTempOrCacheLocation(relativePath);
    }

    private static void Walk(string root, string directory, int depth, List<string> found, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth || !Directory.Exists(directory))
            return;

        cancellationToken.ThrowIfCancellationRequested();

        // A folder qualifying at this depth is collected but NOT walked further, its own contents are
        // what gets emptied, not treated as more vendor/app folders to recurse into.
        // The cache-like name has to be BELOW the root: IsSafeToEmpty looks at every segment of the full path,
        // so a profile folder that happens to be named like one (Windows' temporary profile folder TEMP, an
        // account called "tmp" or "Logs") would otherwise qualify every folder of AppData at depth 1.
        if (depth > 0
            && NameQualifies(Path.GetRelativePath(root, directory), depth)
            && CleanupSafety.IsSafeToEmpty(directory))
        {
            found.Add(directory);
            return;
        }

        // Nothing below the deepest level can qualify, so its children are not even listed.
        if (depth == MaxDepth)
            return;

        foreach (var child in SafeEnumerate.Directories(directory))
            Walk(root, child, depth + 1, found, cancellationToken);
    }
}
