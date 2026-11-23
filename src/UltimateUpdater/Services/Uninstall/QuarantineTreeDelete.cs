using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Removes an expired quarantine folder with everything in it, the way cleanup removes a folder it emptied: each
/// directory is locked (no rename, no replacement by a link while it is listed), files and links are deleted
/// through handles that are opened without following a link and proven to lie inside the locked directory, a link
/// is removed as the link and never entered, and a directory goes only once it is empty and only non-recursively.
/// A recursive path delete is never used on quarantine content: what a machine quarantine holds came from places a
/// user can write to, so a path that is walked twice (listing, then delete) could be re-pointed by a junction
/// between the two steps and send an elevated delete anywhere.
/// </summary>
internal static class QuarantineTreeDelete
{
    /// <summary>Nesting deeper than this is not walked (a damaged or planted tree); the folder then stays.</summary>
    internal const int MaxDepth = 64;

    private static readonly EnumerationOptions AllEntries = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Test seam: invoked with a directory after it was locked and listed, before its entries are touched
    /// (the window in which an attacker would swap one of them for a link).</summary>
    internal static readonly AsyncLocal<Action<string>?> AfterListingForTest = new();

    /// <summary>
    /// Deletes <paramref name="folder"/> and everything below it. True when it is gone; false when anything
    /// stays (a locked file, a link that could not be removed, a path that no longer proves safe), which is
    /// logged once and tried again at the next prune. Never throws.
    /// </summary>
    internal static bool Delete(string folder, LoggingService logging)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var parent = Path.GetDirectoryName(full);
            if (parent is null)
                return false;

            using var parentLock = CleanupSafety.TryLockForUse(parent);
            var parentFinal = parentLock is null ? null : VerifiedDelete.FinalPathOf(parentLock);
            if (parentFinal is null)
            {
                logging.Warn($"Leftover quarantine: '{parent}' could not be locked; '{Path.GetFileName(full)}' not removed.");
                return false;
            }

            var walk = new Walk();
            var gone = DeleteDirectory(parentFinal, full, 0, walk);
            if (!gone && walk.First is not null)
                logging.Warn($"Leftover quarantine: '{Path.GetFileName(full)}' was not removed completely: {walk.First}");

            return gone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException)
        {
            logging.Warn($"Leftover quarantine: could not remove '{folder}': {ex.Message}");
            return false;
        }
    }

    private sealed class Walk
    {
        public string? First { get; private set; }

        public void Failed(string message) => First ??= message;
    }

    /// <summary>Empties <paramref name="directory"/> under its own lock, then removes it through
    /// <paramref name="parentFinal"/> (the final path of the locked directory it lies in).</summary>
    private static bool DeleteDirectory(string parentFinal, string directory, int depth, Walk walk)
    {
        if (depth > MaxDepth)
        {
            walk.Failed($"'{directory}' is nested deeper than {MaxDepth} levels");
            return false;
        }

        var complete = true;
        using (var directoryLock = CleanupSafety.TryLockForUse(directory))
        {
            var final = directoryLock is null ? null : VerifiedDelete.FinalPathOf(directoryLock);
            if (final is null || !VerifiedDelete.IsInsideRoot(parentFinal, final))
            {
                walk.Failed($"'{directory}' is a link now, or cannot be locked");
                return false;
            }

            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", AllEntries).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                walk.Failed($"'{directory}' cannot be listed: {ex.Message}");
                return false;
            }

            AfterListingForTest.Value?.Invoke(directory);

            foreach (var entry in entries)
            {
                var path = Path.Combine(directory, entry.Name);
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        complete &= Succeeded(VerifiedDelete.TryDeleteLink(final, path), path, walk);
                    else if ((entry.Attributes & FileAttributes.Directory) != 0)
                        complete &= DeleteDirectory(final, path, depth + 1, walk);
                    else
                        complete &= Succeeded(VerifiedDelete.TryDeleteFile(final, path, out _), path, walk);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    walk.Failed($"'{path}': {ex.Message}");
                    complete = false;
                }
            }
        }

        // The lock has no delete share, so it is released first; only an EMPTY directory goes (the delete is
        // non-recursive and goes through a handle proven to lie inside the parent), so anything that arrived
        // meanwhile keeps the folder.
        if (!complete)
            return false;

        try
        {
            return Succeeded(VerifiedDelete.TryDeleteEmptyDirectory(parentFinal, directory), directory, walk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            walk.Failed($"'{directory}': {ex.Message}");
            return false;
        }
    }

    private static bool Succeeded(VerifiedDelete.Outcome outcome, string path, Walk walk)
    {
        if (outcome is VerifiedDelete.Outcome.Deleted or VerifiedDelete.Outcome.Vanished)
            return true;

        walk.Failed($"'{path}' is not what the listing said (a link or a swapped entry); left alone");
        return false;
    }
}
