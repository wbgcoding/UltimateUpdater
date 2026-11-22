using System.IO;

namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
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
            logging.Warn($"Registry cleanup: no backup settings available for '{target.Id}'; {cleanable.Count} finding(s) kept, nothing written.");
            return new CleanupOutcome(0, 0, cleanable.Count, Failed: true);
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
            logging.Warn($"Registry cleanup: backup failed or found nothing for '{target.Id}'; {cleanable.Count} finding(s) kept, nothing was deleted.");
            return new CleanupOutcome(0, 0, cleanable.Count, Failed: true);
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
}
