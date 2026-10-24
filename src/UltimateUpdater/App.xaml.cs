using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater;

public partial class App : Application
{
    private const string TrayArgument = "--tray";

    /// <summary>
    /// Test-only sentinel a worker-mode launch can carry as its 4th argument. A real worker
    /// launch (<see cref="ElevationService.StartWorker"/>) never passes it; when present, the worker
    /// branch below reports whether <see cref="AppPaths"/> was ever touched during this run, through
    /// <see cref="AppPathsTouchTracker"/> rather than AppPaths itself (see that type's remarks for why).
    /// </summary>
    internal const string ReportAppPathsTouchArgument = "--report-apppaths-touch";

    private LoggingService? _logging;
    private SettingsService? _settings;
    private TrayIconService? _tray;
    private ScanScheduler? _scheduler;
    private ElevatedHost? _elevated;
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

        // Installed before anything else this class does, before WPF has even loaded the resource
        // dictionaries. A failure that early happens before the real logging exists, and the process
        // then disappears leaving nothing behind but a Windows error report, which is exactly the
        // position we were in with a crash we could not explain. This writes one line to a place
        // that always exists and that no build ever deletes.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteLastResortCrashNote(args.ExceptionObject as Exception);

            // The CLR terminates the process right after every subscriber to this event returns, with no
            // normal Shutdown()/OnExit in between, so whatever the OTHER handler on this same event
            // (InstallCrashHandlers, once startup has gotten that far) already queued through the
            // background writer must be forced out here rather than left for an exit path that is never
            // going to run. Bounded, and harmless when nothing is pending or _logging is still null.
            _logging?.Flush();
        };
    }

    /// <summary>
    /// Makes the window buttons (minimise / maximise / close) light up under the mouse.
    /// WPF-UI drives that highlight ONLY from a win32 message hook; <c>TitleBarButton.Hover()</c> is called
    /// from WM_NCHITTEST, and the TitleBar installs the hook in its handler for the window's ContentRendered
    /// event. Nothing in the control reacts to WPF's own IsMouseOver, so wherever that hook does not end up
    /// installed the buttons never highlight at all.
    /// Done in code rather than in App.xaml because WPF-UI keys no base style for TitleBarButton: a XAML
    /// style deriving from the implicit style for its own target type is a self-reference, which WPF resolves
    /// to nothing and which therefore strips the control's template outright. Reading the existing style
    /// first and replacing the entry afterwards has no such ambiguity.
    /// The trigger writes the button's OWN MouseOverBackground, so the close button stays red and, where the
    /// hook does work, both paths set the same brush and nothing flickers.
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

            // Same treatment the ordinary log gets: the user's own name out of the paths, and anything
            // credential-shaped in an exception message out of the file entirely. Started fresh once it
            // passes a quarter of a megabyte, so this cannot become a growing archive of stack traces in
            // a folder nobody looks at.
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

        // WpfHarness builds a real Application only to load WPF-UI's resource dictionaries and give each
        // test a Dispatcher; it never calls Run(). WPF schedules OnStartup to fire the moment that
        // Dispatcher starts pumping messages regardless, so without this guard every WPF-harness test
        // process quietly ran the WHOLE real startup path too: SourceRegistry.CreateAll and, when
        // Scan.OnStart is on, a live scan of every real source on the machine, through a second, genuine
        // MainViewModel sharing the one Dispatcher every test's own MainViewModel runs on. That second
        // scan's flush timer and the test's own ScanAsync/Flush calls then interleaved on the same thread
        // whenever a test pumped a nested dispatcher frame, which is what produced a
        // ListCollectionView.PrepareLocalArray NullReferenceException, and the same interleaving could
        // as easily land as a timeout or a wrong row count depending on how far the real scan had gotten.
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

        // DriveMediaTypeReader logged nothing at all, so a published build's "unknown" media
        // type had no trail to follow. Wired once, here, so every drive's WMI read (success or failure)
        // reaches the real log from the very first cleanup catalogue build.
        Services.Cleanup.DriveMediaTypeReader.Log = logging.Info;

        // Anything that escapes must reach the log. Without this a startup failure only showed up
        // as the process disappearing, with nothing written down and no way to tell what happened.
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
                    Shutdown(await ElevatedWorker.RunAsync(e.Args[1], serverProcessId, logging));
                }
                finally
                {
                    if (e.Args.Contains(ReportAppPathsTouchArgument))
                        Console.WriteLine($"AppPathsTouched={AppPathsTouchTracker.WasTouched}");
                }

                return;
            }

            // Setup asks for the autostart entry rather than writing it itself: it may be running under a
            // different administrator account than the person installing, and its HKCU is that account's,
            // not theirs. Started with runasoriginaluser, this lands in the right hive. No window, no
            // single-instance guard, no services: it writes one registry value and leaves.
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
                // Hand over to the instance that is already running. If it cannot be reached, say
                // so rather than vanishing: an exit with no window on screen reads as a failure to
                // start, which is exactly how this looked in practice.
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

            // Started here rather than after the UI is up: building the settings, the sources and the
            // window takes long enough that a second launch in that window would fail to hand over and
            // tell the user the app is "already running" instead of raising it.
            SingleInstanceService.StartListening(RequestFocus);

            StartUi(e, logging);
            logging.Info("Startup complete.");
        }
        catch (Exception ex)
        {
            // The outermost boundary of startup: whatever failed, wherever, this is what turns it into a
            // logged error and a dialog instead of the process vanishing with no window and no explanation.
            logging.Error("Startup failed", ex);
            ReportFatal(ex, logging);
            Shutdown(1);
        }
    }

    private void StartUi(StartupEventArgs e, LoggingService logging)
    {
        var settings = _settings = new SettingsService(logging);

        // Retention sweeps over however many days of logs and history have piled up. Pure housekeeping
        // that nothing else in startup needs, so it runs fire-and-forget through Background.Run (a failure
        // is still logged) and off the UI thread, where it cannot delay first paint.
        _ = Background.Run(
            Task.Run(() => logging.CleanupOldLogs(settings.Current.Logs.RetentionDays)), logging, "log retention sweep");

        // Runs on every start until a slider is moved by hand, not only a genuine first launch:
        // recomputing the same machine's numbers again before that point is harmless, and needs no
        // one-shot flag of its own. Once ParallelismChosenByUser is true this never runs again, not
        // even across an update.
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
        var iconCache = new IconCacheService(logging);
        var history = new HistoryService(logging);

        // Same reasoning as the log retention sweep above: fire-and-forget off the UI thread so it never
        // delays first paint. Safe to race a concurrent Append from an install that finishes before this
        // sweep gets scheduled: HistoryService's own _lock serialises the two, and whichever runs first
        // simply wins the ordering, the same as if this still ran synchronously right here.
        _ = Background.Run(
            Task.Run(() => history.CleanupOldEntries(settings.Current.HistoryRetentionDays)), logging, "history retention sweep");
        _ = Background.Run(Task.Run(() => AppDataCleaner.DeleteStaleTempSiblings(AppPaths.DataDirectory, TimeSpan.FromDays(1))), logging, "stale temp sweep");

        // An installed update leaves its downloaded setup in the temp folder (it runs from there after this
        // process is gone); a day later nothing can still be using it.
        _ = Background.Run(
            Task.Run(() => Services.SelfUpdate.SelfUpdateService.SweepStaleDownloads(
                Path.GetTempPath(), TimeSpan.FromDays(1), logging)),
            logging, "stale update download sweep");

        // --mock-sources: deterministic fake providers for offline UI and VM testing.
        // isMockSession rides the SAME switch, so InstallQueue never elevates for a mock row -
        // the elevated worker builds its own provider set from SourceRegistry (the real one) and has never
        // heard of this flag, and a mock row deliberately carries the same ids a real one would.
        var isMockSession = e.Args.Contains("--mock-sources");
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
        // No client-side timeout: HttpClient's 100 second default also covers reading the response
        // body, and the artifacts are well over 100 MB, so every download on an ordinary connection
        // was cancelled part-way through. The download enforces its own size cap and honours the
        // caller's cancellation token, which is what should end it.
        var updateTrafficHandler = new Services.Security.TrafficLoggingHandler { Logging = logging };
        var updateHttpClient = new System.Net.Http.HttpClient(updateTrafficHandler) { Timeout = Timeout.InfiniteTimeSpan };
        var selfUpdate = new Services.SelfUpdate.SelfUpdateService(updateHttpClient, logging, updateChannel);
        var wingetBootstrap = new Services.WingetBootstrapService(updateHttpClient, logging);

        var mainViewModel = new MainViewModel(
            settings, logging, themeService, scanOrchestrator, iconCache, installQueue, history, autostart,
            notifications, selfUpdate, sources, _elevated, wingetBootstrap: wingetBootstrap);
        _mainViewModel = mainViewModel;
        var mainWindow = new MainWindow(mainViewModel, themeService, settings, logging);

        MainWindow = mainWindow;

        _tray = new TrayIconService(logging);
        _tray.OpenRequested += () => Dispatcher.Invoke(mainWindow.BringToFront);
        _tray.ScanRequested += () => Dispatcher.Invoke(() => mainViewModel.ScanCommand.Execute(null));
        _tray.ExitRequested += () => Dispatcher.Invoke(ExitApplication);
        _tray.Show();

        notifications.NotificationRequested += (title, message) => _tray.ShowBalloon(title, message);
        mainViewModel.LanguageChanged += () => _tray.RefreshLocalization();

        // The tray menu is WinForms and paints from colours captured when it was built, so it is rebuilt
        // on a theme change too, otherwise it stayed in the previous theme until the next restart.
        themeService.ThemeChanged += () => Dispatcher.BeginInvoke(() => _tray?.RefreshLocalization());

        _scheduler = new ScanScheduler(
            settings, logging, () => mainViewModel.RunScheduledScanAsync(), isOnline: NetworkAvailability.IsOnline);
        _scheduler.Start();
        mainViewModel.ScanCompleted += () => _scheduler.NotifyScanCompleted();

        // --tray: started by autostart, so come up in the background without stealing focus
        StartedInTray = e.Args.Contains(TrayArgument);

        // So the installer can put this copy back after replacing it: started from
        // the tray, it comes back to the tray rather than throwing a window at the user.
        RestartRegistration.Register(StartedInTray ? TrayArgument : null, logging);

        if (!StartedInTray)
        {
            // Process start to the first painted frame. ContentRendered can fire again later
            // (e.g. after certain layout passes), so the handler detaches itself after the first hit;
            // only the FIRST paint answers "how long until the user sees something".
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

        // An autostart (tray) launch has nobody looking at the window, so its scan goes the scheduled way and
        // announces what it found: otherwise only an interval scan ever notified, and a PC shut down every
        // evening never reached one with the default 24-hour interval, so the user was never told.
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
    /// Nobody is there to click anything: a build, a test run or a scripted launch. Recognised from the
    /// `--unattended` switch and from WU_UNATTENDED, which build.bat sets for its own steps. Also true when
    /// the headless dialog suppression is on, so the render harness can never be stopped by a dialog.
    /// Public and computed live (not cached from <see cref="StartupEventArgs"/>) so any caller can ask,
    /// including the pre-install blocker check and the interactive-retry gate, which run long after
    /// startup and have no <see cref="StartupEventArgs"/> of their own to read.
    /// </summary>
    public static bool IsUnattended =>
        !ForceAttendedForTest &&
        (Environment.GetCommandLineArgs().Contains("--unattended", StringComparer.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("WU_UNATTENDED") is { Length: > 0 }
        || Dialogs.SuppressInteractiveDialogs);

    /// <summary>
    /// Test-only (never set outside <c>UltimateUpdater.Tests</c>, and only for the one test's duration,
    /// reset in its own <c>finally</c>): <c>WpfHarness</c> sets <see cref="Dialogs.SuppressInteractiveDialogs"/>
    /// once, process-wide, which makes <see cref="IsUnattended"/> true for the entire test run, including
    /// paths, like the interactive-retry gate, whose own real behavior a test needs to exercise. This
    /// is the one switch to get past that without weakening the gate itself.
    /// </summary>
    internal static bool ForceAttendedForTest { get; set; }

    /// <summary>
    /// Test-only, set by <c>WpfHarness</c> before its Application is ever constructed, for the whole test
    /// process: OnStartup runs the real production pipeline (real sources, a real scan, a real
    /// MainViewModel) the instant the harness's Dispatcher starts pumping, whether or not anything ever
    /// calls Run(). A render or view-model test wants a Dispatcher and WPF-UI's resource dictionaries,
    /// never a second, genuine application instance racing its own MainViewModel on that Dispatcher.
    /// </summary>
    internal static bool SuppressRealStartupForTests { get; set; }

    /// <summary>Raises the window, or remembers to once there is one.</summary>
    private void RequestFocus() => Dispatcher.Invoke(() =>
    {
        if (MainWindow is Views.MainWindow window)
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
            var message = string.Format(
                LocalizationService.Instance["FatalError"],
                AppPaths.LogsDirectory);

            // The full exception goes into the copyable Details panel, never into a shipped log, but the
            // user in front of the crash can read and copy it (SECURITY.md keeps it local, on request).
            Dialogs.Error(
                LocalizationService.Instance["FatalErrorHeadline"],
                message,
                exception.ToString());
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
        _isExiting = true;
        base.OnSessionEnding(e);
    }

    /// <summary>Real exit, as opposed to close-to-tray.</summary>
    internal void ExitApplication()
    {
        // Quitting kills a running drive optimisation (there is no scheduled task behind
        // it to carry on), unlike a plain cleanup run, which keeps going in the background regardless of
        // this window. Both quit paths (the tray icon and the main window's own exit action) reach here,
        // which is what lets one check cover both.
        if (DefragRunState.IsRunning && !ConfirmQuitDuringDriveOptimisation())
            return;

        // An install batch or a cleanup run dies with the process (the elevated helper goes with it), leaving
        // half-installed packages and no history. Closing the window hides it while installs run; tray Exit
        // came straight here and ended them without a word.
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
        // Settings are written on a delay while the app runs, so the last click before the exit: a
        // theme switch, the window placement saved on close; can still be sitting in that delay.
        // First thing here, before anything else that could fail: it belongs on disk either way.
        _settings?.Flush();

        // After a data folder move this run still writes to the old folder until the restart; what it
        // wrote since the copy has to follow, or the new folder would start without it.
        AppPaths.SyncPendingDataFolder();

        _scheduler?.Dispose();
        _tray?.Dispose();

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

    /// <summary>
    /// Picks starting values for both parallelism sliders from this machine's own hardware, so a slow or
    /// battery-only PC does not inherit numbers tuned for a fast desktop. The caller only invokes this
    /// while <see cref="Models.Settings.AppSettings.ParallelismChosenByUser"/> is still false, so it never
    /// runs again once either slider has been moved by hand. A failed RAM read is treated as 0 bytes,
    /// which <see cref="ParallelismAutoTune"/> reads the same way a genuinely low-memory machine would,
    /// the safe direction to fail in.
    /// </summary>
    private static void TuneParallelismForThisMachine(SettingsService settings, LoggingService logging)
    {
        var cores = Environment.ProcessorCount;

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var totalRamBytes = GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0L;

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
