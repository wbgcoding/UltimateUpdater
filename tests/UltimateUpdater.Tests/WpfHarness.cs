using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateUpdater.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Tests;

/// <summary>
/// Renders the real windows through WPF's own renderer and writes them out as PNGs.
/// This exists because there is no other way to SEE this UI: a screen capture of a WPF window on a
/// headless test machine comes back empty (nothing composites it), and every layout defect found so
/// far, overlapping panels, an invisible background, text clipped by a fixed-width column, was
/// invisible to every other kind of test.
/// </summary>
internal static class WpfHarness
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>
    /// Upper bound for every wait on the harness thread. There used to be none at all: one run parked a
    /// testhost.exe for 38 minutes because a dispatcher that had stopped pumping was waited on for ever.
    /// A minute is far longer than any render here takes, so hitting it always means something is wrong.
    /// </summary>
    private static readonly TimeSpan UiWaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The test that last used the harness, so a later failure can name what left it broken.</summary>
    private static string? _lastTestOnHarness;

    /// <summary>First exception that reached the harness thread unhandled, kept for the same reason.</summary>
    private static Exception? _lastHarnessException;

    /// <summary>Where the rendered PNGs land, next to the test binaries.</summary>
    public static string OutputDirectory { get; } = CreateOutputDirectory();

    /// <summary>
    /// One fresh temp directory for the whole run, created the first time this class is touched (a field
    /// initializer runs as part of the type's static constructor, which the runtime completes before any
    /// member, including <see cref="GetDispatcher"/> and <see cref="OnUiThread"/>, can execute) and
    /// removed at process exit. <see cref="RedirectUserDataForTest"/> hands out one subdirectory of this
    /// per distinct test name, so <see cref="ScanResultCache"/> and <see cref="HistoryService"/> never
    /// reach the real %APPDATA%\UltimateUpdater\ files AND two different MainViewModel tests never share
    /// one scan-cache/history file either, the second contaminating the first the same way the real file
    /// used to contaminate a whole run, just moved one level down. A test that calls <see
    /// cref="OnUiThread"/> more than once keeps the SAME subdirectory across those calls (keyed by name,
    /// not by call), which is what lets a test simulate two sessions against its own one cache file.
    /// </summary>
    private static readonly string RedirectedUserDataRoot = CreateRedirectedUserDataRoot();

    private static string CreateRedirectedUserDataRoot()
    {
        var directory = Directory.CreateTempSubdirectory("wu-test-userdata-").FullName;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only; a locked file here would not make the run's own result wrong.
            }
        };

        return directory;
    }

    /// <summary>Points <see cref="ScanResultCache.PathOverride"/> and <see cref="HistoryService.PathOverride"/>
    /// at <paramref name="testName"/>'s own subdirectory of <see cref="RedirectedUserDataRoot"/>, creating
    /// it on first use. Test names are plain C# identifiers, safe to use as directory names outright.
    /// </summary>
    private static void RedirectUserDataForTest(string testName)
    {
        var directory = Path.Combine(RedirectedUserDataRoot, testName);
        Directory.CreateDirectory(directory);
        ScanResultCache.PathOverride = Path.Combine(directory, "last-scan.json");
        HistoryService.PathOverride = Path.Combine(directory, "history.jsonl");
    }

    /// <summary>
    /// One STA thread with one Application for the whole test run: WPF allows a single Application
    /// per process, and every visual must be created on the thread that owns it.
    /// </summary>
    private static Dispatcher GetDispatcher()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                VerifyUsable(_dispatcher);
                return _dispatcher;
            }

            var ready = new ManualResetEventSlim();
            Dispatcher? created = null;

            var thread = new Thread(() =>
            {
                // This build box is a normal interactive desktop, not a headless CI machine: several other
                // worktree lanes render their own WPF windows off-screen through this same harness at the
                // same time, all sharing the one GPU. Hardware (Direct3D) rendering put every process's
                // RenderTargetBitmap.Render calls through the same driver and DWM composition surfaces,
                // and under that contention the testhost occasionally died outright mid-run with "Der
                // Testhostprozess ist abgestürzt" and no managed exception, no crash dump and no Windows
                // Error Reporting entry, consistent with a native fault in the GPU rendering path rather
                // than anything in this app's own code. Software rendering never touches the GPU or DWM at
                // all, so it cannot contend with a sibling process for either; it must be set before the
                // Application (and any window) is constructed, since WPF picks the render tier per process
                // the first time something renders.
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

                // Teach this process about "pack:" before any XAML that uses one is loaded. A real app gets
                // this from its own startup; in a test host the entry assembly is testhost.exe, and loading
                // a themes dictionary then died in WebRequest.Create with "The URI prefix is not recognized"
                //, inside a window constructor, so the Application tore down and every later render only
                // reported "the application object is being shut down".
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

                // The scheme needs BOTH halves: the URI parser (above) and a WebRequest handler for it.
                // Only the parser comes for free here, so loading a themes dictionary died in
                // WebRequest.Create with "The URI prefix is not recognized". A real app registers this
                // during its own startup; a test host does not.
                try
                {
                    System.Net.WebRequest.RegisterPrefix("pack:", new System.IO.Packaging.PackWebRequestFactory());
                }
                catch (Exception)
                {
                    // already registered by something else, that is exactly what we wanted
                }

                // Set once only, assigning it a second time throws and takes the host with it.
                if (Application.ResourceAssembly is null)
                    Application.ResourceAssembly = typeof(UltimateUpdater.App).Assembly;

                // Before construction: WPF schedules OnStartup to fire the moment this thread's Dispatcher
                // starts pumping (below, via Dispatcher.Run()), even though nothing here ever calls
                // Application.Run(). Left unset, OnStartup ran the whole real production pipeline once per
                // test process (a real SourceRegistry, a real scan, a second genuine MainViewModel), all
                // sharing this one Dispatcher with every test's own MainViewModel, which is what produced
                // This fix's cross-test ListCollectionView reentrancy.
                UltimateUpdater.App.SuppressRealStartupForTests = true;

                var app = new UltimateUpdater.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent(); // brings in the WPF-UI dictionaries and the converters

                // WPF auto-assigns Application.MainWindow to the first window ever SHOWN while it is still
                // null; real startup used to supply that (as a side effect of the very thing suppressed
                // above) so it was never null for long. Left unset here, whichever test's own dialog
                // happened to be shown first would silently become MainWindow, and MainViewModel.ShowOwned
                // sets a dialog's Owner to Application.Current.MainWindow: the moment that dialog IS
                // MainWindow, WPF refuses "Owner = itself". WPF also refuses an Owner that has never been
                // shown at all, so this one is shown once, then hidden; nothing here ever needs to see it.
                // An explicit, empty Style keeps it from picking up the app's own implicit Window style
                // (WPF-UI's Fluent chrome sets AllowsTransparency, which a plain Window cannot re-coerce
                // mid-Show and crashed the harness thread outright).
                var placeholderMainWindow = new Window
                {
                    ShowInTaskbar = false,
                    Width = 0,
                    Height = 0,
                    Style = new Style(typeof(Window)),
                };
                placeholderMainWindow.Show();
                placeholderMainWindow.Hide();
                app.MainWindow = placeholderMainWindow;

                // No user in front of this: a modal dialog here (the crash handler raises one) waits for a
                // click that never comes and hangs the entire test run.
                UltimateUpdater.Services.Dialogs.SuppressInteractiveDialogs = true;

                // An exception that escapes onto this dispatcher ends Dispatcher.Run() and takes the one
                // shared Application with it, so every later test in the run reports only "the application
                // object is being shut down" and the real fault is dozens of tests back. Handled keeps the
                // loop pumping; the exception is kept so the next health check can name it.
                app.DispatcherUnhandledException += (_, args) =>
                {
                    _lastHarnessException ??= args.Exception;
                    args.Handled = true;
                };

                // Nothing can stop this one from ending the process, but recording it means the message the
                // run ends with names the exception instead of its aftermath.
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                    _lastHarnessException ??= args.ExceptionObject as Exception;

                created = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            },
                // WPF's layout and render walks recurse once per visual, and this app's table is a deep
                // tree (rows inside groups inside a list inside cards), the default stack does not have
                // room for that walk and overflows with a hard crash and no exception. The size below is
                // the fix, not a workaround: a render thread simply needs room for the walk.
                maxStackSize: 32 * 1024 * 1024)
            {
                IsBackground = true,
                Name = "wpf-render-harness",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Generous on purpose: this thread builds a WPF Application and loads the WPF-UI resource
            // dictionaries, which is not quick on a machine that is busy doing something else. A short
            // timeout here risks failing tests that have nothing to do with the code under test, simply
            // because the Application was not ready yet.
            if (!ready.Wait(TimeSpan.FromMinutes(2)) || created is null)
                throw new InvalidOperationException("The WPF harness thread did not start in time.");

            _dispatcher = created;
            return _dispatcher;
        }
    }

    /// <summary>
    /// Refuses to hand out a dispatcher that can no longer run anything. Without this a dead harness turns
    /// into a queue of tests that all wait for a pump that will never come, or into dozens of identical
    /// "the application object is being shut down" failures with no sign of what actually broke. The
    /// message carries both halves of the cause: the last test that used the harness, and whatever
    /// exception reached its thread.
    /// </summary>
    private static void VerifyUsable(Dispatcher dispatcher)
    {
        string? reason = null;

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            reason = "its dispatcher has shut down";
        else if (Application.Current is null)
            reason = "its Application is gone";
        else if (Application.Current.Dispatcher.HasShutdownStarted)
            reason = "its Application is shutting down";

        if (reason is null)
            return;

        throw new InvalidOperationException($"The WPF harness is no longer usable: {reason}. {Diagnosis()}");
    }

    /// <summary>What is known about how the harness got into the state it is in.</summary>
    private static string Diagnosis()
    {
        var last = _lastTestOnHarness is null ? "no test has used it yet" : $"last test on it: {_lastTestOnHarness}";
        var cause = _lastHarnessException is null
            ? "no exception was recorded on the harness thread"
            : $"the harness thread saw: {_lastHarnessException}";

        return $"{last}; {cause}.";
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread and rethrows whatever it threw there.</summary>
    public static void OnUiThread(Action action, [CallerMemberName] string testName = "")
    {
        var dispatcher = GetDispatcher();
        RedirectUserDataForTest(testName);
        _lastTestOnHarness = testName;

        Exception? failure = null;

        void Guarded()
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // Already on the harness thread (one helper calling into another): waiting on a queued operation
        // from here would be waiting for the pump this call is standing in.
        if (dispatcher.CheckAccess())
        {
            Guarded();
        }
        else if (!dispatcher.InvokeAsync(Guarded).Task.Wait(UiWaitTimeout))
        {
            // Loud beats silent: a bare Invoke had no upper bound, so a dispatcher that stopped pumping
            // held the whole run instead of failing the one test that met it.
            throw new TimeoutException(
                $"The WPF harness did not run '{testName}' within {UiWaitTimeout.TotalSeconds:0} seconds. {Diagnosis()}");
        }

        if (failure is not null)
            throw new InvalidOperationException($"Rendering failed on the UI thread: {failure.Message}", failure);
    }

    /// <summary>
    /// Puts a control inside a real Window and applies the app's styles to it, so a test can read what a
    /// control's properties actually resolve to at runtime. Returns the window, which the caller keeps alive
    /// for the length of the assertion, a collected window takes its inherited values with it.
    /// </summary>
    public static Window HostForStyling(FrameworkElement content)
    {
        var window = new Window { Content = content };
        content.UpdateLayout();
        return window;
    }

    /// <summary>
    /// Applies a theme to the harness's one shared Application the same way <see cref="ThemeService.ApplyCore"/>
    /// itself does, so a test never leaves the NEXT test to inherit whatever it wrote. Before this existed,
    /// each theme test called <c>ApplicationThemeManager.Apply</c> on its own and left it at that: a test
    /// that rendered the dark theme wrote <see cref="ThemeService.ApplyDarkPaletteOverrides"/>'s opaque dark
    /// surface keys straight into <c>Application.Resources</c>, and nothing ever removed them again, so a
    /// LATER light-theme test in the same run read the previous test's dark card colour and failed, only
    /// when the two ran in that order, never alone. Always removes the dark overrides for a light theme and
    /// always clears the high-contrast surface keys first, whether or not this particular test path ever
    /// wrote them, because the point is a clean slate for the NEXT test, not just correctness for this one.
    /// High-contrast clears BEFORE the dark palette is applied/removed, never after: the two override sets
    /// share five keys (CardBackgroundFillColorDefaultBrush among them), so clearing high-contrast AFTER
    /// writing the dark palette would delete the dark override it just wrote, exactly the order
    /// <see cref="ThemeService.ApplyCore"/> itself uses (high-contrast leaves first, the dark palette is
    /// decided last).
    /// </summary>
    public static void ApplyTheme(ApplicationTheme theme)
    {
        ThemeService.RemoveHighContrastSurfaceOverrides();

        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true);
        ThemeService.ApplyAccentContrast(ApplicationAccentColorManager.SystemAccent);

        if (theme == ApplicationTheme.Dark)
            ThemeService.ApplyDarkPaletteOverrides();
        else
            ThemeService.RemoveDarkPaletteOverrides();
    }

    /// <summary>
    /// Lays the window's content out at the given size and writes it to a PNG. The content is
    /// rendered rather than the Window itself, because an unshown Window has no client area yet.
    /// </summary>
    /// <param name="scale">Display scaling to simulate; 2.0 is Windows' 200% setting.</param>
    public static RenderResult RenderWindow(Window window, string fileName, int width, int height, double scale = 1.0,
        bool expandGroups = true)
    {
        var root = window.Content as FrameworkElement
            ?? throw new InvalidOperationException($"{window.GetType().Name} has no content to render.");

        // Detach from the Window so the element can be measured and arranged on its own, and carry
        // the DataContext over by hand, because that inheritance is exactly what the Window was
        // providing. Without it everything data-bound renders empty and only the static chrome shows.
        var dataContext = window.DataContext;
        window.Content = null;

        var host = new Border
        {
            Width = width,
            Height = height,
            Child = root,
            Background = window.Background ?? Brushes.White,
            DataContext = dataContext,
        };

        // A LayoutTransform on the content is what display scaling does: the same pixels, half the
        // logical room, so text and fixed-width columns collide exactly as they would at 200%.
        // The window keeps its pixel size; WPF measures the transformed child in the space that is
        // left once it is drawn twice as big, which is what the layout has to survive.
        if (scale != 1.0)
            root.LayoutTransform = new ScaleTransform(scale, scale);

        // Several passes, each ending in a throwaway render. The grouped, virtualizing list only
        // realizes its rows once it knows its viewport, which it does not on the first arrange; and
        // the expanders animate, so a single pass would catch them mid-transition with rows half drawn.
        // A render pass is what advances the animation clock, and the wait gives it time to finish.
        RenderTargetBitmap bitmap;

        for (var pass = 0; ; pass++)
        {
            host.Measure(new Size(width, height));
            host.Arrange(new Rect(0, 0, width, height));
            host.UpdateLayout();

            // Done every pass, because virtualization only realizes further groups once the viewport
            // is known, a group that appears on pass two would otherwise still come out shut.
            if (expandGroups)
                ExpandCategoryGroups(host);
            host.UpdateLayout();

            PumpToIdle();

            bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);

            if (pass >= 3)
                break;

            Thread.Sleep(120);
        }

        var path = Path.Combine(OutputDirectory, fileName + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        return new RenderResult(path, CountDistinctColours(bitmap), width, height);
    }

    /// <summary>
    /// Lets everything already queued run, down to the idle priority the layout and render work sits at.
    /// RenderWindow runs on the harness thread itself, so this is a nested pump rather than a cross-thread
    /// wait, and the timeout is what the bare Invoke here lacked: a queue that never reaches idle used to
    /// park the run with no upper bound at all.
    /// </summary>
    private static void PumpToIdle()
    {
        try
        {
            GetDispatcher().Invoke(() => { }, DispatcherPriority.SystemIdle, CancellationToken.None, UiWaitTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"The WPF harness queue did not reach idle within {UiWaitTimeout.TotalSeconds:0} seconds. {Diagnosis()}");
        }
    }

    /// <summary>
    /// The table's category groups take their expanded state from the group Expander's Loaded event,
    /// which never fires here: the harness renders detached content and never shows a window. Without
    /// this every group comes out shut and no row ever reaches a snapshot, the layout the snapshots
    /// exist to prove would be invisible. Only the table's group headers are touched (their DataContext
    /// is a CollectionViewGroup), plus a cleanup row's own detail-list Expander when it actually has
    /// entries to show, plus, ONLY when that is true, the cleanup CATEGORY card
    /// (an Expander carrying the window's own CleanupGroupCard template) containing it, since a collapsed
    /// outer card would otherwise hide the very row a test populated entries on. A category card with
    /// nothing inside it worth revealing stays exactly as collapsed as a real first-time user would see
    /// it, same as the Sources card in the settings window.
    /// </summary>
    private static void ExpandCategoryGroups(DependencyObject root)
    {
        if (root is Expander { DataContext: System.Windows.Data.CollectionViewGroup } groupExpander)
            groupExpander.IsExpanded = true;

        if (root is Expander { DataContext: ViewModels.CleanupItemViewModel { HasEntries: true } } entryExpander)
            entryExpander.IsExpanded = true;

        if (root is Expander { DataContext: ViewModels.CleanupGroupViewModel { } categoryGroup } categoryCard
            && categoryGroup.Items.Any(item =>
                item.HasEntries || item.HasDriverDeviceCandidates || item.HasDriverPackageCandidates))
        {
            categoryCard.IsExpanded = true;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            ExpandCategoryGroups(VisualTreeHelper.GetChild(root, i));
    }

    /// <summary>
    /// A render that produced one flat colour means the window came up blank, which is exactly the
    /// failure a screenshot-free test suite cannot otherwise see.
    /// </summary>
    private static int CountDistinctColours(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var colours = new HashSet<uint>();
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 7) // every seventh pixel is plenty
            colours.Add(BitConverter.ToUInt32(pixels, i));

        return colours.Count;
    }

    private static string CreateOutputDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ui-snapshots");
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Waits for a <see cref="ViewModels.CleanupViewModel"/>'s catalogue to finish building: the
    /// constructor only starts that work now (see its own doc comment), wrapping the result into view
    /// models on whichever thread the view model was itself constructed on, queued there rather than run
    /// inline. A plain blocking wait would deadlock here, because that queued work never runs unless
    /// something pumps THIS thread's own dispatcher; a nested <see cref="DispatcherFrame"/> does exactly
    /// that, on both a bare xUnit thread and the harness's own pumped dispatcher thread (a nested frame is
    /// the ordinary way to enter a second message loop within one already running).
    /// </summary>
    public static void WaitForCleanupCatalog(ViewModels.CleanupViewModel viewModel)
    {
        var task = viewModel.CatalogReadyTask;
        if (task.IsCompleted)
            return;

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);

        // Rethrow anything the background build itself threw, same as an ordinary await would.
        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Lets everything already posted to the dispatcher a view model marshals to run, then returns. The same
    /// dispatcher choice as the view model's own: the application's if one exists, else this thread's.
    /// </summary>
    public static void DrainViewModelDispatcher()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            return;
        }

        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

internal sealed record RenderResult(string Path, int DistinctColours, int Width, int Height);
