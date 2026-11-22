using System.IO;
using System.IO.Enumeration;

namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
    /// <summary>The fixed list for every ordinary target; discovered fresh for <see cref="CleanupKind.AppCacheSweep"/>,
    /// re-discovered rather than cached, so a clean run sees exactly what is on disk right now.</summary>
    private static IReadOnlyList<string> ResolveDirectories(CleanupTarget target, CancellationToken cancellationToken) =>
        target.Kind == CleanupKind.AppCacheSweep
            // A discovered root that is itself excluded (Temp and the pip cache are catalogue folders with
            // their own rows) would be walked in full only to drop every file again.
            ? AppCacheDiscovery.DiscoverCacheDirectories(cancellationToken)
                .Where(d => !IsExcluded(d, target.ExcludedDirectories))
                .ToList()
            : target.Directories;

    /// <summary>Detail-list rows above this are folded into one "…and N more" summary;
    /// large enough that "app-cache-sweep" or "user-temp" on a heavily-used machine still shows everything
    /// worth seeing, small enough that the elevated pipe never has to carry an unbounded list.</summary>
    private const int MaxEntries = 500;

    /// <summary>
    /// Which detail-list row a file belongs to: for
    /// <see cref="CleanupKind.AppCacheSweep"/>, <paramref name="root"/> already IS one program's own cache
    /// folder (see <see cref="AppCacheDiscovery"/>), so every file under it shares that one row. For an
    /// ordinary <see cref="CleanupKind.Directories"/> root: a shared location like %TEMP% that many
    /// unrelated programs write into: the row is the file's own first-level subfolder, falling back to
    /// the root itself for a file sitting loose directly inside it.
    /// </summary>
    private static string BucketFor(string root, string file, bool isAppCacheSweep)
    {
        if (isAppCacheSweep)
            return root;

        var relative = Path.GetRelativePath(root, file);
        var separatorIndex = relative.IndexOf(Path.DirectorySeparatorChar);
        return separatorIndex < 0 ? root : Path.Combine(root, relative[..separatorIndex]);
    }

    /// <summary>
    /// The same question for a SUBDIRECTORY rather than a file. It cannot share <see cref="BucketFor"/>:
    /// a first-level folder has no separator in its relative path, so that method would answer "the root"
    /// for the very folder whose own files it buckets under <c>root\folder</c>.
    /// </summary>
    private static string BucketForDirectory(string root, string directory, bool isAppCacheSweep)
    {
        if (isAppCacheSweep)
            return root;

        var relative = Path.GetRelativePath(root, directory);
        var separatorIndex = relative.IndexOf(Path.DirectorySeparatorChar);
        return Path.Combine(root, separatorIndex < 0 ? relative : relative[..separatorIndex]);
    }

    /// <summary>The safety gate for one directory of a target. A catalogue folder that exists on disk but is
    /// refused is logged with the rule that refused it, so the support log explains a row that reads "nothing
    /// found". The discovered folders of the cache sweep are not logged: nearly all of them fail the name test.</summary>
    private bool IsTargetDirectorySafe(CleanupTarget target, string directory)
    {
        if (CleanupSafety.IsSafeToEmpty(directory, out var reason))
            return true;

        if (target.Kind == CleanupKind.Directories && Directory.Exists(directory))
            logging.Info($"cleanup target {target.Id} refused: {reason} ('{directory}')");

        return false;
    }

    /// <summary>The moment before which a file must have been created AND last written to be taken, or null
    /// when the target has no age rule.</summary>
    internal static DateTime? AgeCutoffUtc(CleanupTarget target, DateTime nowUtc) =>
        target.MinFileAge is { } age ? nowUtc - age : null;

    /// <summary>True when a listed file lies inside the age window. A stamp that is unset (the FILETIME epoch
    /// of 1601) or lies in the future cannot be trusted and reads as young: keep it.</summary>
    internal static bool IsTooNew(DateTime creationUtc, DateTime lastWriteUtc, DateTime cutoffUtc) =>
        creationUtc.Year <= 1601 || lastWriteUtc.Year <= 1601 || creationUtc > cutoffUtc || lastWriteUtc > cutoffUtc;

    private CleanupScanResult ScanDirectories(CleanupTarget target, CancellationToken cancellationToken)
    {
        var cutoff = AgeCutoffUtc(target, DateTime.UtcNow);
        var tooNew = 0;
        long bytes = 0;
        var files = 0;
        var isAppCacheSweep = target.Kind == CleanupKind.AppCacheSweep;
        var buckets = new Dictionary<string, EntryAccumulator>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<(uint VolumeSerial, ulong FileIndex)>();

        foreach (var directory in ResolveDirectories(target, cancellationToken))
        {
            if (!IsTargetDirectorySafe(target, directory))
                continue;

            // Two spellings of one folder (an 8.3 %TEMP%, other casing) are measured once, under the first
            // spelling, which is also the one the clean keeps; otherwise every figure doubles and a row
            // excluded under one spelling would still be cleaned under the other.
            if (CleanupSafety.TryReadDirectoryIdentity(directory, out var identity) && !identities.Add(identity))
                continue;

            // Size and write time come out of the directory listing itself; asking each file again
            // (a FileInfo per file) was one more disk round trip per file on a tree of hundreds of thousands.
            foreach (var found in EnumerateFileDetails(directory, target.ExcludedDirectories, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = found.Path;
                if (!CleanupSafety.IsDeletableFile(directory, file))
                    continue;

                // Measured by the same rule the clean applies, so the figure is what a run would free.
                if (cutoff is { } limit && IsTooNew(found.CreationUtc, found.LastWriteUtc, limit))
                {
                    tooNew++;
                    continue;
                }

                // Logical length on purpose: the clean routes know the space a file occupies from the handle
                // they hold anyway, but opening every file here would make the scan far slower. Hardlinked,
                // compressed and sparse files can therefore free less than this figure promises.
                bytes += found.Length;
                files++;

                var bucket = BucketFor(directory, file, isAppCacheSweep);
                if (!buckets.TryGetValue(bucket, out var accumulator))
                    accumulator = buckets[bucket] = new EntryAccumulator();
                accumulator.Add(found.Length, found.LastWriteUtc, Path.GetExtension(file));
            }
        }

        // The single files a target names: measured by the rule the clean applies, each one its own row.
        foreach (var file in target.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!CleanupSafety.IsSafeToDeleteFile(file, out var refusal))
            {
                if (File.Exists(file))
                    logging.Info($"cleanup target {target.Id} refused file: {refusal} ('{file}')");
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Directory is null || !CleanupSafety.IsDeletableFile(info.Directory.FullName, file))
                    continue;

                if (cutoff is { } limit && IsTooNew(info.CreationTimeUtc, info.LastWriteTimeUtc, limit))
                {
                    tooNew++;
                    continue;
                }

                bytes += info.Length;
                files++;
                if (!buckets.TryGetValue(file, out var accumulator))
                    accumulator = buckets[file] = new EntryAccumulator();
                accumulator.Add(info.Length, info.LastWriteTimeUtc, Path.GetExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Info($"cleanup target {target.Id} could not read file '{file}': {ex.Message}");
            }
        }

        if (tooNew > 0)
            logging.Info($"Cleanup: {tooNew} recent file(s) of '{target.Id}' are left out of the figure (younger than {target.MinFileAge}).");

        var (entries, omittedCount, omittedBytes) = CapEntries(buckets
            .Select(kv => kv.Value.ToEntry(kv.Key, isAppCacheSweep))
            .OrderByDescending(e => e.Bytes)
            .ToList());

        return new CleanupScanResult(bytes, files, 0)
        {
            Entries = entries, OmittedEntryCount = omittedCount, OmittedEntryBytes = omittedBytes,
        };
    }

    /// <summary>The row's shown name. A swept cache folder is named after its program only through its parent
    /// ("Cache" alone would repeat on dozens of rows), so a sweep row reads <c>parent\leaf</c>; the row's path
    /// stays the full path.</summary>
    internal static string EntryDisplayName(string path, bool isAppCacheSweep)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (!isAppCacheSweep)
            return leaf;

        var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        return string.IsNullOrEmpty(parent) ? leaf : Path.Combine(parent, leaf);
    }

    /// <summary>Accumulates one detail-list row while a directory scan walks its files.</summary>
    private sealed class EntryAccumulator
    {
        private long _bytes;
        private int _files;
        private DateTimeOffset _oldest = DateTimeOffset.MaxValue;
        private DateTimeOffset _newest = DateTimeOffset.MinValue;
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

        public void Add(long size, DateTimeOffset writeTime, string extension)
        {
            _bytes += size;
            _files++;
            if (writeTime < _oldest)
                _oldest = writeTime;
            if (writeTime > _newest)
                _newest = writeTime;
            if (extension.Length > 0)
                _extensions.Add(extension);
        }

        public CleanupEntry ToEntry(string path, bool isAppCacheSweep) => new(
            path, EntryDisplayName(path, isAppCacheSweep), _bytes, _files, _oldest, _newest,
            CleanupEntryClassifier.Classify(path, _extensions));
    }

    /// <summary>Keeps the biggest <see cref="MaxEntries"/> rows (already sorted by size) and folds the rest
    /// into a count/byte total the caller can show as one "…and N more" line, never as fake data.</summary>
    private static (IReadOnlyList<CleanupEntry> Entries, int OmittedCount, long OmittedBytes) CapEntries(
        IReadOnlyList<CleanupEntry> sortedByBytesDescending)
    {
        if (sortedByBytesDescending.Count <= MaxEntries)
            return (sortedByBytesDescending, 0, 0);

        var kept = sortedByBytesDescending.Take(MaxEntries).ToList();
        var omitted = sortedByBytesDescending.Skip(MaxEntries).ToList();
        return (kept, omitted.Count, omitted.Sum(e => e.Bytes));
    }

    private CleanupOutcome CleanDirectories(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause, IReadOnlyCollection<string>? excludedEntryPaths, bool recycleInsteadOfDelete = false)
    {
        long freed = 0;
        long skippedBytes = 0;
        long protectedBytes = 0;
        var deleted = 0;
        var skipped = 0;
        var protectedFiles = 0;
        var foldersKept = 0;
        string? firstFolderKept = null;
        var isAppCacheSweep = target.Kind == CleanupKind.AppCacheSweep;
        var useRecycleBin = ShouldUseRecycleBin(recycleInsteadOfDelete, target.NeedsElevation);
        var cutoff = AgeCutoffUtc(target, DateTime.UtcNow);
        var tooNew = 0;
        var excludedEntries = excludedEntryPaths is { Count: > 0 }
            ? new HashSet<string>(excludedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        var directories = ResolveDirectories(target, cancellationToken)
            .Where(d => IsTargetDirectorySafe(target, d))
            // A folder with its own clean command is emptied by that command only (see
            // RunDirectoryCleanCommandsAsync); the file deleter must never touch it, whatever the age rule says.
            .Where(d => !target.CleanCommands.ContainsKey(d))
            // An AppCacheSweep row IS one whole directory (see BucketFor); deselecting it skips the
            // directory outright rather than needing a per-file check below.
            .Where(d => excludedEntries is null || !(isAppCacheSweep && excludedEntries.Contains(d)))
            .ToList();

        // One directory at a time: lock it, list it once under that lock, delete, release, then the next.
        // While its handle is open nobody can rename or delete the directory and put a junction aimed
        // elsewhere in its place, and one lock at a time keeps every other folder renamable for the apps
        // that use it. A directory that cannot be locked is skipped, never used bare. Each file is still
        // re-checked right before its delete below.
        //
        // The bar is the directory's share: (index + files done in it / files in it) / directories * 100.
        // The directory count comes from a probe that locks and releases each directory just to read its
        // identity, so two spellings of one folder (8.3 name, other casing) are one share. That keeps the
        // percentage monotonic: a later directory's file count is unknown, and a count that grew mid-run
        // would make the bar jump backward.
        var identities = new HashSet<(uint VolumeSerial, ulong FileIndex)>();
        var directoryCount = 0;
        foreach (var candidate in directories)
        {
            using var probe = CleanupSafety.TryLockForUse(candidate);
            if (probe is null || !VerifiedDelete.TryGetIdentity(probe, out var probed) || identities.Add(probed))
                directoryCount++;
        }

        directoryCount = Math.Max(1, directoryCount);
        identities.Clear();
        var directoryIndex = 0;

        foreach (var candidate in directories)
        {
            using var directoryLock = CleanupSafety.TryLockForUse(candidate);

            // The root's own final path is what every delete below is checked against; without it nothing
            // below can be proven to still lie inside this directory.
            string? rootFinalPath = null;
            var identity = default((uint VolumeSerial, ulong FileIndex));
            if (directoryLock is null
                || (rootFinalPath = VerifiedDelete.FinalPathOf(directoryLock)) is null
                || !VerifiedDelete.TryGetIdentity(directoryLock, out identity))
            {
                // Logged, not counted: the figure reports files, and this folder's files were never listed.
                logging.Warn($"Cleanup: '{candidate}' could not be locked for use; left alone.");
                directoryIndex++;
                continue;
            }

            // The same folder under another spelling was already cleaned: not skipped, just not again.
            if (!identities.Add(identity))
                continue;

            var thisDirectory = directoryIndex++;
            var directory = candidate;
            var files = EnumerateFileDetails(candidate, target.ExcludedDirectories, cancellationToken).ToList();
            var filesDone = 0;
            var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();

            foreach (var listed in files)
            {
                var file = listed.Path;

                // Between files, so Pause takes hold within one file's worth of work and Stop leaves at once.
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                filesDone++;

                if (!CleanupSafety.IsDeletableFile(directory, file))
                    continue;

                // Deselected in the detail list; left alone, same as a file the safety
                // gate refused. AppCacheSweep never reaches here for an excluded row: its whole directory
                // was already filtered out above, before any file inside it was even enumerated.
                if (excludedEntries is not null && excludedEntries.Contains(BucketFor(directory, file, isAppCacheSweep)))
                    continue;

                long size = 0;
                try
                {
                    BeforeFileDeleteForTest.Value?.Invoke(file);
                    var outcome = DeleteVerified(rootFinalPath!, file, useRecycleBin, cutoff, cancellationToken, out size);

                    if (outcome == VerifiedDelete.Outcome.Deleted)
                    {
                        freed += size;
                        deleted++;
                    }
                    else if (outcome == VerifiedDelete.Outcome.Refused)
                    {
                        logging.Warn($"Cleanup: '{file}' was not deleted (outside '{directory}' now, or refused); left alone.");
                        skipped++;
                        skippedBytes += size;
                        protectedFiles++;
                        protectedBytes += size;
                    }

                    else if (outcome == VerifiedDelete.Outcome.TooNew)
                    {
                        // Judged on the opened file, so a file rewritten since the listing is kept too.
                        tooNew++;
                    }

                    // A file that vanished meanwhile is neither deleted nor skipped: this run did nothing to it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Not ours to delete, typically a system file without admin rights: kept apart from
                    // "in use" so the summary does not point at a program that holds nothing.
                    // The open failed before the size was read: the listed length stands in for it.
                    var leftBytes = size > 0 ? size : listed.Length;
                    skipped++;
                    skippedBytes += leftBytes;
                    protectedFiles++;
                    protectedBytes += leftBytes;
                }
                catch (IOException)
                {
                    // In use by another process. Expected; keep going, but its size must not be counted as
                    // freed, and the user is told how much stayed behind.
                    skipped++;
                    skippedBytes += size > 0 ? size : listed.Length;
                }

                // On a clock, not a file count: about four reports a second whether the folder holds a million
                // tiny files (a count would flood the dispatcher and the pipe) or a few huge ones (a count would
                // stay silent for minutes while the freed figure sits still). The figure in each report is
                // what is gone so far.
                // The FULL path, not just the file name: the dialog states exactly what is being deleted,
                // and a bare name says nothing about which of a dozen temp folders it came from.
                if (progress is not null
                    && System.Diagnostics.Stopwatch.GetElapsedTime(lastReport) >= ProgressInterval)
                {
                    lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                    var share = (thisDirectory + filesDone / (double)Math.Max(1, files.Count)) / directoryCount * 100.0;
                    progress.Report(new CleanupProgress(file, Math.Min(100.0, share), freed));
                }
            }

            // Then the (now mostly empty) subdirectories, EVERY nested level, deepest first, each removed
            // NON-recursively: a non-recursive `Directory.Delete` only succeeds on a TRULY empty directory,
            // so anything unexpected still inside it (new content written after the file pass' snapshot, or
            // a file that pass skipped as locked/protected) makes the call fail, and that failure is the
            // safety: the directory and its new content simply stay.
            var allSubdirectories = EnumerateAllDirectories(directory, target.ExcludedDirectories)
                .OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar));

            foreach (var subdirectory in allSubdirectories)
            {
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // The single-file extraction root of this app or any other running one, or our own data
                // directory when it fell back to Temp, never even attempted.
                if (CleanupSafety.IsOwnPath(subdirectory) || CleanupSafety.ContainsOwnPath(subdirectory))
                    continue;

                // Deselected in the detail list, the same check the file pass above makes. Without it a row
                // the user unticked was still taken apart whenever its subtree held only empty folders:
                // no file ever reached the exclusion check, and this pass removed the folders regardless.
                if (excludedEntries is not null
                    && excludedEntries.Contains(BucketForDirectory(directory, subdirectory, isAppCacheSweep)))
                {
                    continue;
                }

                try
                {
                    if (CleanupSafety.AncestryLeadsThroughLink(subdirectory))
                        continue;

                    // Through a handle that is checked to lie inside the root, so a folder swapped for a
                    // junction since the listing cannot redirect this delete. A non-empty directory fails
                    // exactly like the non-recursive path delete did.
                    if (VerifiedDelete.TryDeleteEmptyDirectory(rootFinalPath!, subdirectory, cutoff) == VerifiedDelete.Outcome.Refused)
                        logging.Warn($"Cleanup: '{subdirectory}' no longer lies inside '{directory}'; left alone.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not empty (new content, a still-there deeper subdirectory that failed to delete, or a
                    // locked file) or inaccessible: it simply stays. Not counted, the figure reports files.
                    // Summed into one line below: without admin rights a system cache keeps thousands of
                    // folders, and a line each pushed the rest of the day out of the size-capped log.
                    foldersKept++;
                    firstFolderKept ??= $"'{subdirectory}': {ex.Message}";
                }
            }
        }

        var fileTally = CleanNamedFiles(target, excludedEntries, useRecycleBin, cutoff, pause, cancellationToken);
        freed += fileTally.Freed;
        deleted += fileTally.Deleted;
        skipped += fileTally.Skipped;
        skippedBytes += fileTally.SkippedBytes;
        protectedFiles += fileTally.Protected;
        protectedBytes += fileTally.ProtectedBytes;
        tooNew += fileTally.TooNew;

        if (tooNew > 0)
            logging.Info($"Cleanup: {tooNew} recent file(s) of '{target.Id}' were kept (younger than {target.MinFileAge}).");

        if (foldersKept > 0)
            logging.Info($"Cleanup: {foldersKept} folder(s) of '{target.Id}' were not removed (not empty or no access), first {firstFolderKept}");

        return new CleanupOutcome(
            freed, deleted, skipped, skippedBytes, ProtectedFiles: protectedFiles, ProtectedBytes: protectedBytes);
    }

    /// <summary>
    /// Deletes one file through the verified handle, or hands it to the recycle bin after the same check.
    /// The recycle bin path signals failure by return value, never an exception, so it needs its own success
    /// check rather than sharing the callers' catch. Both routes first prove the file still lies inside the
    /// locked root; a folder swapped for a junction since the listing would otherwise send the delete elsewhere.
    /// </summary>
    private VerifiedDelete.Outcome DeleteVerified(
        string rootFinalPath, string file, bool useRecycleBin, DateTime? cutoff, CancellationToken cancellationToken,
        out long size)
    {
        if (!useRecycleBin)
            return VerifiedDelete.TryDeleteFile(rootFinalPath, file, out size, cutoff);

        var outcome = VerifiedDelete.CheckFile(rootFinalPath, file, out size, cutoff);
        if (outcome == VerifiedDelete.Outcome.Cleared)
        {
            outcome = RecycleBinDeleter.DeleteToRecycleBin(file, logging, cancellationToken)
                ? VerifiedDelete.Outcome.Deleted
                : VerifiedDelete.Outcome.Refused;

            // A cancelled wait reads as a refusal; the user asked to stop, not to be told
            // the file is protected.
            if (outcome == VerifiedDelete.Outcome.Refused)
                cancellationToken.ThrowIfCancellationRequested();
        }

        return outcome;
    }

    /// <summary>The logical length of a file that could not be opened for the verified delete, 0 when it
    /// cannot be read either. Only the skipped figure uses it.</summary>
    private static long LengthOrZero(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private readonly record struct FileTally(
        long Freed, int Deleted, int Skipped, long SkippedBytes, int Protected, long ProtectedBytes, int TooNew);

    /// <summary>
    /// Deletes the single files a target names. Each one passes <see cref="CleanupSafety.IsSafeToDeleteFile"/>,
    /// then its parent folder is locked so the delete can be proven to lie inside it, and the delete goes
    /// through the same verified handle as every other file: a link, a swapped parent and a file younger than
    /// the age rule are all left alone.
    /// </summary>
    private FileTally CleanNamedFiles(
        CleanupTarget target, HashSet<string>? excludedEntries, bool useRecycleBin, DateTime? cutoff, PauseGate? pause,
        CancellationToken cancellationToken)
    {
        long freed = 0, skippedBytes = 0, protectedBytes = 0;
        int deleted = 0, skipped = 0, protectedFiles = 0, tooNew = 0;

        foreach (var file in target.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            pause?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!CleanupSafety.IsSafeToDeleteFile(file, out var reason))
            {
                if (File.Exists(file))
                    logging.Info($"cleanup target {target.Id} refused file: {reason} ('{file}')");
                continue;
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(file));
            if (parent is null || !CleanupSafety.IsDeletableFile(parent, file))
                continue;

            // The row of a single file is the file itself, so unticking it in the detail list leaves it be.
            if (excludedEntries is not null && excludedEntries.Contains(file))
                continue;

            using var parentLock = CleanupSafety.TryLockForUse(parent);
            string? rootFinalPath = null;
            if (parentLock is null || (rootFinalPath = VerifiedDelete.FinalPathOf(parentLock)) is null)
            {
                logging.Warn($"Cleanup: '{parent}' could not be locked for use; '{file}' left alone.");
                continue;
            }

            long size = 0;
            try
            {
                BeforeFileDeleteForTest.Value?.Invoke(file);
                var outcome = DeleteVerified(rootFinalPath, file, useRecycleBin, cutoff, cancellationToken, out size);
                if (outcome == VerifiedDelete.Outcome.Deleted)
                {
                    freed += size;
                    deleted++;
                }
                else if (outcome == VerifiedDelete.Outcome.Refused)
                {
                    logging.Warn($"Cleanup: '{file}' was not deleted (outside '{parent}' now, or refused); left alone.");
                    skipped++;
                    skippedBytes += size;
                    protectedFiles++;
                    protectedBytes += size;
                }
                else if (outcome == VerifiedDelete.Outcome.TooNew)
                {
                    tooNew++;
                }
            }
            catch (UnauthorizedAccessException)
            {
                var leftBytes = size > 0 ? size : LengthOrZero(file);
                skipped++;
                skippedBytes += leftBytes;
                protectedFiles++;
                protectedBytes += leftBytes;
            }
            catch (IOException)
            {
                skipped++;
                skippedBytes += size > 0 ? size : LengthOrZero(file);
            }
        }

        return new FileTally(freed, deleted, skipped, skippedBytes, protectedFiles, protectedBytes, tooNew);
    }

    /// <summary>
    /// Cleans a Directories target: the file deleter first (it leaves folders that have their own clean command
    /// alone), then those commands. Only a Directories target can carry commands.
    /// </summary>
    private async Task<CleanupOutcome> CleanDirectoriesAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause, IReadOnlyCollection<string>? excludedEntryPaths, bool recycleInsteadOfDelete)
    {
        var outcome = CleanDirectories(target, progress, cancellationToken, pause, excludedEntryPaths, recycleInsteadOfDelete);
        if (target.Kind != CleanupKind.Directories || target.CleanCommands.Count == 0)
            return outcome;

        var commands = await RunDirectoryCleanCommandsAsync(target, excludedEntryPaths, pause, cancellationToken);
        return outcome with
        {
            FreedBytes = outcome.FreedBytes + commands.FreedBytes,
            DeletedFiles = outcome.DeletedFiles + commands.DeletedFiles,
            SkippedFiles = outcome.SkippedFiles + commands.SkippedFiles,
            SkippedBytes = outcome.SkippedBytes + commands.SkippedBytes,
        };
    }

    /// <summary>How the command-managed folders get their tool resolved; tests replace it.</summary>
    internal Func<string, string?> TrustedToolResolver { get; set; } = ProcessRunner.ResolveTrusted;

    /// <summary>How a folder's clean command is started; tests replace it so no real tool runs.</summary>
    internal Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>> ToolRunner { get; set; } =
        (exe, arguments, timeout, token) => ProcessRunner.RunAsync(exe, arguments, timeout, token, killOnTimeout: false);

    /// <summary>The tool for a folder's clean command: the trusted lookup first, then the one fixed fallback
    /// location, accepted only as a plain file reached through no link. Null when neither is usable.</summary>
    private string? ResolveCleanTool(DirectoryCleanCommand command)
    {
        if (TrustedToolResolver(command.ToolName) is { } resolved)
            return resolved;

        if (command.FallbackPath is not { } fallback || !Path.IsPathFullyQualified(fallback))
            return null;

        try
        {
            var info = new FileInfo(fallback);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                || info.Directory is null || CleanupSafety.AncestryLeadsThroughLink(info.Directory.FullName))
            {
                return null;
            }

            return info.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Empties the folders that must go as a whole by running their tool. A folder is only handed to the tool
    /// when every rule that protects the file deleter also holds: the folder passes the safety gate and is
    /// reached through no link, no file in it is younger than the age rule (a tool using the cache right now),
    /// the user left no detail row of it unticked (a whole-folder clean cannot honour that, so it does
    /// nothing), the row is not an elevated one and this is not the elevated worker. Every folder that is not
    /// cleaned counts as not freed. The tool is told the folder explicitly, so neither its own default nor an
    /// environment variable can point it somewhere else.
    /// </summary>
    private async Task<CleanupOutcome> RunDirectoryCleanCommandsAsync(
        CleanupTarget target, IReadOnlyCollection<string>? excludedEntryPaths, PauseGate? pause,
        CancellationToken cancellationToken)
    {
        long freed = 0, skippedBytes = 0;
        int deleted = 0, skipped = 0;
        var cutoff = AgeCutoffUtc(target, DateTime.UtcNow);

        foreach (var directory in target.Directories.Where(d => target.CleanCommands.ContainsKey(d)))
        {
            pause?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsTargetDirectorySafe(target, directory))
                continue;

            var command = target.CleanCommands[directory];
            // uv hardlinks cache files into environments, so a name that vanishes can leave its data on disk.
            // Only a file nothing else links to frees its bytes; one that was already in use that way never does.
            var soleOwned = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var (beforeBytes, beforeFiles, youngFiles) = MeasureFolder(directory, cutoff, cancellationToken, soleOwned);
            var notRun = await TryRunCleanCommandAsync(
                target, directory, command, excludedEntryPaths, youngFiles, cancellationToken);

            if (notRun is not null)
            {
                logging.Warn($"Cleanup: '{directory}' of '{target.Id}' was left as it is; {notRun}.");
                skipped += beforeFiles;
                skippedBytes += beforeBytes;
                continue;
            }

            // What the folder holds now decides what was freed; a tool that said yes but left files behind
            // does not get credit for them.
            var (afterBytes, afterFiles, _) = MeasureFolder(directory, null, cancellationToken);
            foreach (var (file, length) in soleOwned)
            {
                if (!File.Exists(file))
                    freed += length;
            }

            deleted += Math.Max(0, beforeFiles - afterFiles);
            skipped += afterFiles;
            skippedBytes += afterBytes;
        }

        return new CleanupOutcome(freed, deleted, skipped, skippedBytes);
    }

    /// <summary>Runs one folder's clean command. Null when the tool ran and succeeded, otherwise the reason it
    /// did not (for the log).</summary>
    private async Task<string?> TryRunCleanCommandAsync(
        CleanupTarget target, string directory, DirectoryCleanCommand command,
        IReadOnlyCollection<string>? excludedEntryPaths, int youngFiles, CancellationToken cancellationToken)
    {
        if (target.NeedsElevation || ElevationService.IsElevatedWorkerProcess)
            return "an elevated run never starts it";

        // The tool is a user-writable file; an app that itself runs with administrator rights (UAC off, or
        // started as administrator) would start it with those rights.
        if (ElevationService.IsElevated)
            return "this app runs with administrator rights and the tool is a user-writable file";

        if (excludedEntryPaths is not null && excludedEntryPaths.Any(e => IsExcluded(e, [directory])))
            return "a row inside it is unticked and a whole-folder clean cannot keep it";

        if (youngFiles > 0)
            return $"{youngFiles} file(s) in it are younger than {target.MinFileAge}";

        // Released at once: the tool may remove the folder itself, which a held lock would forbid.
        using (var probe = CleanupSafety.TryLockForUse(directory))
        {
            if (probe is null)
                return "it could not be locked for use";
        }

        if (ResolveCleanTool(command) is not { } tool)
            return $"{command.ToolName} was not found";

        var arguments = command.Arguments
            .Select(argument => argument switch
            {
                DirectoryCleanCommand.FolderToken => directory,
                DirectoryCleanCommand.ParentFolderToken => Path.GetDirectoryName(directory) ?? directory,
                _ => argument,
            })
            .ToList();
        try
        {
            var result = await ToolRunner(tool, arguments, command.Timeout, cancellationToken);
            if (result.TimedOut)
                return $"{command.Label} ran past {command.Timeout.TotalSeconds:0} s; it was left to finish on its own";

            return result.ExitCode != 0 ? $"{command.Label} ended with exit code {result.ExitCode}" : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"{command.Label} could not be started: {ex.Message}";
        }
    }

    /// <summary>Size and count of every file below a folder, links not followed, plus how many of them lie
    /// inside the age window (<paramref name="cutoffUtc"/> null counts none).</summary>
    private (long Bytes, int Files, int Young) MeasureFolder(
        string directory, DateTime? cutoffUtc, CancellationToken cancellationToken,
        Dictionary<string, long>? soleOwned = null)
    {
        long bytes = 0;
        var files = 0;
        var young = 0;

        // A clean command may remove the whole root: that is an empty folder, not a listing failure.
        if (!Directory.Exists(directory))
            return (bytes, files, young);

        foreach (var found in EnumerateFileDetails(directory, [], cancellationToken))
        {
            bytes += found.Length;
            files++;
            if (soleOwned is not null && VerifiedDelete.HasSingleLink(found.Path))
                soleOwned[found.Path] = found.Length;

            if (cutoffUtc is { } limit && IsTooNew(found.CreationUtc, found.LastWriteUtc, limit))
                young++;
        }

        return (bytes, files, young);
    }

    /// <summary>
    /// The recycle-bin option never applies to a target that needs elevation, even when the user has it
    /// switched on: a locked-down system location's files stay a permanent delete, no exceptions. Pulled
    /// out as its own pure function (no I/O) so the POLICY can be proven by a test without that test having
    /// to move a real file anywhere.
    /// </summary>
    public static bool ShouldUseRecycleBin(bool recycleInsteadOfDelete, bool targetNeedsElevation) =>
        recycleInsteadOfDelete && !targetNeedsElevation;

    /// <summary>
    /// True for anything at or below one of the target's excluded directories: the paths another target
    /// owns. Asked about files and about subdirectories alike, so an excluded folder is neither
    /// measured, emptied, nor removed by the target that merely contains it.
    /// </summary>
    private static bool IsExcluded(string path, IReadOnlyList<string> excluded)
    {
        foreach (var root in excluded)
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Test seam: called with the directory each time <see cref="EnumerateFiles"/> starts walking
    /// one, so a test can prove a cleanup lists every directory once. Per async flow.</summary>
    internal static readonly AsyncLocal<Action<string>?> EnumerateFilesHookForTest = new();

    /// <summary>Test seam: called with a file's path right before it is opened for the verified delete, so
    /// a test can swap a folder below the locked root for a junction in the gap between listing and
    /// deleting. Per async flow.</summary>
    internal static readonly AsyncLocal<Action<string>?> BeforeFileDeleteForTest = new();

    /// <summary>Enumerates lazily and tolerates an unreadable subtree, which a plain recursive call cannot.</summary>
    private IEnumerable<string> EnumerateFiles(
        string directory, IReadOnlyList<string> excluded, CancellationToken cancellationToken)
    {
        EnumerateFilesHookForTest.Value?.Invoke(directory);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(directory, "*", options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Cleanup: cannot enumerate '{directory}': {ex.Message}");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string current;
                try
                {
                    if (!enumerator.MoveNext())
                        break;

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A .NET file enumerator cannot be resumed once MoveNext has thrown, so this directory's
                    // listing ends here. Said in the log, because the files after this point were not seen.
                    logging.Warn($"Cleanup: the listing of '{directory}' stopped early: {ex.Message}");
                    break;
                }

                if (IsExcluded(current, excluded))
                    continue;

                yield return current;
            }
        }
    }

    /// <summary>One listed file with the facts the directory listing already carries.</summary>
    private readonly record struct ListedFile(string Path, long Length, DateTime LastWriteUtc, DateTime CreationUtc);

    /// <summary>
    /// The same walk as <see cref="EnumerateFiles"/> (same options, same exclusion, same tolerance for an
    /// unreadable subtree), but each file arrives with its length and write time taken from the listing, so
    /// measuring needs no second call per file.
    /// </summary>
    private IEnumerable<ListedFile> EnumerateFileDetails(
        string directory, IReadOnlyList<string> excluded, CancellationToken cancellationToken)
    {
        EnumerateFilesHookForTest.Value?.Invoke(directory);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        IEnumerator<ListedFile> enumerator;
        try
        {
            enumerator = new FileSystemEnumerable<ListedFile>(
                directory,
                static (ref FileSystemEntry entry) =>
                    new ListedFile(
                        entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime,
                        entry.CreationTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
                ShouldRecursePredicate = RecursePredicate<ListedFile>(excluded),
            }.GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Cleanup: cannot enumerate '{directory}': {ex.Message}");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ListedFile current;
                try
                {
                    if (!enumerator.MoveNext())
                        break;

                    current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logging.Warn($"Cleanup: the listing of '{directory}' stopped early: {ex.Message}");
                    break;
                }

                if (excluded.Count > 0 && IsExcluded(current.Path, excluded))
                    continue;

                yield return current;
            }
        }
    }

    /// <summary>Stops a walk from entering an excluded folder: every file below it would be dropped one by one
    /// after being listed. Null (the default, recurse everywhere) when nothing is excluded.</summary>
    private static FileSystemEnumerable<T>.FindPredicate? RecursePredicate<T>(IReadOnlyList<string> excluded) =>
        excluded.Count == 0
            ? null
            : (ref FileSystemEntry entry) => !IsExcluded(entry.ToFullPath(), excluded);

    /// <summary>Every nested subdirectory, any depth: the caller sorts by depth to walk bottom-up.</summary>
    private IEnumerable<string> EnumerateAllDirectories(string directory, IReadOnlyList<string> excluded)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            return new FileSystemEnumerable<string>(
                directory, static (ref FileSystemEntry entry) => entry.ToFullPath(), options)
            {
                ShouldIncludePredicate = excluded.Count == 0
                    ? static (ref FileSystemEntry entry) => entry.IsDirectory
                    : (ref FileSystemEntry entry) => entry.IsDirectory && !IsExcluded(entry.ToFullPath(), excluded),
                ShouldRecursePredicate = RecursePredicate<string>(excluded),
            }.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Cleanup: cannot list '{directory}': {ex.Message}");
            return [];
        }
    }

    /// <summary>Finds plausible leftovers (see <see cref="LeftoverInstallDiscovery"/>) without touching
    /// anything. Files carries the candidate count for the row's summary text; Candidates carries the
    /// actual list the UI must show individually.</summary>
    private CleanupScanResult ScanLeftovers(CancellationToken cancellationToken)
    {
        var candidates = LeftoverInstallDiscovery.DiscoverCandidates(DateTimeOffset.UtcNow, cancellationToken);
        var bytes = candidates.Sum(c => c.SizeBytes);

        return new CleanupScanResult(bytes, candidates.Count, 0) { Candidates = candidates };
    }

    /// <summary>
    /// Deletes ONLY the paths the user explicitly ticked, never "everything the last scan found", which
    /// is what every other target's clean means but this one may never mean. Each
    /// selected path is re-judged against a FRESH discovery pass right before deleting: real time passes
    /// while the user reviews the list, same TOCTOU reasoning as the registry cleaner's re-scan-after-
    /// backup. A path that stops looking like a plausible leftover (something now uses it, or a real
    /// install appeared) is silently skipped rather than deleted anyway. Deletion goes through the
    /// recycle bin, never a permanent delete: the one thing that makes a wrong judgment recoverable.
    /// </summary>
    private CleanupOutcome CleanLeftovers(IReadOnlyCollection<string>? selectedPaths, CancellationToken cancellationToken)
    {
        if (selectedPaths is not { Count: > 0 })
            return new CleanupOutcome(0, 0, 0);

        var fresh = LeftoverInstallDiscovery.DiscoverCandidates(DateTimeOffset.UtcNow, cancellationToken)
            .ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);

        long freed = 0;
        var deleted = 0;
        var skipped = 0;

        foreach (var path in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!fresh.TryGetValue(path, out var candidate))
            {
                skipped++;
                continue;
            }

            if (RecycleBinDeleter.DeleteToRecycleBin(candidate.Path, logging, cancellationToken))
            {
                freed += candidate.SizeBytes;
                deleted++;
            }
            else
            {
                skipped++;
            }
        }

        return new CleanupOutcome(freed, deleted, skipped);
    }
}
