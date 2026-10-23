// The app has process-wide state that tests legitimately touch: the LocalizationService singleton,
// the WPF Application, and settings.json under AppPaths (a throwaway folder, see TestDataFolder). Running classes in parallel made those
// collide and produced failures that only appeared in a full run. The whole suite takes a few
// seconds, so serialising it costs nothing worth having.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UltimateUpdater.Tests;

/// <summary>
/// Points the app's data folder at a fresh temp folder before any test touches it, so nothing in the suite
/// reads or writes the developer's real settings, history or logs. Every module initializer below calls
/// <see cref="EnsureSet"/>, because the order of module initializers is not something to rely on.
/// </summary>
internal static class TestDataFolder
{
    private static readonly object Gate = new();
    private static string? _path;

    internal static void EnsureSet()
    {
        lock (Gate)
        {
            if (_path is not null)
                return;

            _path = Path.Combine(Path.GetTempPath(), "uu-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
            UltimateUpdater.Services.AppPathsTestOverride.DataDirectory = _path;

            var folder = _path;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception)
                {
                    // a leftover temp folder is harmless
                }
            };
        }
    }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => EnsureSet();
}

/// <summary>
/// Teaches this process about the "pack:" URI scheme before ANY test runs.
/// A real WPF app wires this up during its own startup; a test host does not, and without it loading any
/// XAML, including WPF-UI's theme dictionary, which happens inside a window constructor, dies in
/// WebRequest.Create with "The URI prefix is not recognized". That failure tears the Application down, so
/// every later render then reports only "the application object is being shut down" and a whole run of UI
/// snapshots is lost.
/// A module initializer runs before any test does, so registration never depends on which thread
/// first touches a pack URI.
/// </summary>
internal static class PackSchemeInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        TestDataFolder.EnsureSet();

        // The scheme needs BOTH halves: the URI parser, and a WebRequest handler for it.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

        try
        {
            System.Net.WebRequest.RegisterPrefix("pack:", new System.IO.Packaging.PackWebRequestFactory());
        }
        catch (Exception)
        {
            // Already registered by something else, which is exactly the state we wanted.
        }
    }
}

/// <summary>
/// A test that reaches <see cref="UltimateUpdater.Services.ElevationService.StartWorker"/>
/// despite every other seam meant to stop it first (a fake elevated host, <see
/// cref="UltimateUpdater.Services.Dialogs.SuppressInteractiveDialogs"/>) must fail loudly instead of
/// popping a real UAC prompt on whoever's machine is running the suite. Runs before any test, the same
/// way <see cref="PackSchemeInitializer"/> does.
/// </summary>
internal static class ElevationGuardInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register()
    {
        TestDataFolder.EnsureSet();
        UltimateUpdater.Services.ElevationService.BlockRealLaunchForTests = true;
    }
}
