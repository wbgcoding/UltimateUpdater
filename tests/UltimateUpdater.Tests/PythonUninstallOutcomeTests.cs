using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

public class PythonUninstallOutcomeTests : IDisposable
{
    private const string UninstallRoot = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string PythonRoot = @"HKCU\SOFTWARE\Python\PythonCore";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wu-pyoutcome-" + Guid.NewGuid());

    public PythonUninstallOutcomeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        PythonVersionCleanupExecutor.RunnerForTest.Value = null;
        ElevationService.IsElevatedForTest.Value = null;
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _subKeys = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry Add(string root, string key, params (string Name, string Value)[] values)
        {
            if (!_subKeys.TryGetValue(root, out var keys))
                _subKeys[root] = keys = [];
            keys.Add(key);
            foreach (var (name, value) in values)
                Set($@"{root}\{key}", name, value);
            return this;
        }

        public FakeRegistry Set(string keyPath, string name, string value)
        {
            if (!_values.TryGetValue(keyPath, out var map))
                _values[keyPath] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[name] = value;
            return this;
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) => _subKeys.TryGetValue(keyPath, out var k) ? k : [];

        public string? Value(string keyPath, string valueName) =>
            _values.TryGetValue(keyPath, out var map) ? map.GetValueOrDefault(valueName) : null;
    }

    /// <summary>Three per-user interpreters (3.8 and 3.9 old, 3.13 newest) with a real uninstaller file each.</summary>
    private (FakeRegistry Registry, string[] Selected, string[] Uninstallers) ThreeInterpreters()
    {
        var registry = new FakeRegistry();
        var selected = new List<string>();
        var uninstallers = new List<string>();
        foreach (var tag in new[] { "3.8", "3.9", "3.13" })
        {
            var dir = Path.Combine(_folder, "Python" + tag);
            Directory.CreateDirectory(dir);
            var python = Path.Combine(dir, "python.exe");
            var uninstall = Path.Combine(dir, "uninstall.exe");
            File.WriteAllText(python, "x");
            File.WriteAllText(uninstall, "x");
            registry.Add(PythonRoot, tag);
            registry.Set($@"{PythonRoot}\{tag}\InstallPath", "ExecutablePath", python);
            registry.Add(UninstallRoot, "py" + tag,
                ("DisplayName", $"Python {tag}.1 (64-bit)"), ("InstallLocation", dir), ("QuietUninstallString", $"\"{uninstall}\" /quiet"));
            if (tag != "3.13")
            {
                selected.Add(python);
                uninstallers.Add(uninstall);
            }
        }

        return (registry, selected.ToArray(), uninstallers.ToArray());
    }

    [Fact]
    public async Task An_unsigned_uninstaller_is_skipped_and_the_next_candidate_still_runs()
    {
        ElevationService.IsElevatedForTest.Value = false;
        var (registry, selected, uninstallers) = ThreeInterpreters();
        var launched = new List<string>();
        PythonVersionCleanupExecutor.RunnerForTest.Value = (file, _, _, _) =>
        {
            launched.Add(file);
            if (string.Equals(file, uninstallers[0], StringComparison.OrdinalIgnoreCase))
                throw new UntrustedToolException(file, "unsigned");
            return Task.FromResult(new ProcessResult(0, "", ""));
        };

        var outcome = await PythonVersionCleanupExecutor.CleanAsync(registry, selected, new LoggingService(), CancellationToken.None);

        Assert.Equal(2, launched.Count);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.False(outcome.WasStopped);
    }

    [Fact]
    public async Task An_app_running_as_administrator_never_runs_a_per_user_uninstaller_and_reports_it_skipped()
    {
        var (registry, selected, _) = ThreeInterpreters();
        ElevationService.IsElevatedForTest.Value = true;
        var launched = 0;
        PythonVersionCleanupExecutor.RunnerForTest.Value = (_, _, _, _) =>
        {
            launched++;
            return Task.FromResult(new ProcessResult(0, "", ""));
        };

        var outcome = await PythonVersionCleanupExecutor.CleanAsync(registry, selected, new LoggingService(), CancellationToken.None);

        Assert.Equal(0, launched);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(2, outcome.SkippedFiles);
    }

    [Fact]
    public async Task Stop_after_one_finished_uninstall_reports_that_one_as_deleted_and_stopped()
    {
        ElevationService.IsElevatedForTest.Value = false;
        var (registry, selected, _) = ThreeInterpreters();
        using var stop = new CancellationTokenSource();
        var launched = 0;
        PythonVersionCleanupExecutor.RunnerForTest.Value = (_, _, _, _) =>
        {
            launched++;
            stop.Cancel();
            return Task.FromResult(new ProcessResult(0, "", ""));
        };

        var outcome = await PythonVersionCleanupExecutor.CleanAsync(registry, selected, new LoggingService(), stop.Token);

        Assert.Equal(1, launched);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.True(outcome.WasStopped);
    }

    [Fact]
    public async Task A_slow_uninstall_gets_thirty_minutes_and_a_cut_off_one_is_a_skip()
    {
        ElevationService.IsElevatedForTest.Value = false;
        var (registry, selected, _) = ThreeInterpreters();
        TimeSpan? seen = null;
        PythonVersionCleanupExecutor.RunnerForTest.Value = (_, _, timeout, _) =>
        {
            seen ??= timeout;
            return Task.FromResult(new ProcessResult(-1, "", "") { TimedOut = true });
        };

        var outcome = await PythonVersionCleanupExecutor.CleanAsync(registry, selected, new LoggingService(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), seen);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(2, outcome.SkippedFiles);
    }

    [Fact]
    public void An_anaconda_entry_that_merely_mentions_python_is_never_the_uninstaller()
    {
        var registry = new FakeRegistry()
            .Add(UninstallRoot, "conda",
                ("DisplayName", "Anaconda3 2023.09-0 (Python 3.11.5 64-bit)"), ("UninstallString", @"C:\ana\Uninstall-Anaconda3.exe"))
            .Add(UninstallRoot, "mini",
                ("DisplayName", "Miniconda3 py311_23 (Python 3.11.5 64-bit)"), ("UninstallString", @"C:\mini\Uninstall-Miniconda3.exe"));
        var interpreter = new PythonInterpreter(@"C:\Elsewhere\Python311\python.exe", new Version(3, 11), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void A_fallback_entry_must_start_with_python()
    {
        var registry = new FakeRegistry()
            .Add(UninstallRoot, "tool",
                ("DisplayName", "Some Tool bundling Python 3.11.5"), ("UninstallString", @"C:\tool\uninstall.exe"));
        var interpreter = new PythonInterpreter(@"C:\Elsewhere\Python311\python.exe", new Version(3, 11), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void The_worker_result_message_carries_the_stopped_flag()
    {
        var message = new CleanupWorkerMessage { Type = "result", Stopped = true };

        var round = System.Text.Json.JsonSerializer.Deserialize<CleanupWorkerMessage>(
            System.Text.Json.JsonSerializer.Serialize(message));

        Assert.True(round!.Stopped);
    }
}
