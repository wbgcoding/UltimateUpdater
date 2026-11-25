using System.IO;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
    /// <summary>Every "~"-prefixed pip leftover on the machine, gathered fresh every scan the same way
    /// <see cref="ScanDrivers"/> re-reads its own inventory every time, shown as an ordinary detail list so
    /// the generic per-row exclusion mechanism (<paramref name="cancellationToken"/> aside, the same one
    /// every other Entries-bearing target already has) applies here for free.</summary>
    private static CleanupScanResult ScanPipLeftovers(CancellationToken cancellationToken)
    {
        var candidates = PythonCleanupDiscovery.DiscoverPipLeftovers(WindowsRegistryView.Instance);
        var entries = candidates
            .Select(c => new CleanupEntry(
                c.Path, Path.GetFileName(c.Path.TrimEnd(Path.DirectorySeparatorChar)), c.SizeBytes, 0,
                DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.InstallLeftovers))
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var (capped, omittedCount, omittedBytes) = CapEntries(entries.OrderByDescending(e => e.Bytes).ToList());

        return new CleanupScanResult(candidates.Sum(c => c.SizeBytes), candidates.Count, 0)
        {
            Entries = capped, OmittedEntryCount = omittedCount, OmittedEntryBytes = omittedBytes,
        };
    }

    /// <summary>
    /// Hard-deletes every "~"-prefixed folder a FRESH discovery pass still finds, skipping anything the
    /// user deselected in the detail list. Never a recycle-bin delete, the same reasoning as the leftover
    /// folder being unambiguous junk by name alone (see <see cref="PythonCleanupDiscovery"/>'s own doc
    /// comment): recycling a folder this narrowly matched adds nothing a permanent delete does not already
    /// give safely.
    /// </summary>
    private CleanupOutcome CleanPipLeftovers(IReadOnlyCollection<string>? excludedEntryPaths, CancellationToken cancellationToken)
    {
        var excluded = excludedEntryPaths is { Count: > 0 }
            ? new HashSet<string>(excludedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        long freed = 0;
        var deleted = 0;
        var skipped = 0;

        foreach (var candidate in PythonCleanupDiscovery.DiscoverPipLeftovers(WindowsRegistryView.Instance, measure: false, logSkip: logging.Info))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (excluded is not null && excluded.Contains(candidate.Path))
                continue;

            if (!IsSafePipLeftover(candidate.Path))
            {
                skipped++;
                continue;
            }

            var (removed, freedHere) = DeletePipLeftoverTree(candidate.Path);
            freed += freedHere;
            if (removed)
                deleted++;
            else
                skipped++;
        }

        return new CleanupOutcome(freed, deleted, skipped);
    }

    /// <summary>Removes one leftover folder through a verified handle, the way <see cref="CleanDirectories"/>
    /// does: the folder is locked, every file and folder under it is proven to lie inside it right before
    /// its delete, and a link inside is left alone, never followed. Removed is true only when the folder
    /// itself is gone; Freed is what actually went, also when something in use kept the folder standing.</summary>
    internal (bool Removed, long Freed) DeletePipLeftoverTree(string path)
    {
        long freed = 0;
        using (var rootLock = CleanupSafety.TryLockForUse(path))
        {
            if (rootLock is null || VerifiedDelete.FinalPathOf(rootLock) is not { } rootFinalPath)
            {
                logging.Warn($"Cleanup: '{path}' could not be locked for use; left alone.");
                return (false, 0);
            }

            foreach (var file in EnumerateFileDetails(path, [], CancellationToken.None).Select(f => f.Path).ToList())
            {
                try
                {
                    var outcome = VerifiedDelete.TryDeleteFile(rootFinalPath, file, out var size);
                    if (outcome == VerifiedDelete.Outcome.Deleted)
                        freed += size;
                    else if (outcome == VerifiedDelete.Outcome.Refused)
                        logging.Warn($"Cleanup: '{file}' is not inside '{path}' any more; left alone.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use or not ours to delete: it stays, and so does the folder around it
                }
            }

            foreach (var subdirectory in EnumerateAllDirectories(path, [])
                         .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)))
            {
                try
                {
                    VerifiedDelete.TryDeleteEmptyDirectory(rootFinalPath, subdirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // not empty: it simply stays
                }
            }
        }

        // The folder cannot go while its own lock is open. It is empty by now, so a non-recursive delete
        // is all it needs; anything left inside makes the call fail and the folder stays.
        try
        {
            // A folder with a desktop.ini carries the read-only attribute and Windows refuses to remove it
            // then. Cleared only on an emptied folder that is not a link, right before its own delete.
            if (!Directory.EnumerateFileSystemEntries(path).Any())
                ClearReadOnlyAttribute(path);

            Directory.Delete(path, recursive: false);
            return (true, freed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, freed);
        }
    }

    /// <summary>
    /// Re-checked immediately before every delete, same TOCTOU reasoning as every other target's own
    /// re-verification: still a "~"-prefixed folder, still sitting directly inside a real site-packages
    /// folder (never anything named "~..." anywhere else on disk), not this app's own path, and not
    /// reached through a reparse point anywhere on its way up.
    /// </summary>
    private static bool IsSafePipLeftover(string path)
    {
        if (!Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).StartsWith('~'))
            return false;

        if (CleanupSafety.IsOwnPath(path) || CleanupSafety.ContainsOwnPath(path))
            return false;

        if (!Directory.Exists(path))
            return false;

        if (CleanupSafety.AncestryLeadsThroughLink(path))
            return false;

        var parentName = Path.GetFileName(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? string.Empty);
        return string.Equals(parentName, "site-packages", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every interpreter older than the newest of its own architecture, gathered fresh every scan;
    /// no byte total worth trusting across the whole target, the same reasoning as <see cref="ScanDrivers"/>,
    /// the per-row size is what the dialog actually shows.</summary>
    internal static CleanupScanResult ScanOldPythonVersions(
        string? callerProfilePath, CancellationToken cancellationToken, PythonCallerView? caller = null)
    {
        var candidates = PythonCleanupDiscovery.CountVenvUses(
            PythonCleanupDiscovery.DiscoverOldVersions(WindowsRegistryView.Instance, caller: caller),
            callerProfilePath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new CleanupScanResult(candidates.Sum(c => c.SizeBytes), candidates.Count, 0)
        {
            PythonVersionCandidates = candidates,
        };
    }

    /// <summary>
    /// Uninstalls exactly the interpreters the user selected, re-checked against a FRESH discovery pass
    /// immediately before running anything; see <see cref="PythonVersionCleanupExecutor"/> for the full
    /// validation.
    /// </summary>
    private async Task<CleanupOutcome> CleanOldPythonVersionsAsync(
        IReadOnlyCollection<string>? selectedPythonExePaths, CancellationToken cancellationToken)
    {
        if (selectedPythonExePaths is not { Count: > 0 })
            return new CleanupOutcome(0, 0, 0);

        return await PythonVersionCleanupExecutor.CleanAsync(
            WindowsRegistryView.Instance, selectedPythonExePaths, logging, cancellationToken, PythonCaller);
    }
}
