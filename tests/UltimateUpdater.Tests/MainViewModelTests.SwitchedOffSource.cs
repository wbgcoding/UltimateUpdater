using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>Counts its scans and can hold one open, so a test sees both a rescan being requested and a scan in flight.</summary>
    private sealed class CountingSource(string id, params UpdateItem[] items) : IUpdateSource
    {
        private int _scans;

        public int Scans => Volatile.Read(ref _scans);

        public TaskCompletionSource? Gate { get; set; }

        public string Id => id;

        public UpdateCategory Category { get; init; } = UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _scans);
            if (Gate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);

            foreach (var item in items)
                yield return item;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>The orchestrator skips a switched-off source without a word, so the next scan used to rebuild the pills without it and the main window offered no way back.</summary>
    [Fact]
    public void A_source_switched_off_keeps_its_dimmed_pill_through_the_next_scan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new CountingSource("winget", Item("git", "Git", "winget")), new CountingSource("npm", Item("eslint", "eslint", "npm"))]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            viewModel.ToggleSourceCommand.Execute("winget");
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var pill = Assert.Single(viewModel.PillView.Cast<ProviderStatusViewModel>(), p => p.SourceId == "winget");
            Assert.False(pill.IsEnabled);
            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Item.SourceId == "winget");
            Assert.Contains(viewModel.Updates, r => r.Item.SourceId == "winget");
        });
    }

    [Fact]
    public void Switching_a_source_back_on_after_a_scan_brings_its_rows_back_through_a_rescan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var winget = new CountingSource("winget", Item("git", "Git", "winget"));
            var viewModel = Create([winget, new CountingSource("npm", Item("eslint", "eslint", "npm"))]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            viewModel.ToggleSourceCommand.Execute("winget");
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var scansBefore = winget.Scans;

            viewModel.ToggleSourceCommand.Execute("winget");
            WaitUntil(() => winget.Scans > scansBefore && !viewModel.IsScanning);

            Assert.True(winget.Scans > scansBefore);
            var pill = Assert.Single(viewModel.PillView.Cast<ProviderStatusViewModel>(), p => p.SourceId == "winget");
            Assert.True(pill.IsEnabled);
            Assert.Contains(viewModel.Updates, r => r.Item.SourceId == "winget");
        });
    }

    [Fact]
    public void The_scanning_subheadline_uses_the_language_s_list_separator()
    {
        WpfHarness.OnUiThread(() =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var viewModel = Create([
                new CountingSource("winget", Item("git", "Git", "winget")) { Gate = gate },
                new CountingSource("npm", Item("eslint", "eslint", "npm")) { Gate = gate },
            ]);
            try
            {
                LocalizationService.Instance.Apply("ja");
                var scan = viewModel.ScanCommand.ExecuteAsync(null);
                WaitUntil(() => viewModel.ProviderStatuses.Count(p => p.State == ProviderScanState.Scanning) == 2);

                var text = viewModel.SubheadlineText;
                Assert.Contains("、", text);
                Assert.DoesNotContain(", ", text);

                gate.SetResult();
                Pump(scan);
            }
            finally
            {
                gate.TrySetResult();
                LocalizationService.Instance.Apply("en");
            }
        });
    }

    /// <summary>"Remind me later" on a row that was ignored moves it out of the ignored rows at the bottom: the sort key changed and the view does not sort live.</summary>
    [Fact]
    public void Snoozing_an_ignored_row_returns_it_to_its_sorted_place()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var first = new UpdateRowViewModel(Item("a-tool", "Alpha", "npm"));
            var second = new UpdateRowViewModel(Item("b-tool", "Bravo", "npm"));
            var third = new UpdateRowViewModel(Item("c-tool", "Charlie", "npm"));
            viewModel.Updates.AddRange([first, second, third]);
            viewModel.ShowIgnored = true;
            ((IRowActions)viewModel).IgnoreAllVersions(first);
            Assert.Equal([second, third, first], viewModel.UpdatesView.Cast<UpdateRowViewModel>().ToList());

            ((IRowActions)viewModel).Snooze(first, "day");

            Assert.Equal([first, second, third], viewModel.UpdatesView.Cast<UpdateRowViewModel>().ToList());
        });
    }

    /// <summary>ApplyFilter already refreshes the selection state; a row menu click must not walk the rows a second time.</summary>
    [Fact]
    public void Ignoring_a_row_refreshes_the_selection_state_once()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(Item("a-tool", "Alpha", "npm"));
            viewModel.Updates.Add(row);
            var before = viewModel.RefreshSelectionStateRunCountForTest;

            viewModel.IgnoreCommand.Execute(row);

            Assert.Equal(1, viewModel.RefreshSelectionStateRunCountForTest - before);
        });
    }

    /// <summary>A History-window rollback of an npm package shows under its own group for the run, not under "Software".</summary>
    [Fact]
    public void A_transient_row_takes_its_category_from_its_source()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new CountingSource("npm") { Category = UpdateCategory.DeveloperTools }]);

            var row = viewModel.CreateTransientRow("npm", "eslint", "eslint", "9.0.0");
            var unknown = viewModel.CreateTransientRow("gone", "x", "x", "1.0.0");

            Assert.Equal(nameof(UpdateCategory.DeveloperTools), row.CategoryKey);
            Assert.Equal(nameof(UpdateCategory.Software), unknown.CategoryKey);
        });
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
