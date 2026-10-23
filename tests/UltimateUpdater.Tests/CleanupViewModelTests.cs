using System.Globalization;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A deselected target must stay deselected across window closes, a user who turns off "shader cache"
/// every night should not have to repeat it, except the recycle bin and leftover-installs, whose "off"
/// default is a safety decision that must survive no matter what was checked last time.
/// </summary>
[Collection("UiLanguage")]
public partial class CleanupViewModelTests
{
    private static CleanupViewModel Create(
        SettingsService settings, CleanupHistoryService? history = null, CleanupService? service = null)
    {
        var logging = new LoggingService();
        var viewModel = new CleanupViewModel(service ?? new CleanupService(logging), logging, settings, history: history);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        return viewModel;
    }

    private static SettingsService CreateSettings() => new(
        new LoggingService(),
        Path.Combine(Directory.CreateTempSubdirectory("wu-cleanup-selection-").FullName, "settings.json"));

    /// <summary>Isolated from a real machine's cleanup-history.json, the default `history: null` falls back to
    /// the production path, which is fine for tests that never look at History but wrong for ones that
    /// assert on it (a machine with real past runs would make an "is empty" test fail).</summary>
    private static CleanupHistoryService CreateHistory() => new(
        new LoggingService(),
        Path.Combine(Directory.CreateTempSubdirectory("wu-cleanup-history-").FullName, "history.json"));

    private static CleanupItemViewModel Find(CleanupViewModel viewModel, string targetId) =>
        viewModel.Items.First(i => i.Target.Id == targetId);

    /// <summary>A scan against the real machine (real directories, real registry, a real
    /// recycle bin) can run for minutes on a loaded dev box, far past what a unit test should ever wait
    /// on. This returns instantly for every target except the first, which always throws, standing in
    /// for a target this process genuinely cannot measure.</summary>
    private sealed class InstantCleanupService(LoggingService logging) : CleanupService(logging)
    {
        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            target.Id == UnmeasurableTargetId
                ? Task.FromException<CleanupScanResult>(new IOException("simulated: cannot be measured"))
                : Task.FromResult(new CleanupScanResult(0, 0, 0));
    }

    private const string UnmeasurableTargetId = "user-temp";

    /// <summary>Every real (non-elevated) target waits the same fixed delay, standing in for an
    /// independent directory/registry walk. If the scan still ran them one after another, the total would
    /// sit close to count * delay; running them with bounded parallelism must land well under that.</summary>
    private sealed class DelayedCleanupService(LoggingService logging, TimeSpan delay) : CleanupService(logging)
    {
        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            await Task.Delay(delay, cancellationToken);
            return new CleanupScanResult(0, 0, 0);
        }
    }

    /// <summary>
    /// Stands in for a real recycle-bin scan whose result carries the requested minimum age (so a test can
    /// tell which call's result actually landed) and whose completion is held open under test control, per
    /// age value, until <see cref="Release"/> is called for it. This is what lets a test start an OLDER
    /// value's rescan first and still let a NEWER value's rescan finish earlier - proving which one the row
    /// ends up showing is about which value was issued last, never which scan happened to finish last.
    /// </summary>
    private sealed class OrderedRecycleBinCleanupService(LoggingService logging) : CleanupService(logging)
    {
        private readonly Dictionary<int, TaskCompletionSource> _started = new();
        private readonly Dictionary<int, TaskCompletionSource> _gates = new();
        private readonly object _lock = new();

        private TaskCompletionSource StartedFor(int minAgeDays)
        {
            lock (_lock)
            {
                if (!_started.TryGetValue(minAgeDays, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _started[minAgeDays] = started;
                }

                return started;
            }
        }

        private TaskCompletionSource GateFor(int minAgeDays)
        {
            lock (_lock)
            {
                if (!_gates.TryGetValue(minAgeDays, out var gate))
                {
                    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates[minAgeDays] = gate;
                }

                return gate;
            }
        }

        public void Release(int minAgeDays) => GateFor(minAgeDays).TrySetResult();

        public async Task<bool> WaitForScanStarted(int minAgeDays, TimeSpan timeout) =>
            await Task.WhenAny(StartedFor(minAgeDays).Task, Task.Delay(timeout)) == StartedFor(minAgeDays).Task;

        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            if (target.Kind != CleanupKind.RecycleBin)
                return new CleanupScanResult(0, 0, 0);

            StartedFor(recycleBinMinAgeDays).TrySetResult();

            using var registration = cancellationToken.Register(
                () => GateFor(recycleBinMinAgeDays).TrySetCanceled(cancellationToken));
            await GateFor(recycleBinMinAgeDays).Task;

            return new CleanupScanResult(recycleBinMinAgeDays, recycleBinMinAgeDays, 0);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was never met.");

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Before this fix: every recycle-bin spin-box change fired an uncancelled rescan, so two rapid clicks
    /// overlapped and whichever scan happened to FINISH last won, even if it was answering an older value the
    /// user had already moved past. Proven here by making the OLDER value's scan the slower one: it must
    /// never be allowed to overwrite the row after the NEWER value's faster scan already applied its result.
    /// </summary>
    [Fact]
    public async Task RecycleBinMinAgeDaysChanged_the_last_issued_value_wins_not_the_last_scan_to_finish()
    {
        var service = new OrderedRecycleBinCleanupService(new LoggingService());
        var viewModel = Create(CreateSettings(), service: service);
        var row = Find(viewModel, "recycle-bin");

        // First spin-box change: age 10. Its scan starts but is held open.
        row.RecycleBinMinAgeDays = 10;
        Assert.True(
            await service.WaitForScanStarted(10, TimeSpan.FromSeconds(2)), "the first rescan never started");

        // A second, later change supersedes it before the first ever completes - the real "fast spin
        // clicks" case, not two well-separated changes.
        row.RecycleBinMinAgeDays = 20;
        Assert.True(
            await service.WaitForScanStarted(20, TimeSpan.FromSeconds(2)), "the second rescan never started");

        // The newer value's scan finishes FIRST.
        service.Release(20);
        await WaitUntilAsync(() => row.Bytes == 20, TimeSpan.FromSeconds(2));

        // The older, superseded scan finishes SECOND (or is cancelled outright); either way it must never
        // overwrite the row with its now-stale answer.
        service.Release(10);
        await Task.Delay(150);

        Assert.Equal(20, row.Bytes);
    }

    [Fact]
    public async Task StartScanAsync_measures_unelevated_targets_with_bounded_parallelism_not_serially()
    {
        var logging = new LoggingService();
        var delay = TimeSpan.FromMilliseconds(200);
        var viewModel = Create(CreateSettings(), service: new DelayedCleanupService(logging, delay));
        // No elevated host is wired up (elevated: null), so every item scans through this fake service,
        // the same "continue unelevated" fallback a declined or unavailable prompt takes in production.
        var unelevatedCount = viewModel.Items.Count;
        Assert.True(unelevatedCount >= 4, "the catalogue must offer enough targets for this to be meaningful");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await viewModel.StartScanAsync();
        stopwatch.Stop();

        // The scan runs min(4, cores) targets at once. A two-core build server can at best halve the serial
        // time, so the bound leaves room for overhead while still failing a scan that runs one at a time.
        var degree = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
        if (degree < 2)
            return;

        var limit = delay * unelevatedCount * 0.75;
        Assert.True(
            stopwatch.Elapsed < limit,
            $"expected under {limit.TotalMilliseconds} ms (three quarters of the serial time) for {unelevatedCount} targets, took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_deselected_target_is_still_deselected_after_closing_and_reopening()
    {
        var settings = CreateSettings();
        var first = Create(settings);
        var target = first.Items.First(i => i.Target.Kind == CleanupKind.Directories && i.IsSelected);

        Find(first, target.Target.Id).IsSelected = false;
        first.WindowClosed();

        var second = Create(settings);

        Assert.False(Find(second, target.Target.Id).IsSelected);
    }

    [Fact]
    public void A_selected_target_that_defaults_off_stays_selected_after_reopening()
    {
        var settings = CreateSettings();
        var first = Create(settings);
        var target = first.Items.First(i => i.Target is { DefaultEnabled: false, Kind: not (CleanupKind.RecycleBin or CleanupKind.LeftoverInstalls) });

        Find(first, target.Target.Id).IsSelected = true;
        first.WindowClosed();

        var second = Create(settings);

        Assert.True(Find(second, target.Target.Id).IsSelected);
    }

    /// <summary>The recycle bin defaults off as a safety decision, not a convenience, checking it once
    /// must never make it come back pre-checked.</summary>
    [Fact]
    public void The_recycle_bin_never_comes_back_preselected_even_if_it_was_checked_last_time()
    {
        var settings = CreateSettings();
        var first = Create(settings);
        var recycleBin = Find(first, "recycle-bin");
        Assert.False(recycleBin.IsSelected); // sanity: confirms the catalogue default this test relies on

        recycleBin.IsSelected = true;
        first.WindowClosed();

        var second = Create(settings);

        Assert.False(Find(second, "recycle-bin").IsSelected);
        Assert.False(settings.Current.CleanupSelection.ContainsKey("recycle-bin"));
    }

    /// <summary>The registry catalogue merged 5 rows into 2, each with its own new settings id;
    /// selecting either merged row must persist exactly like any other target's.</summary>
    [Fact]
    public void The_merged_registry_categories_selection_survives_a_settings_round_trip()
    {
        var settings = CreateSettings();
        var first = Create(settings);

        Find(first, "registry-orphaned-references").IsSelected = true;
        Find(first, "registry-orphaned-uninstall").IsSelected = true;
        first.WindowClosed();

        var second = Create(settings);

        Assert.True(Find(second, "registry-orphaned-references").IsSelected);
        Assert.True(Find(second, "registry-orphaned-uninstall").IsSelected);
    }

    /// <summary>An id from a settings.json written by an older build, naming a target this catalogue no
    /// longer has, must be silently ignored rather than throwing or leaving a phantom row.</summary>
    [Fact]
    public void An_unknown_saved_id_is_ignored()
    {
        var settings = CreateSettings();
        settings.Current.CleanupSelection["target-from-a-future-version"] = true;

        var viewModel = Create(settings);

        Assert.DoesNotContain(viewModel.Items, i => i.Target.Id == "target-from-a-future-version");
        // construction did not throw, and a real target's own default still came through untouched
        foreach (var item in viewModel.Items)
            Assert.Equal(item.Target.DefaultEnabled, item.IsSelected);
    }

    /// <summary>A target added to the catalogue after the user's settings.json was last written has no
    /// saved entry yet, so it must fall back to its own default rather than starting deselected.</summary>
    [Fact]
    public void A_target_with_no_saved_entry_keeps_its_own_default()
    {
        var settings = CreateSettings();

        var viewModel = Create(settings);

        foreach (var item in viewModel.Items)
            Assert.Equal(item.Target.DefaultEnabled, item.IsSelected);
    }

    // ---- Detail-list entries drive the target's own size, and Clean respects them ----

    /// <summary>The acceptance criterion, word for word: "kann einzelne abwählen, die Zielgröße ändert
    /// sich mit", unchecking one detail-list row must move the target's own displayed total right away.</summary>
    [Fact]
    public void Unchecking_a_detail_list_entry_reduces_the_targets_own_size_immediately()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.Bytes = 100;
        item.Files = 10;
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);

        item.Entries.First(e => e.DisplayName == "A").IsSelected = false;

        Assert.Equal(40, item.Bytes);
        Assert.Equal(4, item.Files);

        // Re-checking must restore it exactly, no drift from the round trip.
        item.Entries.First(e => e.DisplayName == "A").IsSelected = true;
        Assert.Equal(100, item.Bytes);
        Assert.Equal(10, item.Files);
    }

    /// <summary>The other half: a deselected entry's path must actually reach Clean, so the run frees
    /// exactly what stayed checked.</summary>
    [Fact]
    public void Unchecking_a_detail_list_entry_excludes_its_path_from_what_clean_would_receive()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);

        Assert.Empty(item.ExcludedEntryPaths);

        item.Entries.First(e => e.DisplayName == "A").IsSelected = false;

        Assert.Equal([@"C:\Temp\A"], item.ExcludedEntryPaths);
    }

    /// <summary>Tri-state header checkbox: all ticked reports true, all unticked reports false, a mix
    /// reports null (indeterminate), same contract as <c>CleanupGroupViewModel.IsSelected</c>.</summary>
    [Fact]
    public void AllEntriesSelected_reflects_every_entry_none_or_a_mix()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.Bytes = 100;
        item.Files = 10;
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);

        Assert.True(item.AllEntriesSelected);

        item.Entries.First(e => e.DisplayName == "A").IsSelected = false;
        Assert.Null(item.AllEntriesSelected);

        item.Entries.First(e => e.DisplayName == "B").IsSelected = false;
        Assert.False(item.AllEntriesSelected);
    }

    /// <summary>One click always resolves to a definite state: fewer than all selected turns every entry
    /// on, and the target's own Bytes/Files land on the sum of the whole list, not an accumulated drift
    /// from toggling row by row.</summary>
    [Fact]
    public void ToggleAllEntries_selects_everything_and_sums_bytes_and_files_once()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.Bytes = 100;
        item.Files = 10;
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);
        item.Entries.First(e => e.DisplayName == "A").IsSelected = false;
        Assert.Equal(40, item.Bytes);

        item.ToggleAllEntriesCommand.Execute(null);

        Assert.True(item.Entries.All(e => e.IsSelected));
        Assert.Equal(100, item.Bytes);
        Assert.Equal(10, item.Files);
        Assert.True(item.AllEntriesSelected);
    }

    /// <summary>The other direction: anything selected (all or a mix) turns every entry off and the
    /// target's own total drops to zero.</summary>
    [Fact]
    public void ToggleAllEntries_clears_everything_once_all_are_already_selected()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.Bytes = 100;
        item.Files = 10;
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);

        item.ToggleAllEntriesCommand.Execute(null);

        Assert.True(item.Entries.All(e => !e.IsSelected));
        Assert.Equal(0, item.Bytes);
        Assert.Equal(0, item.Files);
        Assert.False(item.AllEntriesSelected);
    }

    /// <summary>A drive optimisation row is a multi-hour commitment and must only ever be turned
    /// on by ticking its own row, never swept in by its group's own header checkbox.
    /// Skipped rather than asserting nothing if this machine's own drives never resolve a media type.</summary>
    [Fact]
    public void A_group_header_toggle_never_selects_a_drive_optimisation_row()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var defragRows = viewModel.Items.Where(i => i.Target.Kind == CleanupKind.Defragmentation).ToList();
        if (defragRows.Count == 0)
            return; // this machine's own drives never resolved a media type; nothing to prove here

        var group = viewModel.Groups.First(g => g.Key == CleanupGroupKey.WindowsMaintenance);
        group.ToggleSelectionCommand.Execute(null);
        Assert.All(defragRows, row => Assert.False(row.IsSelected));
    }

    /// <summary>The "default selection" button must restore exactly the same set a fresh scan ticks, no
    /// more and no less: every default-on target back on, everything the user had additionally ticked
    /// (a risky, default-off target) turned back off.</summary>
    [Fact]
    public void RestoreDefaultSelection_restores_exactly_the_default_set()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var defaultOn = viewModel.Items.Where(i => i.Target.DefaultEnabled).ToList();
        var defaultOff = viewModel.Items.Where(i => !i.Target.DefaultEnabled).ToList();
        Assert.NotEmpty(defaultOn);
        Assert.NotEmpty(defaultOff);

        // Scramble the selection away from the default in both directions.
        foreach (var item in defaultOn)
            item.IsSelected = false;
        foreach (var item in defaultOff)
            item.IsSelected = true;

        viewModel.RestoreDefaultSelectionCommand.Execute(null);

        Assert.All(defaultOn, item => Assert.True(item.IsSelected));
        Assert.All(defaultOff, item => Assert.False(item.IsSelected));
    }

    /// <summary>The two system repair rows (DISM, sfc) start unticked: they free no space and run for a
    /// long time, so a fresh selection, and the default-selection button, leave them off.</summary>
    [Fact]
    public void RestoreDefaultSelection_leaves_the_system_repair_rows_off()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var repair = viewModel.Items
            .Where(i => i.Target.Id is "system-repair-dism" or "system-repair-sfc")
            .ToList();
        Assert.Equal(2, repair.Count);
        Assert.All(repair, item => Assert.False(item.IsSelected));

        foreach (var item in repair)
            item.IsSelected = true;

        viewModel.RestoreDefaultSelectionCommand.Execute(null);

        Assert.All(repair, item => Assert.False(item.IsSelected));
    }

    /// <summary>A risky/default-off target (here: the recycle bin, always default-off by design) is never
    /// selected by the "default selection" button, even though it is visible and otherwise eligible for
    /// "select all".</summary>
    [Fact]
    public void RestoreDefaultSelection_never_selects_a_default_off_risky_target()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var recycleBin = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RecycleBin);
        recycleBin.IsSelected = true;

        viewModel.RestoreDefaultSelectionCommand.Execute(null);

        Assert.False(recycleBin.IsSelected);
    }

    /// <summary>"Open folder" must stay offered for a real filesystem entry that happens to have no date
    /// (a recycle-bin drive, real size, no per-item date) and stay hidden only for a registry finding
    /// (neither date nor size, its Path is a registry key, not a folder): the gate must check
    /// HasDate || HasSize, never HasDate alone.</summary>
    [Fact]
    public void Open_folder_is_offered_for_a_dateless_but_sized_entry_like_a_recycle_bin_drive()
    {
        var registryFinding = new CleanupEntryViewModel(
            new CleanupEntry(@"HKCU\Software\Foo", "Foo", 0, 0, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
            _ => { });
        var recycleBinDrive = new CleanupEntryViewModel(
            new CleanupEntry(@"C:\", "C: (System)", 500, 12, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
            _ => { });

        Assert.False(registryFinding.HasOpenablePath);
        Assert.True(recycleBinDrive.HasOpenablePath);
    }

    /// <summary>The right-click "permanently exclude" command unchecks the entry right away (the same
    /// immediate feedback unticking it by hand gives) AND writes the path to settings.</summary>
    [Fact]
    public void Permanently_excluding_an_entry_unchecks_it_and_persists_the_path()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.SetEntries(
        [
            new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);
        var entryA = item.Entries.First(e => e.DisplayName == "A");

        entryA.PermanentlyExcludeCommand.Execute(null);

        Assert.False(entryA.IsSelected);
        Assert.True(item.Entries.First(e => e.DisplayName == "B").IsSelected);
        Assert.Contains(@"C:\Temp\A", settings.Current.CleanupPermanentlyExcludedPaths);
    }

    /// <summary>The other half, a real observed scenario: a path excluded today starts UNCHECKED the next time
    /// a scan finds it again, not just for the run it was excluded in. Case-insensitive, since Windows
    /// does not guarantee the same casing back on every enumeration.</summary>
    [Fact]
    public void A_permanently_excluded_path_starts_unchecked_on_the_next_scan_even_with_different_casing()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.SetEntries([new CleanupEntry(@"C:\Temp\A", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache)]);
        item.Entries.First().PermanentlyExcludeCommand.Execute(null);

        // A fresh scan replaces Entries wholesale, every entry starts checked again, same as any other
        // rescan, until ApplyPermanentExclusions (called right after SetEntries on the real scan paths)
        // reapplies what was remembered.
        item.SetEntries(
        [
            new CleanupEntry(@"c:\temp\a", "A", 60, 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
            new CleanupEntry(@"C:\Temp\B", "B", 40, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache),
        ]);
        Assert.All(item.Entries, e => Assert.True(e.IsSelected));

        viewModel.ApplyPermanentExclusions(item);

        Assert.False(item.Entries.First(e => e.DisplayName == "A").IsSelected);
        Assert.True(item.Entries.First(e => e.DisplayName == "B").IsSelected);
    }

    /// <summary>An exclusion stored as a bare registry key path (the format before value rows had their own
    /// paths) still starts every value row under that key unchecked.</summary>
    [Fact]
    public void A_bare_registry_key_exclusion_unchecks_the_value_rows_under_that_key()
    {
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
        var settings = CreateSettings();
        settings.Current.CleanupPermanentlyExcludedPaths.Add(key);
        var viewModel = Create(settings);
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RegistryCleanup);
        var a = new RegistryFinding(RegistryCleanupCategory.SharedDlls, key, @"C:\gone\a.dll", "a.dll", @"C:\gone\a.dll");
        var b = new RegistryFinding(RegistryCleanupCategory.AppPaths, @"HKLM\SOFTWARE\Other", null, "other", @"C:\gone.exe");
        item.SetRegistryFindings([a, b]);
        item.SetEntries(
        [
            new CleanupEntry(CleanupService.RegistryEntryPath(a), "a.dll", 0, 1, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
            new CleanupEntry(CleanupService.RegistryEntryPath(b), "other", 0, 1, DateTimeOffset.MinValue, DateTimeOffset.MinValue, CleanupEntryType.Other),
        ]);

        viewModel.ApplyPermanentExclusions(item);

        Assert.False(item.Entries.First(e => e.DisplayName == "a.dll").IsSelected);
        Assert.True(item.Entries.First(e => e.DisplayName == "other").IsSelected);
    }

    // ---- Overview band facts (top-3 bars, free space now, last cleanup) ----

    [Fact]
    public void TopAreas_lists_up_to_three_biggest_selected_areas_largest_first_with_a_full_width_bar()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        var directories = viewModel.Items.Where(i => i.Target.Kind == CleanupKind.Directories).Take(4).ToList();

        foreach (var item in viewModel.Items)
            item.IsSelected = false;

        for (var i = 0; i < directories.Count; i++)
        {
            directories[i].IsSelected = true;
            directories[i].Bytes = (4 - i) * 100; // 400, 300, 200, 100, a fourth area to prove the cap
        }

        var top = viewModel.TopAreas;

        Assert.True(viewModel.HasTopAreas);
        Assert.Equal(3, top.Count);
        Assert.Equal(directories[0].Title, top[0].Title);
        Assert.Equal(directories[1].Title, top[1].Title);
        Assert.Equal(directories[2].Title, top[2].Title);
        Assert.Equal(110, top[0].BarWidth); // the largest of the three always fills the scale
        Assert.True(top[1].BarWidth < top[0].BarWidth && top[1].BarWidth > 0);
        Assert.True(top[2].BarWidth < top[1].BarWidth && top[2].BarWidth > 0);
    }

    [Fact]
    public void HasTopAreas_is_false_when_nothing_selected_has_a_measured_size()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);

        foreach (var item in viewModel.Items)
            item.IsSelected = false;

        Assert.False(viewModel.HasTopAreas);
        Assert.Empty(viewModel.TopAreas);
    }

    /// <summary>The system drive is always queryable on a real Windows machine, the same assumption the
    /// existing finished-run free-space figure already makes.</summary>
    [Fact]
    public void HasFreeSpaceNow_is_true_while_choosing_and_false_once_the_phase_moves_on()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);

        Assert.True(viewModel.HasFreeSpaceNow);
        Assert.NotEmpty(viewModel.FreeSpaceNowText);

        viewModel.Phase = CleanupPhase.Cleaning;

        Assert.False(viewModel.HasFreeSpaceNow);
    }

    /// <summary>The free-space line states the gain: what the drive has now, and what the selection adds
    /// to it (the selected bytes, not the sum). The drive name sits in the header caption instead.</summary>
    [Fact]
    public void FreeSpaceNowText_states_the_free_space_and_the_gain_of_the_selection()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);
        try
        {
            LocalizationService.Instance.Apply("de");
            var text = viewModel.BuildFreeSpaceNowText(
                (long)(216.5 * 1024 * 1024 * 1024), (long)(156.8 * 1024 * 1024));

            Assert.Equal("216,5 GB frei, +156,8 MB nach Bereinigung", text);
            Assert.StartsWith("Laufwerk ", viewModel.DriveCaptionText);
            Assert.EndsWith(":", viewModel.DriveCaptionText);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void HasLastCleanup_is_false_when_there_is_no_history_yet()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings, CreateHistory());

        Assert.False(viewModel.HasLastCleanup);
        Assert.Equal(string.Empty, viewModel.LastCleanupText);
    }

    [Fact]
    public void HasLastCleanup_reports_the_most_recent_runs_date_and_freed_amount_while_choosing()
    {
        var settings = CreateSettings();
        var history = CreateHistory();
        var timestamp = DateTimeOffset.UtcNow.AddDays(-1);
        history.Append(new CleanupHistoryEntry
        {
            Timestamp = timestamp,
            FreedBytes = 123L * 1024 * 1024,
            Areas = [],
        });

        LocalizationService.Instance.Apply("de");
        var germanViewModel = Create(settings, history);
        Assert.True(germanViewModel.HasLastCleanup);
        Assert.Contains(timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture), germanViewModel.LastCleanupText);
        Assert.Contains("123", germanViewModel.LastCleanupText);

        LocalizationService.Instance.Apply("en");
        var englishViewModel = Create(settings, history);
        // Explicit provider, not just the pattern from CurrentUICulture: a custom format string's "/"
        // is the AMBIENT CurrentCulture's date separator, not a literal slash, the same missing-provider
        // mistake WhenText_follows_the_UI_culture_... below guards against in production code, here in
        // the test's own expected-value computation.
        Assert.Contains(
            timestamp.LocalDateTime.ToString($"{CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern} HH:mm", CultureInfo.CurrentUICulture),
            englishViewModel.LastCleanupText);
    }

    /// <summary>Exactly one selected target reporting exactly one measured file reads "1 file measured",
    /// not "1 files measured", the same singular/plural key-split pattern as <c>CleanupSelectedCount</c>,
    /// applied to the aggregate <c>FileCountText</c>.</summary>
    [Fact]
    public void FileCountText_uses_the_singular_wording_for_exactly_one_file()
    {
        var settings = CreateSettings();
        var viewModel = Create(settings);

        foreach (var item in viewModel.Items)
            item.IsSelected = false;

        var registryItem = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RegistryCleanup);
        registryItem.IsSelected = true;
        registryItem.Files = 1;

        Assert.Equal(string.Format(LocalizationService.Instance["CleanupFileCountOne"], 1), viewModel.FileCountText);

        registryItem.Files = 3;
        Assert.Equal(string.Format(LocalizationService.Instance["CleanupFileCount"], 3), viewModel.FileCountText);
    }

    /// <summary>A run that only touched one area reads "1 area cleaned", not "1 areas cleaned", the
    /// plural resource key had only ever been exercised with more than one area.</summary>
    [Fact]
    public void A_history_entry_with_exactly_one_area_uses_the_singular_wording()
    {
        var entry = new CleanupHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            FreedBytes = 10,
            Areas = [new CleanupHistoryArea { Id = "temp-files", TitleKey = "CleanupGroupTempFiles", FreedBytes = 10 }],
        };
        var row = new CleanupHistoryRowViewModel(entry);

        Assert.Equal(string.Format(LocalizationService.Instance["CleanupHistoryEntrySummaryOne"], 1), row.SummaryText);
    }

    /// <summary>The history overview's four figures: everything freed across every run, how many runs,
    /// the newest one's date, and the three categories that gave up the most, summed across runs, not
    /// per run, since the same category can appear in several of them.</summary>
    [Fact]
    public void The_history_overview_sums_every_run_and_ranks_categories_across_all_of_them()
    {
        LocalizationService.Instance.Apply("en");
        var settings = CreateSettings();
        var history = CreateHistory();
        history.Append(new CleanupHistoryEntry
        {
            Timestamp = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            FreedBytes = 100L * 1024 * 1024,
            Areas =
            [
                new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 90L * 1024 * 1024 },
                new CleanupHistoryArea { Id = "thumbnail-cache", TitleKey = "CleanupThumbnails", FreedBytes = 10L * 1024 * 1024 },
            ],
        });
        history.Append(new CleanupHistoryEntry
        {
            Timestamp = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero),
            FreedBytes = 50L * 1024 * 1024,
            Areas = [new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupUserTemp", FreedBytes = 50L * 1024 * 1024 }],
        });
        var viewModel = Create(settings, history);

        Assert.Contains("150", viewModel.HistoryTotalFreedText);
        Assert.Contains("2", viewModel.HistoryRunCountText);
        Assert.Contains(viewModel.History[0].WhenText, viewModel.HistoryRunCountText);
        Assert.True(viewModel.HasFreedAnything);

        var top = viewModel.HistoryTopCategories;
        Assert.Equal(2, top.Count);
        // "User Temp" summed across both runs (90+50=140MB) outranks "Thumbnails" (10MB alone).
        Assert.Equal(LocalizationService.Instance["CleanupUserTemp"], top[0].Title);
        Assert.Contains("140", top[0].FreedText);
        Assert.Equal("93%", top[0].ShareText);
    }

    /// <summary>A history where every run found nothing to free has no categories to rank, the overview
    /// says so in a sentence rather than showing an empty list.</summary>
    [Fact]
    public void The_history_overview_reports_nothing_freed_when_every_run_found_nothing()
    {
        var settings = CreateSettings();
        var history = CreateHistory();
        history.Append(new CleanupHistoryEntry { Timestamp = DateTimeOffset.UtcNow, FreedBytes = 0, Areas = [] });
        var viewModel = Create(settings, history);

        Assert.False(viewModel.HasFreedAnything);
        Assert.True(viewModel.HasNotFreedAnything);
        Assert.Empty(viewModel.HistoryTopCategories);
    }

    /// <summary><see cref="CleanupHistoryRowViewModel.WhenText"/> composes its format STRING from
    /// `CurrentUICulture` but must also pass that same culture as the format PROVIDER to `ToString`, the
    /// implicit-provider overload silently falls back to the thread's `CurrentCulture`, which
    /// `LocalizationService.Apply` never touches (only `CurrentUICulture`). Diverging the two explicitly
    /// is the only way to catch a missing provider argument: on a machine where they already match, the
    /// bug is invisible.</summary>
    [Fact]
    public void WhenText_follows_the_UI_culture_even_when_the_threads_ambient_culture_differs()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ja-JP");
        try
        {
            LocalizationService.Instance.Apply("de");
            var entry = new CleanupHistoryEntry
            {
                Timestamp = new DateTimeOffset(2026, 3, 4, 13, 5, 0, TimeSpan.Zero),
                FreedBytes = 10,
                Areas = [new CleanupHistoryArea { Id = "temp-files", TitleKey = "CleanupGroupTempFiles", FreedBytes = 10 }],
            };
            var row = new CleanupHistoryRowViewModel(entry);

            Assert.Equal(
                entry.Timestamp.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                row.WhenText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            LocalizationService.Instance.Apply("de");
        }
    }

    // ---- Deleting history, one row or all of it ----

    [Fact]
    public void ClearHistoryCommand_empties_the_collection_and_flips_to_the_empty_state()
    {
        var settings = CreateSettings();
        var history = CreateHistory();
        history.Append(new CleanupHistoryEntry { Timestamp = DateTimeOffset.UtcNow, FreedBytes = 10, Areas = [] });
        var viewModel = Create(settings, history);
        Assert.True(viewModel.HasHistory);

        viewModel.ClearHistoryCommand.Execute(null);

        Assert.False(viewModel.HasHistory);
        Assert.True(viewModel.HasNoHistory);
        Assert.Empty(viewModel.History);
        Assert.Empty(history.Load());
    }

    [Fact]
    public void A_rows_own_DeleteCommand_removes_just_that_row_from_both_the_collection_and_the_file()
    {
        var settings = CreateSettings();
        var history = CreateHistory();
        var keep = DateTimeOffset.UtcNow.AddDays(-1);
        var drop = DateTimeOffset.UtcNow;
        history.Append(new CleanupHistoryEntry { Timestamp = keep, FreedBytes = 10, Areas = [] });
        history.Append(new CleanupHistoryEntry { Timestamp = drop, FreedBytes = 20, Areas = [] });
        var viewModel = Create(settings, history);
        var row = viewModel.History.First(r => r.Timestamp == drop);

        row.DeleteCommand.Execute(null);

        Assert.Single(viewModel.History);
        Assert.DoesNotContain(viewModel.History, r => r.Timestamp == drop);
        var reloaded = history.Load();
        Assert.Single(reloaded);
        Assert.Equal(keep, reloaded[0].Timestamp);
    }

    /// <summary>
    /// The dialog's sweep runs at the pace of the run, and that pace comes from how closely the progress
    /// ticks follow each other. Ticks arriving one after another have to push the figure up.
    /// </summary>
    [Fact]
    public void Ticks_in_quick_succession_drive_the_activity_figure_up()
    {
        var activity = 0.5;
        for (var tick = 0; tick < 40; tick++)
            activity = CleanupViewModel.SmoothActivity(activity, 0.05);

        Assert.True(activity > 0.9, $"a fast run settled at {activity:0.000}");
    }

    /// <summary>A step that has gone quiet, a long DISM pass, must decay instead of holding the last
    /// fast reading, which is the whole point of weighting the smoothing by the gap itself.</summary>
    [Fact]
    public void A_long_silence_lets_the_activity_figure_decay()
    {
        Assert.True(CleanupViewModel.SmoothActivity(1, 6) < 0.2);
        Assert.True(CleanupViewModel.SmoothActivity(1, 1.5) < CleanupViewModel.SmoothActivity(1, 0.3));
    }

    /// <summary>It feeds a speed ratio, so it may never leave 0…1, not on a clock that jumped backwards,
    /// and not on a gap that is not a number at all.</summary>
    [Theory]
    [InlineData(-4d)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void The_activity_figure_stays_inside_its_range(double gapSeconds)
    {
        var activity = CleanupViewModel.SmoothActivity(0.5, gapSeconds);

        Assert.InRange(activity, 0, 1);
    }

    // ---- live per-entry status while a run works through an open detail list ----
}
