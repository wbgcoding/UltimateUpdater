using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// What the user picked, and only that, is what gets installed. The table is filtered, by search,
/// by "critical only" and by the ignore list, so selection must be read from the filtered view, never
/// the raw collection, or rows nobody could see would be installed with admin rights.
/// </summary>
[Collection("wpf")]
public partial class MainViewModelTests
{
    [Fact]
    public void Sorting_by_a_column_toggles_direction_on_the_second_click()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            viewModel.SortByCommand.Execute("date");
            Assert.Equal("date", viewModel.SortColumn);
            Assert.True(viewModel.SortDescending);          // dates default to newest-first
            Assert.Equal(" ▼", viewModel.DateSortGlyph);
            Assert.Equal(string.Empty, viewModel.NameSortGlyph);

            viewModel.SortByCommand.Execute("date");
            Assert.False(viewModel.SortDescending);
            Assert.Equal(" ▲", viewModel.DateSortGlyph);

            viewModel.SortByCommand.Execute("name");
            Assert.Equal("name", viewModel.SortColumn);
            Assert.False(viewModel.SortDescending);          // names default A→Z
        });
    }

    [Fact]
    public void GlyphFor_shows_nothing_for_an_inactive_sortable_column()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            viewModel.SortByCommand.Execute("size");

            Assert.Equal(string.Empty, viewModel.GlyphFor("name"));
            Assert.Equal(string.Empty, viewModel.GlyphFor("date"));
            Assert.Equal(string.Empty, viewModel.GlyphFor("version"));
            Assert.Equal(" ▼", viewModel.GlyphFor("size"));
        });
    }

    [Theory]
    [InlineData("en", "Sort by Date")]
    [InlineData("de", "Nach Datum sortieren")]
    public void Each_sortable_header_has_a_sort_by_tooltip_naming_its_column(string language, string expected)
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            UltimateUpdater.Services.LocalizationService.Instance.Apply(language);
            try
            {
                Assert.Equal(expected, viewModel.DateSortTooltip);
                Assert.Contains(UltimateUpdater.Services.LocalizationService.Instance["ColumnName"], viewModel.NameSortTooltip);
                Assert.Contains(UltimateUpdater.Services.LocalizationService.Instance["ColumnVersion"], viewModel.VersionSortTooltip);
                Assert.Contains(UltimateUpdater.Services.LocalizationService.Instance["ColumnSize"], viewModel.SizeSortTooltip);
            }
            finally
            {
                UltimateUpdater.Services.LocalizationService.Instance.Apply("en");
            }
        });
    }

    [Fact]
    public void Sorting_by_date_and_size_reorders_rows_inside_a_group()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var older = new UpdateItem
            {
                Id = "KB1", SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers, Name = "Older big",
                AvailableVersion = "1", PublishDate = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero),
                DownloadSize = 890L * 1024 * 1024,
            };
            var newer = new UpdateItem
            {
                Id = "KB2", SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers, Name = "Newer small",
                AvailableVersion = "1", PublishDate = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
                DownloadSize = 62L * 1024 * 1024,
            };
            viewModel.Updates.AddRange([new UpdateRowViewModel(older), new UpdateRowViewModel(newer)]);

            List<string> Order() => viewModel.UpdatesView.Cast<UpdateRowViewModel>().Select(r => r.Item.Id).ToList();

            viewModel.SortByCommand.Execute("date");
            Assert.Equal(["KB2", "KB1"], Order());
            viewModel.SortByCommand.Execute("date");
            Assert.Equal(["KB1", "KB2"], Order());

            viewModel.SortByCommand.Execute("size");
            Assert.Equal(["KB1", "KB2"], Order());
            viewModel.SortByCommand.Execute("size");
            Assert.Equal(["KB2", "KB1"], Order());
        });
    }

    [Fact]
    public void Switching_the_language_renames_the_category_group_headers()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.SelectedLanguage = "en";
            viewModel.Updates.Add(new UpdateRowViewModel(new UpdateItem
            {
                Id = "KB1", SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers, Name = "Update",
                InstalledVersion = "1", AvailableVersion = "2",
            }));

            string Header() => (string)viewModel.UpdatesView.Groups!.Cast<System.Windows.Data.CollectionViewGroup>().First().Name;

            var english = Header();
            try
            {
                viewModel.SelectedLanguage = "de";
                Assert.NotEqual(english, Header());
                Assert.Equal(viewModel.Updates[0].CategoryDisplay, Header());
            }
            finally
            {
                viewModel.SelectedLanguage = "en";
            }
        });
    }

    /// <summary>"No connection at all" (WasOffline) wins over "every online source failed while the
    /// system still reports a connection" (AllOnlineSourcesFailed) when both apply, since it explains the
    /// more specific finding too.</summary>
    [Fact]
    public void OfflineBannerText_prefers_the_offline_case_over_the_connection_case()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            Assert.Equal(string.Empty, viewModel.OfflineBannerText);
            Assert.False(viewModel.HasConnectionBanner);

            viewModel.AllOnlineSourcesFailed = true;
            Assert.Equal(viewModel.Loc["ScanNoConnectionBanner"], viewModel.OfflineBannerText);
            Assert.True(viewModel.HasConnectionBanner);

            viewModel.WasOffline = true;
            Assert.Equal(viewModel.Loc["ScanOfflineBanner"], viewModel.OfflineBannerText);
            Assert.True(viewModel.HasConnectionBanner);

            viewModel.WasOffline = false;
            viewModel.AllOnlineSourcesFailed = false;
            Assert.Equal(string.Empty, viewModel.OfflineBannerText);
            Assert.False(viewModel.HasConnectionBanner);
        });
    }

    /// <summary>
    /// Each row's own IsSelected setter used to hop straight into RefreshSelectionState (via
    /// TrackSelection), and that walk is itself O(N) over the visible rows, so ticking N rows in one
    /// SelectAll cost O(N^2). It now suppresses that per-row hop and runs the walk once after the whole
    /// loop. Measured directly: before this fix the delta below was 500, one full pass per row.
    /// </summary>
    [Fact]
    public void Select_all_costs_one_selection_state_pass_not_one_per_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            const int rowCount = 500;
            var items = Enumerable.Range(0, rowCount)
                .Select(i => Item($"pkg{i}", $"Package {i}", "bulk-source"))
                .ToArray();
            var viewModel = Create(sources: [new OneShotSource("bulk-source", items)]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            Assert.Equal(rowCount, viewModel.Updates.Count);

            var before = viewModel.RefreshSelectionStateRunCountForTest;
            viewModel.SelectAllCommand.Execute(null);
            var after = viewModel.RefreshSelectionStateRunCountForTest;

            Assert.All(viewModel.Updates, r => Assert.True(r.IsSelected));
            Assert.True(
                after - before <= 3,
                $"expected O(1) RefreshSelectionState runs for a {rowCount}-row select-all, got {after - before} (before={before}, after={after})");
        });
    }

    [Fact]
    public void Select_all_ticks_only_what_is_on_screen()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            viewModel.SearchQuery = "eslint";

            viewModel.SelectAllCommand.Execute(null);

            var selected = viewModel.Updates.Where(u => u.IsSelected).Select(u => u.Name).ToList();

            Assert.Equal(["eslint"], selected);
        });
    }

    /// <summary>
    /// CategoryKey is what group-collapse state must be keyed by, precisely BECAUSE it does not change
    /// when CategoryDisplay's translation does, keying the collapsed-groups set by the display text
    /// itself would let switching language silently reopen every collapsed group.
    /// </summary>
    [Fact]
    public void CategoryKey_stays_the_same_across_a_language_switch_while_CategoryDisplay_changes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var row = new UpdateRowViewModel(new UpdateItem
            {
                Id = "some-game", SourceId = "steam", Category = UpdateCategory.Games, Name = "Some Game",
            });

            LocalizationService.Instance.Apply("en");
            var keyEn = row.CategoryKey;
            var displayEn = row.CategoryDisplay;

            LocalizationService.Instance.Apply("de");
            var keyDe = row.CategoryKey;
            var displayDe = row.CategoryDisplay;

            Assert.Equal(keyEn, keyDe);
            Assert.NotEqual(displayEn, displayDe);
        });
    }

    /// <summary>
    /// RunningRows/RunningCount follow the batch, not a snapshot taken once, a row's own
    /// InstallState is what decides whether it counts, so a queued or finished row must be excluded and a
    /// row that starts running afterwards must be picked up without anyone re-adding it.
    /// </summary>
    [Fact]
    public void RunningRows_and_RunningCount_follow_the_batch()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var running1 = new UpdateRowViewModel(Item("Mozilla.Firefox", "Mozilla Firefox", "winget"))
            {
                InstallState = RowInstallState.Running,
            };
            var queued = new UpdateRowViewModel(Item("eslint", "eslint", "npm"))
            {
                InstallState = RowInstallState.Queued,
            };
            var finished = new UpdateRowViewModel(Item("git", "Git", "winget"))
            {
                InstallState = RowInstallState.Succeeded,
            };
            viewModel.InstallBatch.Add(running1);
            viewModel.InstallBatch.Add(queued);
            viewModel.InstallBatch.Add(finished);
            InvokeRefreshInstallProgress(viewModel);

            Assert.Equal(1, viewModel.RunningCount);
            Assert.Equal([running1], viewModel.RunningRows);
            Assert.False(viewModel.HasMultipleRunning);

            // The queued row starts running (its turn came up) without ever being re-added to the batch,
            // the running strip picks it up on the next refresh, the same tick that already redraws the
            // rest of the install progress, rather than the whole list being rebuilt from scratch.
            queued.InstallState = RowInstallState.Running;
            InvokeRefreshInstallProgress(viewModel);

            Assert.Equal(2, viewModel.RunningCount);
            Assert.Equal([running1, queued], viewModel.RunningRows);
            Assert.True(viewModel.HasMultipleRunning);
            Assert.False(viewModel.ShowSingleInstallView);
        });
    }

    private static void InvokeRefreshInstallProgress(MainViewModel viewModel) =>
        typeof(MainViewModel).GetMethod("RefreshInstallProgress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, null);

    /// <summary>The running strip used to be a brand-new list every tick, which reset the bound
    /// ItemsControl (and any per-row animation) even when nothing about the running set had changed.
    /// It must now stay the same collection instance and only ever raise Add/Remove, never Reset.</summary>
    [Fact]
    public void RunningRows_stays_the_same_instance_and_never_resets_wholesale()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var rows = Enumerable.Range(0, 5)
                .Select(i => new UpdateRowViewModel(Item($"pkg{i}", $"Package {i}", "winget")))
                .ToList();
            foreach (var row in rows)
                viewModel.InstallBatch.Add(row);

            var instance = viewModel.RunningRows;
            var resetCount = 0;
            instance.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    resetCount++;
            };

            rows[0].InstallState = RowInstallState.Running;
            rows[1].InstallState = RowInstallState.Running;
            InvokeRefreshInstallProgress(viewModel);

            rows[0].InstallState = RowInstallState.Succeeded;
            rows[2].InstallState = RowInstallState.Running;
            InvokeRefreshInstallProgress(viewModel);

            Assert.Same(instance, viewModel.RunningRows);
            Assert.Equal(0, resetCount);
            Assert.Equal([rows[1], rows[2]], viewModel.RunningRows);
        });
    }

    /// <summary>The header select-all checkbox: true when all visible installable rows are ticked,
    /// null when only some are, false when none, and its click toggles the whole visible set.</summary>
    [Fact]
    public void Header_select_all_reflects_the_visible_selection_tri_state()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            Assert.False(viewModel.AllVisibleSelected);      // nothing ticked

            viewModel.Updates.First(u => u.Name == "eslint").IsSelected = true;
            Assert.Null(viewModel.AllVisibleSelected);        // some ticked → indeterminate

            viewModel.SelectAllCommand.Execute(null);
            Assert.True(viewModel.AllVisibleSelected);         // all ticked

            viewModel.SelectAllCommand.Execute(null);
            Assert.False(viewModel.AllVisibleSelected);        // toggled back off
        });
    }

    /// <summary>Only rows on screen count toward the header checkbox's state.</summary>
    [Fact]
    public void Header_select_all_ignores_rows_a_filter_hides()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            viewModel.SearchQuery = "eslint";

            viewModel.SelectAllCommand.Execute(null);

            // only eslint is visible, and it is now the whole (visible) selection
            Assert.True(viewModel.AllVisibleSelected);
        });
    }

    [Fact]
    public void Ignoring_a_row_unticks_it_and_hides_it_at_once_until_ignored_rows_are_shown()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var row = viewModel.Updates.First(u => u.Name == "eslint");
            row.IsSelected = true;

            viewModel.IgnoreCommand.Execute(row);

            // unticked and marked, and gone from the table at once (ignored rows are hidden by default)
            Assert.False(viewModel.ShowIgnored);
            Assert.False(row.IsSelected);
            Assert.True(row.IsIgnored);
            Assert.False(row.CanInstall);
            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");

            // "show ignored" brings it back with its badge and the inline "show again" button
            viewModel.ShowIgnored = true;
            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");
            Assert.False(string.IsNullOrWhiteSpace(row.IgnoredBadgeLabel));
            Assert.True(row.ShowUnignoreButton);

            // and un-ignoring clears the mark
            viewModel.IgnoreCommand.Execute(row);
            Assert.False(row.IsIgnored);
            Assert.True(row.CanInstall);
        });
    }

    /// <summary>
    /// "Ignore this version" is version-specific: the same version stays hidden, but a genuinely newer one
    /// comes back. This is what silences a winget "phantom" upgrade that reappears after installing
    /// (Google Play Games 26.7 → 152.0) without hiding real future updates.
    /// </summary>
    [Fact]
    public void Ignoring_a_version_hides_that_version_only()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.Add(new UpdateRowViewModel(Item("Google.PlayGames", "Google Play Games", "winget")));

            var row = viewModel.Updates.Single();
            viewModel.IgnoreCommand.Execute(row);
            Assert.True(row.IsIgnored);

            // the same package at the SAME version is ignored again on a later scan
            var sameAgain = new UpdateRowViewModel(Item("Google.PlayGames", "Google Play Games", "winget"));
            viewModel.Updates.Clear();
            viewModel.Updates.Add(sameAgain);
            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Google Play Games");

            // …but a genuinely newer version is shown
            var newer = Item("Google.PlayGames", "Google Play Games", "winget");
            newer = new UpdateItem
            {
                Id = newer.Id, SourceId = newer.SourceId, Category = newer.Category, Name = newer.Name,
                InstalledVersion = newer.InstalledVersion, AvailableVersion = "3.0",
            };
            viewModel.Updates.Clear();
            viewModel.Updates.Add(new UpdateRowViewModel(newer));
            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Google Play Games");
        });
    }

    /// <summary>"Ignore all versions" keeps the package hidden even when a newer version turns up.</summary>
    [Fact]
    public void Ignoring_all_versions_hides_every_later_version_too()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.Add(new UpdateRowViewModel(Item("Some.Tool", "Some tool", "winget")));

            ((IRowActions)viewModel).IgnoreAllVersions(viewModel.Updates.Single());

            var newer = new UpdateItem
            {
                Id = "Some.Tool", SourceId = "winget", Category = UpdateCategory.Software, Name = "Some tool",
                InstalledVersion = "1.0", AvailableVersion = "9.9",
            };
            viewModel.Updates.Clear();
            viewModel.Updates.Add(new UpdateRowViewModel(newer));

            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "Some tool");
        });
    }

    /// <summary>An ignored critical update must not come back preselected on the next scan.</summary>
    [Fact]
    public void An_ignored_update_is_never_preselected()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var critical = viewModel.Updates.First(u => u.IsCritical);
            viewModel.IgnoreCommand.Execute(critical);

            // second scan finds the same item again
            var reloaded = Create();
            Populate(reloaded);

            var again = reloaded.Updates.First(u => u.IsCritical);

            Assert.False(again.IsSelected);
        });
    }

    [Fact]
    public void An_ignored_update_stops_being_counted()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var before = viewModel.HeadlineText;
            foreach (var row in viewModel.Updates.Where(u => u.CanInstall).ToList())
                viewModel.IgnoreCommand.Execute(row);

            Assert.NotEqual(before, viewModel.HeadlineText);
            Assert.Equal(LocalizationService.Instance["HeadlineUpToDate"], viewModel.HeadlineText);

            // ignored rows are hidden by default, so with nothing left to show the table is empty because
            // of a filter; a rescan would find the same ignored updates again
            Assert.True(viewModel.IsFilterHidingEverything);

            viewModel.ShowIgnored = true;
            Assert.False(viewModel.IsFilterHidingEverything);
        });
    }

    /// <summary>A deferred row drops out of the headline count the same way an ignored one
    /// already does, not just out of the table.</summary>
    [Fact]
    public void A_snoozed_update_stops_being_counted()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var before = viewModel.HeadlineText;
            foreach (var row in viewModel.Updates.Where(u => u.CanInstall).ToList())
                ((IRowActions)viewModel).Snooze(row, "day");

            Assert.NotEqual(before, viewModel.HeadlineText);
            Assert.Equal(LocalizationService.Instance["HeadlineUpToDate"], viewModel.HeadlineText);
        });
    }

    /// <summary>
    /// A deferred row is governed by the SAME "show ignored" switch an ignored row uses, not a separate
    /// "Zurückgestellt" chip of its own. That switch defaults to off, so a freshly snoozed row leaves the
    /// table at once and comes back, with its due date badge, only while "show ignored" is on.
    /// </summary>
    [Fact]
    public void Snoozing_a_row_hides_it_until_show_ignored_is_on_and_then_shows_its_due_date()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var row = viewModel.Updates.First(u => u.Name == "eslint");
            row.IsSelected = true;

            ((IRowActions)viewModel).Snooze(row, "day");

            Assert.False(row.IsSelected);
            Assert.True(row.IsSnoozed);
            Assert.False(row.CanInstall);
            Assert.InRange(row.SnoozedUntil!.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
            Assert.False(viewModel.ShowIgnored);
            Assert.DoesNotContain(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");

            // Turning "show ignored" on brings the deferred row back exactly like an ignored one.
            viewModel.ShowIgnored = true;
            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");

            viewModel.ShowIgnored = false;
            ((IRowActions)viewModel).Unsnooze(row);
            Assert.False(row.IsSnoozed);
            Assert.True(row.CanInstall);
            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");
        });
    }

    /// <summary>Ignore and Snooze are mutually exclusive: setting either one clears the other.</summary>
    [Fact]
    public void Ignoring_a_snoozed_row_clears_the_snooze_and_snoozing_an_ignored_row_clears_the_ignore()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            var row = viewModel.Updates.First(u => u.Name == "eslint");

            viewModel.IgnoreCommand.Execute(row);
            Assert.True(row.IsIgnored);

            ((IRowActions)viewModel).Snooze(row, "week");
            Assert.False(row.IsIgnored);
            Assert.True(row.IsSnoozed);

            // Ignore, toggled fresh (the row is no longer ignored), takes it back the other way
            viewModel.IgnoreCommand.Execute(row);
            Assert.True(row.IsIgnored);
            Assert.False(row.IsSnoozed);
        });
    }

    /// <summary>
    /// A snooze whose due date has passed no longer hides the row, even before the row's own cached
    /// IsSnoozed flag is refreshed by a scan, the filter always re-checks the persisted due date, never
    /// the row's stale flag alone (EnrichmentCache follows the equivalent rule).
    /// </summary>
    [Fact]
    public void An_expired_snooze_no_longer_hides_the_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";
            settings.Current.Snoozed["winget:eslint"] = DateTimeOffset.UtcNow.AddSeconds(-1);

            var sources = MockUpdateSource.CreateAll();
            var history = new HistoryService(logging);
            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            viewModel.Updates.Add(new UpdateRowViewModel(Item("eslint", "eslint", "winget")));

            Assert.Contains(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => r.Name == "eslint");
            Assert.False(settings.Current.Snoozed.ContainsKey("winget:eslint"));
        });
    }

    /// <summary>
    /// The trickier half of expiry: a row whose version has NOT changed between two scans takes the
    /// "reuse the existing row" fast path in DrainPendingItems, which must still re-check an expired
    /// snooze, the row's own IsSnoozed flag would otherwise stay stuck at whatever the first scan set,
    /// even though the filter itself (the test above) already treats it as not-snoozed.
    /// </summary>
    [Fact]
    public void An_expired_snooze_clears_the_rows_own_flag_on_the_next_scan_even_when_unchanged()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var source = new FixedItemSource();
            var sources = new List<IUpdateSource> { source };
            var history = new HistoryService(logging);
            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);

            ((IRowActions)viewModel).Snooze(row, "day");
            Assert.True(row.IsSnoozed);

            // back-date it as if a day had already passed, then scan again, same item, unchanged version.
            // A real update row (not manual-only) snoozes by VERSION, matching Ignore's own key split,
            // "fixed:persistent-tool" alone (the package key) is a different dictionary entry entirely.
            settings.Current.Snoozed["fixed:persistent-tool@2.0"] = DateTimeOffset.UtcNow.AddSeconds(-1);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var again = Assert.Single(viewModel.Updates);
            Assert.False(again.IsSnoozed);
            Assert.Null(again.SnoozedUntil);
        });
    }

    /// <summary>Always the same one item, unchanged version, exercises DrainPendingItems' "reuse the
    /// existing row" fast path on a second scan.</summary>
    private sealed class FixedItemSource : IUpdateSource
    {
        public string Id => "fixed";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return Item("persistent-tool", "Persistent Tool", "fixed");
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>A source whose tool is not on this machine, ever. Used to prove
    /// <see cref="ProviderScanState.NotInstalled"/> reaches the pill.</summary>
    private sealed class AbsentToolSource : IUpdateSource
    {
        public string Id => "absent-tool";

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>A scan reports the absent source as NotInstalled; the main window then leaves its pill out.</summary>
    [Fact]
    public void A_source_with_no_tool_on_the_machine_is_reported_as_not_installed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create(sources: [new FixedItemSource(), new AbsentToolSource()]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var pill = Assert.Single(viewModel.ProviderStatuses, p => p.SourceId == "absent-tool");
            Assert.Equal(ProviderScanState.NotInstalled, pill.State);
        });
    }

    /// <summary>Same shape as <see cref="FixedItemSource"/>, except it claims it can pin an
    /// exact version, the one capability <see cref="MainViewModel"/>'s own rollback eligibility gates on.</summary>
    private sealed class PinningCapableSource : IUpdateSource
    {
        public string Id => "winget";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public bool SupportsVersionPinning => true;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return Item("eslint", "eslint", "winget");
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// This fix's visibility rule: a package whose source can pin a version (winget) and whose last
    /// successful install is on record offers "back to {version}" the moment its row appears from a scan,
    /// same level <see cref="An_expired_snooze_clears_the_rows_own_flag_on_the_next_scan_even_when_unchanged"/>
    /// applies its own persisted state at.
    /// </summary>
    [Fact]
    public void A_row_offers_a_downgrade_when_its_source_can_pin_a_version_and_history_has_a_success()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var sources = new List<IUpdateSource> { new PinningCapableSource() };
            var history = new HistoryService(logging);
            history.Append(new HistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
                SourceId = "winget", ItemId = "eslint", Name = "eslint",
                FromVersion = "1.0", ToVersion = "2.0", Success = true,
            });

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);

            Assert.Equal("1.0", row.DowngradeTargetVersion);
            Assert.True(row.ShowDowngradeButton);
            Assert.Contains("1.0", row.DowngradeHeader);
        });
    }

    /// <summary>
    /// The downgrade index used to come from a private, per-call rebuild that re-read and re-parsed the
    /// whole history file every time DrainPendingItems ran, once per flush tick during a long scan. It now
    /// reads <see cref="HistoryService.LastSuccessfulInstallByItem"/>'s own cache instead, which is built
    /// once and only dropped by a real <see cref="HistoryService.Append"/>. Proven here by corrupting the
    /// file directly (bypassing HistoryService, so its cache is never told to drop) between two ticks: a
    /// fresh per-tick file read would lose the target on the second tick, the shared cache does not.
    /// </summary>
    [Fact]
    public void Downgrade_index_survives_a_second_flush_tick_without_rereading_the_file()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var historyPath = Path.Combine(
                Directory.CreateTempSubdirectory("wu-history-test-").FullName, "history.jsonl");
            var sources = new List<IUpdateSource> { new PinningCapableSource() };
            var history = new HistoryService(logging, historyPath);
            history.Append(new HistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
                SourceId = "winget", ItemId = "pkg-a", Name = "Package A",
                FromVersion = "1.0", ToVersion = "2.0", Success = true,
            });

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            var onItemFound = typeof(MainViewModel).GetMethod("OnItemFound", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var drainPendingItems = typeof(MainViewModel).GetMethod("DrainPendingItems", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var itemA = new UpdateItem
            {
                Id = "pkg-a", SourceId = "winget", Category = UpdateCategory.Software, Name = "Package A",
                InstalledVersion = "1.5", AvailableVersion = "2.0",
            };

            // A scan builds the index before its first drain; this test drives the drain directly.
            history.EnsureRollbackIndexAsync().GetAwaiter().GetResult();

            onItemFound.Invoke(viewModel, [itemA]);
            var addedA = (List<UpdateRowViewModel>)drainPendingItems.Invoke(viewModel, [])!;
            var rowA = Assert.Single(addedA);
            Assert.Equal("1.0", rowA.DowngradeTargetVersion);

            // Corrupt the file directly, bypassing HistoryService: a caller re-reading the file fresh on
            // the next tick would find nothing left to match.
            File.WriteAllText(historyPath, string.Empty);

            // The exact same item, unchanged: DrainPendingItems takes the existing-row fast path, which
            // still re-applies the downgrade state using whatever index this second tick resolves.
            onItemFound.Invoke(viewModel, [itemA]);
            _ = (List<UpdateRowViewModel>)drainPendingItems.Invoke(viewModel, [])!;

            Assert.Equal("1.0", rowA.DowngradeTargetVersion);
        });
    }

    /// <summary>The same successful history entry buys nothing for a source that cannot pin a version
    /// (every source but winget, today): the row must never offer a rollback it cannot actually run.</summary>
    [Fact]
    public void A_row_offers_no_downgrade_when_its_source_cannot_pin_a_version()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(
                Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
            settings.Current.Ui.Language = "en";

            var source = new FixedItemSource();
            var sources = new List<IUpdateSource> { source };
            var history = new HistoryService(logging);
            history.Append(new HistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-1),
                SourceId = "fixed", ItemId = "persistent-tool", Name = "Persistent Tool",
                FromVersion = "1.0", ToVersion = "2.0", Success = true,
            });

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);

            Assert.Null(row.DowngradeTargetVersion);
            Assert.False(row.ShowDowngradeButton);
        });
    }

    [Fact]
    public void A_search_with_no_match_reports_that_a_filter_is_hiding_everything()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);
            viewModel.SearchQuery = "zzzzz";

            Assert.False(viewModel.HasResults);
            Assert.True(viewModel.IsFilterHidingEverything);
            Assert.Equal(LocalizationService.Instance["EmptyStateFiltered"], viewModel.EmptyStateText);
        });
    }

    /// <summary>Ignoring every single found update, with ignored rows hidden (the default), is a filter hiding
    /// everything too, the same situation as a search with no match, just reached a different way. The
    /// empty state must not claim nothing was found and suggest a rescan: a rescan would find the exact
    /// same, already-ignored updates again.</summary>
    [Fact]
    public void Ignoring_every_found_update_reports_that_a_filter_is_hiding_everything_too()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            foreach (var row in viewModel.Updates.ToList())
                ((IRowActions)viewModel).IgnoreAllVersions(row);

            Assert.False(viewModel.HasResults);
            Assert.True(viewModel.IsFilterHidingEverything);
            Assert.Equal(LocalizationService.Instance["EmptyStateFiltered"], viewModel.EmptyStateText);
        });
    }

    /// <summary>
    /// Regression: a row restored from the previous session's saved scan (LoadCachedScanIfFirstRun) was
    /// never wired into RefreshSelectionState, unlike a row a live scan drains in. Ticking such a row left
    /// it looking selected while CanInstallSelected/SelectionSummary never moved, so the toolbar's install
    /// button stayed disabled and a multi-row install was unreachable through the UI right after a restart.
    /// </summary>
    [Fact]
    public void A_row_restored_from_the_scan_cache_can_be_selected_for_install()
    {
        // Touches the harness before EffectivePath is read below: its static initializer is what installs
        // the redirected test path, and the Save right after this needs that already in place so it lands
        // where the MainViewModel built inside the OnUiThread block underneath will actually look.
        WpfHarness.OnUiThread(() => { });

        var cachePath = ScanResultCache.EffectivePath;
        new ScanResultCache(new LoggingService()).Save([Item("cached-pkg", "Cached Package", "cache-source")]);
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                // Gated so the scan itself never finishes; the cache load itself hops to a background
                // thread now, so pump until the cached row actually lands rather than assuming it is
                // there the instant ExecuteAsync returns.
                var source = new GatedSource();
                var viewModel = Create([source]);

                var scan = viewModel.ScanCommand.ExecuteAsync(null);
                Pump(WaitUntilAsync(() => viewModel.Updates.Any(r => r.Item.Id == "cached-pkg"), TimeSpan.FromSeconds(5)));

                var row = Assert.Single(viewModel.Updates, r => r.Item.Id == "cached-pkg");
                row.IsSelected = true;

                Assert.True(viewModel.CanInstallSelected);
                Assert.NotEqual(string.Empty, viewModel.SelectionSummary);

                source.Release();
                Pump(scan);
            });
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    /// <summary>
    /// Regression: the same hole as the scan-cache test above, but for the OTHER row-creating path that
    /// never went through the scan drain either, CreateTransientRow, built for the History window's
    /// downgrade button when the table has no live row for that package.
    /// </summary>
    [Fact]
    public void A_transient_row_created_for_a_history_downgrade_can_be_selected_for_install()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            // The confirm dialog is not involved: a declined downgrade removes its row again, so the
            // creation path is exercised directly.
            var row = viewModel.CreateTransientRow("winget", "transient-pkg", "Transient Package", "2.0");
            Assert.Contains(row, viewModel.Updates);
            row.IsSelected = true;

            Assert.True(viewModel.CanInstallSelected);
            Assert.NotEqual(string.Empty, viewModel.SelectionSummary);
        });
    }

    /// <summary>
    /// A downgrade started from the History window creates a row for a package the table does not show.
    /// Declining the confirmation must not leave that row behind as a ghost entry.
    /// </summary>
    [Fact]
    public void A_declined_history_downgrade_leaves_no_row_behind()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var before = viewModel.Updates.Count;

            // Dialogs.Confirm answers false headlessly (WpfHarness sets SuppressInteractiveDialogs).
            viewModel.DowngradeFromHistory("winget", "ghost-pkg", "Ghost Package", "2.0", "1.0");

            Assert.Equal(before, viewModel.Updates.Count);
            Assert.DoesNotContain(viewModel.Updates, r => r.Item.Id == "ghost-pkg");
        });
    }

    /// <summary>A row that was already in the table before the History button was pressed stays there
    /// when the downgrade is declined.</summary>
    [Fact]
    public void A_declined_history_downgrade_keeps_a_row_that_existed_before()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var existing = viewModel.CreateTransientRow("winget", "live-pkg", "Live Package", "2.0");

            viewModel.DowngradeFromHistory("winget", "live-pkg", "Live Package", "2.0", "1.0");

            Assert.Contains(existing, viewModel.Updates);
        });
    }

    /// <summary>
    /// Scanning while an install runs is allowed (requirement, 2026-07-27: "scan während update soll möglich
    /// sein! alles dann parallel!"). It was refused back when a scan CLEARED the table and orphaned the rows the
    /// queue was reporting onto; a scan reconciles now, and the reconcile leaves queued and running rows
    /// alone. Selecting more rows mid-batch is allowed for the same reason, they join the running batch.
    /// </summary>
    [Fact]
    public void A_scan_and_an_install_can_run_at_the_same_time()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            Assert.True(viewModel.ScanCommand.CanExecute(null));

            viewModel.IsInstalling = true;

            Assert.True(viewModel.ScanCommand.CanExecute(null));

            viewModel.Updates.First(r => r.CanInstall).IsSelected = true;
            Assert.True(viewModel.CanInstallSelected);
        });
    }

    /// <summary>The headline counts what is not ignored, so ignoring something has to move it.</summary>
    [Fact]
    public void Ignoring_a_row_refreshes_the_headline()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            Populate(viewModel);

            var raised = new List<string?>();
            viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            viewModel.IgnoreCommand.Execute(viewModel.Updates.First(u => u.Name == "eslint"));

            Assert.Contains(nameof(MainViewModel.HeadlineText), raised);
            Assert.Contains(nameof(MainViewModel.SubheadlineText), raised);
        });
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="task"/> is done. Everything has to stay on this thread:
    /// the table is a CollectionView and refuses to be changed from anywhere else.
    /// </summary>
    /// <summary>
    /// Drains the dispatcher's queue down to Background priority without going through a Task/
    /// SynchronizationContext round-trip, used where <see cref="Pump"/>'s own
    /// <c>TaskScheduler.FromCurrentSynchronizationContext()</c> continuation is not the thing being
    /// waited for (there is no task to pump on, just "let whatever is already queued run").
    /// </summary>
    private static void DrainDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Waits for <paramref name="task"/> while the dispatcher keeps running, then rethrows its failure. A
    /// plain GetResult on the UI thread deadlocks as soon as the method awaits work that has not finished
    /// yet, such as the enrichment cache load the view model starts in its constructor.
    /// </summary>
    private static void RunPumped(Task task)
    {
        Pump(task);
        task.GetAwaiter().GetResult();
    }

    private static T RunPumped<T>(Task<T> task)
    {
        Pump(task);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>A real, timed wait rather than a tight <see cref="DrainDispatcher"/> spin: blocker
    /// detection hops to the thread pool now, and a busy loop with no delay between pumps can run through
    /// every iteration before that pool thread ever gets scheduled, especially under load.</summary>
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
    /// Windows Update's COM search in miniature: it takes a while and it takes NO cancellation token, so a
    /// stopped scan leaves it running while the next one starts.
    /// </summary>
    private sealed class SlowWindowsUpdateSource : IUpdateSource
    {
        private readonly object _sync = new();
        private int _running;

        public int MaxConcurrentRuns { get; private set; }

        public string Id => "windowsupdate";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _running++;
                MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, _running);
            }

            try
            {
                // No token, on purpose.
                await Task.Delay(120, CancellationToken.None);
            }
            finally
            {
                lock (_sync)
                    _running--;
            }

            yield return Item("KB5041585", "Security update", "windowsupdate");
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static MainViewModel Create(
        IReadOnlyList<IUpdateSource>? sources = null,
        Func<string, CancellationToken, Task<string?>>? changelogFetcher = null,
        Func<string, CancellationToken, Task<Services.Sources.NpmChangelogLookup.NpmChangelogSource>>? npmChangelogLookup = null,
        Services.Install.InstallBlockers? installBlockers = null,
        Services.SelfUpdate.SelfUpdateService? selfUpdate = null,
        Services.WingetBootstrapService? wingetBootstrap = null,
        Func<bool>? canShowWingetOffer = null)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";

        var allSources = sources ?? MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(allSources, logging),
            new IconCacheService(logging), new InstallQueue(allSources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            selfUpdate ?? new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), allSources,
            changelogFetcher: changelogFetcher, npmChangelogLookup: npmChangelogLookup, wingetBootstrap: wingetBootstrap,
            installBlockers: installBlockers, canShowWingetOffer: canShowWingetOffer);
    }

    /// <summary>Answers the self-update check with a fixed releases payload, so a test can drive the real
    /// <see cref="SelfUpdateService"/> without reaching the network.</summary>
    private sealed class FakeSelfUpdateChannel(string releasesJson) : IUpdateChannel
    {
        public bool IsAvailable => true;

        public Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(releasesJson);

        public Task<bool> DownloadAsync(
            HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    /// <summary>A row whose only changelog signal is a GitHub release-page link, no body text anywhere.
    /// A distinct id per test keeps its EnrichmentCache key from colliding with any other test's.</summary>
    private static UpdateRowViewModel LinkOnlyChangelogRow(string id) => new(new UpdateItem
    {
        Id = id,
        SourceId = "github-release",
        Category = UpdateCategory.Software,
        Name = "Some Tool",
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        Meta = new Dictionary<string, string> { ["changelogUrl"] = $"https://github.com/{id}/releases/tag/v2.0" },
    });

    /// <summary>
    /// The click-triggered changelog fetch persists its result to the SAME on-disk enrichment-cache.json
    /// every other EnrichmentCache-backed test reads (the cache is meant to survive a restart), so a
    /// test that exercises it must remove its own entry afterward, or it silently changes what
    /// EnrichmentAndDiagnosticsTests' exact-count assertions see.
    /// </summary>
    private static void RemovePersistedEnrichmentCacheEntry(string key)
    {
        var path = Path.Combine(AppPaths.DataDirectory, "enrichment-cache.json");
        if (!File.Exists(path))
            return;

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root.Remove(key);
        File.WriteAllText(path, root.ToJsonString());
    }
}
