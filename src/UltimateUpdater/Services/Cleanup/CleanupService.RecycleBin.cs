using System.IO;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
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
    /// <param name="FoundCount">How many items the bin held when the plan was made; the whole-bin call needs
    /// the bin to still hold exactly that many.</param>
    /// <param name="FoundBytes">The byte total of those items; an item restored and another recycled in its
    /// place keeps the count, so the total has to match as well.</param>
    /// <param name="FallbackEntries">The rows to delete one by one when the whole-bin call is no longer safe.</param>
    internal readonly record struct RecycleBinDrivePlan(
        string Root, string Label, bool UseShellEmpty, IReadOnlyList<CleanupEntry> Entries,
        int FoundCount = 0, IReadOnlyList<CleanupEntry>? FallbackEntries = null, long FoundBytes = 0);

    /// <summary>The shell's whole-bin call destroys everything in the bin, including an item recycled after
    /// the plan was made, so it is only taken while a fresh count AND byte total still equal the planned ones. A failed
    /// query proves nothing and counts as a mismatch.</summary>
    internal static bool ShellEmptyStillMatchesPlan(RecycleBinDrivePlan plan, bool queried, int itemsNow, long bytesNow) =>
        queried && itemsNow == plan.FoundCount && bytesNow == plan.FoundBytes;

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
            // A drive without a listing is never "complete": its one aggregate row stands for the whole bin, so
            // "every listed row is selected" says nothing, and the whole-bin call would take items recycled
            // after the confirm. It falls to the per-row path, which refuses the aggregate row and counts it
            // as skipped.
            var complete = drive.DetailsAvailable && !drive.AgeFilterHidItems && drive.SkippedIndexCount == 0
                && drive.FoundCount == drive.ListedCount && selected.Count == drive.ListedCount
                && (confirmedOnDrive is null || confirmedOnDrive == drive.FoundCount);
            plans.Add(new RecycleBinDrivePlan(
                drive.Root, drive.Label, complete, complete ? [] : selected, drive.FoundCount, selected, drive.FoundBytes));
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
            var rowEntries = plan.Entries;
            if (plan.UseShellEmpty)
            {
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var queried = TryQueryDrive(plan.Root, out var driveBytesBefore, out var driveItemsBefore);
                if (!ShellEmptyStillMatchesPlan(plan, queried, driveItemsBefore, driveBytesBefore))
                {
                    // Something arrived since the plan (or the count is unreadable): the whole-bin call would
                    // take it unseen, so only the planned rows go.
                    logging.Warn($"Cleanup: the recycle bin on '{plan.Root}' no longer matches what was confirmed; emptying the confirmed items one by one.");
                    rowEntries = plan.FallbackEntries ?? [];
                    totalSteps += rowEntries.Count - 1;
                }
                else
                {
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
            }

            foreach (var entry in rowEntries)
            {
                pause?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stepsDone++;

                if (DeleteRecycledItem(entry, ownSid, out var partialFreed))
                {
                    freed += entry.Bytes;
                    deleted++;
                }
                else
                {
                    freed += partialFreed;
                    skipped++;
                    skippedBytes += Math.Max(0, entry.Bytes - partialFreed);
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

    /// <summary>Unreadable attributes count as a link: the caller then only tries a non-recursive delete,
    /// which fails on a folder with content, so an unknown folder is kept rather than walked.</summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// One recycled item: its content, then the index file describing it. The path passes
    /// <see cref="CleanupSafety.IsInsideOwnRecycleBin"/> first: a row whose path does not sit inside THIS
    /// user's own bin is refused outright, however it got here. That check is only a snapshot, so the bin
    /// folder holding the item is then opened and locked (no rename or delete of it while this runs) and
    /// must still be exactly the checked, link-free folder; every file is deleted through a handle that is
    /// proven to lie inside it, and a folder item is removed by the same verified walk used for the other
    /// leftover folders. A row with no original path is the aggregate fallback row for a drive that could not
    /// be enumerated; deleting its path would mean deleting a drive root, so it is never attempted.
    /// </summary>
    internal bool DeleteRecycledItem(CleanupEntry entry, string? ownSid) =>
        DeleteRecycledItem(entry, ownSid, out _);

    /// <summary><paramref name="partialFreed"/> is what a folder item that could not be removed completely
    /// still gave back; it is 0 for every other outcome.</summary>
    internal bool DeleteRecycledItem(CleanupEntry entry, string? ownSid, out long partialFreed) =>
        DeleteRecycledItem(entry, ownSid, CleanupSafety.IsInsideOwnRecycleBin, out partialFreed);

    internal bool DeleteRecycledItem(CleanupEntry entry, string? ownSid, Func<string, string?, bool> isInsideOwnBin) =>
        DeleteRecycledItem(entry, ownSid, isInsideOwnBin, out _);

    /// <summary><paramref name="isInsideOwnBin"/> is the path gate; tests pass their own, because a real bin
    /// only exists at a drive root.</summary>
    internal bool DeleteRecycledItem(
        CleanupEntry entry, string? ownSid, Func<string, string?, bool> isInsideOwnBin, out long partialFreed)
    {
        partialFreed = 0;
        if (entry.OriginalPath is null)
        {
            logging.Warn(
                $"Cleanup: the recycle bin on '{entry.Path}' could not be listed, so it was left as it is.");
            return false;
        }

        if (!isInsideOwnBin(entry.Path, ownSid))
        {
            logging.Warn("Cleanup: a recycle-bin row was refused because its path is not inside this user's own bin.");
            return false;
        }

        var binFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(entry.Path));
        using var binLock = string.IsNullOrEmpty(binFolder) ? null : CleanupSafety.TryLockForUse(binFolder, allowEntriesToBeAdded: true);
        if (binLock is null || VerifiedDelete.FinalPathOf(binLock) is not { } binFinalPath)
        {
            logging.Warn("Cleanup: a recycle-bin row was refused because its bin folder could not be locked for use.");
            return false;
        }

        try
        {
            if (IsReparsePoint(entry.Path))
            {
                // A link as the recycled item itself: only the link goes, never what it points at.
                if (!RemoveRecycledLink(binFinalPath, entry.Path))
                    return false;
            }
            else if (Directory.Exists(entry.Path))
            {
                // Links inside first, so nothing the walk meets can lead out of the recycled tree.
                UnlinkReparsePoints(entry.Path);
                if (Directory.Exists(entry.Path))
                {
                    var (removed, freedHere) = DeletePipLeftoverTree(entry.Path);
                    if (!removed)
                    {
                        // The walk deletes file by file: what went before the blocked one is really freed.
                        partialFreed = freedHere;
                        return false;
                    }
                }
            }
            else if (File.Exists(entry.Path))
            {
                if (VerifiedDelete.TryDeleteFile(binFinalPath, entry.Path, out _) != VerifiedDelete.Outcome.Deleted)
                    return false;
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Cleanup: a recycle-bin item could not be removed: '{entry.OriginalPath}' ({ex.Message})");
            return false;
        }

        // The index file only describes content that is already gone; failing to remove it leaves a
        // harmless orphan the next scan skips, so it never turns a deleted item into a failed one.
        var indexPath = RecycleBinScanner.IndexPathFor(entry.Path);
        if (indexPath is not null && isInsideOwnBin(indexPath, ownSid))
        {
            try
            {
                if (VerifiedDelete.TryDeleteFile(binFinalPath, indexPath, out _) == VerifiedDelete.Outcome.Refused)
                    logging.Info($"Recycle bin index '{indexPath}' is not inside its bin folder; left alone.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Info($"Recycle bin index delete left a harmless orphan for '{indexPath}': {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>Removes a link (junction, symlink) that is itself the recycled item, without following it.</summary>
    private static bool RemoveRecycledLink(string binFinalPath, string path) =>
        VerifiedDelete.TryDeleteLink(binFinalPath, path) == VerifiedDelete.Outcome.Deleted;
}
