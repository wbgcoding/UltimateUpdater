using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The column chooser and remembered sort's own state round trip, for all three tables
/// (main, history, cleanup). Outside a running Application there is nowhere to write the App.xaml
/// GridLength resources that actually collapse a column on screen (WindowLayoutTests covers that visual
/// side); these tests are about the persisted state alone: hide/show, sort, and an unknown id being
/// dropped rather than kept around or thrown on.
/// </summary>
public class TableLayoutTests
{
    // English pinned: a view model built on "system" applies the OS language process-wide, which would leak
    // into whichever test runs next.
    private static SettingsService CreateSettings()
    {
        var settings = new SettingsService(
            new LoggingService(),
            Path.Combine(Directory.CreateTempSubdirectory("wu-table-layout-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        return settings;
    }

    // --- Main table ---------------------------------------------------------------------------------

    [Fact]
    public void Hiding_a_main_column_is_reflected_immediately()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        viewModel.Updates.Add(FullRow("Vendor.App"));

        Assert.False(viewModel.IsMainColumnHidden("date"));
        viewModel.SetMainColumnHidden("date", true);
        Assert.True(viewModel.IsMainColumnHidden("date"));

        viewModel.SetMainColumnHidden("date", false);
        Assert.False(viewModel.IsMainColumnHidden("date"));
    }

    [Fact]
    public void Main_column_visibility_and_sort_survive_a_restart()
    {
        var settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);

        viewModel.SetMainColumnHidden("size", true);
        viewModel.SortByCommand.Execute("version");
        viewModel.SaveMainTableLayout();

        var reopened = BuildMainViewModel(settings);
        reopened.Updates.Add(FullRow("Vendor.App"));

        Assert.True(reopened.IsMainColumnHidden("size"));
        Assert.False(reopened.IsMainColumnHidden("date"));
        Assert.Equal("version", reopened.SortColumn);
    }

    [Fact]
    public void An_unknown_saved_main_column_id_is_dropped_rather_than_kept()
    {
        var settings = CreateSettings();
        settings.Current.Ui.Tables["main"] = new TableLayoutState
        {
            HiddenColumns = ["date", "some-removed-column"],
        };

        var viewModel = BuildMainViewModel(settings);

        Assert.True(viewModel.IsMainColumnHidden("date"));
        Assert.False(viewModel.IsMainColumnHidden("some-removed-column"));
    }

    [Fact]
    public void The_name_column_can_never_be_hidden()
    {
        var descriptor = MainViewModel.MainTableColumns.Single(c => c.Id == "name");
        Assert.False(descriptor.CanHide);
    }

    [Fact]
    public void Resetting_main_columns_restores_the_default_hidden_set()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        viewModel.Updates.Add(FullRow("Vendor.App"));

        viewModel.SetMainColumnHidden("date", true);
        viewModel.SetMainColumnHidden("size", true);
        viewModel.SetMainColumnHidden("publisher", false);
        Assert.False(viewModel.IsMainColumnHidden("publisher"));
        viewModel.ResetMainColumns();

        Assert.False(viewModel.IsMainColumnHidden("date"));
        Assert.False(viewModel.IsMainColumnHidden("size"));
        // Publisher defaults BACK to hidden on reset, same as a fresh install.
        Assert.True(viewModel.IsMainColumnHidden("publisher"));
    }

    [Fact]
    public void There_is_no_more_source_column_and_publisher_starts_hidden_on_a_fresh_install()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        viewModel.Updates.Add(FullRow("Vendor.App"));

        Assert.DoesNotContain(MainViewModel.MainTableColumns, c => c.Id == "source");
        Assert.True(viewModel.IsMainColumnHidden("publisher"));
        Assert.False(viewModel.IsMainColumnHidden("date"));
    }

    [Fact]
    public void A_publisher_the_user_switched_on_survives_a_restart_and_a_saved_state_without_it_keeps_it_hidden()
    {
        var settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);
        viewModel.Updates.Add(FullRow("Vendor.App"));
        viewModel.SetMainColumnHidden("publisher", false);

        var reopened = BuildMainViewModel(settings);
        reopened.Updates.Add(FullRow("Vendor.App"));
        Assert.False(reopened.IsMainColumnHidden("publisher"));

        // A settings.json written before the default flipped says nothing about the publisher.
        var legacy = CreateSettings();
        legacy.Current.Ui.Tables["main"] = new TableLayoutState { HiddenColumns = ["size"] };
        var fromLegacy = BuildMainViewModel(legacy);
        fromLegacy.Updates.Add(FullRow("Vendor.App"));
        Assert.True(fromLegacy.IsMainColumnHidden("publisher"));
        Assert.True(fromLegacy.IsMainColumnHidden("size"));
    }

    [Fact]
    public void A_saved_main_column_state_naming_the_removed_source_column_is_dropped()
    {
        var settings = CreateSettings();
        settings.Current.Ui.Tables["main"] = new TableLayoutState
        {
            HiddenColumns = ["date", "source"],
        };

        var viewModel = BuildMainViewModel(settings);

        Assert.True(viewModel.IsMainColumnHidden("date"));
        Assert.False(viewModel.IsMainColumnHidden("source"));
    }

    private static UpdateRowViewModel FullRow(string id) => new(new UpdateItem
    {
        Id = id,
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = id,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        PublishDate = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
        DownloadSize = 4_000_000,
    });

    private static UpdateRowViewModel BareRow(string id) => new(new UpdateItem
    {
        Id = id,
        SourceId = "npm",
        Category = UpdateCategory.DeveloperTools,
        Name = id,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    });

    [Fact]
    public void A_column_no_row_has_a_value_for_is_hidden_and_unticked_in_the_chooser()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        viewModel.Updates.Add(BareRow("eslint"));

        Assert.True(viewModel.IsMainColumnHidden("date"));
        Assert.True(viewModel.IsMainColumnHidden("size"));
        Assert.True(viewModel.IsMainColumnHidden("publisher"));
        Assert.False(viewModel.IsMainColumnHidden("version"));
    }

    [Fact]
    public void One_row_with_a_value_shows_the_column_again()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        viewModel.Updates.Add(BareRow("eslint"));
        Assert.True(viewModel.IsMainColumnHidden("size"));

        var full = FullRow("Vendor.App");
        viewModel.Updates.Add(full);

        Assert.False(viewModel.IsMainColumnHidden("size"));
        Assert.False(viewModel.IsMainColumnHidden("date"));
        // The publisher is off by default whatever the rows carry.
        Assert.True(viewModel.IsMainColumnHidden("publisher"));

        viewModel.Updates.Remove(full);
        Assert.True(viewModel.IsMainColumnHidden("size"));
    }

    [Fact]
    public void A_size_that_arrives_from_enrichment_shows_the_column()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        var row = BareRow("eslint");
        viewModel.Updates.Add(row);
        // The hook that reacts to a row's property change is wired where a scan creates the row.
        var tracked = typeof(MainViewModel).GetMethod(
            "TrackSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        tracked.Invoke(viewModel, [row]);
        Assert.True(viewModel.IsMainColumnHidden("size"));

        row.ApplyEnrichment(null, 5_000_000);

        Assert.False(viewModel.IsMainColumnHidden("size"));
    }

    [Fact]
    public void A_value_only_an_ignored_row_has_does_not_keep_the_column_visible_until_ignored_rows_show()
    {
        var settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);
        var hidden = FullRow("Vendor.App");
        settings.Current.Excludes.Add($"{hidden.Item.SourceId}:{hidden.Item.Id}");

        viewModel.Updates.Add(BareRow("eslint"));
        viewModel.Updates.Add(hidden);
        Assert.True(viewModel.IsMainColumnHidden("size"));
        Assert.True(viewModel.IsMainColumnHidden("date"));

        // the toggle changes what is visible, so the columns must follow it
        viewModel.ShowIgnored = true;
        Assert.False(viewModel.IsMainColumnHidden("size"));
        Assert.False(viewModel.IsMainColumnHidden("date"));

        viewModel.ShowIgnored = false;
        Assert.True(viewModel.IsMainColumnHidden("size"));
        Assert.True(viewModel.IsMainColumnHidden("date"));
    }

    [Fact]
    public void A_value_only_a_merged_away_row_has_does_not_keep_the_column_visible()
    {
        var viewModel = BuildMainViewModel(CreateSettings());
        UpdateItem Dup(string source, long? size) => new()
        {
            Id = "graphify",
            SourceId = source,
            Category = UpdateCategory.DeveloperTools,
            Name = "graphify",
            InstalledVersion = "1.0",
            AvailableVersion = "2.0",
            DownloadSize = size,
        };

        // uv-tool outranks pip for the same package: the pip row (the one with the size) folds into the uv-tool row
        viewModel.Updates.Add(new UpdateRowViewModel(Dup("uv-tool", null)));
        viewModel.Updates.Add(new UpdateRowViewModel(Dup("pip", 5_000_000)));

        Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
        Assert.True(viewModel.IsMainColumnHidden("size"));
    }

    [Fact]
    public void A_pinned_column_stays_visible_while_empty_and_survives_a_restart()
    {
        var settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);
        viewModel.Updates.Add(BareRow("eslint"));
        Assert.True(viewModel.IsMainColumnHidden("size"));

        // Ticking the unticked column pins it.
        viewModel.SetMainColumnHidden("size", false);
        Assert.False(viewModel.IsMainColumnHidden("size"));
        Assert.Contains("size", settings.Current.Ui.Tables["main"].PinnedColumns);

        var reopened = BuildMainViewModel(settings);
        reopened.Updates.Add(BareRow("eslint"));
        Assert.False(reopened.IsMainColumnHidden("size"));
        Assert.True(reopened.IsMainColumnHidden("date"));

        // Unticking a pinned column unpins it and hides it as the user's own choice, even once a row has a size.
        reopened.SetMainColumnHidden("size", true);
        Assert.DoesNotContain("size", settings.Current.Ui.Tables["main"].PinnedColumns);
        reopened.Updates.Add(FullRow("Vendor.App"));
        Assert.True(reopened.IsMainColumnHidden("size"));
    }

    [Fact]
    public void Auto_hidden_columns_are_not_written_as_the_users_hidden_choice()
    {
        var settings = CreateSettings();
        var viewModel = BuildMainViewModel(settings);
        viewModel.Updates.Add(BareRow("eslint"));

        viewModel.SaveMainTableLayout();

        Assert.Empty(settings.Current.Ui.Tables["main"].HiddenColumns);
    }

    private static MainViewModel BuildMainViewModel(SettingsService settings)
    {
        var logging = new LoggingService();
        var sources = MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        return new MainViewModel(
            settings,
            logging,
            new ThemeService(),
            new ScanOrchestrator(sources, logging),
            new IconCacheService(logging),
            new InstallQueue(sources, settings, history, logging),
            history,
            new AutostartService(logging),
            new NotificationService(settings, logging),
            new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)),
            sources);
    }

    // --- History table -------------------------------------------------------------------------------

    [Fact]
    public void History_column_visibility_and_sort_survive_a_restart()
    {
        var settings = CreateSettings();
        var history = new HistoryService(new LoggingService());
        var viewModel = new HistoryViewModel(history, settings: settings);

        viewModel.SetColumnHidden("source", true);
        viewModel.SortByCommand.Execute("size");
        viewModel.SaveTableLayout();

        var reopened = new HistoryViewModel(history, settings: settings);

        Assert.True(reopened.IsColumnHidden("source"));
        Assert.False(reopened.IsColumnHidden("when"));
        Assert.Equal("size", reopened.SortColumn);
    }

    [Fact]
    public void An_unknown_saved_history_column_id_is_dropped()
    {
        var settings = CreateSettings();
        settings.Current.Ui.Tables["history"] = new TableLayoutState { HiddenColumns = ["result", "typo-id"] };

        var viewModel = new HistoryViewModel(new HistoryService(new LoggingService()), settings: settings);

        Assert.True(viewModel.IsColumnHidden("result"));
        Assert.False(viewModel.IsColumnHidden("typo-id"));
    }

    [Fact]
    public void The_history_name_column_can_never_be_hidden()
    {
        var descriptor = HistoryViewModel.HistoryTableColumns.Single(c => c.Id == "name");
        Assert.False(descriptor.CanHide);
    }

    // --- Cleanup detail list --------------------------------------------------------------------------

    [Fact]
    public void Cleanup_size_column_visibility_survives_a_restart()
    {
        var settings = CreateSettings();
        var logging = new LoggingService();
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings);

        Assert.False(viewModel.IsSizeColumnHidden);
        viewModel.SetDetailColumnHidden("size", true);
        Assert.True(viewModel.IsSizeColumnHidden);

        var reopened = new CleanupViewModel(new CleanupService(logging), logging, settings);
        Assert.True(reopened.IsSizeColumnHidden);

        reopened.ResetDetailColumns();
        Assert.False(reopened.IsSizeColumnHidden);

        var reopenedAgain = new CleanupViewModel(new CleanupService(logging), logging, settings);
        Assert.False(reopenedAgain.IsSizeColumnHidden);
    }
}
