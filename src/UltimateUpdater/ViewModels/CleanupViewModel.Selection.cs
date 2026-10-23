using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// What is ticked and what that adds up to: permanent exclusions, the select-all toggle, and every size
// and count figure the dialog shows before a run.
public partial class CleanupViewModel
{
    /// <summary>The write half of "permanently exclude". called from a
    /// <see cref="CleanupItemViewModel"/> whose own entry the user right-clicked. Case-insensitive so a
    /// re-scan matches even if Windows returns the path with different casing next time.</summary>
    private void PermanentlyExcludePath(string path)
    {
        if (_settings.Current.CleanupPermanentlyExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        _settings.Current.CleanupPermanentlyExcludedPaths.Add(path);
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or ArgumentException)
        {
            _logging.Error("Failed to save settings.json", ex);
        }
    }

    /// <summary>
    /// What a run must leave alone for this row: its unticked entries PLUS every permanent exclusion. The
    /// unticked entries alone are not enough: a permanently excluded folder has no row (and so no untick)
    /// when it fell into the "...and N more" tail past the detail list's cap, or when Clean was pressed
    /// before this row's scan had produced its list at all, and it would then simply be emptied. Not for the
    /// recycle bin, whose $R paths are never permanent exclusions and whose plan any exclusion changes.
    /// </summary>
    internal IReadOnlyCollection<string> ExcludedPathsForRun(CleanupItemViewModel item) =>
        item.Target.Kind == CleanupKind.RecycleBin || _settings.Current.CleanupPermanentlyExcludedPaths.Count == 0
            ? item.ExcludedEntryPaths
            : item.ExcludedEntryPaths
                .Concat(_settings.Current.CleanupPermanentlyExcludedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>The read half: after every <c>SetEntries</c> call (a fresh scan, or an elevated reply),
    /// uncheck any entry whose path was permanently excluded: same "unchecked, still visible" shape
    /// unchecking one by hand already has, just remembered across scans instead of only this run.
    /// Internal (not private) so a test can call it directly after its own <c>SetEntries</c>, the same
    /// two-call sequence the real scan paths use.</summary>
    internal void ApplyPermanentExclusions(CleanupItemViewModel item)
    {
        if (_settings.Current.CleanupPermanentlyExcludedPaths.Count == 0)
            return;

        var excludedPaths = new HashSet<string>(
            _settings.Current.CleanupPermanentlyExcludedPaths, StringComparer.OrdinalIgnoreCase);

        // A registry row path is key plus value name. An exclusion stored before that, a bare key path, still
        // covers every value finding under that key.
        var legacyRegistryRows = item.IsRegistryCleanup
            ? item.RegistryFindings
                .Where(f => CleanupService.IsRegistryFindingExcluded(f, excludedPaths))
                .Select(CleanupService.RegistryEntryPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var entry in item.Entries)
        {
            if (excludedPaths.Contains(entry.Model.Path) || legacyRegistryRows?.Contains(entry.Model.Path) == true)
                entry.IsSelected = false;
        }
    }

    /// <summary>The in-flight rescan started by the most recent spin-box change, if any: cancelled the
    /// moment a newer one starts, and checked again after the await, so a slow scan for an OLDER value can
    /// never overwrite the row with stale figures after a newer, faster one already finished (see
    /// <see cref="RescanRecycleBinAsync"/>).</summary>
    private CancellationTokenSource? _recycleBinRescanCts;

    /// <summary>The recycle bin card's own sub-row: saved deferred (like every other settings write off the
    /// UI thread, see <see cref="SettingsService.SaveDeferred"/>), never the blocking <see
    /// cref="SettingsService.Save"/> a spin box's repeat-click burst would otherwise call once per click, and
    /// the row alone is re-measured against the new value, a full rescan of every target (and a possible new
    /// elevation prompt) would be a disproportionate reaction to one number box. Only ever invoked for the
    /// recycle bin row (see its own constructor wiring in the main constructor above); the value itself lives
    /// on that row's own <see cref="CleanupItemViewModel"/>, not here, since exactly one row ever shows it.
    /// </summary>
    private void OnRecycleBinMinAgeDaysChanged(int value)
    {
        _settings.Current.CleanupRecycleBinMinAgeDays = value;
        _settings.SaveDeferred();

        _recycleBinRescanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recycleBinRescanCts = cts;
        _ = RescanRecycleBinAsync(value, cts);
    }

    /// <summary>Re-measures only the recycle bin row, unelevated (it never needs admin), so the sub-row's
    /// effect is visible right away without touching any other target's already-measured figure.
    /// <paramref name="cts"/> is the one <see cref="_recycleBinRescanCts"/> held when this call started: a
    /// later spin-box change cancels it and replaces the field with its own, so both the cancellation itself
    /// and the reference check right after the await stop a superseded call from ever applying its (possibly
    /// later-arriving) result over a newer one's.</summary>
    private async Task RescanRecycleBinAsync(int minAgeDays, CancellationTokenSource cts)
    {
        // Not IsBusy: the component store analysis (DISM, up to minutes) touches no row, and blocking on it
        // left the row showing the old filter's list and size while the next run used the new value.
        if (IsScanning || IsCleaning || _runClaimed)
            return;

        var item = Items.FirstOrDefault(i => i.Target.Kind == CleanupKind.RecycleBin);
        if (item is null)
            return;

        item.IsScanning = true;
        RefreshTotals();
        try
        {
            var result = await _service.ScanAsync(item.Target, cts.Token, minAgeDays);

            // A run claimed meanwhile cleans what the user confirmed; this result must not replace it.
            if (!ReferenceEquals(_recycleBinRescanCts, cts) || _runClaimed || Phase != CleanupPhase.Choosing)
                return;

            item.Bytes = result.Bytes;
            item.Files = result.Files;
            item.SetEntries(result.Entries, result.OmittedEntryCount, result.OmittedEntryBytes, result.RecycleBinDrives);
            ApplyPermanentExclusions(item);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer spin-box value; that call owns applying its own result.
        }
        catch (Exception ex)
        {
            _logging.Warn($"Cleanup scan failed for '{item.Target.Id}': {ex.Message}");
        }
        finally
        {
            // Only the still-current call clears the row's spinner and refreshes totals - otherwise a
            // superseded call's finally, running after a newer one already started, could flip the spinner
            // off (or refresh stale totals) out from under the rescan that is actually still running.
            if (ReferenceEquals(_recycleBinRescanCts, cts))
            {
                item.IsScanning = false;
                RefreshTotals();
            }
        }
    }

    /// <summary>What the ticked targets would free.</summary>
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.Bytes);

    /// <summary>What every target together would free: the ring's 100 %.</summary>
    public long TotalBytes => Items.Sum(i => i.Bytes);

    public string SelectedText => ByteSize.Format(SelectedBytes);

    // ---- the facts either side of the ring, so the top of the dialog is not empty space ----

    /// <summary>"3 of 10 areas": how much of the list is actually going to be touched.</summary>
    public string SelectedCountText =>
        string.Format(Loc[Items.Count == 1 ? "CleanupSelectedCountOne" : "CleanupSelectedCount"], Items.Count(i => i.IsSelected), Items.Count);

    /// <summary>How many files the measured targets add up to; the figure behind the size.</summary>
    public string FileCountText
    {
        get
        {
            var files = Items.Where(i => i.IsSelected).Sum(i => i.Files);
            return string.Format(Loc[files == 1 ? "CleanupFileCountOne" : "CleanupFileCount"], files);
        }
    }

    private const double TopAreaMaxBarWidth = 110;

    /// <summary>
    /// The three biggest selected areas, as a small bar list, with the width scaled against the largest
    /// of the three, not the grand total, so a modest selection still shows visibly different bars instead
    /// of three slivers.
    /// </summary>
    public IReadOnlyList<CleanupTopAreaViewModel> TopAreas
    {
        get
        {
            var top = Items.Where(i => i.IsSelected && i.Bytes > 0)
                .OrderByDescending(i => i.Bytes)
                .Take(3)
                .ToList();

            if (top.Count == 0)
                return [];

            var max = (double)top[0].Bytes;
            return top
                .Select(i => new CleanupTopAreaViewModel(
                    i.Title, ByteSize.Format(i.Bytes), max <= 0 ? 0 : TopAreaMaxBarWidth * i.Bytes / max))
                .ToList();
        }
    }

    public bool HasTopAreas => TopAreas.Count > 0;

    /// <summary>
    /// How much of the list turned out to be empty. Worth its own line: a run that frees little is usually
    /// a machine that is already tidy, and saying so is more use than leaving the reader to count grey
    /// "nothing found" cells.
    /// </summary>
    public string EmptyCountText
    {
        get
        {
            var empty = Items.Count(i => i.IsSelected && i.ShowsNothingFound);
            return string.Format(Loc[empty == 1 ? "CleanupEmptyCountOne" : "CleanupEmptyCount"], empty);
        }
    }

    public bool HasEmpty => Items.Any(i => i.IsSelected && i.ShowsNothingFound);

    public string TotalText => string.Format(Loc["CleanupOfTotal"], ByteSize.Format(TotalBytes));

    private long? _freeSpaceBefore;
    private long? _freeSpaceAfter;

    /// <summary>
    /// What the drive itself has left, read once before the run and once after it. The freed figure counts
    /// what this app deleted; this line answers the question the user actually has afterwards: how much
    /// room is there now. Only shown when both readings succeeded, so a drive that cannot be queried simply
    /// leaves the line out instead of showing a blank or a guess.
    /// </summary>
    public bool HasFreeSpaceResult => IsFinished && _freeSpaceBefore is not null && _freeSpaceAfter is not null;

    public string FreeSpaceText => _freeSpaceBefore is null || _freeSpaceAfter is null
        ? string.Empty
        : string.Format(
            Loc["CleanupFreeSpaceResult"],
            SystemDriveName(),
            ByteSize.Format(_freeSpaceAfter.Value),
            ByteSize.Format(_freeSpaceBefore.Value));

    private static string SystemDriveName() =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

    /// <summary>The caption above the right-hand header block ("Drive C:"): the free-space line, the last
    /// cleanup and the restore point below it all describe that drive. The root's trailing separator is
    /// dropped, people name a drive "C:", not "C:\".</summary>
    public string DriveCaptionText =>
        string.Format(Loc["CleanupHeaderDrive"], SystemDriveName().TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>Null when the drive cannot be queried: a removed or unreadable volume must not throw here.</summary>
    private static long? FreeSpaceOnSystemDrive()
    {
        try
        {
            return new DriveInfo(SystemDriveName()).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the drive has free right now, read once at open: good enough for a figure that just
    /// sits beside the ring while the user ticks boxes, and avoids a disk query on every click. Distinct
    /// from <see cref="_freeSpaceBefore"/>/<see cref="_freeSpaceAfter"/>, which capture an actual run rather
    /// than this arithmetic projection.
    /// </summary>
    private readonly long? _freeSpaceNow = FreeSpaceOnSystemDrive();

    public bool HasFreeSpaceNow => IsChoosing && _freeSpaceNow is not null;

    /// <summary>"216,5 GB frei, +156,8 MB nach Bereinigung": the free space now and what the selection would
    /// add to it. The drive itself is named by <see cref="DriveCaptionText"/> above the line.</summary>
    public string FreeSpaceNowText => _freeSpaceNow is null
        ? string.Empty
        : BuildFreeSpaceNowText(_freeSpaceNow.Value, SelectedBytes);

    internal string BuildFreeSpaceNowText(long freeBytes, long selectedBytes) =>
        string.Format(Loc["CleanupFreeSpaceNow"], ByteSize.Format(freeBytes), ByteSize.Format(selectedBytes));

    private void RefreshTotals()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(RingPercent));
        OnPropertyChanged(nameof(CanClean));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(TopAreas));
        OnPropertyChanged(nameof(HasTopAreas));
        OnPropertyChanged(nameof(FreeSpaceNowText));
        OnPropertyChanged(nameof(EmptyCountText));
        OnPropertyChanged(nameof(HasEmpty));
    }

    /// <summary>Resets every row to exactly the state a fresh scan would give it: <see
    /// cref="CleanupTarget.DefaultEnabled"/>, the same field <see cref="CleanupItemViewModel"/>'s own
    /// constructor already reads it from, reused rather than duplicated as a second rule. A risky or
    /// otherwise default-off target (the recycle bin, leftover installs, drive optimisation, ...) is never
    /// turned on by this, because its own DefaultEnabled is false to begin with.</summary>
    [RelayCommand]
    private void RestoreDefaultSelection()
    {
        foreach (var item in Items)
            item.IsSelected = item.Target.DefaultEnabled;

        RefreshTotals();
    }

    /// <summary>
    /// Headline and warning of the second confirmation. The recycle wording is only true when every
    /// selected file deletion actually lands in the recycle bin: the setting reaches non-elevated
    /// directory and app cache targets only, so an elevated target, the recycle bin row itself and the
    /// kinds that always delete permanently keep the permanent wording for the whole selection.
    /// </summary>
    public (string Headline, string Warning) BuildConfirmationTexts()
    {
        var recycle = _settings.Current.CleanupRecycleInsteadOfDelete && Items.Where(i => i.IsSelected).All(i =>
            i.Target.Kind switch
            {
                CleanupKind.Directories or CleanupKind.AppCacheSweep => !i.Target.NeedsElevation,
                CleanupKind.SystemRepair or CleanupKind.RegistryCleanup
                    or CleanupKind.DriverCleanup or CleanupKind.Defragmentation => true,
                _ => false,
            });

        return recycle
            ? (string.Format(Loc["CleanupConfirmHeadlineRecycle"], SelectedText), Loc["CleanupConfirmWarningRecycle"])
            : (string.Format(Loc["CleanupConfirmHeadline"], SelectedText), Loc["CleanupConfirmWarning"]);
    }

    /// <summary>
    /// The text of the second, red confirmation: every target that will be emptied with its own figure,
    /// and the total. Grouped, with the areas that are hardest to undo named
    /// FIRST regardless of where they sit in the dialog's own browsing order: the recycle bin (bypasses
    /// the one safety net an ordinary Explorer delete already has) and the registry (backed up, but
    /// still a live edit to something Windows itself depends on), followed by driver and Python removals,
    /// which uninstall rather than delete. Built here so the window only has to show it.
    /// </summary>
    public string BuildConfirmationDetail()
    {
        CleanupGroupKey[] confirmationOrder =
        [
            CleanupGroupKey.RecycleBin, CleanupGroupKey.Registry, CleanupGroupKey.Drivers,
            CleanupGroupKey.TempFiles, CleanupGroupKey.Caches,
            CleanupGroupKey.WindowsMaintenance, CleanupGroupKey.SystemRepair,
        ];

        var lines = new List<string>();
        foreach (var key in confirmationOrder)
        {
            var selected = Groups.FirstOrDefault(g => g.Key == key)?.Items.Where(i => i.IsSelected).ToList();
            if (selected is not { Count: > 0 })
                continue;

            var groupTitle = Loc[CleanupGroups.TitleKey(key)];
            var groupBytes = selected.Sum(i => i.Bytes);
            lines.Add(groupBytes > 0 ? $"{groupTitle}: {ByteSize.Format(groupBytes)}" : groupTitle);
            lines.AddRange(selected.Select(i => i.Bytes > 0
                ? $"  • {i.Title}: {ByteSize.Format(i.Bytes)}"
                : $"  • {i.Title}"));
            lines.Add(string.Empty);
        }

        lines.Add(string.Format(Loc["CleanupConfirmTotal"], ByteSize.Format(SelectedBytes)));
        return string.Join(Environment.NewLine, lines);
    }
}
