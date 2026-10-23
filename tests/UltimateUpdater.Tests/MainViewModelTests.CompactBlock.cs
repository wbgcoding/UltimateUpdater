using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>A source that reports its fixed items on the first scan and, once told to, nothing at
    /// all afterward, standing in for Windows Update's row genuinely disappearing between scans.</summary>
    private sealed class OneShotSource(string id, params UpdateItem[] items) : IUpdateSource
    {
        private bool _reportNothing;
        private bool _reportError;

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public void ReportNothingNextScan() => _reportNothing = true;

        /// <summary>Its next scan throws instead of returning anything, so the run ends in
        /// <see cref="ProviderScanState.Error"/> rather than <see cref="ProviderScanState.Done"/> with
        /// zero items - the distinction findings 3's fix depends on.</summary>
        public void ReportErrorNextScan() => _reportError = true;

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_reportError)
                throw new InvalidOperationException("Simulated transient scan failure.");

            if (!_reportNothing)
                foreach (var item in items)
                    yield return item;

            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Adds three rows straight to <see cref="MainViewModel.Updates"/>, bypassing the scan drain that
    /// normally wires each row's PropertyChanged to <see cref="MainViewModel.RefreshSelectionState"/>
    /// (see <c>DrainPendingItems</c>). Selection/visibility properties are now computed once per refresh,
    /// not re-walked on every read, so a test setting <c>row.IsSelected</c> directly needs that same
    /// wiring here or it would read a stale cached value, something a real scan-added row never does.
    /// </summary>
    private static void Populate(MainViewModel viewModel)
    {
        var rows = new[]
        {
            new UpdateRowViewModel(Item("Mozilla.Firefox", "Mozilla Firefox", "winget")),
            new UpdateRowViewModel(Item("KB5041585", "Security update", "windowsupdate", critical: true)),
            new UpdateRowViewModel(Item("eslint", "eslint", "npm")),
        };

        foreach (var row in rows)
        {
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(UpdateRowViewModel.IsSelected)
                    or nameof(UpdateRowViewModel.CanInstall)
                    or nameof(UpdateRowViewModel.DownloadSizeBytes))
                {
                    viewModel.RefreshSelectionState();
                }
            };
        }

        viewModel.Updates.AddRange(rows);
    }

    private static UpdateItem Item(string id, string name, string sourceId, bool critical = false) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = name,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        IsCritical = critical,
    };

    /// <summary>A Tier C "open in the vendor app" row, CanInstall is false, OpenTarget is set, exactly
    /// what UpdateRowViewModel.IsManualOnly requires.</summary>
    private static UpdateItem ManualItem(
        string id, string name, string sourceId = "epic", IReadOnlyList<UpdateItem>? children = null) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Games,
        Name = name,
        Tier = SourceTier.C,
        OpenTarget = "epicgames://open",
        Children = children ?? [],
    };

    /// <summary>
    /// The compact block at the table's end: manual-open rows (a store, a launcher, no install this
    /// app can do, no real values to show) leave the main grouped table and appear in ManualOnlyView
    /// instead, via a second ListCollectionView over the same _visible collection rather than a
    /// GroupStyleSelector (WPF calls that with group=null in this configuration).
    /// </summary>
    [Fact]
    public void A_manual_only_row_leaves_the_main_table_for_the_compact_block()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("Mozilla.Firefox", "Mozilla Firefox", "winget")),
                new UpdateRowViewModel(ManualItem("epic", "Epic Games Launcher")),
            ]);

            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Mozilla Firefox");
            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Epic Games Launcher");

            Assert.DoesNotContain(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>(), r => r.Name == "Mozilla Firefox");
            Assert.Contains(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>(), r => r.Name == "Epic Games Launcher");
        });
    }

    /// <summary>
    /// The drain tick runs five times a second for a whole scan. A Refresh on the compact block's view
    /// raises a Reset, which regenerates its containers and takes an open row context menu and the
    /// keyboard focus down with them, so the tick must not raise one. An ignore toggle still must:
    /// IsIgnored is that view's sort key, and nothing re-applies it on its own.
    /// </summary>
    [Fact]
    public void The_scan_drain_tick_does_not_reset_the_compact_block_but_an_ignore_toggle_does()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.Add(new UpdateRowViewModel(ManualItem("epic", "Epic Games Launcher")));

            var resets = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.ManualOnlyView).CollectionChanged +=
                (_, e) =>
                {
                    if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                        resets++;
                };

            viewModel.Flush();
            viewModel.Flush();
            Assert.Equal(0, resets);

            ((IRowActions)viewModel).IgnoreAllVersions(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>().First());
            Assert.True(resets > 0, "an ignore toggle still re-applies the sort");
        });
    }

    /// <summary>Steam carries real build ids/sizes (HideManualNumbers' own exclusion), it belongs in the
    /// normal table, not among rows that have nothing real to show.</summary>
    [Fact]
    public void A_manual_only_Steam_row_stays_in_the_main_table()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.Add(new UpdateRowViewModel(ManualItem("440", "Team Fortress 2", sourceId: "steam")));

            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Team Fortress 2");
            Assert.DoesNotContain(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>(), r => r.Name == "Team Fortress 2");
        });
    }

    /// <summary>A GitHub-release row gets its own category, sitting directly above the
    /// generic manual-update group, never merged into it, since it carries a real version comparison
    /// the rest of that group has nothing to show for.</summary>
    [Fact]
    public void A_GitHub_release_row_gets_its_own_category_ahead_of_the_generic_manual_group()
    {
        WpfHarness.OnUiThread(() =>
        {
            var github = new UpdateRowViewModel(ManualItem("owner/repo", "Some Tool", sourceId: "github-release"));
            var steam = new UpdateRowViewModel(ManualItem("440", "Team Fortress 2", sourceId: "steam"));

            Assert.NotEqual(steam.CategoryKey, github.CategoryKey);
            Assert.NotEqual(steam.CategoryDisplay, github.CategoryDisplay);
            Assert.True(github.CategoryOrder < steam.CategoryOrder, "the GitHub group must sit ahead of the generic manual-update group");
        });
    }

    /// <summary>Same reasoning as Steam (HideManualNumbers' own exclusion, above): a GitHub-release row
    /// carries a real installed/available version, so it belongs in the main table, not the compact
    /// chip block that has nothing real to show.</summary>
    [Fact]
    public void A_GitHub_release_row_stays_in_the_main_table_like_Steam_does()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.Add(new UpdateRowViewModel(ManualItem("owner/repo", "Some Tool", sourceId: "github-release")));

            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Some Tool");
            Assert.DoesNotContain(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>(), r => r.Name == "Some Tool");
        });
    }

    /// <summary>An aggregate row with its own nested child list (the Steam launcher row) does not fit a
    /// flat row of compact chips, it stays a table row so its Expander still has somewhere to live.</summary>
    [Fact]
    public void A_manual_only_row_with_children_stays_in_the_main_table()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var children = new[] { ManualItem("440", "Team Fortress 2", sourceId: "steam") };
            viewModel.Updates.Add(new UpdateRowViewModel(ManualItem("steam", "Steam", sourceId: "steam", children: children)));

            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Steam");
            Assert.DoesNotContain(viewModel.ManualOnlyView.Cast<UpdateRowViewModel>(), r => r.Name == "Steam");
        });
    }

    /// <summary>A search matching only a compact-block row must not show the "nothing found" empty state
    /// above a non-empty compact block.</summary>
    [Fact]
    public void HasResults_stays_true_when_only_the_compact_block_matches_the_search()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("Mozilla.Firefox", "Mozilla Firefox", "winget")),
                new UpdateRowViewModel(ManualItem("epic", "Epic Games Launcher")),
            ]);

            viewModel.SearchQuery = "Epic";

            Assert.False(viewModel.UpdatesView.Cast<UpdateRowViewModel>().Any());
            Assert.True(viewModel.HasManualOnlyResults);
            Assert.True(viewModel.HasResults);
            Assert.False(viewModel.IsFilterHidingEverything);
        });
    }
}
