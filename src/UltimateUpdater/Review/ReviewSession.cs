#if DEBUG
using System.IO;
using System.Net.Http;
using System.Windows;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Cleanup;
using UltimateUpdater.ViewModels.Settings;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Review;

/// <summary>
/// Everything a surface needs to build its window, wired to throwaway stores: settings live in a scratch
/// folder, sources are the mock set, the update channel is offline, installs and cleanups are fakes.
/// </summary>
internal sealed class ReviewSession : IDisposable
{
    /// <summary>A key under HKCU that only the review build ever reads, so the autostart switch never touches the real entry.</summary>
    private const string ScratchRunKey = @"Software\UltimateUpdaterReview\Run";

    private int _counter;

    public ReviewSession(string scratchRoot, string language, string theme)
    {
        Root = scratchRoot;
        Language = language;
        Theme = theme;
        Logging = new LoggingService();
        ThemeService = new ThemeService();

        // One history file per surface, so an install in one state never fills the history window of another.
        HistoryService.PathOverride = Path.Combine(scratchRoot, "history.jsonl");
    }

    public string Root { get; }

    public string Language { get; }

    public string Theme { get; }

    public LoggingService Logging { get; }

    public ThemeService ThemeService { get; }

    public LocalizationService Loc => LocalizationService.Instance;

    public SettingsService NewSettings()
    {
        var settings = new SettingsService(Logging, Path.Combine(Root, $"settings-{++_counter}.json"));
        settings.Current.Ui.Language = Language;
        settings.Current.Ui.Theme = Theme;
        settings.Current.Ui.WindowPlacement = null;
        settings.Current.Ui.CloseToTray = true;
        return settings;
    }

    public AutostartService NewAutostart() => new(Logging, ScratchRunKey);

    public HistoryService NewHistory() => new(Logging);

    public SelfUpdateService NewSelfUpdate() =>
        new(new HttpClient(), Logging, new OfflineUpdateChannel(), isInstalled: () => false);

    public MainViewModel NewMain(IReadOnlyList<IUpdateSource> sources, out SettingsService settings)
    {
        settings = NewSettings();
        var history = NewHistory();
        var orchestrator = new ScanOrchestrator(sources, Logging, isEnabled: null, isOnline: () => true);
        var queue = new InstallQueue(sources, settings, history, Logging, elevated: null, isMockSession: true);
        var viewModel = new MainViewModel(
            settings, Logging, ThemeService, orchestrator, new IconCacheService(Logging), queue, history,
            NewAutostart(), new NotificationService(settings, Logging), NewSelfUpdate(), sources,
            elevated: null, changelogFetcher: (_, _) => Task.FromResult<string?>(null),
            canShowWingetOffer: () => false);
        viewModel.CleanupPrescanAllowed = false;
        viewModel.UninstallPrefetchAllowed = false;
        return viewModel;
    }

    public SettingsViewModel NewSettingsViewModel(out SettingsService settings)
    {
        settings = NewSettings();
        var ids = MockUpdateSource.CreateAll().Select(s => s.Id).ToList();
        return new SettingsViewModel(settings, Logging, ThemeService, NewAutostart(), ids);
    }

    public CleanupViewModel NewCleanupViewModel(TimeSpan? cleanDelay = null, Action<CleanupHistoryService>? seedHistory = null)
    {
        var settings = NewSettings();
        var history = new CleanupHistoryService(Logging, Path.Combine(Root, "cleanup-history.json"));
        seedHistory?.Invoke(history);
        var viewModel = new CleanupViewModel(
            new ReviewCleanupService(Logging, cleanDelay ?? TimeSpan.Zero), Logging, settings, elevated: null, history: history)
        {
            CreateRestorePointOverride = _ => Task.FromResult(true),
        };
        ReviewPump.Wait(viewModel.CatalogReadyTask);
        return viewModel;
    }

    public UninstallerViewModel NewUninstallerViewModel(IReadOnlyList<UninstallListing> listings, bool expandAll)
    {
        var viewModel = new UninstallerViewModel(new ReviewUninstallerBackend(listings), Logging);
        ReviewPump.Wait(viewModel.LoadAsync());
        if (expandAll)
        {
            foreach (var section in viewModel.Sections)
                section.IsExpanded = true;
        }

        return viewModel;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // scratch only; a locked file must not fail the run
        }
    }
}

/// <summary>Waits for a task while the dispatcher keeps pumping, so a view model that marshals back to the UI thread cannot deadlock the wait.</summary>
internal static class ReviewPump
{
    public static void Wait(Task task, int timeoutSeconds = 60)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(timeoutSeconds) };
        guard.Tick += (_, _) => frame.Continue = false;
        guard.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        guard.Stop();
        if (!task.IsCompleted)
            throw new TimeoutException($"A review step did not finish within {timeoutSeconds} seconds.");

        task.GetAwaiter().GetResult();
    }

    /// <summary>Lets queued layout, binding and render work run, plus a real-time wait for animations to reach their end state.</summary>
    public static async Task SettleAsync(int milliseconds = 600)
    {
        var dispatcher = Application.Current.Dispatcher;
        for (var pass = 0; pass < 3; pass++)
        {
            await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(milliseconds / 3);
        }

        await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
}
#endif
