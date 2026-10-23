using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The main window's quiet banner offering a cleanup once the last one (or, failing that, the
/// app's own setup) is far enough in the past. <see cref="MainViewModel.ShouldShowCleanupReminder"/> is
/// pure (no disk, no clock) so the threshold decision is tested directly; the dismiss fork (x asks once,
/// then "only this time" or "never again") needs a real view model, same minimal construction
/// <see cref="RebootDialogTests"/> already uses for a banner-adjacent feature.
/// </summary>
[Collection("wpf")]
public class CleanupReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static AppSettings Settings(int days = 90, bool off = false) =>
        new() { CleanupReminderDays = days, CleanupReminderOff = off };

    private static CleanupHistoryEntry EntryAt(DateTimeOffset timestamp) => new()
    {
        Timestamp = timestamp,
        FreedBytes = 0,
        SkippedFiles = 0,
        SkippedBytes = 0,
        Stopped = false,
    };

    [Fact]
    public void A_recent_cleanup_keeps_the_banner_hidden()
    {
        var history = new[] { EntryAt(Now.AddDays(-10)) };

        Assert.False(MainViewModel.ShouldShowCleanupReminder(history, Now, Now.AddDays(-500), Settings(days: 90)));
    }

    [Fact]
    public void A_cleanup_older_than_the_threshold_shows_the_banner()
    {
        var history = new[] { EntryAt(Now.AddDays(-91)) };

        Assert.True(MainViewModel.ShouldShowCleanupReminder(history, Now, Now.AddDays(-500), Settings(days: 90)));
    }

    /// <summary>The boundary itself counts as due: "90 days ago, to the day" must not read as still fresh.</summary>
    [Fact]
    public void Exactly_the_threshold_counts_as_overdue()
    {
        var history = new[] { EntryAt(Now.AddDays(-90)) };

        Assert.True(MainViewModel.ShouldShowCleanupReminder(history, Now, Now.AddDays(-500), Settings(days: 90)));
    }

    [Fact]
    public void No_history_falls_back_to_a_recent_install_date_and_stays_hidden()
    {
        Assert.False(MainViewModel.ShouldShowCleanupReminder([], Now, Now.AddDays(-10), Settings(days: 90)));
    }

    [Fact]
    public void No_history_and_a_long_ago_install_date_shows_the_banner()
    {
        Assert.True(MainViewModel.ShouldShowCleanupReminder([], Now, Now.AddDays(-200), Settings(days: 90)));
    }

    /// <summary>Only the newest run matters: an old run must never be the one compared against once a
    /// recent one exists.</summary>
    [Fact]
    public void Several_runs_compare_against_the_newest_one_only()
    {
        var history = new[] { EntryAt(Now.AddDays(-400)), EntryAt(Now.AddDays(-5)) };

        Assert.False(MainViewModel.ShouldShowCleanupReminder(history, Now, Now.AddDays(-500), Settings(days: 90)));
    }

    [Fact]
    public void The_reminder_off_flag_hides_the_banner_no_matter_how_overdue()
    {
        var history = new[] { EntryAt(Now.AddDays(-9000)) };

        Assert.False(MainViewModel.ShouldShowCleanupReminder(history, Now, Now.AddDays(-9000), Settings(days: 90, off: true)));
    }

    private static (MainViewModel ViewModel, SettingsService Settings) CreateViewModel()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-cleanup-reminder-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";

        var sources = MockUpdateSource.CreateAll();
        var history = new HistoryService(logging);

        var viewModel = new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new Services.SelfUpdate.SelfUpdateService(
                new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
            sources);

        return (viewModel, settings);
    }

    [Fact]
    public void The_x_asks_once_and_a_second_click_asks_nothing_further()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _) = CreateViewModel();
            viewModel.CleanupReminderVisible = true;

            viewModel.DismissCleanupReminderCommand.Execute(null);
            Assert.True(viewModel.CleanupReminderAsking);
            Assert.False(viewModel.CleanupReminderOfferVisible);

            // A second click while already asking must not do anything new (there is no way to reach a
            // third state, and the fork must never be offered twice for the same showing).
            viewModel.DismissCleanupReminderCommand.Execute(null);
            Assert.True(viewModel.CleanupReminderAsking);
            Assert.True(viewModel.CleanupReminderVisible);
        });
    }

    [Fact]
    public void Only_this_time_hides_the_banner_without_turning_it_off()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, settings) = CreateViewModel();
            viewModel.CleanupReminderVisible = true;

            viewModel.DismissCleanupReminderCommand.Execute(null);
            viewModel.DismissCleanupReminderOnceCommand.Execute(null);

            Assert.False(viewModel.CleanupReminderVisible);
            Assert.False(viewModel.CleanupReminderAsking);
            Assert.False(settings.Current.CleanupReminderOff);
        });
    }

    [Fact]
    public void Never_again_hides_the_banner_and_sets_the_off_flag()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, settings) = CreateViewModel();
            viewModel.CleanupReminderVisible = true;

            viewModel.DismissCleanupReminderCommand.Execute(null);
            viewModel.DismissCleanupReminderForeverCommand.Execute(null);

            Assert.False(viewModel.CleanupReminderVisible);
            Assert.False(viewModel.CleanupReminderAsking);
            Assert.True(settings.Current.CleanupReminderOff);
        });
    }
}
