using System.Windows.Data;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    private static CleanupEntry Folder(string path, long bytes = 1000) => new(
        path, System.IO.Path.GetFileName(path), bytes, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Cache);

    /// <summary>A recycled file: its own path is the transient <c>$R</c> name in the bin, and the original
    /// path is the only one the service ever reports.</summary>
    private static CleanupEntry Recycled(string binPath, string originalPath, long bytes = 1000) => new(
        binPath, System.IO.Path.GetFileName(originalPath), bytes, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CleanupEntryType.Other, originalPath);

    /// <summary>A target set up exactly as a run leaves it: entries loaded, states reset, running, and its
    /// detail list open, everything the three gates ask for, so a test only varies the one it is about.</summary>
    private static CleanupItemViewModel PreparedTarget(
        CleanupViewModel viewModel, CleanupKind kind, params CleanupEntry[] entries)
    {
        var item = viewModel.Items.First(i => i.Target.Kind == kind);
        item.SetEntries(entries);
        item.ResetEntryStates();
        item.RunState = CleanupTargetState.Running;
        item.IsDetailsExpanded = true;
        return item;
    }

    /// <summary>One progress report through the same path a real run takes: buffered, then flushed by what
    /// the 200 ms tick would have called.</summary>
    private static void Report(CleanupViewModel viewModel, CleanupItemViewModel item, string path, long freed)
    {
        viewModel.ReportEntryProgress(item, new CleanupProgress(path, 0, freed));
        viewModel.FlushEntryProgress();
    }

    private static CleanupEntryViewModel Entry(CleanupItemViewModel item, string path) =>
        item.Entries.First(e => e.Model.Path == path);

    /// <summary>The deepest folder wins, a target's own list holds nested folders, and the shallow one
    /// would otherwise claim every file underneath the deep one as well.</summary>
    [Fact]
    public void A_reported_file_marks_the_entry_with_the_longest_matching_path_prefix()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(
            viewModel, CleanupKind.Directories,
            Folder(@"C:\Temp"), Folder(@"C:\Temp\Inner"), Folder(@"C:\Temp2"));

        Report(viewModel, item, @"C:\Temp\Inner\deep\thing.log", freed: 500);

        Assert.Equal(EntryState.Running, Entry(item, @"C:\Temp\Inner").State);
        Assert.Equal(EntryState.Pending, Entry(item, @"C:\Temp").State);
        Assert.Equal(EntryState.Pending, Entry(item, @"C:\Temp2").State);
    }

    /// <summary>A prefix that stops mid-segment is not a match at all: "C:\Temp" must never claim a file
    /// that lives in "C:\Temp2".</summary>
    [Fact]
    public void A_sibling_folder_with_a_longer_name_is_never_matched_by_the_shorter_one()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(viewModel, CleanupKind.Directories, Folder(@"C:\Temp"), Folder(@"C:\Temp2"));

        Report(viewModel, item, @"C:\Temp2\thing.log", freed: 500);

        Assert.Equal(EntryState.Running, Entry(item, @"C:\Temp2").State);
        Assert.Equal(EntryState.Pending, Entry(item, @"C:\Temp").State);
    }

    /// <summary>The recycle bin reports the file's ORIGINAL path, so its rows match exactly, and its own
    /// <c>$R</c> path, which the row carries but no report ever names, must match nothing.</summary>
    [Fact]
    public void A_recycled_file_is_matched_on_its_original_path_not_on_its_bin_path()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(
            viewModel, CleanupKind.RecycleBin,
            Recycled(@"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt", @"C:\Users\Someone\Documents\note.txt"),
            Recycled(@"C:\$Recycle.Bin\S-1-5-21-1\$RBBB.dat", @"D:\Games\save.dat"));

        Report(viewModel, item, @"D:\Games\save.dat", freed: 1000);

        Assert.Equal(EntryState.Running, Entry(item, @"C:\$Recycle.Bin\S-1-5-21-1\$RBBB.dat").State);
        Assert.Equal(EntryState.Pending, Entry(item, @"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt").State);

        Report(viewModel, item, @"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt", freed: 2000);

        Assert.Equal(EntryState.Pending, Entry(item, @"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt").State);
    }

    /// <summary>The bin's freed figure only grows for an item that was really removed, so a reported item
    /// that moved it by nothing is one that stayed behind, it must not end up ticked as cleaned.</summary>
    [Fact]
    public void A_recycled_file_that_freed_nothing_reads_as_skipped_not_cleaned()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(
            viewModel, CleanupKind.RecycleBin,
            Recycled(@"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt", @"C:\Users\Someone\Documents\note.txt"),
            Recycled(@"C:\$Recycle.Bin\S-1-5-21-1\$RBBB.dat", @"D:\Games\save.dat"));

        Report(viewModel, item, @"C:\Users\Someone\Documents\note.txt", freed: 1000);
        Report(viewModel, item, @"D:\Games\save.dat", freed: 1000);

        Assert.Equal(EntryState.Cleaned, Entry(item, @"C:\$Recycle.Bin\S-1-5-21-1\$RAAA.txt").State);
        Assert.Equal(EntryState.Skipped, Entry(item, @"C:\$Recycle.Bin\S-1-5-21-1\$RBBB.dat").State);
    }

    /// <summary>The requirement's first half: a closed detail list has nothing to show, so nothing is matched
    /// for it at all.</summary>
    [Fact]
    public void A_collapsed_detail_list_is_never_marked_up()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(viewModel, CleanupKind.Directories, Folder(@"C:\Temp"));
        item.IsDetailsExpanded = false;

        Report(viewModel, item, @"C:\Temp\thing.log", freed: 500);

        Assert.Equal(EntryState.Pending, Entry(item, @"C:\Temp").State);
    }

    /// <summary>The requirement's second half: a run carrying on behind a closed or minimized dialog costs
    /// nothing, and picks straight back up once the dialog is on screen again.</summary>
    [Fact]
    public void A_run_behind_a_hidden_dialog_is_never_marked_up()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(viewModel, CleanupKind.Directories, Folder(@"C:\Temp"));
        viewModel.IsWindowVisible = false;

        Report(viewModel, item, @"C:\Temp\thing.log", freed: 500);

        Assert.Equal(EntryState.Pending, Entry(item, @"C:\Temp").State);

        viewModel.IsWindowVisible = true;
        Report(viewModel, item, @"C:\Temp\thing.log", freed: 900);

        Assert.Equal(EntryState.Running, Entry(item, @"C:\Temp").State);
    }

    /// <summary>Everything a target says between two ticks is worth exactly one match: the newest.</summary>
    [Fact]
    public void Only_the_newest_report_of_a_tick_is_matched()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(viewModel, CleanupKind.Directories, Folder(@"C:\A"), Folder(@"C:\B"));

        viewModel.ReportEntryProgress(item, new CleanupProgress(@"C:\A\one.log", 0, 100));
        viewModel.ReportEntryProgress(item, new CleanupProgress(@"C:\B\two.log", 0, 200));
        viewModel.FlushEntryProgress();

        Assert.Equal(EntryState.Running, Entry(item, @"C:\B").State);
        Assert.Equal(EntryState.Pending, Entry(item, @"C:\A").State);
    }

    /// <summary>Nothing may still read "running" once the target itself is finished, including the rows the
    /// run never got round to reporting.</summary>
    [Fact]
    public void No_entry_is_left_running_once_its_target_is_done()
    {
        var viewModel = Create(CreateSettings());
        var item = PreparedTarget(
            viewModel, CleanupKind.Directories, Folder(@"C:\A"), Folder(@"C:\B"), Folder(@"C:\C"));

        Report(viewModel, item, @"C:\A\one.log", freed: 400);
        Report(viewModel, item, @"C:\B\two.log", freed: 900);

        item.RunState = CleanupTargetState.Done;
        item.ResolveEntryStates(completed: true);

        Assert.DoesNotContain(item.Entries, e => e.State is EntryState.Running or EntryState.Pending);
        Assert.All(item.Entries, e => Assert.Equal(EntryState.Cleaned, e.State));
    }

    /// <summary>A finished row already shows the green tick (IsDone); the grey run-state capsule beside it
    /// must disappear rather than render empty (RunStateText is deliberately empty for Done). Every other
    /// non-Idle state still shows its own word.</summary>
    [Theory]
    [InlineData(CleanupTargetState.Done, false)]
    [InlineData(CleanupTargetState.Running, true)]
    [InlineData(CleanupTargetState.Waiting, true)]
    [InlineData(CleanupTargetState.Stopped, true)]
    [InlineData(CleanupTargetState.Skipped, true)]
    [InlineData(CleanupTargetState.Idle, false)]
    public void ShowsRunState_is_false_only_for_Idle_and_Done(CleanupTargetState state, bool expected)
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);

        item.RunState = state;

        Assert.Equal(expected, item.ShowsRunState);
        if (state == CleanupTargetState.Done)
            Assert.Equal(string.Empty, item.RunStateText);
    }

    /// <summary>Same empty-capsule guard, mirrored for the "needs admin" capsule: the capsule must never
    /// show while its own flag is off, and stays tied to that flag turning on and off live (ElevatedHost
    /// state changes flip it after construction, not just once).</summary>
    [Fact]
    public void ShowsElevationCapsule_follows_ShowsElevationHint_on_and_off()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.NeedsElevation);

        item.ShowsElevationHint = true;
        Assert.True(item.ShowsElevationCapsule);

        item.ShowsElevationHint = false;
        Assert.False(item.ShowsElevationCapsule);
    }

    /// <summary>The admin explanation lives on the capsule itself, not in a footer line.</summary>
    [Fact]
    public void The_elevation_capsule_tooltip_says_the_area_is_only_partly_handled_without_admin()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.NeedsElevation);

        Assert.Equal(LocalizationService.Instance["CleanupAdminMissing"], item.ElevationTooltip);
        Assert.False(string.IsNullOrWhiteSpace(item.ElevationTooltip));
    }

    /// <summary>The risky row's dot must paint the CRITICAL brush, not caution amber: red, but
    /// its own word, so the scale never reads like update severity. The colour itself
    /// lives purely in two XAML <c>DataTrigger</c>s (the category-row dot and the per-entry dot), so the
    /// brush KEY is asserted directly against the markup rather than through a full render, the same way
    /// <see cref="XamlBindingTests"/> reads the file's own text for its checks.</summary>
    [Fact]
    public void Both_risky_dot_triggers_paint_the_critical_brush_not_caution()
    {
        var xaml = File.ReadAllText(Path.Combine(FindViewsDirectory(), "CleanupWindow.xaml"));

        var riskyTriggers = System.Text.RegularExpressions.Regex.Matches(
            xaml,
            @"<DataTrigger Binding=""\{Binding (?:Target\.)?RiskLevel\}"" Value=""Risky"">\s*" +
            @"<Setter Property=""Background"" Value=""\{DynamicResource (?<brush>\w+)\}""",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.Equal(2, riskyTriggers.Count);
        Assert.All(riskyTriggers, m => Assert.Equal("SystemFillColorCriticalBrush", m.Groups["brush"].Value));
    }
}
