using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Measures and frees disk space for the cleanup dialog. Two rules run through all of
/// it: every path passes <see cref="CleanupSafety"/> before anything is deleted, and a file that is locked
/// or refused is COUNTED and skipped rather than aborting the run; on a live Windows machine that happens
/// constantly and is entirely normal.
/// </summary>
public class CleanupService(LoggingService logging)
{
    /// <summary>Measures one target without changing anything. <paramref name="recycleBinMinAgeDays"/>
    /// only matters for <see cref="CleanupKind.RecycleBin"/>; every other kind ignores it. Virtual so
    /// tests can stand in a fake that returns instantly instead of measuring the real machine:
    /// the real implementation walks real directories, the real registry and the real recycle bin, which
    /// on a live dev machine can run for minutes, far past a test's own patience.</summary>
    public virtual Task<CleanupScanResult> ScanAsync(
        CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
        Task.Run(() =>
        {
            // A scan nobody is waiting on (the startup prescan, no window attached) runs in background
            // thread mode to keep the rest of the machine responsive. A scan for an open window does not:
            // somebody is looking at its spinner. The walks are fully synchronous, so the mode ends on the
            // same pool thread before it is handed back.
            using (BackgroundScan.Value ? BackgroundWork.Enter() : null)
            {
                return target.Kind switch
                {
                    CleanupKind.Directories or CleanupKind.AppCacheSweep => ScanDirectories(target, cancellationToken),
                    CleanupKind.RecycleBin => ScanRecycleBin(cancellationToken, recycleBinMinAgeDays),
                    CleanupKind.RegistryCleanup => ScanRegistry(target, cancellationToken),
                    CleanupKind.LeftoverInstalls => ScanLeftovers(cancellationToken),
                    CleanupKind.DriverCleanup => ScanDrivers(cancellationToken),
                    CleanupKind.PipInstallLeftovers => ScanPipLeftovers(cancellationToken),
                    CleanupKind.OldPythonVersions => ScanOldPythonVersions(cancellationToken),
                    // Disk Cleanup reports nothing before it runs, and guessing a figure would be a lie.
                    _ => new CleanupScanResult(0, 0, 0),
                };
            }
        }, cancellationToken);

    /// <summary>Carries the caller's priority choice into <see cref="ScanAsync(CleanupTarget, CancellationToken, int)"/>
    /// without changing its signature (tests override it). <c>Task.Run</c> copies the value into the pool
    /// thread's context.</summary>
    private static readonly AsyncLocal<bool> BackgroundScan = new();

    /// <summary>How often a file pass reports what it has freed so far: about four times a second.</summary>
    internal TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Test seam: what the caller asked for, readable from inside an overridden
    /// <see cref="ScanAsync(CleanupTarget, CancellationToken, int)"/> that stands in for the real walk.</summary>
    internal static bool BackgroundRequestedForTest => BackgroundScan.Value;

    /// <summary>
    /// Measures one target and says whether it may run in background thread mode: true only for a scan
    /// nobody waits on (the startup prescan). The plain overload always runs at normal priority.
    /// </summary>
    public Task<CleanupScanResult> ScanAsync(
        CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays, bool background)
    {
        BackgroundScan.Value = background;
        try
        {
            return ScanAsync(target, cancellationToken, recycleBinMinAgeDays);
        }
        finally
        {
            BackgroundScan.Value = false;
        }
    }

    /// <summary>
    /// Frees one target. NEVER call this without the user having confirmed the warning first: that
    /// confirmation is the whole safety model.
    /// <paramref name="registrySettings"/> only matters for <see cref="CleanupKind.RegistryCleanup"/>;
    /// <paramref name="selectedLeftoverPaths"/> only for <see cref="CleanupKind.LeftoverInstalls"/>; every
    /// other kind ignores the one that is not its own. <paramref name="recycleInsteadOfDelete"/> only applies
    /// to a not-<see cref="CleanupTarget.NeedsElevation"/> Directories/AppCacheSweep target
    /// (<see cref="CleanDirectories"/> re-checks that itself, so passing true elsewhere does nothing).
    /// <paramref name="excludedEntryPaths"/> applies to EVERY kind with a detail list: it is the user's own
    /// "leave this one alone". <paramref name="confirmedEntryPaths"/> (recycle bin only) is the set of rows the
    /// user saw and kept ticked; null means the caller gives no such set. <paramref name="recycleBinMinAgeDays"/> only matters for
    /// <see cref="CleanupKind.RecycleBin"/> and must be the SAME value the scan behind
    /// <paramref name="excludedEntryPaths"/> used, or the fresh re-scan right before deleting could
    /// rediscover an item the user never saw and delete it unseen.
    /// </summary>
    public virtual Task<CleanupOutcome> CleanAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
        IReadOnlyCollection<string>? selectedLeftoverPaths = null,
        IReadOnlyList<RegistryFinding>? registryFindings = null,
        IReadOnlyCollection<string>? excludedEntryPaths = null,
        bool recycleInsteadOfDelete = false,
        int recycleBinMinAgeDays = 0,
        IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
        IReadOnlyCollection<string>? selectedDriverPackageNames = null,
        IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
        IReadOnlyCollection<string>? confirmedEntryPaths = null) =>
        Task.Run(async () => target.Kind switch
        {
            CleanupKind.Directories or CleanupKind.AppCacheSweep =>
                CleanDirectories(target, progress, cancellationToken, pause, excludedEntryPaths, recycleInsteadOfDelete),
            CleanupKind.RecycleBin => EmptyRecycleBin(
                excludedEntryPaths, confirmedEntryPaths, recycleBinMinAgeDays, progress, cancellationToken, pause),
            CleanupKind.DiskCleanup => await RunDiskCleanupAsync(cancellationToken),
            CleanupKind.SystemRepair => await RunSystemRepairAsync(target, progress, cancellationToken, pause),
            CleanupKind.RegistryCleanup =>
                await CleanRegistryAsync(target, registrySettings, registryFindings, excludedEntryPaths, cancellationToken),
            CleanupKind.LeftoverInstalls => CleanLeftovers(selectedLeftoverPaths, cancellationToken),
            CleanupKind.Defragmentation => await RunDefragAsync(target, progress, cancellationToken),
            CleanupKind.DriverCleanup =>
                await CleanDriversAsync(selectedDriverDeviceInstanceIds, selectedDriverPackageNames, cancellationToken),
            CleanupKind.PipInstallLeftovers => CleanPipLeftovers(excludedEntryPaths, cancellationToken),
            CleanupKind.OldPythonVersions => await CleanOldPythonVersionsAsync(selectedPythonVersionPaths, cancellationToken),
            _ => new CleanupOutcome(0, 0, 0),
        }, cancellationToken);

    /// <summary>
    /// The exact command line <see cref="AnalyzeComponentStoreAsync"/> runs, split out so a test can
    /// assert on it without launching DISM. <c>/English</c> forces DISM's message text to English on any
    /// Windows display language; the German labels <see cref="ComponentStoreAnalysisParser"/> carries are
    /// only a defensive fallback.
    /// </summary>
    internal static readonly IReadOnlyList<string> AnalyzeComponentStoreArguments =
        ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"];

    /// <summary>
    /// Read-only measurement of the WinSxS component store via DISM's own AnalyzeComponentStore switch.
    /// Needs administrator rights like every other DISM call in this file; the caller is
    /// responsible for only ever running this from the elevated worker. Unlike
    /// <see cref="RunSystemRepairAsync"/> this writes nothing to the component store, so cancelling it
    /// mid-run is fine and no <see cref="PauseGate"/> is offered, there is nothing to pause.
    /// </summary>
    public async Task<ComponentStoreAnalysis> AnalyzeComponentStoreAsync(CancellationToken cancellationToken)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Dism.exe");
        if (!File.Exists(dism))
        {
            logging.Warn("Cleanup: component store analysis skipped; Dism.exe was not found.");
            return ComponentStoreAnalysis.Unknown;
        }

        try
        {
            // Decoded losslessly as Latin1 (1:1 byte-to-char), not the UTF-8 every other
            // ProcessRunner caller gets, because a real German-language run's bytes turned out to be the
            // machine's own OEM code page, and recovering THAT shape needs the original bytes, which a
            // UTF-8 decode already destroys (every OEM byte becomes an unrecoverable U+FFFD). See
            // OemTextRecovery's own doc comment for the full story.
            var result = await ProcessRunner.RunAsync(
                dism, AnalyzeComponentStoreArguments,
                TimeSpan.FromMinutes(5), cancellationToken, outputEncoding: Encoding.Latin1);

            // A killed-after-5-minutes DISM must read as "this timed out", never as a run that "finished"
            // with a stray -1 exit code (result.ExitCode is meaningless once TimedOut is set, the process
            // was killed rather than exiting) or as one that quietly reported nothing at all.
            if (result.TimedOut)
            {
                logging.Warn("Cleanup: component store analysis timed out after 5 minutes and was stopped.");
                return ComponentStoreAnalysis.TimedOutResult;
            }

            if (result.ExitCode != 0)
                logging.Info($"Cleanup: component store analysis finished with exit code {result.ExitCode}.");

            // DISM writes UTF-16LE once it notices its output is redirected (the shape RunStreamedStepAsync
            // already recovers). This whole-output capture needs the same recovery, or every label match
            // fails silently and the card shows dashes. Both recovery shapes are safe to apply
            // unconditionally: an ordinary ASCII run trips neither heuristic and passes through untouched.
            var output = result.StandardOutput;
            if (Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(output))
                output = Utf16RedirectRecovery.Recover(output);
            else if (OemTextRecovery.NeedsRecovery(output))
                output = OemTextRecovery.Recover(output);

            var analysis = ComponentStoreAnalysisParser.Parse(output);

            logging.Info(analysis.ActualSizeBytes is null && analysis.SharedWithWindowsBytes is null
                ? "Cleanup: component store analysis completed, but DISM reported no sizes."
                : $"Cleanup: component store analysis completed, actual={analysis.ActualSizeBytes?.ToString() ?? "?"} " +
                  $"shared={analysis.SharedWithWindowsBytes?.ToString() ?? "?"} " +
                  $"recommended={analysis.CleanupRecommended?.ToString() ?? "?"}.");

            return analysis;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logging.Warn($"Cleanup: component store analysis could not be run: {ex.Message}");
            return ComponentStoreAnalysis.Unknown;
        }
    }

    /// <summary>The fixed list for every ordinary target; discovered fresh for <see cref="CleanupKind.AppCacheSweep"/>,
    /// re-discovered rather than cached, so a clean run sees exactly what is on disk right now.</summary>
    private static IReadOnlyList<string> ResolveDirectories(CleanupTarget target, CancellationToken cancellationToken) =>
        target.Kind == CleanupKind.AppCacheSweep
            ? AppCacheDiscovery.DiscoverCacheDirectories(cancellationToken)
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

    private CleanupScanResult ScanDirectories(CleanupTarget target, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var files = 0;
        var skipped = 0;
        var isAppCacheSweep = target.Kind == CleanupKind.AppCacheSweep;
        var buckets = new Dictionary<string, EntryAccumulator>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<(uint VolumeSerial, ulong FileIndex)>();

        foreach (var directory in ResolveDirectories(target, cancellationToken))
        {
            if (!CleanupSafety.IsSafeToEmpty(directory))
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

                bytes += found.Length;
                files++;

                var bucket = BucketFor(directory, file, isAppCacheSweep);
                if (!buckets.TryGetValue(bucket, out var accumulator))
                    accumulator = buckets[bucket] = new EntryAccumulator();
                accumulator.Add(found.Length, found.LastWriteUtc, Path.GetExtension(file));
            }
        }

        var (entries, omittedCount, omittedBytes) = CapEntries(buckets
            .Select(kv => kv.Value.ToEntry(kv.Key, isAppCacheSweep))
            .OrderByDescending(e => e.Bytes)
            .ToList());

        return new CleanupScanResult(bytes, files, skipped)
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
        var excludedEntries = excludedEntryPaths is { Count: > 0 }
            ? new HashSet<string>(excludedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        var directories = ResolveDirectories(target, cancellationToken)
            .Where(CleanupSafety.IsSafeToEmpty)
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
            var files = EnumerateFiles(candidate, target.ExcludedDirectories, cancellationToken).ToList();
            var filesDone = 0;
            var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();

            foreach (var file in files)
            {
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
                    // The recycle bin path signals failure by return value, never an exception, so it needs
                    // its own success check rather than sharing the catch below. Both routes first prove the
                    // file still lies inside the locked root; a folder swapped for a junction since the
                    // listing would otherwise send the delete elsewhere.
                    VerifiedDelete.Outcome outcome;
                    if (useRecycleBin)
                    {
                        outcome = VerifiedDelete.CheckFile(rootFinalPath!, file);
                        if (outcome == VerifiedDelete.Outcome.Cleared)
                        {
                            size = new FileInfo(file).Length;
                            outcome = RecycleBinDeleter.DeleteToRecycleBin(file, logging, cancellationToken)
                                ? VerifiedDelete.Outcome.Deleted
                                : VerifiedDelete.Outcome.Refused;

                            // A cancelled wait reads as a refusal; the user asked to stop, not to be told
                            // the file is protected.
                            if (outcome == VerifiedDelete.Outcome.Refused)
                                cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    else
                    {
                        outcome = VerifiedDelete.TryDeleteFile(rootFinalPath!, file, out size);
                    }

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

                    // A file that vanished meanwhile is neither deleted nor skipped: this run did nothing to it.
                }
                catch (UnauthorizedAccessException)
                {
                    // Not ours to delete, typically a system file without admin rights: kept apart from
                    // "in use" so the summary does not point at a program that holds nothing.
                    skipped++;
                    skippedBytes += size;
                    protectedFiles++;
                    protectedBytes += size;
                }
                catch (IOException)
                {
                    // In use by another process. Expected; keep going, but its size must not be counted as
                    // freed, and the user is told how much stayed behind.
                    skipped++;
                    skippedBytes += size;
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
                    if (VerifiedDelete.TryDeleteEmptyDirectory(rootFinalPath!, subdirectory) == VerifiedDelete.Outcome.Refused)
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

        if (foldersKept > 0)
            logging.Info($"Cleanup: {foldersKept} folder(s) of '{target.Id}' were not removed (not empty or no access), first {firstFolderKept}");

        return new CleanupOutcome(
            freed, deleted, skipped, skippedBytes, ProtectedFiles: protectedFiles, ProtectedBytes: protectedBytes);
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
    private readonly record struct ListedFile(string Path, long Length, DateTime LastWriteUtc);

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
                    new ListedFile(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory,
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
            return Directory.EnumerateDirectories(directory, "*", options)
                .Where(d => !IsExcluded(d, excluded))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Cleanup: cannot list '{directory}': {ex.Message}");
            return [];
        }
    }

    // Recycle bin: the shell owns it, so the shell API is the only correct way to measure and empty it.

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    /// <summary>Test-only override of the fixed drives <see cref="ScanRecycleBin"/> walks. Null (the only
    /// value ever set in production) means the real machine's own fixed drives; a test that needs an
    /// isolated recycle bin sets this to a fake root/label pair for the lifetime of its call and restores
    /// it to null afterwards. Exists so a scan can be proven against a fake recycle-bin root instead of the
    /// host's real one, which otherwise holds an unknown, unbounded number of entries. Scan-only: while set,
    /// <see cref="EmptyRecycleBin"/> refuses to run rather than act on a faked drive root.</summary>
    internal static IReadOnlyList<(string Root, string Label)>? RecycleBinDriveRootsOverride { get; set; }

    /// <summary>
    /// Every deleted file the user still has, one row each, grouped by drive: the bin's own <c>$I</c> index
    /// files are read (<see cref="RecycleBinScanner"/>) so a row can name the file, its origin and when it
    /// went. FIXED drives only. A drive whose bin cannot be enumerated falls back to the shell's aggregate
    /// figure as ONE row (the only case where a row is not an individual file); a drive that fails even
    /// that is skipped rather than failing the scan. Bytes/Files stay the TRUE combined total either way.
    /// <paramref name="minAgeDays"/> filters individually enumerated items before anything else is computed
    /// from them; the aggregate fallback row carries no per-item date and is never filtered.
    /// </summary>
    private CleanupScanResult ScanRecycleBin(CancellationToken cancellationToken, int minAgeDays = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var ownSid = RecycleBinScanner.TryGetOwnSid();
        var drives = new List<(string Root, string Label, IReadOnlyList<RecycleBinItem>? Items, long ShellBytes, int ShellItems, bool AgeFilterHidItems, int SkippedIndexCount)>();

        var driveRoots = RecycleBinDriveRootsOverride ?? DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => (Root: drive.RootDirectory.FullName, Label: DriveEntryDisplayName(drive.Name, TryGetVolumeLabel(drive))))
            .ToList();

        foreach (var (root, label) in driveRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ownSid is not null
                && RecycleBinScanner.TryEnumerate(root, ownSid, cancellationToken, out var items, out var skippedIndexCount))
            {
                var filtered = items
                    .Where(item => RecycleBinAgeFilter.PassesAgeFilter(item.DeletedAt, minAgeDays, now))
                    .ToList();
                drives.Add((root, label, filtered, 0, 0, filtered.Count != items.Count, skippedIndexCount));
                continue;
            }

            if (TryQueryDrive(root, out var shellBytes, out var shellItems))
                drives.Add((root, label, null, shellBytes, shellItems, minAgeDays > 0, 0));
        }

        var budgets = SplitEntryBudget(drives
            .Where(d => d.Items is { Count: > 0 })
            .ToDictionary(d => d.Root, d => d.Items!.Count, StringComparer.OrdinalIgnoreCase));

        long totalBytes = 0;
        var totalItems = 0;
        var entries = new List<CleanupEntry>();
        var summaries = new List<RecycleBinDriveSummary>();

        foreach (var drive in drives)
        {
            if (drive.Items is null)
            {
                totalBytes += drive.ShellBytes;
                totalItems += drive.ShellItems;

                // A count, not a size: a bin holding only empty files still has something to empty.
                if (drive.ShellItems <= 0)
                    continue;

                entries.Add(new CleanupEntry(
                    drive.Root, drive.Label, drive.ShellBytes, drive.ShellItems,
                    DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other));
                summaries.Add(new RecycleBinDriveSummary(
                    drive.Root, drive.Label, 1, 1, drive.ShellBytes, drive.ShellBytes, DetailsAvailable: false,
                    AgeFilterHidItems: drive.AgeFilterHidItems));
                continue;
            }

            var found = drive.Items.OrderByDescending(i => i.SizeBytes).ToList();
            totalBytes += found.Sum(i => i.SizeBytes);
            totalItems += found.Count;
            if (found.Count == 0)
                continue;

            var listed = found.Take(budgets.TryGetValue(drive.Root, out var budget) ? budget : found.Count).ToList();
            entries.AddRange(listed.Select(item => new CleanupEntry(
                item.ContentPath,
                Path.GetFileName(item.OriginalPath.TrimEnd(Path.DirectorySeparatorChar)),
                item.SizeBytes,
                1,
                item.DeletedAt,
                item.DeletedAt,
                CleanupEntryClassifier.Classify(item.OriginalPath, [Path.GetExtension(item.OriginalPath)]),
                item.OriginalPath)));
            summaries.Add(new RecycleBinDriveSummary(
                drive.Root, drive.Label, found.Count, listed.Count,
                found.Sum(i => i.SizeBytes), listed.Sum(i => i.SizeBytes), DetailsAvailable: true,
                AgeFilterHidItems: drive.AgeFilterHidItems, SkippedIndexCount: drive.SkippedIndexCount));
        }

        return new CleanupScanResult(totalBytes, totalItems, 0)
        {
            Entries = entries,
            RecycleBinDrives = summaries,
            OmittedEntryCount = summaries.Sum(s => s.OmittedCount),
            OmittedEntryBytes = summaries.Sum(s => s.OmittedBytes),
        };
    }

    /// <summary>Smallest share a drive with content may get, however many drives there are: a handful of
    /// rows would be worse than no list at all.</summary>
    private const int MinEntriesPerDrive = 50;

    /// <summary>
    /// How the <see cref="MaxEntries"/> row budget is split between drives: an equal share each (never
    /// below <see cref="MinEntriesPerDrive"/>), and whatever a drive with fewer items than its share does
    /// not need is handed to the drives that do. Pure, so the split can be proven without a recycle bin.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> SplitEntryBudget(IReadOnlyDictionary<string, int> foundPerDrive)
    {
        var withContent = foundPerDrive
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var budgets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (withContent.Count == 0)
            return budgets;

        var share = Math.Max(MinEntriesPerDrive, MaxEntries / withContent.Count);
        foreach (var drive in withContent)
            budgets[drive.Key] = Math.Min(drive.Value, share);

        var spare = withContent.Sum(drive => share - budgets[drive.Key]);
        while (spare > 0)
        {
            var needy = withContent.Where(drive => budgets[drive.Key] < drive.Value).ToList();
            if (needy.Count == 0)
                break;

            var each = Math.Max(1, spare / needy.Count);
            foreach (var drive in needy)
            {
                if (spare <= 0)
                    break;

                var give = Math.Min(each, Math.Min(spare, drive.Value - budgets[drive.Key]));
                budgets[drive.Key] += give;
                spare -= give;
            }
        }

        return budgets;
    }

    private bool TryQueryDrive(string root, out long bytes, out int items)
    {
        bytes = 0;
        items = 0;

        try
        {
            var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            if (SHQueryRecycleBin(root, ref info) != 0)
                return false;

            bytes = info.i64Size;
            items = (int)Math.Min(info.i64NumItems, int.MaxValue);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logging.Warn($"Cleanup: recycle bin could not be queried: {ex.Message}");
            return false;
        }
        catch (IOException)
        {
            // this one drive vanished or refused between GetDrives() and the query; skip it, not the scan
            return false;
        }
    }

    /// <summary>A drive with a real name over the bare letter, otherwise just the letter;
    /// <see cref="DriveInfo.VolumeLabel"/> throws for an unready/inaccessible drive, which is why the
    /// caller reads it through a try/catch rather than inline.</summary>
    internal static string DriveEntryDisplayName(string driveName, string? volumeLabel) =>
        string.IsNullOrWhiteSpace(volumeLabel) ? driveName : $"{driveName} ({volumeLabel})";

    /// <summary>Internal rather than private: <see cref="CleanupCatalog"/>'s per-drive defragmentation
    /// targets need the exact same "unready drive throws" handling.</summary>
    internal static string? TryGetVolumeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>How one drive's bin is emptied: the shell's whole-bin call, or exactly these rows.</summary>
    internal readonly record struct RecycleBinDrivePlan(
        string Root, string Label, bool UseShellEmpty, IReadOnlyList<CleanupEntry> Entries);

    /// <summary>
    /// Which drives are emptied and how. The shell's fast path is only taken for a drive where every listed
    /// row is still selected AND nothing was left off the list: it removes the whole bin, including items
    /// past the per-drive budget that the user never saw. Anything less takes the per-row path, which
    /// deletes exactly what is selected and nothing else. A drive with nothing selected is not touched at
    /// all: it is not "skipped" either, the user simply kept it. Pure, so both decisions can be proven
    /// without a real recycle bin.
    /// </summary>
    internal static IReadOnlyList<RecycleBinDrivePlan> PlanRecycleBinEmpty(
        IReadOnlyList<CleanupEntry> entries,
        IReadOnlyList<RecycleBinDriveSummary> drives,
        IReadOnlyCollection<string>? excludedEntryPaths,
        IReadOnlyCollection<string>? confirmedEntryPaths = null)
    {
        var excluded = excludedEntryPaths is { Count: > 0 }
            ? new HashSet<string>(excludedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        // What the user saw and confirmed in the dialog. The plan is made from a fresh scan, and an item
        // recycled since then was never shown: it may not be deleted, neither row by row nor by the
        // shell's whole-bin call.
        var confirmed = confirmedEntryPaths is not null
            ? new HashSet<string>(confirmedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        var plans = new List<RecycleBinDrivePlan>();
        foreach (var drive in drives)
        {
            var selected = entries
                .Where(entry => IsOnDrive(entry.Path, drive.Root))
                .Where(entry => excluded is null || !excluded.Contains(entry.Path))
                .Where(entry => confirmed is null || confirmed.Contains(entry.Path))
                .ToList();

            var confirmedOnDrive = confirmed?.Count(path => IsOnDrive(path, drive.Root));

            if (selected.Count == 0)
                continue;

            // A drive whose enumeration failed arrives as ONE aggregate row standing for the whole bin, and
            // no per-file exclusion can ever match a drive root, so "every listed row is still selected" is
            // true here by construction rather than because the user kept everything, and the shell's
            // whole-bin call would permanently destroy the very files they unticked. A selection that cannot
            // be honoured is honoured by touching nothing: the drive is left exactly as it is.
            if (!drive.DetailsAvailable && excluded is not null
                && excludedEntryPaths!.Any(path => IsOnDrive(path, drive.Root)))
                continue;

            // An aggregate row under an active age filter cannot tell old from young, so neither path can
            // honour the filter: the drive is left alone, same as above.
            if (!drive.DetailsAvailable && drive.AgeFilterHidItems)
                continue;

            // Files the age filter kept off the list are still in the bin, and the shell's whole-bin call
            // would delete them too; only the listed rows may go, one by one.
            // An index file that could not be read may stand for an item that is not on the list either.
            // The whole-bin call also takes anything that arrived after the scan, so it needs the bin to hold
            // exactly what was confirmed.
            var complete = !drive.AgeFilterHidItems && drive.SkippedIndexCount == 0
                && drive.FoundCount == drive.ListedCount && selected.Count == drive.ListedCount
                && (confirmedOnDrive is null || confirmedOnDrive == drive.FoundCount);
            plans.Add(new RecycleBinDrivePlan(drive.Root, drive.Label, complete, complete ? [] : selected));
        }

        return plans;
    }

    private static bool IsOnDrive(string path, string root)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
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

    /// <summary>Counts broken references (see <see cref="RegistryCleanupScanner"/>) without touching anything.
    /// A merged row scans every category it covers and reports their findings together; each
    /// finding still carries its own real category (<see cref="RegistryFinding.Category"/>), only the
    /// catalogue row they are grouped under changed.</summary>
    private CleanupScanResult ScanRegistry(CleanupTarget target, CancellationToken cancellationToken)
    {
        if (target.RegistryCategories.Count == 0)
            return new CleanupScanResult(0, 0, 0);

        var findings = target.RegistryCategories
            .SelectMany(category => RegistryCleanupScanner.Scan(category, cancellationToken, logging))
            .ToList();

        // One detail-list row per finding; RegistryEntryPath as the path, the broken reference as the "reason".
        // No byte size (same as Disk Cleanup/System Repair): a registry entry has no
        // meaningful "bytes freed", so the count travels in Files instead; CleanupItemViewModel reads it
        // as an entry count for this one kind rather than as a file count.
        var (entries, omittedCount, omittedBytes) = CapEntries(findings
            .Select(f => new CleanupEntry(
                RegistryEntryPath(f), f.DisplayName, Bytes: 0, Files: 1,
                DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other))
            .ToList());

        return new CleanupScanResult(0, findings.Count, 0)
        {
            RegistryFindings = findings, Entries = entries, OmittedEntryCount = omittedCount, OmittedEntryBytes = omittedBytes,
        };
    }

    /// <summary>
    /// A registry row's detail-list path. Findings that remove one value (SharedDLLs, Run entries) share their
    /// key, so the value name is part of the path; otherwise unticking one of them would exclude them all.
    /// </summary>
    internal static string RegistryEntryPath(RegistryFinding finding) =>
        string.IsNullOrEmpty(finding.ValueName) ? finding.KeyPath : $@"{finding.KeyPath}\{finding.ValueName}";

    /// <summary>
    /// Whether an exclusion list covers a registry finding: by its own row path, or, for a finding that
    /// removes one value, by the bare key path an earlier version stored for those rows (it then excludes
    /// every value finding under that key, as it did back then).
    /// </summary>
    internal static bool IsRegistryFindingExcluded(RegistryFinding finding, ISet<string> excluded) =>
        excluded.Contains(RegistryEntryPath(finding))
        || (!string.IsNullOrEmpty(finding.ValueName) && excluded.Contains(finding.KeyPath));

    /// <summary>
    /// Cleans exactly the findings the dialog's own scan already reported, never a fresh scan: a full
    /// category walk costs real time, and <see cref="RegistryCleanupScanner.Delete"/> already re-judges
    /// each finding against the registry as it is RIGHT NOW immediately before its own delete, the cheap
    /// per-entry TOCTOU guard. A target with nothing to clean returns instantly, with no backup and no
    /// registry read. Otherwise every root key the category could touch is backed up fresh: the backup
    /// must cover the exact state right before the write, not the state when the dialog opened.
    /// </summary>
    private async Task<CleanupOutcome> CleanRegistryAsync(
        CleanupTarget target, Models.Settings.RegistryCleanupSettings? registrySettings,
        IReadOnlyList<RegistryFinding>? findings, IReadOnlyCollection<string>? excludedEntryPaths,
        CancellationToken cancellationToken)
    {
        if (target.RegistryCategories.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        if (findings is not { Count: > 0 })
            return new CleanupOutcome(0, 0, 0);

        // One filter, immediately before the only code below that writes, answering what the findings list
        // cannot: may THIS target delete THIS key? A merged row covers several categories, so membership is
        // checked against the finding's OWN category (never guessed from the target), and that category is
        // what IsUnderRoots re-validates the key path against.
        // Out of scope: the elevated worker hands every selected target the same flat list, while the
        // backup below covers only the roots of the target running now; the first target through would
        // otherwise delete the others' keys before those were backed up.
        // Excluded: a row the user unticked for this run, or excluded permanently from the context menu.
        // A registry row's detail-list path is RegistryEntryPath (see ScanRegistry), matched case-insensitively
        // like CleanDirectories matches a file row's.
        var excluded = excludedEntryPaths is { Count: > 0 }
            ? new HashSet<string>(excludedEntryPaths, StringComparer.OrdinalIgnoreCase)
            : null;

        var cleanable = findings
            .Where(f => target.RegistryCategories.Contains(f.Category)
                && RegistryCleanupScanner.IsUnderRoots(f.Category, f.KeyPath, f.ValueName)
                && (excluded is null || !IsRegistryFindingExcluded(f, excluded)))
            .ToList();

        if (cleanable.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        if (registrySettings is null)
        {
            logging.Error($"Registry cleanup: no backup settings available for '{target.Id}'; refusing to write.");
            return new CleanupOutcome(0, 0, 0);
        }

        // One combined backup ahead of the whole row's write, covering every root any of its categories
        // could touch; Distinct because Autostart/Uninstall's own root lists already repeat across HKLM and
        // HKCU views and a merged row must not back up (or count) the same key twice.
        var rootKeys = target.RegistryCategories
            .SelectMany(RegistryCleanupScanner.RootKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var backup = await RegistryBackupService.BackupRootsBeforeWriteAsync(
            rootKeys, registrySettings.BackupFolder, registrySettings.RetentionCount, logging, cancellationToken);

        if (backup is null)
        {
            logging.Error($"Registry cleanup: backup failed or found nothing for '{target.Id}'; nothing was deleted.");
            return new CleanupOutcome(0, 0, 0);
        }

        logging.Info($"Registry cleanup: backed up {backup.ExportedRoots.Count} key(s) to '{backup.Path}' before cleaning '{target.Id}'.");

        // Only what the backup actually holds may be deleted: a root that was a link or absent when the
        // backup ran is not in it, whatever it looks like by the time the delete starts.
        var (covered, notCovered) = SplitByExportedRoots(cleanable, backup.ExportedRoots);
        if (notCovered > 0)
            logging.Warn($"Registry cleanup: {notCovered} finding(s) lie under a key that was not backed up; leaving them alone.");

        var deleted = RegistryCleanupScanner.Delete(covered, logging, cancellationToken);
        return new CleanupOutcome(0, deleted, cleanable.Count - deleted);
    }

    /// <summary>The findings that lie on or under one of the exported roots (matched on a whole path
    /// segment, case-insensitive), and how many were left out.</summary>
    internal static (IReadOnlyList<RegistryFinding> Covered, int NotCovered) SplitByExportedRoots(
        IReadOnlyList<RegistryFinding> findings, IReadOnlyList<string> exportedRoots)
    {
        var covered = findings
            .Where(f => exportedRoots.Any(root =>
                f.KeyPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                || f.KeyPath.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return (covered, findings.Count - covered.Count);
    }

    /// <summary>Disconnected devices plus old driver packages, gathered fresh every scan the same
    /// way <see cref="ScanRegistry"/> re-reads the registry every time. No byte total, same as a registry
    /// finding: the count travels in Files.</summary>
    private static CleanupScanResult ScanDrivers(CancellationToken cancellationToken)
    {
        IDriverInventoryProvider provider = new SetupApiDriverInventoryProvider();
        var devices = provider.GatherDevices(cancellationToken);
        var packages = provider.GatherPackages(cancellationToken);

        var disconnectedDevices = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, packages);
        var oldPackages = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        return new CleanupScanResult(0, disconnectedDevices.Count + oldPackages.Count, 0)
        {
            DriverDeviceCandidates = disconnectedDevices,
            DriverPackageCandidates = oldPackages,
        };
    }

    /// <summary>
    /// Cleans exactly the rows the user selected, re-checked against a FRESH inventory immediately before
    /// pnputil is ever called; see <see cref="DriverCleanupExecutor"/> for the full validation.
    /// </summary>
    private async Task<CleanupOutcome> CleanDriversAsync(
        IReadOnlyCollection<string>? selectedDeviceInstanceIds, IReadOnlyCollection<string>? selectedPackageNames,
        CancellationToken cancellationToken)
    {
        if (selectedDeviceInstanceIds is not { Count: > 0 } && selectedPackageNames is not { Count: > 0 })
            return new CleanupOutcome(0, 0, 0);

        // pnputil needs administrator rights, and staging a backup unelevated would leave a user-owned
        // ProgramData folder behind that every later elevated run then refuses to trust.
        if (!ElevationService.IsElevated)
        {
            var selected = (selectedDeviceInstanceIds?.Count ?? 0) + (selectedPackageNames?.Count ?? 0);
            logging.Warn($"Driver cleanup: {selected} selected item(s) skipped, administrator rights are required.");
            return new CleanupOutcome(0, 0, selected);
        }

        return await DriverCleanupExecutor.CleanAsync(
            new SetupApiDriverInventoryProvider(), selectedDeviceInstanceIds ?? [], selectedPackageNames ?? [],
            logging, cancellationToken);
    }

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

        foreach (var candidate in PythonCleanupDiscovery.DiscoverPipLeftovers(WindowsRegistryView.Instance, measure: false))
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

            foreach (var file in EnumerateFiles(path, [], CancellationToken.None).ToList())
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
    private static CleanupScanResult ScanOldPythonVersions(CancellationToken cancellationToken)
    {
        var candidates = PythonCleanupDiscovery.DiscoverOldVersions(WindowsRegistryView.Instance);
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
            WindowsRegistryView.Instance, selectedPythonExePaths, logging, cancellationToken);
    }

    /// <summary>
    /// Empties each drive's bin according to <see cref="PlanRecycleBinEmpty"/>: the shell's own call for a
    /// drive the user left completely selected, otherwise the selected files one by one. Re-scanned first,
    /// so the plan is made from what is on disk NOW rather than from what the dialog was told minutes ago.
    /// The re-scan uses the SAME <paramref name="minAgeDays"/> the dialog's own scan used, or an item the
    /// filter had hidden from the user could resurface here, unexcluded, and be deleted unseen.
    /// The "move to the recycle bin instead of deleting" setting deliberately does not apply here: these
    /// files ARE in the recycle bin, and there is nowhere further to move them.
    /// </summary>
    private CleanupOutcome EmptyRecycleBin(
        IReadOnlyCollection<string>? excludedEntryPaths, IReadOnlyCollection<string>? confirmedEntryPaths, int minAgeDays, IProgress<CleanupProgress>? progress,
        CancellationToken cancellationToken, PauseGate? pause)
    {
        if (RecycleBinDriveRootsOverride is not null)
            throw new InvalidOperationException(
                $"{nameof(RecycleBinDriveRootsOverride)} is scan-only; {nameof(EmptyRecycleBin)} refuses to run while it is set.");

        var before = ScanRecycleBin(cancellationToken, minAgeDays);
        var plans = PlanRecycleBinEmpty(before.Entries ?? [], before.RecycleBinDrives ?? [], excludedEntryPaths, confirmedEntryPaths);
        var ownSid = RecycleBinScanner.TryGetOwnSid();

        long freed = 0;
        long skippedBytes = 0;
        var deleted = 0;
        var skipped = 0;
        var totalSteps = plans.Sum(plan => plan.UseShellEmpty ? 1 : plan.Entries.Count);
        var stepsDone = 0;

        foreach (var plan in plans)
        {
            if (plan.UseShellEmpty)
            {
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                TryQueryDrive(plan.Root, out var driveBytesBefore, out var driveItemsBefore);
                EmptyDriveThroughShell(plan.Root);

                // Derived from what is ACTUALLY still there afterwards: the shell reports failure by
                // HRESULT and can also simply leave items behind that something else had open.
                TryQueryDrive(plan.Root, out var driveBytesAfter, out var driveItemsAfter);
                freed += Math.Max(0, driveBytesBefore - driveBytesAfter);
                deleted += Math.Max(0, driveItemsBefore - driveItemsAfter);
                skipped += Math.Max(0, driveItemsAfter);
                skippedBytes += Math.Max(0, driveBytesAfter);

                stepsDone++;
                progress?.Report(new CleanupProgress(plan.Label, Percent(stepsDone, totalSteps), freed));
                continue;
            }

            foreach (var entry in plan.Entries)
            {
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stepsDone++;

                if (DeleteRecycledItem(entry, ownSid))
                {
                    freed += entry.Bytes;
                    deleted++;
                }
                else
                {
                    skipped++;
                    skippedBytes += entry.Bytes;
                }

                // The ORIGINAL path, never the $R name: "$RA1B2C3.tmp" tells the user nothing about which
                // of their files just went.
                progress?.Report(new CleanupProgress(
                    entry.OriginalPath ?? entry.Path, Percent(stepsDone, totalSteps), freed));
            }
        }

        return new CleanupOutcome(freed, deleted, skipped, skippedBytes);
    }

    private static double Percent(int done, int total) => total > 0 ? done * 100.0 / total : 100;

    private void EmptyDriveThroughShell(string root)
    {
        try
        {
            // The app's own confirmation already happened; a second shell prompt would be asked twice.
            var hr = SHEmptyRecycleBin(IntPtr.Zero, root, NoConfirmation | NoProgressUi | NoSound);
            if (hr != 0)
                logging.Warn($"Cleanup: emptying the recycle bin for '{root}' returned 0x{hr:X8}.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            logging.Warn($"Cleanup: recycle bin could not be emptied: {ex.Message}");
        }
    }

    /// <summary>Clears the read-only attribute on every file below <paramref name="directory"/>. Links are
    /// neither followed nor touched (their attributes would be set on the target): they are skipped, as is
    /// everything below a linked folder, and Directory.Delete removes a link itself without following it.</summary>
    private static void ClearReadOnlyAttributes(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            ClearReadOnlyAttribute(file);
    }

    private static void ClearReadOnlyAttribute(string file)
    {
        try
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The delete that follows reports it.
        }
    }

    /// <summary>
    /// Removes every link (junction, symlink, any reparse point) below <paramref name="root"/>, and the root
    /// itself when it is one, WITHOUT following any of them: only the link goes, never what it points at.
    /// A user can plant a link inside a folder that sits in the recycle bin, and the recursive delete that
    /// follows must find nothing in it that leads out of the tree.
    /// </summary>
    internal static void UnlinkReparsePoints(string root)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (IsReparsePoint(directory))
            {
                Directory.Delete(directory, recursive: false);
                continue;
            }

            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (isDirectory)
                    {
                        Directory.Delete(entry.FullName, recursive: false);
                    }
                    else
                    {
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                        entry.Delete();
                    }
                }
                else if (isDirectory)
                {
                    pending.Push(entry.FullName);
                }
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// One recycled item: its content, then the index file describing it. Both paths go through
    /// <see cref="CleanupSafety.IsInsideOwnRecycleBin"/> immediately before the delete call: a row whose
    /// path does not sit inside THIS user's own bin is refused outright, however it got here. A row with no
    /// original path is the aggregate fallback row for a drive that could not be enumerated; deleting its
    /// path would mean deleting a drive root, so it is never attempted.
    /// </summary>
    private bool DeleteRecycledItem(CleanupEntry entry, string? ownSid)
    {
        if (entry.OriginalPath is null)
            return false;

        if (!CleanupSafety.IsInsideOwnRecycleBin(entry.Path, ownSid))
        {
            logging.Warn("Cleanup: a recycle-bin row was refused because its path is not inside this user's own bin.");
            return false;
        }

        try
        {
            if (Directory.Exists(entry.Path))
            {
                // Links first, so nothing the recursive delete meets can lead out of the recycled tree. When
                // the item itself was a link, unlinking it already finished the job.
                UnlinkReparsePoints(entry.Path);
                if (Directory.Exists(entry.Path))
                {
                    try
                    {
                        Directory.Delete(entry.Path, recursive: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Directory.Delete honours the read-only attribute: a recycled git working copy (read-only
                        // pack files) was half deleted and then failed, its $I index left describing an item that
                        // could no longer be restored in full, and every later run failed the same way.
                        ClearReadOnlyAttributes(entry.Path);
                        Directory.Delete(entry.Path, recursive: true);
                    }
                }
            }
            else if (File.Exists(entry.Path))
            {
                ClearReadOnlyAttribute(entry.Path);
                File.Delete(entry.Path);
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // The index file only describes content that is already gone; failing to remove it leaves a
        // harmless orphan the next scan skips, so it never turns a deleted item into a failed one.
        var indexPath = RecycleBinScanner.IndexPathFor(entry.Path);
        if (indexPath is not null && CleanupSafety.IsInsideOwnRecycleBin(indexPath, ownSid))
        {
            try
            {
                File.Delete(indexPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Info($"Recycle bin index delete left a harmless orphan for '{indexPath}': {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Windows' own Disk Cleanup, run WITHOUT its dialogs. It can only run unattended from a saved
    /// profile, so the profile is written first: each handler under VolumeCaches gets a StateFlags value
    /// for our own sageset number, and `/sagerun` then processes exactly those. Only handlers as safe as
    /// the rest of this feature are enabled (temporary files, caches, thumbnails, error reports, delivery
    /// optimisation), NEVER anything that removes user data or a rollback path (no Downloads handler, no
    /// previous-Windows installation, no recycle bin: that is its own opt-in target).
    /// Writing the profile needs admin. Without it the target is skipped with a log line: starting
    /// cleanmgr with its own UI would leave the dialog waiting on a window it cannot cancel.
    /// </summary>
    private async Task<CleanupOutcome> RunDiskCleanupAsync(CancellationToken cancellationToken)
    {
        var cleanmgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe");
        if (!File.Exists(cleanmgr))
        {
            logging.Warn("Cleanup: cleanmgr.exe was not found.");
            return new CleanupOutcome(0, 0, 0);
        }

        // Without admin the profile cannot be written, and starting cleanmgr with its OWN dialogs would
        // leave this dialog waiting on a window the user did not ask for, and unable to cancel it.
        // Skip the target instead and say why.
        var outcome = await RunUnderSagesetProfileAsync(TryWriteSagesetProfile, RemoveSagesetProfile, async () =>
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cleanmgr,
                    // /sagerun processes the saved profile with no prompts; /verylowdisk is not used because it
                    // silently includes handlers we did not choose.
                    Arguments = $"/sagerun:{SagesetNumber}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });

                // Bounded AND cancellable: closing the dialog must not leave it waiting for the full limit.
                if (process is not null)
                {
                    var waited = await WaitForDiskCleanupAsync(process, DiskCleanupLimit, cancellationToken);
                    if (waited is { } early)
                        return early;
                }

                // cleanmgr reports nothing about what it freed, and guessing a figure would be a lie.
                return new CleanupOutcome(0, 0, 0);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logging.Warn($"Cleanup: Disk Cleanup could not be started: {ex.Message}");
                return new CleanupOutcome(0, 0, 0);
            }
        });

        if (outcome is null)
        {
            logging.Info("Cleanup: Disk Cleanup skipped: an unattended run needs administrator rights.");
            return new CleanupOutcome(0, 0, 0);
        }

        return outcome.Value;
    }

    /// <summary>
    /// Keeps the machine-wide profile only for the run itself: a flag left behind would make any later
    /// <c>cleanmgr /sagerun:4711</c> (by hand or another tool) silently run the whole selection again. It is
    /// removed however the run ends (finished, stopped, timed out, failed to start). Returns null when the
    /// profile could not be written, so the caller skips the target.
    /// </summary>
    internal static async Task<CleanupOutcome?> RunUnderSagesetProfileAsync(
        Func<bool> writeProfile, Action removeProfile, Func<Task<CleanupOutcome>> run)
    {
        try
        {
            if (!writeProfile())
                return null;

            return await run();
        }
        finally
        {
            removeProfile();
        }
    }

    /// <summary>Takes our flag off every handler it may have been set on: the counterpart of the write, so
    /// this code stays the only writer of the profile.</summary>
    private void RemoveSagesetProfile()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        var flagName = $"StateFlags{SagesetNumber:0000}";

        try
        {
            using var caches = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root, writable: true);
            if (caches is null)
                return;

            foreach (var handler in SafeVolumeCaches)
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    key?.DeleteValue(flagName, throwOnMissingValue: false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    logging.Info($"Cleanup: could not remove the Disk Cleanup selection from \"{handler}\": {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logging.Info($"Cleanup: could not remove the Disk Cleanup profile: {ex.Message}");
        }
    }

    /// <summary>How long Disk Cleanup may run. "Update Cleanup" alone can take a long while on a machine with
    /// many superseded updates, so the cap is generous; it only exists so a hung cleanmgr cannot hold the
    /// run for ever.</summary>
    internal static readonly TimeSpan DiskCleanupLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Waits for cleanmgr to end. Returns null when it did; otherwise the outcome that ends the target early
    /// (cleanmgr is killed first either way).
    /// </summary>
    internal async Task<CleanupOutcome?> WaitForDiskCleanupAsync(
        System.Diagnostics.Process process, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            // Told apart from the caller's own token: the time limit is a failure, a Stop is not.
            var timedOut = !cancellationToken.IsCancellationRequested;

            // Stop means stop: leaving cleanmgr running after Stop would make the dialog say the run
            // had ended while Windows carried on deleting. Ending it is safe: each file it removes
            // is removed completely, so an abandoned run leaves no half state. The time limit
            // ends it the same way.
            logging.Info(timedOut
                ? $"Cleanup: Disk Cleanup ran longer than {limit.TotalMinutes:0} minutes and was ended."
                : "Cleanup: Disk Cleanup was stopped.");

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                // it finished between the check and the kill, or Windows refused; either way it is gone
            }

            // Caught here rather than left to propagate, because this method still has cleanmgr to
            // kill first. WasStopped tells the caller not to mark the row "done" for a run the user
            // just stopped; TimedOut tells it the run failed instead.
            return new CleanupOutcome(0, 0, 0, WasStopped: !timedOut, TimedOut: timedOut);
        }
    }

    /// <summary>
    /// Windows' own repair pass, run in whatever order the catalogue lists its steps; DISM before sfc
    /// today, because DISM repairs the component store that sfc restores files FROM. Both need admin
    /// and both take a while, so each is run with its own generous timeout and reported as it goes.
    /// They free no measurable space; a repair is not a deletion, and claiming freed bytes here would
    /// be a lie.
    /// </summary>
    private async Task<CleanupOutcome> RunSystemRepairAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause)
    {
        var steps = target.RepairSteps;
        if (steps.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        var done = 0;
        var share = 100.0 / steps.Count;
        foreach (var (exe, args, label) in steps)
        {
            // Held and stopped BETWEEN steps only; see the note on the run itself below.
            pause?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var stepBase = done * share;
            progress?.Report(new CleanupProgress(label, stepBase, 0));

            if (!File.Exists(exe))
            {
                logging.Warn($"Cleanup: {label} skipped; {Path.GetFileName(exe)} was not found.");
                done++;
                continue;
            }

            try
            {
                // CancellationToken.None on purpose: cancelling ProcessRunner KILLS the process tree, and
                // killing DISM mid-write can leave the component store inconsistent; Microsoft says so
                // explicitly. The loop above checks the token BETWEEN steps instead (unlike the
                // defrag step, which passes its real token straight through). The timeout kills too, so it
                // is set far beyond any healthy run and only ever ends a step that is truly stuck.
                var result = await RunStreamedStepAsync(
                    exe, args, label, stepBase, share, progress, RepairStepTimeout, CancellationToken.None);

                if (result.TimedOut)
                    logging.Warn($"Cleanup: {label} timed out.");
                else if (result.ExitCode != 0)
                    logging.Info($"Cleanup: {label} finished with exit code {result.ExitCode}.");
                else
                    logging.Info($"Cleanup: {label} completed.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Without admin these refuse to start at all; expected, and not worth failing the run over.
                logging.Warn($"Cleanup: {label} could not be run: {ex.Message}");
            }

            done++;
        }

        progress?.Report(new CleanupProgress(string.Empty, 100, 0));
        return new CleanupOutcome(0, 0, 0);
    }

    /// <summary>
    /// Windows' own drive optimisation: defragmentation on a rotational disk, retrim on an SSD, the SAME
    /// command for both (`defrag &lt;drive&gt;: /O` decides from the drive's media type). Deliberately no
    /// scheduled task: started directly through the elevated worker like the repair steps and Disk
    /// Cleanup, so a run never outlives the app. Frees no measurable space. Unlike sfc/DISM the real
    /// cancellation token is passed straight through: interrupting drive optimisation is its ordinary way
    /// of being stopped (a shutdown, or Stop in the Optimize Drives UI).
    /// </summary>
    private async Task<CleanupOutcome> RunDefragAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target.DriveLetter is null)
        {
            logging.Warn("Cleanup: drive optimisation skipped; the target named no drive.");
            return new CleanupOutcome(0, 0, 0);
        }

        var defrag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "defrag.exe");
        if (!File.Exists(defrag))
        {
            logging.Warn("Cleanup: drive optimisation skipped; defrag.exe was not found.");
            return new CleanupOutcome(0, 0, 0);
        }

        var label = string.Format(LocalizationService.Instance["CleanupDefragName"], target.DriveLetter);

        using var runMarker = DefragRunState.Enter();

        var result = await RunStreamedStepAsync(
            defrag, [target.DriveLetter, "/O", "/U", "/V"], label, stepBase: 0, share: 100, progress,
            TimeSpan.FromHours(6), cancellationToken);

        if (result.TimedOut)
            logging.Warn("Cleanup: drive optimisation timed out.");
        else if (result.ExitCode != 0)
            logging.Info($"Cleanup: drive optimisation finished with exit code {result.ExitCode}.");
        else
            logging.Info("Cleanup: drive optimisation completed.");

        progress?.Report(new CleanupProgress(string.Empty, 100, 0));
        return new CleanupOutcome(0, 0, 0);
    }

    /// <summary>
    /// One externally-progressed tool, shared by every repair step and by the defrag command: reports
    /// its percentage scaled into [<paramref name="stepBase"/>, <paramref name="stepBase"/> +
    /// <paramref name="share"/>], applies the UTF-16-redirect recovery, and falls back to the honest
    /// "no progress" hint once <see cref="ShouldShowNoProgressHint"/> says the tool never reports one.
    /// Argument arrays, no shell: the same rule every other external tool here follows.
    /// </summary>
    /// <summary>A RestoreHealth on a slow disk or a slow Windows Update source can pass 45 minutes and is
    /// still healthy; a kill at that point is the mid-write corruption the no-cancel rule exists to avoid.</summary>
    internal static readonly TimeSpan RepairStepTimeout = TimeSpan.FromHours(4);

    private static Task<ProcessResult> RunStreamedStepAsync(
        string exe, IReadOnlyList<string> args, string label, double stepBase, double share,
        IProgress<CleanupProgress>? progress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reached = 0.0;
        var hasReadableProgress = false;
        var lastReport = -1.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextTextAt = TimeSpan.Zero;
        var nextHintAt = NoProgressHintDelay;

        return ProcessRunner.RunStreamingAsync(
            exe,
            args,
            timeout,
            segment =>
            {
                // Sfc (and DISM, on the same path) writes UTF-16LE once it notices its output is
                // redirected; ProcessRunner decodes every tool as UTF-8, which turns that into a real
                // character followed by a NUL, over and over, so the percentage below never matched. Undone
                // here, scoped to this one caller, rather than by changing how every OTHER tool's output is
                // decoded.
                if (Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(segment))
                    segment = Utf16RedirectRecovery.Recover(segment);

                var line = segment.Trim();
                if (line.Length == 0)
                    return;

                if (ProgressParser.Parse(line)?.Percent is { } percent)
                {
                    hasReadableProgress = true;

                    // Never backwards: DISM restarts its bar for each internal phase, and a ring that jumps
                    // back looks like the run came undone.
                    reached = Math.Max(reached, percent);

                    // Throttled to whole percent: a redrawn bar arrives many times a second and every
                    // report crosses a dispatcher (and, elevated, a pipe).
                    if (Math.Floor(reached) <= lastReport)
                        return;

                    lastReport = Math.Floor(reached);
                    progress?.Report(new CleanupProgress(
                        $"{label}: {PercentFormat.Format(reached)}", stepBase + (reached / 100.0 * share), 0));
                }
                else if (clock.Elapsed >= nextTextAt)
                {
                    // A plain output line is what the step is currently saying; shown at most a few times a
                    // second so a chatty tool cannot flood the UI.
                    nextTextAt = clock.Elapsed + TimeSpan.FromMilliseconds(300);
                    progress?.Report(new CleanupProgress(
                        $"{label}: {line}", stepBase + (reached / 100.0 * share), 0));
                }

                // A step that has said nothing readable for a while is not "stuck at 0 %",
                // it simply never reports one; the honest running state replaces the plain-text line above
                // once ShouldShowNoProgressHint says so (throttled the same way the plain-text line is),
                // and this keeps checking for the rest of the step, so a tool that starts reporting late
                // still switches straight back.
                if (ShouldShowNoProgressHint(hasReadableProgress, clock.Elapsed, nextHintAt))
                {
                    nextHintAt = clock.Elapsed + NoProgressHintRepeat;
                    progress?.Report(new CleanupProgress(
                        $"{label}: {LocalizationService.Instance["CleanupNoProgressHint"]}", stepBase, 0));
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// How long a repair step is given before "no percentage yet" turns into "this step apparently never
    /// reports one". Long enough that DISM's own multi-second startup, or a step that is
    /// simply slow to print its first line, does not flash the hint needlessly.
    /// </summary>
    private static readonly TimeSpan NoProgressHintDelay = TimeSpan.FromSeconds(8);

    /// <summary>Once shown, re-shown at most this often, the same throttling reason the plain-text line
    /// above already has: every report crosses a dispatcher and, elevated, a pipe.</summary>
    private static readonly TimeSpan NoProgressHintRepeat = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pure so the decision is directly testable without running a real process for 8 real seconds: never
    /// once a real percentage has been read, and not sooner than <paramref name="nextHintAt"/> (the caller's
    /// own throttle, seeded at <see cref="NoProgressHintDelay"/> and advanced by
    /// <see cref="NoProgressHintRepeat"/> each time this returns true).
    /// </summary>
    internal static bool ShouldShowNoProgressHint(bool hasReadableProgress, TimeSpan elapsed, TimeSpan nextHintAt) =>
        !hasReadableProgress && elapsed >= nextHintAt;

    /// <summary>Our own profile slot. High enough not to collide with a profile the user saved themselves.</summary>
    private const int SagesetNumber = 4711;

    /// <summary>
    /// The cleanmgr handlers we are willing to run unattended. Deliberately a short allow-list: everything
    /// here is a cache or a temporary file. Anything that could remove user data or a rollback path is
    /// absent on purpose, and absence is the safe default: an unlisted handler simply does not run.
    /// </summary>
    private static readonly string[] SafeVolumeCaches =
    [
        "Temporary Files",
        "Temporary Setup Files",
        "Downloaded Program Files",
        "Internet Cache Files",
        "Thumbnail Cache",
        "Delivery Optimization Files",
        "Windows Error Reporting Files",
        "Windows Error Reporting Queue Files",
        "Windows Error Reporting System Queue Files",
        "Old ChkDsk Files",
        "Setup Log Files",
        "Update Cleanup",
    ];

    /// <summary>
    /// Makes the profile slot belong to the allow-list alone: a handler outside it that already carries our
    /// flag (left by the user's own <c>cleanmgr /sageset</c> or another tool) would otherwise run with the
    /// rest, e.g. the Downloads folder or the recycle bin. Each such flag is cleared; if one cannot be, the
    /// result is false and no unattended run may start. Pure over the registry facts so a test never touches HKLM.
    /// </summary>
    /// <param name="handlers">Every subkey name under VolumeCaches and whether it carries the flag.</param>
    /// <param name="allowed">The handlers that may keep the flag (and get it set afterwards).</param>
    /// <param name="tryClearFlag">Removes the flag from one handler; true when it is gone.</param>
    /// <param name="failedHandler">The first handler whose flag could not be cleared, else null.</param>
    internal static bool ClearForeignHandlerFlags(
        IEnumerable<(string Name, bool HasFlag)> handlers, IReadOnlyCollection<string> allowed,
        Func<string, bool> tryClearFlag, out string? failedHandler)
    {
        failedHandler = null;
        foreach (var (name, hasFlag) in handlers)
        {
            if (!hasFlag || allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!tryClearFlag(name))
                failedHandler ??= name;
        }

        return failedHandler is null;
    }

    private bool TryWriteSagesetProfile()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        var flagName = $"StateFlags{SagesetNumber:0000}";
        var written = 0;

        try
        {
            using var caches = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root, writable: true);
            if (caches is null)
                return false;

            var handlers = caches.GetSubKeyNames().Select(name =>
            {
                try
                {
                    using var key = caches.OpenSubKey(name, writable: false);
                    return (Name: name, HasFlag: key?.GetValue(flagName) is not null);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // unreadable: assume it carries the flag, so a clear is attempted and a failure fails closed
                    return (Name: name, HasFlag: true);
                }
            }).ToList();

            var clean = ClearForeignHandlerFlags(handlers, SafeVolumeCaches, handler =>
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    key?.DeleteValue(flagName, throwOnMissingValue: false);
                    return true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    return false;
                }
            }, out var stuckHandler);

            if (!clean)
            {
                logging.Info($"Cleanup: no unattended Disk Cleanup profile: the handler \"{stuckHandler}\" carries a foreign selection that could not be cleared.");
                return false;
            }

            foreach (var handler in SafeVolumeCaches)
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    if (key is null)
                        continue;       // handler not present on this Windows build

                    // 2 = include in this sageset run; every other handler is left untouched (and so off).
                    key.SetValue(flagName, 2, Microsoft.Win32.RegistryValueKind.DWord);
                    written++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // one handler refusing is not worth abandoning the rest
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logging.Info($"Cleanup: no unattended Disk Cleanup profile (needs admin): {ex.Message}");
            return false;
        }

        if (written == 0)
            logging.Info("Cleanup: could not prepare an unattended Disk Cleanup profile; starting it with its own UI.");

        return written > 0;
    }
}
