using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater;

public partial class App : Application
{
    private const string TrayArgument = "--tray";

    /// <summary>
    /// Test-only sentinel a worker-mode launch can carry as its 4th argument: the worker then reports
    /// whether <see cref="AppPaths"/> was touched, through <see cref="AppPathsTouchTracker"/>.
    /// </summary>
    internal const string ReportAppPathsTouchArgument = "--report-apppaths-touch";

    private LoggingService? _logging;
    private SettingsService? _settings;
    private TrayIconService? _tray;
    private ScanScheduler? _scheduler;
    private ElevatedHost? _elevated;
    private BusySignal? _busySignal;
    private ExitRequestListener? _exitRequests;
    private DispatcherTimer? _busyTimer;
    private System.Windows.Interop.HwndSource? _installerListener;
    private bool _isExiting;
    private bool _isWorker;

    /// <summary>A focus request that arrived before there was a window to show.</summary>
    private bool _focusRequestedEarly;

    internal bool IsExiting => _isExiting;

    /// <summary>Set once the UI is up, so a real exit can ask before it ends installs or a cleanup run.</summary>
    private MainViewModel? _mainViewModel;

    /// <summary>Known before OnStartup, because the constructor already has to act on it.</summary>
    private static bool RunningAsWorker => ElevationService.IsElevatedWorkerProcess;

    public App()
    {
        // The elevated worker writes nothing to disk: %TEMP% belongs to the unelevated user, who can
        // point it somewhere only administrators may write. It reports through the pipe instead.
        if (RunningAsWorker)
            return;

        // Installed first, before WPF loads its resources and before the real log exists: a crash this early writes one line to a place that always exists.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteLastResortCrashNote(args.ExceptionObject as Exception);

            // The CLR ends the process right after this handler returns, so flush what the background log writer still holds.
            _logging?.Flush();
        };
    }

    /// <summary>
    /// Makes the window buttons (minimise / maximise / close) light up under the mouse. WPF-UI drives that
    /// highlight only from a win32 message hook that may never be installed here. Done in code because a XAML
    /// style deriving from the implicit TitleBarButton style is a self-reference that strips the template.
    /// The trigger sets the button's own MouseOverBackground, so the close button stays red.
    /// </summary>
    private void AddTitleBarHover()
    {
        try
        {
            var key = typeof(Wpf.Ui.Controls.TitleBarButton);
            if (Resources[key] is not Style existing)
                return;

            var style = new Style(key, existing);
            var trigger = new System.Windows.Trigger
            {
                Property = System.Windows.Controls.Control.IsMouseOverProperty,
                Value = true,
            };

            trigger.Setters.Add(new Setter(
                System.Windows.Controls.Control.BackgroundProperty,
                new System.Windows.Data.Binding(nameof(Wpf.Ui.Controls.Button.MouseOverBackground))
                {
                    RelativeSource = System.Windows.Data.RelativeSource.Self,
                }));

            style.Triggers.Add(trigger);
            style.Seal();
            Resources[key] = style;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A cosmetic hover state is never worth failing startup over.
            _logging?.Warn($"Could not add the title-bar hover state: {ex.Message}");
        }
    }

    /// <summary>Best effort by definition: it runs when everything else has already failed.</summary>
    private static void WriteLastResortCrashNote(Exception? exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "UltimateUpdater-crash.log");

            // Same redaction as the ordinary log; the file restarts above 256 KB so it cannot grow unbounded.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.Delete(path);

            var line = LoggingService.Redact($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}{Environment.NewLine}");
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch
        {
            // nothing left to try
        }
    }

    /// <summary>
    /// True when autostart launched this copy into the tray. Closing the window then keeps it there
    /// regardless of the close-to-tray setting: the whole point of autostart is the interval scanner,
    /// and it would otherwise stop the first time the user looked at the window and closed it again.
    /// </summary>
    internal bool StartedInTray { get; private set; }

    internal const string SetAutostartArgument = "--set-autostart";
    internal const string ClearAutostartArgument = "--clear-autostart";

    /// <summary>
    /// Handles the installer's autostart request and reports whether this process was started only for
    /// that. Returns false for an ordinary start, which then continues as usual.
    /// </summary>
    private bool ApplyAutostartRequest(string[] args, LoggingService logging)
    {
        var enable = args.Contains(SetAutostartArgument, StringComparer.OrdinalIgnoreCase);
        var disable = args.Contains(ClearAutostartArgument, StringComparer.OrdinalIgnoreCase);

        if (!enable && !disable)
            return false;

        var applied = new AutostartService(logging).TrySet(enable);
        logging.Info($"Autostart {(enable ? "enabled" : "removed")} on request from Setup: {(applied ? "done" : "refused by the registry")}.");

        if (applied)
        {
            // The settings window reads the stored preference, not the registry, so leaving it behind
            // would show the switch off while the app really does start with Windows.
            // Save() throws once its retries run out (a scanner holding the file); the registry change
            // already happened, so that must not turn into a fatal dialog and a failed exit for Setup.
            var settings = new SettingsService(logging);
            settings.Current.Autostart = enable;
            try
            {
                settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Warn($"Autostart applied, but settings.json could not be updated: {ex.Message}");
            }
        }

        Shutdown(applied ? 0 : 1);
        return true;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WpfHarness builds a real Application but never calls Run(); WPF still fires OnStartup, so real startup is skipped for tests.
        if (SuppressRealStartupForTests)
            return;

        // Decided before anything else: the worker must stay headless even when it crashes, otherwise
        // a dialog nobody can see would block its shutdown, and it must not open a log file, because
        // every directory it could log into belongs to the unelevated user.
        _isWorker = ElevationService.IsWorkerInvocation(e.Args);

        var logging = _isWorker ? new LoggingService(ElevatedWorker.CollectLog) : new LoggingService();
        _logging = logging;
        ProcessRunner.Diagnostics = logging.Warn;

        if (NativeLibraryHardening.DefaultDirectoriesError != 0)
            logging.Warn($"Could not restrict the native library search path (error {NativeLibraryHardening.DefaultDirectoriesError}).");

        // Wired once so every drive's WMI read reaches the real log.
        Services.Cleanup.DriveMediaTypeReader.Log = logging.Info;

        InstallCrashHandlers(logging);

        if (!_isWorker)
            AddTitleBarHover();

        try
        {
            // Elevated worker mode: no UI, no single-instance guard (it is a child of the running app).
            if (_isWorker)
            {
                try
                {
                    // Fail closed: without the id of the process that serves the pipe there is no way to
                    // tell the app apart from anyone else who may be serving that name by then.
                    if (e.Args.Length < 3 || !int.TryParse(e.Args[2], out var serverProcessId))
                    {
                        logging.Error("Worker started without a pipe name and a server process id.");
                        Shutdown(6);
                        return;
                    }

                    logging.Info("Starting elevated worker.");
                    // The touch report is a test probe that never has a server to reach: a short connect
                    // timeout keeps that test from waiting out the full production timeout.
                    var connectTimeoutMs = e.Args.Contains(ReportAppPathsTouchArgument) ? 1_000 : 30_000;
                    Shutdown(await ElevatedWorker.RunAsync(e.Args[1], serverProcessId, logging, connectTimeoutMs));
                }
                finally
                {
                    if (e.Args.Contains(ReportAppPathsTouchArgument))
                        Console.WriteLine($"AppPathsTouched={AppPathsTouchTracker.WasTouched}");
                }

                return;
            }

            // Setup asks for the autostart entry instead of writing it: it may run as a different administrator whose HKCU is not the user's. Started with runasoriginaluser, this lands in the right hive.
            if (ApplyAutostartRequest(e.Args, logging))
                return;

            // As early as possible in the normal process, before any user action could start an elevated
            // job; see ElevationService.LockSelfImage.
            ElevationService.LockSelfImage(logging);

            // A portable self-update starts this copy while the old one is still shutting down: waiting for it
            // first is what keeps the single-instance lock and the old image free by the time they are needed.
            if (!Services.SelfUpdate.AfterUpdateHandoff.AcquireAfterWaiting(
                    e.Args,
                    Services.SelfUpdate.AfterUpdateHandoff.WaitForProcessExit,
                    Services.SelfUpdate.SelfUpdateService.CleanupPreviousUpdate,
                    () => SingleInstanceService.TryAcquire(logging)))
            {
                // Hand over to the running instance. If it cannot be reached, say so: an exit with no window reads as a failure to start.
                if (await SingleInstanceService.NotifyExistingInstanceAsync())
                {
                    logging.Info("Another instance is already running; asked it to show itself.");
                }
                else
                {
                    logging.Warn("Another instance holds the lock but did not respond to the focus request.");
                    Dialogs.Info(LocalizationService.Instance["AlreadyRunning"]);
                }

                Shutdown();
                return;
            }

            logging.Info("Application starting.");

            // Started before the UI is built so a second launch in that window is handed over instead of reported as "already running".
            SingleInstanceService.StartListening(RequestFocus);

            StartUi(e, logging);
            logging.Info("Startup complete.");
        }
        catch (Exception ex)
        {
            logging.Error("Startup failed", ex);
            ReportFatal(ex, logging);
            Shutdown(1);
        }
    }

    private void StartUi(StartupEventArgs e, LoggingService logging)
    {
        var settings = _settings = new SettingsService(logging);

        // Retention sweeps for logs and history: housekeeping, fire-and-forget and off the UI thread so first paint is not delayed.
        _ = Background.Run(
            Task.Run(() => logging.CleanupOldLogs(settings.Current.Logs.RetentionDays)), logging, "log retention sweep");

        // Runs on every start until a slider is moved by hand; recomputing the same numbers is harmless.
        if (!settings.Current.ParallelismChosenByUser)
            TuneParallelismForThisMachine(settings, logging);

        logging.Event(
            ActivityLog.App,
            $"start {Services.SelfUpdate.SelfUpdateService.CurrentVersion} "
            + $"{(AppPaths.IsInstalled ? "installed" : "portable")}{(e.Args.Contains(TrayArgument) ? " tray" : string.Empty)}");

        // The sources are built by SourceRegistry, which has no settings, so the download virus scan
        // reads a static gate. Set it before anything can download.
        Services.Security.MalwareScanner.ScanDownloads = settings.Current.Security.ScanDownloads;

        var themeService = new ThemeService();
        themeService.SetUseSystemAccent(settings.Current.Ui.UseSystemAccent);
        var iconCache = new IconCacheService(logging);
        var history = new HistoryService(logging);

        // Fire-and-forget off the UI thread; HistoryService's own lock serialises it against a concurrent Append.
        _ = Background.Run(
            Task.Run(() => history.CleanupOldEntries(settings.Current.HistoryRetentionDays)), logging, "history retention sweep");
        _ = Background.Run(Task.Run(() => AppDataCleaner.DeleteStaleTempSiblings(AppPaths.DataDirectory, TimeSpan.FromDays(1))), logging, "stale temp sweep");

        // Removed leftovers stay restorable for 14 days; the user's quarantine folders past that go now (the
        // machine ones go with the next elevated removal, which needs no extra prompt).
        _ = Background.Run(
            Task.Run(() => Services.Uninstall.QuarantineStore.Prune(
                Services.Uninstall.QuarantineStore.UserRoot(), DateTime.Now, logging)),
            logging, "leftover quarantine sweep");

        // An installed update leaves its downloaded setup in the temp folder (it runs from there after this
        // process is gone); a day later nothing can still be using it.
        _ = Background.Run(
            Task.Run(() => Services.SelfUpdate.SelfUpdateService.SweepStaleDownloads(
                Path.GetTempPath(), TimeSpan.FromDays(1), logging)),
            logging, "stale update download sweep");

        // --mock-sources: fake providers for offline UI and VM testing. isMockSession rides the same switch so InstallQueue never elevates for a mock row (the elevated worker builds the real provider set).
        var isMockSession = e.Args.Contains("--mock-sources");
#if DEBUG
        // --crash-test: a Debug build only; raises an unhandled exception a moment after start so the crash dump and the crash window can be driven end to end.
        if (e.Args.Contains("--crash-test"))
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => throw new InvalidOperationException("Forced crash for testing.")));
#endif
        var sources = isMockSession
            ? MockUpdateSource.CreateAll(showcase: e.Args.Contains("--showcase"))
            : SourceRegistry.CreateAll(logging);
        var scanOrchestrator = new ScanOrchestrator(
            sources, logging, id => settings.Current.Sources.GetValueOrDefault(id, true),
            maxParallelSources: () => settings.Current.Scan.MaxParallelSources);
        // One administrator prompt for the whole session: the first admin job starts the helper, everything
        // after it (installs, removals, cleanup) goes to that same process.
        _elevated = new ElevatedHost(logging);
        var installQueue = new InstallQueue(sources, settings, history, logging, _elevated, isMockSession);

        // Removes the .old image a previous portable self-update left behind, for a start that did not come
        // through the update hand-over (which cleans it up itself, before the single-instance lock).
        Services.SelfUpdate.SelfUpdateService.CleanupPreviousUpdate();

        var autostart = new AutostartService(logging);
        var notifications = new NotificationService(settings, logging);

        Services.SelfUpdate.IUpdateChannel updateChannel =
            new Services.SelfUpdate.PublicUpdateChannel(logging);
        // No client-side timeout: HttpClient's 100 second default also covers reading the body and the artifacts are over 100 MB. The download enforces its own size cap and honours the cancellation token.
        var updateTrafficHandler = new Services.Security.TrafficLoggingHandler { Logging = logging };
        var updateHttpClient = new System.Net.Http.HttpClient(updateTrafficHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var selfUpdate = new Services.SelfUpdate.SelfUpdateService(updateHttpClient, logging, updateChannel);
        var wingetBootstrap = new WingetBootstrapService(updateHttpClient, logging);

        var mainViewModel = new MainViewModel(
            settings, logging, themeService, scanOrchestrator, iconCache, installQueue, history, autostart,
            notifications, selfUpdate, sources, _elevated, wingetBootstrap: wingetBootstrap);
        _mainViewModel = mainViewModel;
        mainViewModel.CleanupPrescanAllowed = !IsUnattended;
        var mainWindow = new MainWindow(mainViewModel, themeService, settings, logging);

        MainWindow = mainWindow;

        _tray = new TrayIconService(logging);
        _tray.OpenRequested += () => Dispatcher.Invoke(mainWindow.BringToFront);
        _tray.ScanRequested += () => Dispatcher.Invoke(() => mainViewModel.ScanCommand.Execute(null));
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApplication);
        _tray.Show();

        notifications.NotificationRequested += (title, message) => _tray.ShowBalloon(title, message);
        mainViewModel.LanguageChanged += () => _tray.RefreshLocalization();


        _scheduler = new ScanScheduler(
            settings, logging, () => mainViewModel.RunScheduledScanAsync(), isOnline: NetworkAvailability.IsOnline);
        _scheduler.Start();
        mainViewModel.ScanCompleted += () => _scheduler.NotifyScanCompleted();

        StartInstallerSignals(mainWindow, mainViewModel);

        // --tray: started by autostart, so come up in the background without stealing focus
        StartedInTray = e.Args.Contains(TrayArgument);

        // So the installer can put this copy back after replacing it: started from
        // the tray, it comes back to the tray rather than throwing a window at the user.
        RestartRegistration.Register(StartedInTray ? TrayArgument : null, logging);

        if (!StartedInTray)
        {
            // Process start to the first painted frame; ContentRendered can fire again, so the handler detaches after the first hit.
            var processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
            EventHandler? onFirstPaint = null;
            onFirstPaint = (_, _) =>
            {
                mainWindow.ContentRendered -= onFirstPaint;
                logging.Event(ActivityLog.App, ActivityLog.FirstPaint(processStart, DateTime.Now));
            };
            mainWindow.ContentRendered += onFirstPaint;

            mainWindow.Show();
        }

        // a handover that arrived while the window was still being built
        if (_focusRequestedEarly)
        {
            _focusRequestedEarly = false;
            mainWindow.BringToFront();
        }

        // An autostart (tray) launch has nobody looking at the window, so its scan goes the scheduled way and announces what it found.
        if (settings.Current.Scan.OnStart)
        {
            _ = StartedInTray
                ? Background.Run(mainViewModel.RunScheduledScanAsync(), logging, "tray start scan")
                : mainViewModel.ScanCommand.ExecuteAsync(null);
        }

        // A local file read only, so this runs synchronously and unconditionally, including
        // unattended launches, unlike the network check right below.
        mainViewModel.EvaluateCleanupReminder();

        // On a background thread so the release lookup and its first synchronous steps never hold up the
        // dispatcher during startup. WPF marshals the resulting property changes itself.
        _ = Task.Run(() => mainViewModel.CheckForSelfUpdateAsync(CancellationToken.None));
    }

    /// <summary>
    /// True when nobody is there to click anything: a build, a test run or a scripted launch (`--unattended`,
    /// or WU_UNATTENDED set by build.bat), or when headless dialog suppression is on. Computed live so callers
    /// long after startup can ask too.
    /// </summary>
    public static bool IsUnattended =>
        !ForceAttendedForTest &&
        (Environment.GetCommandLineArgs().Contains("--unattended", StringComparer.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("WU_UNATTENDED") is { Length: > 0 }
        || Dialogs.SuppressInteractiveDialogs);

    /// <summary>
    /// Test-only: <c>WpfHarness</c> makes <see cref="IsUnattended"/> true for the whole test run; this lets one
    /// test exercise a path such as the interactive-retry gate without weakening the gate itself.
    /// </summary>
    internal static bool ForceAttendedForTest { get; set; }

    /// <summary>
    /// Test-only, set by <c>WpfHarness</c> before its Application is constructed: skips the real startup
    /// pipeline (sources, scan, MainViewModel) that OnStartup would otherwise run on the harness Dispatcher.
    /// </summary>
    internal static bool SuppressRealStartupForTests { get; set; }

    /// <summary>Raises the window, or remembers to once there is one.</summary>
    private void RequestFocus() => Dispatcher.Invoke(() =>
    {
        if (MainWindow is MainWindow window)
            window.BringToFront();
        else
            _focusRequestedEarly = true;
    });

    private void InstallCrashHandlers(LoggingService logging)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            logging.Error("Unhandled UI exception", args.Exception);

            // ReportFatal shows a modal dialog and can sit there for a while; the line above must not be
            // waiting behind that, in memory only, for however long the user takes to close it.
            logging.Flush();

            ReportFatal(args.Exception, logging);
            args.Handled = true;
            _isExiting = true; // the main window must not ask "quit anyway?" behind the fatal-error box
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            logging.Error("Unhandled exception", args.ExceptionObject as Exception);

            // This event has no Shutdown()/OnExit after it, the CLR ends the process the moment every
            // handler here returns, so this line is the only chance it gets to reach disk.
            logging.Flush();

            // Nor does OnExit run to remove the tray icon, which otherwise lingers until hovered over.
            try
            {
                _tray?.Dispose();
            }
            catch
            {
                // the process is ending regardless
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // background work failing must be recorded, but it must not take the app down
            logging.Error("Unobserved background exception", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Set while a fatal error is being reported, so one failure cannot become a stack of dialogs.</summary>
    private static bool _reportingFatal;

    /// <summary>Generic message to the user, detail to the log (SECURITY.md).</summary>
    private void ReportFatal(Exception exception, LoggingService logging)
    {
        // the elevated worker has no desktop of its own to show anything on
        if (_isWorker)
            return;

        // A modal dialog runs a nested message pump, so the app's own timers keep ticking behind it. If the
        // same fault recurs on one of those ticks the handler fires again and stacks another modal: an
        // unclosable pile of error windows instead of an exit.
        if (_reportingFatal)
            return;

        _reportingFatal = true;

        try
        {
            var logFolder = AppPaths.LogsDirectory;
            var message = string.Format(LocalizationService.Instance["FatalError"], logFolder);

            // A small dump beside the logs, written before the dialog: the dialog waits for the user. The render
            // harness never writes one.
            if (!Dialogs.SuppressInteractiveDialogs && CrashDumpWriter.TryWrite(logFolder) is { } dump)
                logging.Warn($"Crash dump written to '{dump}'.");

            // The full exception goes into the copyable Details panel, never into a shipped log, but the
            // user in front of the crash can read and copy it (SECURITY.md keeps it local, on request).
            Dialogs.ErrorWithAction(
                LocalizationService.Instance["FatalErrorHeadline"],
                message,
                exception.ToString(),
                LocalizationService.Instance["FatalErrorOpenLogFolder"],
                () => UriLauncher.OpenFolderDetached(logFolder, "log folder", logging));
        }
        catch (Exception ex)
        {
            // Last resort: this is already the crash-reporting path, so a failure here has nowhere else to
            // go but the log; throwing again would compound the original crash with a second one.
            logging.Error("Could not display the error dialog", ex);
        }
    }

    /// <summary>Windows logging off or shutting down: the main window's Closing must not ask "quit anyway?"
    /// in the middle of it (the answer cannot be honoured; it only holds the shutdown until Windows kills
    /// the app).</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // WPF's own hidden window also receives an installer's close request (it arrives as a session-end
        // query) and shuts the app down unless this cancels it. A busy app refuses that request like the
        // installer listener does; only a real logoff or shutdown is let through.
        if (ShouldRefuseSessionEnd(_mainViewModel?.WorkIsRunning == true, SessionReallyEnding()))
        {
            e.Cancel = true;
            LogRefusedCloseRequest();
            return;
        }

        _isExiting = true;
        base.OnSessionEnding(e);
    }

    internal static bool ShouldRefuseSessionEnd(bool workRunning, bool sessionReallyEnding) =>
        workRunning && !sessionReallyEnding;

    private const int SmShuttingDown = 0x2000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Set by Windows only while the session itself is ending; a Restart Manager close request
    /// from an installer leaves it clear.</summary>
    private static bool SessionReallyEnding() => GetSystemMetrics(SmShuttingDown) != 0;

    /// <summary>Two things the installer relies on: it can see whether work is running (a mutex that exists
    /// exactly then), and it can ask the app to close even when the app only hides on "close". The close
    /// request is listened for on the main window and on a hidden window of its own, because an autostart
    /// into the tray never gives the main window a handle.</summary>
    private void StartInstallerSignals(MainWindow mainWindow, MainViewModel mainViewModel)
    {
        _busySignal = new BusySignal(logging: _logging);
        _busyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _busyTimer.Tick += (_, _) => _busySignal.Set(mainViewModel.WorkIsRunning);
        _busyTimer.Start();

        // The timer alone can show the signal half a second late, so the two flags every install and cleanup run raises first are followed at once.
        mainViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsInstalling) or nameof(MainViewModel.IsCleanupRunning))
                _busySignal?.Set(mainViewModel.WorkIsRunning);
        };

        // An elevated copy cannot be closed by the unelevated Setup, so it is asked through an event instead. The
        // request arrives on a pool thread and is judged on the UI thread, where the running state is read.
        _exitRequests = ExitRequestListener.Start(
            () => _ = Dispatcher.InvokeAsync(() => HandleInstallerExitRequest(mainViewModel)), _logging);

        Func<bool> isBusy = () => mainViewModel.WorkIsRunning;
        _installerListener = InstallerCloseRequest.CreateListener(Dispatcher, ExitWithoutPrompt, isBusy, LogRefusedCloseRequest);
        mainWindow.SourceInitialized += (_, _) =>
        {
            if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle) is { } source)
                InstallerCloseRequest.Attach(source, Dispatcher, ExitWithoutPrompt, isBusy, LogRefusedCloseRequest);
        };
    }

    /// <summary>The installer asked while a scan, install or cleanup is running: the app stays up and the
    /// installer (or the person behind it) decides what to do next.</summary>
    private void LogRefusedCloseRequest() =>
        _logging?.Info("The installer asked the app to close, but work is running: refusing");

    /// <summary>Same rule as the close request above: a busy app refuses. Nothing that works with the system
    /// (an install batch, a cleanup, a drive optimisation) is ever cut off by a signal any process of this user
    /// may send, and no question is put to a person who may not be there.</summary>
    private void HandleInstallerExitRequest(MainViewModel mainViewModel)
    {
        if (_isExiting)
            return;

        if (mainViewModel.WorkIsRunning)
        {
            LogRefusedCloseRequest();
            return;
        }

        ExitWithoutPrompt();
    }

    /// <summary>The installer asked an idle app to close because it needs the files: no question is put to a
    /// person who may not be there at all. A busy app never gets here, it refuses the request.</summary>
    private void ExitWithoutPrompt()
    {
        _logging?.Info("The installer asked the app to close: exiting");
        _isExiting = true;
        Shutdown();
    }

    /// <summary>Real exit, as opposed to close-to-tray.</summary>
    internal void ExitApplication()
    {
        // Quitting kills a running drive optimisation (no scheduled task carries on), unlike a plain cleanup run. Both quit paths reach here.
        if (DefragRunState.IsRunning && !ConfirmQuitDuringDriveOptimisation())
            return;

        // An install batch or a cleanup run dies with the process, leaving half-installed packages and no history.
        if (!DefragRunState.IsRunning && _mainViewModel?.WorkIsRunning == true && !ConfirmQuitDuringRunningWork())
            return;

        _isExiting = true;
        Shutdown();
    }

    private static bool ConfirmQuitDuringRunningWork()
    {
        var loc = LocalizationService.Instance;
        return Dialogs.Confirm(
            loc["WorkQuitWarningTitle"],
            loc["WorkQuitWarningBody"],
            confirmText: loc["DefragQuitAnyway"],
            cancelText: loc["DefragKeepRunning"],
            destructive: true);
    }

    private static bool ConfirmQuitDuringDriveOptimisation()
    {
        var loc = LocalizationService.Instance;
        return Dialogs.Confirm(
            loc["DefragQuitWarningTitle"],
            loc["DefragQuitWarningBody"],
            confirmText: loc["DefragQuitAnyway"],
            cancelText: loc["DefragKeepRunning"],
            destructive: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Settings are written on a delay, so the last click before exit may still be pending: flush first, before anything that could fail.
        _settings?.Flush();

        // After a data folder move this run still writes to the old folder until the restart; what it
        // wrote since the copy has to follow, or the new folder would start without it.
        AppPaths.SyncPendingDataFolder();

        _mainViewModel?.DiscardUnstartedSelfUpdate();

        _scheduler?.Dispose();
        _tray?.Dispose();
        _busyTimer?.Stop();
        _busySignal?.Dispose();
        _installerListener?.Dispose();
        _exitRequests?.Dispose();

        // Closing the pipe is the shutdown protocol for the elevated helper; bounded wait since this
        // runs on the UI thread with the window gone.
        _elevated?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));

        SingleInstanceService.Release();
        _logging?.Event(ActivityLog.App, "exit");

        // Anything still being counted as a repeat belongs in the file before the process is gone.
        _logging?.Flush();
        base.OnExit(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    /// <summary>
    /// Picks starting values for both parallelism sliders from this machine's hardware. Only called while
    /// <see cref="Models.Settings.AppSettings.ParallelismChosenByUser"/> is false. A failed RAM read counts as
    /// 0 bytes, which <see cref="ParallelismAutoTune"/> treats as a low-memory machine (the safe direction).
    /// </summary>
    private static void TuneParallelismForThisMachine(SettingsService settings, LoggingService logging)
    {
        var cores = Environment.ProcessorCount;

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var visibleRamBytes = GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0L;
        var totalRamBytes = ParallelismAutoTune.RamForTuning(
            GetPhysicallyInstalledSystemMemory(out var installedKilobytes) ? (long)installedKilobytes : 0L,
            visibleRamBytes);

        var hasBattery = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus
            != System.Windows.Forms.BatteryChargeStatus.NoSystemBattery;

        var result = ParallelismAutoTune.Compute(cores, totalRamBytes, hasBattery);
        settings.Current.Scan.MaxParallelSources = result.MaxParallelSources;
        settings.Current.Install.MaxParallelInstalls = result.MaxParallelInstalls;

        // Deferred, not the blocking Save(): this runs on every start before the user has touched either
        // slider, and the values are already live in settings.Current for this run either way.
        settings.SaveDeferred();

        logging.Event(
            ActivityLog.App,
            $"parallelism auto-tune: {cores} cores, {ByteSize.FormatInvariant(totalRamBytes)} RAM, "
            + $"battery {hasBattery} -> {result.MaxParallelSources} sources / {result.MaxParallelInstalls} installs");
    }
}
