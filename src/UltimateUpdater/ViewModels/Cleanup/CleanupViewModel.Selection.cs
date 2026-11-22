using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Cleanup;

// What is ticked and what that adds up to: permanent exclusions, the select-all toggle, and every size
// and count figure the dialog shows before a run.
public partial class CleanupViewModel
{
    /// <summary>The write half of "permanently exclude", called from the entry the user right-clicked. Case-insensitive so a rescan matches a different path casing.</summary>
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

    /// <summary>What a run must leave alone for this row: its unticked entries plus every permanent exclusion. The unticked entries alone are not enough: an excluded folder may have no row (past the detail list's cap, or Clean pressed before the list existed) and would be emptied. Not for the recycle bin, whose $R paths are never permanent exclusions.</summary>
    internal IReadOnlyCollection<string> ExcludedPathsForRun(CleanupItemViewModel item) =>
        item.Target.Kind == CleanupKind.RecycleBin || _settings.Current.CleanupPermanentlyExcludedPaths.Count == 0
            ? item.ExcludedEntryPaths
            : item.ExcludedEntryPaths
                .Concat(_settings.Current.CleanupPermanentlyExcludedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>The read half: after every <c>SetEntries</c> call, unchecks any entry whose path was permanently excluded. Internal so a test can call it directly after its own <c>SetEntries</c>.</summary>
    internal void ApplyPermanentExclusions(CleanupItemViewModel item)
    {
        if (_settings.Current.CleanupPermanentlyExcludedPaths.Count == 0)
            return;

        var excludedPaths = new HashSet<string>(
            _settings.Current.CleanupPermanentlyExcludedPaths, StringComparer.OrdinalIgnoreCase);

        // A registry row path is key plus value name; an exclusion stored as a bare key path still covers every value under that key.
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

    /// <summary>The in-flight rescan from the latest spin-box change: cancelled when a newer one starts and checked again after the await, so a slow older scan can never overwrite newer figures (see <see cref="RescanRecycleBinAsync"/>).</summary>
    private CancellationTokenSource? _recycleBinRescanCts;

    /// <summary>The recycle bin card's sub-row: saved deferred (see <see cref="SettingsService.SaveDeferred"/>) so a spin box's repeat-click burst does not block, and only that row is re-measured, since a full rescan would be disproportionate. The value lives on that row's <see cref="CleanupItemViewModel"/>.</summary>
    private void OnRecycleBinMinAgeDaysChanged(int value)
    {
        _settings.Current.CleanupRecycleBinMinAgeDays = value;
        _settings.SaveDeferred();

        StartRecycleBinRescan(value);
    }

    /// <summary>Set when an age change arrived while a scan or run owned the row: the value is saved but the list on screen answers the old one. Settled once the scan ends (see <see cref="RunPendingRecycleBinRescan"/>).</summary>
    private bool _recycleBinRescanPending;

    private void StartRecycleBinRescan(int minAgeDays)
    {
        _recycleBinRescanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recycleBinRescanCts = cts;
        _ = RescanRecycleBinAsync(minAgeDays, cts);
    }

    /// <summary>Re-measures the bin row for an age change swallowed by a running scan; waits while a run holds the rows.</summary>
    private void RunPendingRecycleBinRescan()
    {
        if (!_recycleBinRescanPending || IsScanning || IsCleaning || _runClaimed || Phase != CleanupPhase.Choosing)
            return;

        _recycleBinRescanPending = false;
        if (Items.FirstOrDefault(i => i.Target.Kind == CleanupKind.RecycleBin) is { } bin)
            StartRecycleBinRescan(bin.RecycleBinMinAgeDays);
    }

    /// <summary>Re-measures only the recycle bin row, unelevated, so the sub-row's effect is visible at once. <paramref name="cts"/> is the <see cref="_recycleBinRescanCts"/> held at call start: cancellation and the reference check after the await stop a superseded call from applying its result over a newer one's.</summary>
    private async Task RescanRecycleBinAsync(int minAgeDays, CancellationTokenSource cts)
    {
        // Not IsBusy: the component store analysis (DISM, up to minutes) touches no row and must not block this.
        if (IsScanning || IsCleaning || _runClaimed)
        {
            _recycleBinRescanPending = true;
            return;
        }

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
        }
        catch (Exception ex)
        {
            _logging.Warn($"Cleanup scan failed for '{item.Target.Id}': {ex.Message}");
        }
        finally
        {
            // Only the still-current call clears the spinner and refreshes totals; a superseded call's finally must not flip the spinner off under the running rescan.
            if (ReferenceEquals(_recycleBinRescanCts, cts))
            {
                item.IsScanning = false;
                RefreshTotals();
            }
        }
    }

    /// <summary>What the ticked targets would really free now. A row an unelevated run skips for missing admin rights adds nothing, as in the group headers and the confirmation.</summary>
    public long SelectedBytes => Items.Where(CountsAsSelected).Sum(i => i.SelectedEntryBytes);

    /// <summary>What the scan found in every target together: the ring's 100 %. Independent of the detail ticks.</summary>
    public long TotalBytes => Items.Sum(i => i.Bytes);

    public string SelectedText => ByteSize.Format(SelectedBytes);

    /// <summary>True when a run started now cannot clean this row: only an administrator can delete from it and neither this process nor a running helper has admin rights. The row is skipped, so the confirmation must not promise its figure.</summary>
    internal bool WillSkipForMissingAdmin(CleanupItemViewModel item) =>
        CleanupService.SkipsWithoutAdmin(item.Target, ElevationService.IsElevated || _elevated is { IsRunning: true });

    /// <summary>Ticked, and a run started now would really clean it: what every selection figure of the window counts.</summary>
    private bool CountsAsSelected(CleanupItemViewModel item) => item.IsSelected && !WillSkipForMissingAdmin(item);

    /// <summary>"3 of 10 areas": how much of the list is actually going to be touched.</summary>
    public string SelectedCountText =>
        string.Format(Loc[Items.Count == 1 ? "CleanupSelectedCountOne" : "CleanupSelectedCount"], Items.Count(CountsAsSelected), Items.Count);

    /// <summary>"1,2 GB / 3,4 GB selected": what the ticks would free against everything the scan found.</summary>
    public string SelectedOfFoundText =>
        string.Format(Loc["CleanupSelectedOfFound"], ByteSize.Format(SelectedBytes), ByteSize.Format(TotalBytes));

    /// <summary>How many files the measured targets add up to; the figure behind the size.</summary>
    public string FileCountText
    {
        get
        {
            var files = Items.Where(CountsAsSelected).Sum(i => i.SelectedEntryFiles);
            return string.Format(Loc[files == 1 ? "CleanupFileCountOne" : "CleanupFileCount"], files);
        }
    }

    private const double TopAreaMaxBarWidth = 110;

    /// <summary>The three biggest selected areas as a small bar list, scaled against the largest of the three so a modest selection still shows different bars.</summary>
    public IReadOnlyList<CleanupTopAreaViewModel> TopAreas
    {
        get
        {
            var top = Items.Where(i => CountsAsSelected(i) && i.Bytes > 0)
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

    /// <summary>How much of the list turned out to be empty: a run that frees little is usually a machine that is already tidy.</summary>
    public string EmptyCountText
    {
        get
        {
            var empty = Items.Count(i => i.IsSelected && i.ShowsNothingFound);
            return string.Format(Loc[empty == 1 ? "CleanupEmptyCountOne" : "CleanupEmptyCount"], empty);
        }
    }

    public bool HasEmpty => Items.Any(i => i.IsSelected && i.ShowsNothingFound);

    public string TotalText => string.Format(Loc["CleanupFoundTotal"], ByteSize.Format(TotalBytes));

    private static string SystemDriveName() =>
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

    /// <summary>The caption above the right-hand header block ("Drive C:"); the root's trailing separator is dropped.</summary>
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

    /// <summary>What the drive has free, read once at open: good enough beside the ring while the user ticks boxes, and avoids a disk query per click.</summary>
    private readonly long? _freeSpaceNow = FreeSpaceOnSystemDrive();

    public bool HasFreeSpaceNow => IsChoosing && _freeSpaceNow is not null;

    /// <summary>"216,5 GB frei, +156,8 MB nach Bereinigung": free space now and what the selection would add. The drive is named by <see cref="DriveCaptionText"/>.</summary>
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
        OnPropertyChanged(nameof(SelectedOfFoundText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(TopAreas));
        OnPropertyChanged(nameof(HasTopAreas));
        OnPropertyChanged(nameof(FreeSpaceNowText));
        OnPropertyChanged(nameof(EmptyCountText));
        OnPropertyChanged(nameof(HasEmpty));
    }

    /// <summary>Resets every row to the state a fresh scan gives it (<see cref="CleanupTarget.DefaultEnabled"/>). A default-off target such as the recycle bin is never turned on by this.</summary>
    [RelayCommand]
    private void RestoreDefaultSelection()
    {
        foreach (var item in Items)
            item.IsSelected = item.Target.DefaultEnabled;

        RefreshTotals();
    }

    /// <summary>Headline and warning of the second confirmation. The recycle wording is true only when every selected file deletion lands in the recycle bin; elevated targets, the recycle bin row and kinds that always delete permanently keep the permanent wording for the whole selection.</summary>
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
            ? (string.Format(Loc["CleanupConfirmHeadlineRecycle"], ByteSize.Format(SelectedBytes)), Loc["CleanupConfirmWarningRecycle"])
            : (string.Format(Loc["CleanupConfirmHeadline"], ByteSize.Format(SelectedBytes)), Loc["CleanupConfirmWarning"]);
    }

    /// <summary>The text of the second, red confirmation: every target to be emptied with its figure, and the total. The hardest to undo are named first: the recycle bin, then the registry (backed up, but a live edit), then driver and Python removals, which uninstall rather than delete.</summary>
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
            var groupBytes = selected.Where(i => !WillSkipForMissingAdmin(i)).Sum(i => i.SelectedEntryBytes);
            lines.Add(groupBytes > 0 ? $"{groupTitle}: {ByteSize.Format(groupBytes)}" : groupTitle);
            lines.AddRange(selected.Select(i => WillSkipForMissingAdmin(i)
                ? $"  • {i.Title}: {Loc["CleanupNeedsAdmin"]}"
                : i.SelectedEntryBytes > 0
                    ? $"  • {i.Title}: {ByteSize.Format(i.SelectedEntryBytes)}"
                    : $"  • {i.Title}"));
            lines.Add(string.Empty);
        }

        lines.Add(string.Format(Loc["CleanupConfirmTotal"], ByteSize.Format(SelectedBytes)));
        return string.Join(Environment.NewLine, lines);
    }
}
