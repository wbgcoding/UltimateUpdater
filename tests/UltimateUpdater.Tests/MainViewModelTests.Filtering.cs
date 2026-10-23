using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>The filter menu's entries drive exactly what the toolbar switches did, and the button label
    /// counts how many are on.</summary>
    [Fact]
    public void Filter_menu_entries_change_what_the_table_shows_and_the_button_counts_them()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            viewModel.Updates.Add(new UpdateRowViewModel(ManualItem("launcher", "Some Launcher")));
            ((IRowActions)viewModel).IgnoreAllVersions(viewModel.Updates.First(u => u.Name == "eslint"));

            var entries = viewModel.BuildFilterEntries();
            Assert.Equal(["critical", "ignored", "manual"], entries.Select(e => e.Id));
            Assert.Equal("Filter", viewModel.FilterButtonText);
            Assert.Equal(0, viewModel.ActiveFilterCount);

            int Shown() => viewModel.UpdatesView.Cast<UpdateRowViewModel>().Count();
            FilterEntry Entry(string id) => entries.Single(e => e.Id == id);

            // default: the ignored row is out, the manual row is in
            Assert.Equal(2, Shown());
            Assert.True(viewModel.HasManualOnlyResults);

            Entry("critical").Set(true);
            Assert.True(viewModel.ShowOnlyCritical);
            Assert.Equal(1, Shown());
            Assert.Equal("Filter (1)", viewModel.FilterButtonText);

            Entry("ignored").Set(true);
            Assert.True(viewModel.ShowIgnored);
            Assert.Equal("Filter (2)", viewModel.FilterButtonText);
            Entry("critical").Set(false);
            Assert.Equal(3, Shown());
            Assert.Equal("Filter (1)", viewModel.FilterButtonText);

            Entry("manual").Set(true);
            Assert.True(viewModel.HideManual);
            Assert.False(viewModel.HasManualOnlyResults);
            Assert.Equal("Filter (2)", viewModel.FilterButtonText);

            Assert.True(Entry("manual").IsOn());
            Entry("manual").Set(false);
            Entry("ignored").Set(false);
            Assert.Equal("Filter", viewModel.FilterButtonText);
            Assert.True(viewModel.HasManualOnlyResults);
            Assert.Equal(2, Shown());
        });
    }

    /// <summary>The persistent-failure entry is offered only while there is something to collect, or while
    /// its filter is still on (so it can always be turned off again).</summary>
    [Fact]
    public void The_failure_entry_of_the_filter_menu_appears_only_while_it_has_something_to_show()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            Assert.DoesNotContain(viewModel.BuildFilterEntries(), e => e.Id == "failures");

            viewModel.Updates.First(u => u.Name == "eslint").InstallState = RowInstallState.NeedsManualWork;
            var entry = Assert.Single(viewModel.BuildFilterEntries(), e => e.Id == "failures");

            entry.Set(true);
            Assert.True(viewModel.ShowOnlyPersistentFailures);
            Assert.Equal("Filter (1)", viewModel.FilterButtonText);
            Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
        });
    }

    /// <summary>The header checkbox is the only select-all control left; its tooltip and accessible name flip
    /// to "clear" once every visible row is ticked.</summary>
    [Fact]
    public void The_select_all_tooltip_flips_to_clear_once_everything_visible_is_selected()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            Assert.Equal(viewModel.Loc["SelectAllButton"], viewModel.SelectAllTooltip);

            viewModel.SelectAllCommand.Execute(null);
            Assert.True(viewModel.AllVisibleSelected);
            Assert.Equal(viewModel.Loc["SelectAllClear"], viewModel.SelectAllTooltip);
        });
    }

    /// <summary>The headline counts what is still pending, including rows already queued or running, and a
    /// row that succeeded drops out (and moves the headline on its own).</summary>
    [Fact]
    public void The_headline_counts_queued_and_running_rows_until_they_succeed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var rows = Enumerable.Range(1, 4)
                .Select(i => new UpdateRowViewModel(Item($"pkg{i}", $"Package {i}", "winget")))
                .ToList();
            var track = typeof(MainViewModel).GetMethod("TrackSelection", BindingFlags.NonPublic | BindingFlags.Instance)!;
            foreach (var row in rows)
                track.Invoke(viewModel, [row]);
            viewModel.Updates.AddRange(rows);

            string Available(int count) => string.Format(viewModel.Loc["HeadlineUpdates"], count);

            Assert.Equal(Available(4), viewModel.HeadlineText);

            rows[0].InstallState = RowInstallState.Running;
            rows[1].InstallState = RowInstallState.Running;
            rows[2].InstallState = RowInstallState.Queued;
            Assert.Equal(Available(4), viewModel.HeadlineText);

            var raised = new List<string?>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            rows[0].InstallState = RowInstallState.Succeeded;

            Assert.Contains(nameof(MainViewModel.HeadlineText), raised);
            Assert.Equal(Available(3), viewModel.HeadlineText);
        });
    }

    /// <summary>Between scans the subheadline is empty (the critical count lives on the rows, the badge and
    /// the footer's selection summary), so the header carries no second line.</summary>
    [Fact]
    public void The_subheadline_is_empty_after_a_scan_while_the_footer_still_names_the_critical_count()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            Assert.Single(viewModel.Updates, u => u.IsCritical);
            Assert.Equal(string.Empty, viewModel.SubheadlineText);

            viewModel.Updates.First(u => u.IsCritical).IsSelected = true;

            Assert.Contains("1", viewModel.SelectionSummary);
            Assert.Equal(
                string.Format(viewModel.Loc["SelectionSummaryWithCritical"], 1, 1),
                viewModel.SelectionSummary);
        });
    }

    /// <summary>
    /// A filter change must touch ONLY the rows whose visibility changed (requirement, 2026-07-26: "es soll
    /// sich immer nur der Teil aktualisieren der sich auch ändert"). A Reset would make the table throw away
    /// every row container and re-animate rows that did not move, which is exactly what was observed.
    /// </summary>
    [Fact]
    public void Switching_a_filter_removes_only_the_rows_that_left()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
            ((System.Collections.Specialized.INotifyCollectionChanged)viewModel.UpdatesView).CollectionChanged +=
                (_, e) => actions.Add(e.Action);

            // two of the three rows are not critical, so exactly those two leave
            viewModel.ShowOnlyCritical = true;

            Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
            Assert.Equal(
                2,
                actions.Count(a => a == System.Collections.Specialized.NotifyCollectionChangedAction.Remove));
            Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
        });
    }

    /// <summary>And the row that stayed is the SAME object, not a rebuilt copy of itself.</summary>
    [Fact]
    public void A_row_that_stays_visible_survives_a_filter_change()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var before = viewModel.UpdatesView.Cast<UpdateRowViewModel>().Single(r => r.IsCritical);

            viewModel.ShowOnlyCritical = true;
            viewModel.ShowOnlyCritical = false;

            Assert.Same(before, viewModel.UpdatesView.Cast<UpdateRowViewModel>().Single(r => r.IsCritical));
            Assert.Equal(3, viewModel.UpdatesView.Cast<UpdateRowViewModel>().Count());
        });
    }

    // ---- the "keeps failing" filter chip ----

    /// <summary>Never a chip sitting there empty: with nothing flagged, both the count and the visibility
    /// flag behind it read as "nothing to show".</summary>
    [Fact]
    public void PersistentFailureChip_is_hidden_when_nothing_is_flagged()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new OneShotSource("npm", Item("eslint", "eslint", "npm"));
            var viewModel = Create(sources: [source]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Equal(0, viewModel.PersistentFailureCount);
            Assert.False(viewModel.HasPersistentFailures);
        });
    }

    /// <summary>Turning the chip on shows exactly the rows the three-strikes tally promoted to
    /// NeedsManualWork, nothing else, and the chip's own count matches.</summary>
    [Fact]
    public void Turning_on_the_chip_shows_exactly_the_flagged_rows()
    {
        WpfHarness.OnUiThread(() =>
        {
            var flaky = new OneShotSource("npm", Item("eslint", "eslint", "npm"));
            var healthy = new OneShotSource("winget", Item("Mozilla.Firefox", "Firefox", "winget"));
            var viewModel = Create(sources: [flaky, healthy]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var flaggedRow = viewModel.Updates.Single(u => u.Item.SourceId == "npm");
            flaggedRow.InstallState = RowInstallState.NeedsManualWork;

            Assert.Equal(1, viewModel.PersistentFailureCount);
            Assert.True(viewModel.HasPersistentFailures);

            viewModel.ShowOnlyPersistentFailures = true;

            var shown = viewModel.UpdatesView.Cast<UpdateRowViewModel>().ToList();
            Assert.Contains(flaggedRow, shown);
            Assert.DoesNotContain(shown, u => u.Item.SourceId == "winget");
        });
    }

    /// <summary>Ignoring a flagged row from its own hint (the same command the row's context menu already
    /// uses) is how a row leaves this one view: the tally is done being collected once ignored, even
    /// though an ignored row otherwise stays in the normal table.</summary>
    [Fact]
    public void Ignoring_a_flagged_row_removes_it_from_the_filtered_view()
    {
        WpfHarness.OnUiThread(() =>
        {
            var flaky = new OneShotSource("npm", Item("eslint", "eslint", "npm"));
            var viewModel = Create(sources: [flaky]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var row = viewModel.Updates.Single();
            row.InstallState = RowInstallState.NeedsManualWork;
            viewModel.ShowOnlyPersistentFailures = true;

            Assert.Contains(row, viewModel.UpdatesView.Cast<UpdateRowViewModel>());

            row.IgnoreCommand.Execute(null);

            Assert.DoesNotContain(row, viewModel.UpdatesView.Cast<UpdateRowViewModel>());
        });
    }
}
