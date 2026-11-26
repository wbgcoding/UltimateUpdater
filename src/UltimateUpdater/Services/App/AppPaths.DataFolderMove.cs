using System.IO;

namespace UltimateUpdater.Services.App;

/// <summary>Moving the data folder: the pointer file, the copy and sync of live data, and the mirror of the old folder.</summary>
public static partial class AppPaths
{
    /// <summary>A 32,767-character long path still fits, even at 3 bytes per character in UTF-8.</summary>
    internal const long MaxPointerFileBytes = 128 * 1024;

    /// <summary>Internal so a test can prove the round trip through an arbitrary temp path, never through
    /// the real <see cref="PointerFilePath"/>.</summary>
    internal static string? TryReadPointer(string pointerPath)
    {
        try
        {
            if (!File.Exists(pointerPath))
                return null;

            // Runs during static initialisation: an unbounded read of a corrupt or huge file would crash every
            // start, so an oversized pointer reads like a missing one.
            var text = AtomicFile.TryReadAllText(pointerPath, MaxPointerFileBytes)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Records the new location so the next launch resolves <see cref="DataDirectory"/> there; this
    /// run keeps using the folder it started with.</summary>
    public static bool WriteDataFolderPointer(string newPath)
    {
        var written = WriteDataFolderPointer(newPath, PointerFilePath);
        if (written)
            _pendingDataFolder = newPath;

        return written;
    }

    /// <summary>The folder the pointer now names while this run still writes to the old one; null when no move
    /// happened in this run.</summary>
    private static volatile string? _pendingDataFolder;

    /// <summary>The files the running app keeps appending to or replacing after a data folder copy. The rest
    /// of the folder (logs, the rebuildable caches) tolerates a stale copy. The saved error reports belong
    /// here only while they live in the data folder itself (see <see cref="ReportsEntry"/>).</summary>
    private static readonly string[] LiveWrittenEntries =
    [
        "history.jsonl", "history.jsonl.1", "cleanup-history.json",
        "driver-backups", "restore-point.json", "pip-blocked.json", "last-scan.json", "enrichment-cache.json",
    ];

    /// <summary>Saved error reports live in <see cref="LocalDirectory"/>. That is the data folder only for a
    /// portable copy or one whose local folder could not be used; an installed copy keeps them elsewhere and
    /// the data folder holds none to carry over.</summary>
    private const string ReportsEntry = "reports";

    /// <summary>Brings the live-written entries (history, cleanup history, driver backups, the restore point,
    /// the pip block list, the last scan, the enrichment cache and, where they live in the data folder, the
    /// error reports) in the new data folder up to date with the folder this run writes to. Called right before the pointer is written, so the earlier
    /// copy does not miss what happened meanwhile.</summary>
    public static bool SyncLiveDataTo(string destination) =>
        SyncLiveData(DataDirectory, destination, includeSettings: false, includeReports: !LocalDirectoryIsSeparate);

    /// <summary>At exit, once the delayed settings write has landed: everything written after the pointer was
    /// set goes to the new folder too, so the restart finds nothing missing. Best effort, an exit never fails
    /// on it.</summary>
    public static void SyncPendingDataFolder()
    {
        if (_pendingDataFolder is { } pending)
            SyncLiveData(DataDirectory, pending, includeSettings: true, includeReports: !LocalDirectoryIsSeparate);
    }

    /// <summary>Makes each live-written entry in <paramref name="destination"/> a copy of the one in
    /// <paramref name="source"/>, overwriting what is there and removing what the source no longer has (the
    /// history pruning deletes its rotated file). Links are never followed. False when something could not be
    /// written.</summary>
    internal static bool SyncLiveData(string source, string destination, bool includeSettings, bool includeReports = true)
    {
        IEnumerable<string> entries = LiveWrittenEntries;
        if (includeReports)
            entries = entries.Append(ReportsEntry);
        if (includeSettings)
            entries = entries.Append("settings.json");

        try
        {
            foreach (var entry in entries)
            {
                var from = Path.Combine(source, entry);
                var to = Path.Combine(destination, entry);

                if (Directory.Exists(from))
                {
                    if (IsReparsePoint(from))
                        return false;

                    // Mirrored file by file: a report the user deleted meanwhile must not come back with the
                    // restart, and a large folder (driver backups) is not copied over again in full.
                    if (!MirrorDirectory(from, to))
                        return false;
                }
                else if (File.Exists(from))
                {
                    if (IsReparsePoint(from))
                        return false;

                    File.Copy(from, to, overwrite: true);
                }
                else
                {
                    if (Directory.Exists(to))
                        Directory.Delete(to, recursive: true);
                    else if (File.Exists(to))
                        File.Delete(to);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Makes <paramref name="to"/> hold exactly what <paramref name="from"/> holds, recursively: files
    /// that are missing or differ are copied, files and folders the source no longer has are removed. A link
    /// anywhere in the source fails it, like every other copy of the data folder. False when something could
    /// not be written.</summary>
    private static bool MirrorDirectory(string from, string to)
    {
        // A link in the new folder would carry the copies and the deletions below to wherever it points.
        if (ClassifyEntry(to) == EntryKind.Link)
            return false;

        if (File.Exists(to))
            File.Delete(to);

        Directory.CreateDirectory(to);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var kind = ClassifyEntry(file);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
                return false;

            var name = Path.GetFileName(file);
            var target = Path.Combine(to, name);
            if (ClassifyEntry(target) == EntryKind.Link)
                return false;

            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                var source = new FileInfo(file);
                var existing = new FileInfo(target);

                // Copy keeps the write time, so equal length and write time means the file did not change.
                if (!existing.Exists || existing.Length != source.Length || existing.LastWriteTimeUtc != source.LastWriteTimeUtc)
                    File.Copy(file, target, overwrite: true);

                names.Add(name);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted by the running app after it was listed: nothing left to carry over.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
        {
            var kind = ClassifyEntry(directory);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
                return false;

            var name = Path.GetFileName(directory);
            names.Add(name);
            if (!MirrorDirectory(directory, Path.Combine(to, name)))
                return false;
        }

        foreach (var stale in Directory.EnumerateFileSystemEntries(to))
        {
            if (names.Contains(Path.GetFileName(stale)))
                continue;

            if (Directory.Exists(stale))
                Directory.Delete(stale, recursive: true);
            else
                File.Delete(stale);
        }

        return true;
    }

    internal static bool WriteDataFolderPointer(string newPath, string pointerFilePath)
    {
        try
        {
            var pointerDirectory = Path.GetDirectoryName(pointerFilePath);
            if (!string.IsNullOrEmpty(pointerDirectory))
                Directory.CreateDirectory(pointerDirectory);

            // Atomic, so a crash mid-write cannot leave a truncated path.
            AtomicFile.WriteAllText(pointerFilePath, newPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies everything under <see cref="DataDirectory"/> into <paramref name="destination"/>, and reports
    /// whether the result is safe to switch to. A skipped file that is not under <see cref="LogsDirectory"/>
    /// means something real (settings, history, a cache entry) was left behind, so the whole move fails: the
    /// partial copy is removed and nothing is switched. A skipped log (the file currently being written)
    /// is fine to leave behind; its copy starts fresh at the new location on the next launch.
    /// </summary>
    public static bool TryMoveDataFolder(string destination, out IReadOnlyList<string> blockingSkippedFiles) =>
        TryMoveDataFolder(DataDirectory, LogsDirectory, destination, out blockingSkippedFiles);

    internal static bool TryMoveDataFolder(
        string source, string logsDirectory, string destination, out IReadOnlyList<string> blockingSkippedFiles,
        Action<string>? afterEnumerated = null)
    {
        if (!TryCopyDirectory(source, destination, out var skipped, afterEnumerated))
        {
            blockingSkippedFiles = [];
            TryDeleteBestEffort(destination);
            return false;
        }

        var blocking = skipped.Where(file => !IsUnder(file, logsDirectory)).ToList();
        if (blocking.Count == 0)
        {
            blockingSkippedFiles = [];
            return true;
        }

        TryDeleteBestEffort(destination);
        blockingSkippedFiles = blocking;
        return false;
    }

    /// <summary>
    /// Undoes a copy whose later step (final sync, settings copy, pointer write) failed: the dedicated
    /// subfolder is removed so a retry to the same target is not refused as non-empty. Only a folder this
    /// very move created is touched, never one that was already there, and a link in its place is left alone.
    /// Never throws.
    /// </summary>
    public static void DiscardCreatedDataFolder(string destination, bool createdByThisMove)
    {
        if (!createdByThisMove || ClassifyEntry(destination) != EntryKind.Plain)
            return;

        TryDeleteBestEffort(destination);
    }

    /// <summary>Cleanup after a failed copy/move; never throws, never blocks the caller from reporting or
    /// retrying its own failure.</summary>
    private static void TryDeleteBestEffort(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool TryCopyDirectory(
        string source, string destination, out IReadOnlyList<string> skippedFiles, Action<string>? afterEnumerated = null)
    {
        var skipped = new List<string>();
        try
        {
            Directory.CreateDirectory(destination);
            CopyRecursive(source, destination, skipped, afterEnumerated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            skippedFiles = skipped;
            return false;
        }

        skippedFiles = skipped;
        return true;
    }

    /// <summary>
    /// A junction or symlink inside the folder being moved would make the copy read from wherever it points
    /// (another user's folder, a system directory) and write that into the new data folder. Every reparse
    /// point, file or directory, is therefore skipped and reported in <paramref name="skipped"/>, which fails
    /// the move like any other left-behind file. No "still under the source root" comparison of full paths:
    /// <c>Path.GetFullPath</c> never resolves a link, so it could not catch a swapped folder.
    /// </summary>
    private static void CopyRecursive(
        string source, string destination, List<string> skipped, Action<string>? afterEnumerated)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Test seam for the window between listing an entry and using it, where the running app can delete it.
            afterEnumerated?.Invoke(file);
            var name = Path.GetFileName(file);
            // Neither belongs in a copy of the data: the pointer names a location (copying it would point the new
            // folder at itself), the write probe and the migration marker are transient.
            if (string.Equals(name, PointerFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ProbeFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, MigrationPendingFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = ClassifyEntry(file);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
            {
                skipped.Add(file);
                continue;
            }

            try
            {
                File.Copy(file, Path.Combine(destination, name), overwrite: true);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted by the running app after it was listed: nothing left to carry over, not a locked file.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            afterEnumerated?.Invoke(directory);

            var kind = ClassifyEntry(directory);
            if (kind == EntryKind.Gone)
                continue;

            if (kind == EntryKind.Link)
            {
                skipped.Add(directory);
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            try
            {
                CopyRecursive(directory, target, skipped, afterEnumerated);
            }
            catch (DirectoryNotFoundException)
            {
                // The folder was deleted while it was being copied: drop the half-made copy of it.
                TryDeleteBestEffort(target);
            }
        }
    }

    private enum EntryKind { Plain, Link, Gone }

    /// <summary>An entry whose attributes cannot be read is treated as a link: fail closed, never copy it.
    /// One that no longer exists is told apart, since a deleted entry is nothing to carry over.</summary>
    private static EntryKind ClassifyEntry(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ? EntryKind.Link : EntryKind.Plain;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return EntryKind.Gone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return EntryKind.Link;
        }
    }

    private static bool IsReparsePoint(string path) => ClassifyEntry(path) != EntryKind.Plain;
}
