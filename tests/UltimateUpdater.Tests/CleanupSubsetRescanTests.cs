using System.Windows;
using System.Collections.Concurrent;
using System.Windows.Controls;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// The right-click "Rescan" of one category or one sub-category: it runs the window's own measuring
/// pass over the chosen rows only, and is refused whenever the full rescan is.
/// </summary>
[Collection("UiLanguage")]
public class CleanupSubsetRescanTests
{
    /// <summary>Answers each target with a figure that grows by one step per call, so a test can tell the
    /// first pass from the rescan, records what was measured, and can be held open on a gate.</summary>
    private sealed class StepService(LoggingService logging) : CleanupService(logging)
    {
        private int _round;

        public ConcurrentQueue<string> Scanned { get; } = new();

        public Task? Gate { get; set; }

        /// <summary>Starts a new "round": every figure handed out from now on is larger.</summary>
        public void NextRound() => Interlocked.Increment(ref _round);

        public static long BytesFor(int round) => 1000L * (round + 1);

        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            Scanned.Enqueue(target.Id);
            if (Gate is { } gate)
                await gate.WaitAsync(cancellationToken);

            var bytes = BytesFor(Volatile.Read(ref _round));
            var now = DateTimeOffset.UtcNow;
            return new CleanupScanResult(bytes, 1, 0)
            {
                Entries = [new CleanupEntry($@"C:\fake\{target.Id}\round{bytes}", $"round {bytes}", bytes, 1, now, now, CleanupEntryType.Cache)],
            };
        }
    }

    private sealed record Rig(CleanupViewModel ViewModel, StepService Service, Func<int> ElevationRequests);

    private static Rig CreateRig()
    {
        // An elevated test runner would skip the elevation path and prove nothing.
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var service = new StepService(logging);
        var host = new ElevatedHost(logging);
        host.SetRunningForTest(false);
        var requests = 0;
        host.EnsureStartedOverrideForTest = _ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(false);
        };

        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-subset-rescan-").FullName, "settings.json"));
        var viewModel = new CleanupViewModel(service, logging, settings, elevated: host);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        return new Rig(viewModel, service, () => Volatile.Read(ref requests));
    }

    private static CleanupItemViewModel UserItem(CleanupViewModel viewModel) =>
        viewModel.Items.First(i => !i.Target.NeedsElevation && !i.Target.Id.StartsWith("drive", StringComparison.Ordinal));

    [Fact]
    public async Task Rescanning_one_item_measures_only_that_item_and_replaces_its_entries()
    {
        var rig = CreateRig();
        await rig.ViewModel.StartScanAsync();
        var item = UserItem(rig.ViewModel);
        var other = rig.ViewModel.Items.First(i => i != item && !i.Target.NeedsElevation && i.Entries.Count > 0);
        var before = other.Bytes;
        var otherEntry = other.Entries.Single();

        rig.Service.NextRound();
        while (rig.Service.Scanned.TryDequeue(out _))
        {
        }

        await rig.ViewModel.RescanItemCommand.ExecuteAsync(item);

        Assert.Equal([item.Target.Id], rig.Service.Scanned.ToArray());
        Assert.Equal(StepService.BytesFor(1), item.Bytes);
        Assert.Equal($"round {StepService.BytesFor(1)}", Assert.Single(item.Entries).DisplayName);
        Assert.Equal(before, other.Bytes);
        Assert.Same(otherEntry, Assert.Single(other.Entries));
        Assert.All(rig.ViewModel.Items, i => Assert.False(i.IsScanning));
        Assert.False(rig.ViewModel.IsScanning);
    }

    [Fact]
    public async Task Rescanning_a_category_measures_exactly_its_items()
    {
        var rig = CreateRig();
        await rig.ViewModel.StartScanAsync();
        var group = rig.ViewModel.Groups.First(g => g.Items.Count > 1 && g.Items.Any(i => !i.Target.NeedsElevation));
        var outside = rig.ViewModel.Items.Except(group.Items).First(i => i.Entries.Count > 0);
        var outsideBytes = outside.Bytes;

        rig.Service.NextRound();
        while (rig.Service.Scanned.TryDequeue(out _))
        {
        }

        await rig.ViewModel.RescanGroupCommand.ExecuteAsync(group);

        Assert.Equal(
            group.Items.Select(i => i.Target.Id).Order().ToArray(),
            rig.Service.Scanned.Order().ToArray());
        Assert.All(group.Items, i => Assert.Equal(StepService.BytesFor(1), i.Bytes));
        Assert.Equal(outsideBytes, outside.Bytes);
    }

    [Fact]
    public async Task A_subset_rescan_recomputes_the_totals_and_leaves_ticks_alone()
    {
        var rig = CreateRig();
        await rig.ViewModel.StartScanAsync();
        var vm = rig.ViewModel;
        var item = UserItem(vm);
        var ticks = vm.Items.ToDictionary(i => i, i => i.IsSelected);
        item.IsSelected = true;
        ticks[item] = true;
        var othersTotal = vm.Items.Where(i => i != item).Sum(i => i.Bytes);
        var totalBefore = vm.TotalBytes;

        rig.Service.NextRound();
        await vm.RescanItemCommand.ExecuteAsync(item);

        Assert.NotEqual(totalBefore, vm.TotalBytes);
        Assert.Equal(othersTotal + StepService.BytesFor(1), vm.TotalBytes);
        Assert.Equal(
            vm.Items.Where(i => i.IsSelected).Sum(i => i.SelectedEntryBytes),
            vm.SelectedBytes);
        Assert.True(vm.SelectedBytes >= StepService.BytesFor(1));
        Assert.All(vm.Items, i => Assert.Equal(ticks[i], i.IsSelected));
        Assert.Equal(vm.Groups.First(g => g.Items.Contains(item)).TotalBytes,
            vm.Groups.First(g => g.Items.Contains(item)).Items.Sum(i => i.Bytes));
    }

    [Fact]
    public async Task A_subset_rescan_asks_for_rights_only_when_a_chosen_admin_row_is_ticked()
    {
        var rig = CreateRig();
        await rig.ViewModel.StartScanAsync();
        var vm = rig.ViewModel;
        var admin = vm.Items.First(i => i.Target.NeedsElevation);

        // Dialogs are suppressed in tests, so the question is answered "no": that answer marks the admin
        // rows with their hint, which is how a test tells that the question was asked at all.
        foreach (var row in vm.Items.Where(i => i.Target.NeedsElevation))
        {
            row.IsSelected = false;
            row.ShowsElevationHint = false;
        }

        await vm.RescanItemCommand.ExecuteAsync(UserItem(vm));
        Assert.False(admin.ShowsElevationHint);

        await vm.RescanItemCommand.ExecuteAsync(admin);
        Assert.False(admin.ShowsElevationHint);

        admin.IsSelected = true;
        await vm.RescanItemCommand.ExecuteAsync(admin);
        Assert.True(admin.ShowsElevationHint);
    }

    [Fact]
    public async Task A_subset_rescan_is_refused_while_a_scan_or_a_run_is_going()
    {
        var rig = CreateRig();
        var vm = rig.ViewModel;
        var item = UserItem(vm);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Service.Gate = release.Task;

        var scan = vm.StartScanAsync();
        Assert.True(vm.IsScanning);
        Assert.False(vm.RescanItemCommand.CanExecute(item));
        Assert.False(vm.RescanGroupCommand.CanExecute(vm.Groups[0]));

        var calls = rig.Service.Scanned.Count;
        await vm.RescanItemCommand.ExecuteAsync(item);
        await vm.RescanGroupCommand.ExecuteAsync(vm.Groups[0]);
        Assert.Equal(calls, rig.Service.Scanned.Count);

        release.SetResult();
        await scan;
        Assert.True(vm.RescanItemCommand.CanExecute(item));

        vm.Phase = CleanupPhase.Cleaning;
        Assert.False(vm.RescanItemCommand.CanExecute(item));
        Assert.False(vm.RescanGroupCommand.CanExecute(vm.Groups[0]));
    }

    [Fact]
    public async Task Items_and_groups_carry_the_window_commands()
    {
        var rig = CreateRig();
        await rig.ViewModel.StartScanAsync();

        Assert.All(rig.ViewModel.Items, i => Assert.Same(rig.ViewModel.RescanItemCommand, i.RescanCommand));
        Assert.All(rig.ViewModel.Groups, g => Assert.Same(rig.ViewModel.RescanGroupCommand, g.RescanCommand));
    }

    [Fact]
    public void Both_context_menus_are_wired_to_the_commands_in_the_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            var rig = CreateRig();
            var window = new CleanupWindow(rig.ViewModel);
            // The render detaches the content from the window, so take hold of it first.
            var root = (FrameworkElement)window.Content;
            WpfHarness.RenderWindow(window, "cleanup-subset-rescan-menu", 760, 900);

            // The group header: the toggle button of the first expander.
            var expander = Descendants<Expander>(root).First(e => e.DataContext is CleanupGroupViewModel);
            expander.IsExpanded = true;
            root.UpdateLayout();

            var toggle = Descendants<System.Windows.Controls.Primitives.ToggleButton>(expander)
                .First(t => t.ContextMenu is not null);
            AssertRescanMenu(toggle, rig.ViewModel.RescanGroupCommand, (CleanupGroupViewModel)expander.DataContext);

            // The sub-category card: the border that carries a menu inside that group.
            var card = Descendants<Border>(expander)
                .First(b => b.ContextMenu is not null && b.DataContext is CleanupItemViewModel);
            AssertRescanMenu(card, rig.ViewModel.RescanItemCommand, (CleanupItemViewModel)card.DataContext);

            // The detail rows keep their own menu, not this one.
            Assert.DoesNotContain(
                Descendants<Grid>(card).Where(g => g.DataContext is CleanupEntryViewModel && g.ContextMenu is not null),
                g => g.ContextMenu!.Items.OfType<MenuItem>().Any(m => ReferenceEquals(m.Command, rig.ViewModel.RescanItemCommand)));
        });
    }

    /// <summary>Every visual below <paramref name="node"/> of the given type, templates applied as far as
    /// they are.</summary>
    internal static IEnumerable<T> Descendants<T>(DependencyObject node)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                yield return match;

            foreach (var inner in Descendants<T>(child))
                yield return inner;
        }
    }

    private static void AssertRescanMenu(FrameworkElement owner, System.Windows.Input.ICommand command, object parameterOwner)
    {
        var menu = owner.ContextMenu!;

        // A ContextMenu is not in the visual tree: it takes its DataContext from the element it opens on,
        // which is what the opening code does, so do the same here.
        menu.PlacementTarget = owner;
        menu.IsOpen = true;
        try
        {
            menu.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var item = Assert.Single(menu.Items.OfType<MenuItem>());
            Assert.Same(command, item.Command);
            Assert.Same(parameterOwner, item.CommandParameter);
            Assert.Equal(LocalizationService.Instance["CleanupRescanMenu"], item.Header);
        }
        finally
        {
            menu.IsOpen = false;
        }
    }
}
