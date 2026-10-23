using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>One candidate the leftover scanner found, not proven orphaned, only plausible (see
/// <see cref="LeftoverInstallSafety"/>). The UI must show these individually and never pre-select one.</summary>
public sealed record LeftoverCandidate(string Path, string DisplayName, long SizeBytes, DateTimeOffset NewestWriteTime);

/// <summary>
/// Finds top-level Program Files/LOCALAPPDATA\Programs/ProgramData folders that plausibly belong to an
/// uninstalled program. Only DIRECT children of the four roots are ever
/// considered, never deeper, so this can never mistake a real app's own cache subfolder for a second
/// candidate. Every root comes from <see cref="Environment.GetFolderPath"/>, same "never a literal
/// machine-specific path" rule as <see cref="AppCacheDiscovery"/>.
/// </summary>
public static class LeftoverInstallDiscovery
{
    /// <summary>Deliberately generous, a portable app someone still uses gets touched far more often
    /// than this in practice; the goal is to under-detect, never to over-detect.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(180);

    /// <summary>Bounds the "how recently was anything in here touched" scan, a leftover judgment is a
    /// heuristic, not worth walking a multi-gigabyte folder file by file for.</summary>
    private const int MaxFilesScannedPerCandidate = 2000;
    private const int MaxScanDepth = 3;

    private static IReadOnlyList<string> Roots => ProgramFilesPaths.Roots()
        .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"))
        .Append(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
        .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<LeftoverCandidate> DiscoverCandidates(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var registeredPaths = RegistryCleanupScanner.AllUninstallInstallPaths();
        var found = new List<LeftoverCandidate>();

        foreach (var root in Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                continue;

            foreach (var candidate in SafeEnumerate.Directories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var newestWrite = NewestWriteTime(candidate, cancellationToken);
                if (!LeftoverInstallSafety.IsPlausibleLeftover(candidate, registeredPaths, newestWrite, now, MinimumAge))
                    continue;

                found.Add(new LeftoverCandidate(candidate, Path.GetFileName(candidate), DirectorySizeWalker.Measure(candidate, MaxScanDepth, MaxFilesScannedPerCandidate, cancellationToken), newestWrite));
            }
        }

        return found;
    }

    /// <summary>The most recent write time of anything found within the scan bound, the folder's own
    /// timestamp counts too, so an empty folder is judged by when it was last touched, not treated as
    /// infinitely old.</summary>
    internal static DateTimeOffset NewestWriteTime(string directory, CancellationToken cancellationToken)
    {
        // Anything that stops the walk from seeing a folder in full means "recent" (MaxValue), so the
        // folder is never offered: a partial listing could hide the file that proves it is in use.
        DateTimeOffset newest;
        try
        {
            if (!Directory.Exists(directory))
                return DateTimeOffset.MaxValue; // gone since it was listed; nothing to judge

            newest = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MaxValue;
        }

        var scanned = 0;
        var listingFailed = false;
        // Hitting the file cap or the depth limit is a partial view too: the newest file may be exactly the
        // part that was not looked at, so it counts like a failed listing.
        var truncated = false;
        void OnListingError() => listingFailed = true;

        void Walk(string dir, int depth)
        {
            if (depth > MaxScanDepth || scanned >= MaxFilesScannedPerCandidate)
            {
                truncated = true;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in SafeEnumerate.Files(dir, OnListingError))
            {
                if (scanned++ >= MaxFilesScannedPerCandidate)
                {
                    truncated = true;
                    return;
                }

                try
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (written > newest.UtcDateTime)
                        newest = new DateTimeOffset(written, TimeSpan.Zero);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort housekeeping only.
                }
            }

            foreach (var sub in SafeEnumerate.Directories(dir, OnListingError))
            {
                if (scanned >= MaxFilesScannedPerCandidate)
                {
                    truncated = true;
                    return;
                }

                Walk(sub, depth + 1);
            }
        }

        Walk(directory, 0);
        return listingFailed || truncated ? DateTimeOffset.MaxValue : newest;
    }
}
