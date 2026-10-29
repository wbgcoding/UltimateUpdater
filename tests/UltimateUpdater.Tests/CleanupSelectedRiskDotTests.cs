using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The warning dot that bubbles up: the category header and the "Details (n)" header show the worst level
/// among what is ticked underneath, live, and show nothing (never green) while that is Safe.
/// </summary>
[Collection("UiLanguage")]
public class CleanupSelectedRiskDotTests
{
    /// <summary>Every target answers with a Safe cache row, plus a crash-report row (level Considered) for
    /// the ids in <see cref="WithCrashRow"/>.</summary>
    private sealed class TypedService(LoggingService logging) : CleanupService(logging)
    {
        public HashSet<string> WithCrashRow { get; } = [];

        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            var now = DateTimeOffset.UtcNow;
            var entries = new List<CleanupEntry>
            {
                new($@"C:\fake\{target.Id}\cache", "cache", 1000, 1, now, now, CleanupEntryType.Cache),
            };

            if (WithCrashRow.Contains(target.Id))
                entries.Add(new($@"C:\fake\{target.Id}\crash", "crash", 500, 1, now, now, CleanupEntryType.CrashReports));

            return Task.FromResult(new CleanupScanResult(entries.Sum(e => e.Bytes), entries.Count, 0) { Entries = entries });
        }
    }

    private static async Task<(CleanupViewModel ViewModel, TypedService Service)> CreateScannedAsync(params string[] crashIds)
    {
        ElevationService.IsElevatedForTest.Value = true;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var service = new TypedService(logging);
        foreach (var id in crashIds)
            service.WithCrashRow.Add(id);

        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-risk-dot-").FullName, "settings.json"));
        var viewModel = new CleanupViewModel(service, logging, settings);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        await viewModel.StartScanAsync();
        return (viewModel, service);
    }

    private static void UntickEverything(CleanupViewModel viewModel)
    {
        foreach (var item in viewModel.Items)
            item.IsSelected = false;
    }

    private static CleanupItemViewModel FirstWithTargetLevel(CleanupViewModel viewModel, CleanupRiskLevel level) =>
        viewModel.Items.First(i => i.Target.RiskLevel == level && i.Target.Kind == CleanupKind.Directories);

    private static CleanupGroupViewModel GroupOf(CleanupViewModel viewModel, CleanupItemViewModel item) =>
        viewModel.Groups.First(g => g.Items.Contains(item));

    [Fact]
    public async Task The_group_shows_the_worst_ticked_level_and_nothing_when_it_is_safe()
    {
        var probe = await CreateScannedAsync();
        var safe = FirstWithTargetLevel(probe.ViewModel, CleanupRiskLevel.Safe);
        var (vm, _) = await CreateScannedAsync(safe.Target.Id);
        safe = vm.Items.First(i => i.Target.Id == safe.Target.Id);
        var group = GroupOf(vm, safe);
        UntickEverything(vm);

        var raised = new List<string?>();
        group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // Nothing ticked: no dot, and Safe is not "green", it is absent.
        Assert.Equal(CleanupRiskLevel.Safe, group.SelectedRiskLevel);
        Assert.False(group.ShowsSelectedRisk);

        // A Safe target whose ticked detail row is Considered lifts the whole category to Considered.
        safe.IsSelected = true;
        Assert.Equal(CleanupRiskLevel.Considered, safe.SelectedEntryRiskLevel);
        Assert.True(safe.ShowsSelectedEntryRisk);
        Assert.Equal(CleanupRiskLevel.Considered, group.SelectedRiskLevel);
        Assert.True(group.ShowsSelectedRisk);
        Assert.Contains(nameof(CleanupGroupViewModel.ShowsSelectedRisk), raised);

        // Unticking that detail row takes the dot away again, in both places.
        raised.Clear();
        var crash = safe.Entries.Single(e => e.RiskLevel == CleanupRiskLevel.Considered);
        crash.IsSelected = false;
        Assert.False(safe.ShowsSelectedEntryRisk);
        Assert.Equal(CleanupRiskLevel.Safe, group.SelectedRiskLevel);
        Assert.False(group.ShowsSelectedRisk);
        Assert.Contains(nameof(CleanupGroupViewModel.SelectedRiskLevel), raised);

        // Select-all of the detail list puts it back, and a second click clears every row.
        safe.ToggleAllEntriesCommand.Execute(null);
        Assert.Equal(CleanupRiskLevel.Considered, group.SelectedRiskLevel);
        safe.ToggleAllEntriesCommand.Execute(null);
        Assert.Equal(CleanupRiskLevel.Safe, group.SelectedRiskLevel);
        safe.ToggleAllEntriesCommand.Execute(null);
        Assert.Equal(CleanupRiskLevel.Considered, group.SelectedRiskLevel);

        // The row itself unticked: its rows no longer count, however they are ticked.
        safe.IsSelected = false;
        Assert.False(safe.ShowsSelectedEntryRisk);
        Assert.Equal(CleanupRiskLevel.Safe, group.SelectedRiskLevel);
    }

    [Fact]
    public async Task A_ticked_target_counts_with_its_own_level_even_without_detail_rows()
    {
        var (vm, _) = await CreateScannedAsync();
        UntickEverything(vm);
        var risky = vm.Items.First(i => i.Target.RiskLevel == CleanupRiskLevel.Risky);
        var group = GroupOf(vm, risky);

        Assert.False(group.ShowsSelectedRisk);

        risky.IsSelected = true;
        Assert.Equal(CleanupRiskLevel.Risky, group.SelectedRiskLevel);
        Assert.True(group.ShowsSelectedRisk);

        // The tooltip names the level through the same word the item dot uses.
        LocalizationService.Instance.Apply("en");
        Assert.Equal("At least one selected entry has level Risky.", group.SelectedRiskHint);

        risky.IsSelected = false;
        Assert.False(group.ShowsSelectedRisk);
    }

    [Fact]
    public async Task The_group_checkbox_and_the_standard_selection_move_the_dot_too()
    {
        var (vm, _) = await CreateScannedAsync();
        UntickEverything(vm);
        var considered = FirstWithTargetLevel(vm, CleanupRiskLevel.Considered);
        var group = GroupOf(vm, considered);
        var expected = group.Items
            .Where(i => i.Target.Kind != CleanupKind.Defragmentation)
            .Max(i => i.Target.RiskLevel);

        var raised = new List<string?>();
        group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        group.ToggleSelectionCommand.Execute(null);
        Assert.Equal(expected, group.SelectedRiskLevel);
        Assert.Equal(expected > CleanupRiskLevel.Safe, group.ShowsSelectedRisk);
        Assert.Contains(nameof(CleanupGroupViewModel.SelectedRiskLevel), raised);

        group.ToggleSelectionCommand.Execute(null);
        Assert.Equal(CleanupRiskLevel.Safe, group.SelectedRiskLevel);
        Assert.False(group.ShowsSelectedRisk);
    }

    [Fact]
    public async Task The_hint_is_localised_with_the_level_word()
    {
        var (vm, _) = await CreateScannedAsync();
        var item = vm.Items.First();

        LocalizationService.Instance.Apply("de");
        try
        {
            Assert.Equal(
                $"Ausgewählt ist mindestens ein Eintrag mit Stufe {LocalizationService.Instance["CleanupRiskConsidered"]}.",
                CleanupItemViewModel.SelectedRiskHint(CleanupRiskLevel.Considered));
            Assert.NotNull(item);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public async Task A_collapsed_group_with_a_ticked_moderate_item_shows_the_amber_dot_in_the_dark_theme()
    {
        var (vm, _) = await CreateScannedAsync();
        UntickEverything(vm);
        var considered = FirstWithTargetLevel(vm, CleanupRiskLevel.Considered);
        var group = GroupOf(vm, considered);
        var quiet = vm.Groups.First(g => g != group);
        considered.IsSelected = true;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            WpfHarness.ApplyTheme(ApplicationTheme.Dark);

            var window = new CleanupWindow(vm);
            var root = (FrameworkElement)window.Content;
            var result = WpfHarness.RenderWindow(window, "cleanup-selected-risk-dot-dark", 760, 700, expandGroups: false);

            var dots = Descendants<Border>(root).Where(b => b.Name == "GroupRiskDot").ToList();
            var loudDot = dots.Single(b => ReferenceEquals(b.DataContext, group));
            var quietDot = dots.Single(b => ReferenceEquals(b.DataContext, quiet));

            Assert.Equal(Visibility.Visible, loudDot.Visibility);
            Assert.True(loudDot.ActualWidth > 0, "the dot was never laid out");
            Assert.Equal(Visibility.Collapsed, quietDot.Visibility);
            Assert.Equal(
                ((SolidColorBrush)loudDot.FindResource("SystemFillColorCautionBrush")).Color,
                ((SolidColorBrush)loudDot.Background).Color);
            Assert.Equal(LocalizationService.Instance["CleanupSelectedRiskHint"].Replace("{0}", LocalizationService.Instance["CleanupRiskConsidered"]),
                (string)loudDot.ToolTip);
            Assert.True(result.DistinctColours > 8);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject node)
        where T : DependencyObject => CleanupSubsetRescanTests.Descendants<T>(node);
}
