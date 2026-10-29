using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// An elevated target whose helper drops out mid-way falls back to a local pass. What the helper confirmed
/// gone before it dropped counts once, not twice, in the run total.
/// </summary>
[Collection("UiLanguage")]
public class CleanupHelperFallbackTests
{
    /// <summary>The local pass: reports a live figure, then returns its own outcome (or throws).</summary>
    private sealed class LocalPassService(LoggingService logging, long liveBytes, long finalBytes, bool fail = false)
        : CleanupService(logging)
    {
        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0) =>
            Task.FromResult(new CleanupScanResult(0, 0, 0));

        public override async Task<CleanupOutcome> CleanAsync(
            CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
            PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
            IReadOnlyCollection<string>? selectedLeftoverPaths = null,
            IReadOnlyList<RegistryFinding>? registryFindings = null,
            IReadOnlyCollection<string>? excludedEntryPaths = null,
            bool recycleInsteadOfDelete = false,
            int recycleBinMinAgeDays = 0,
            IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
            IReadOnlyCollection<string>? selectedDriverPackageNames = null,
            IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
            IReadOnlyCollection<string>? confirmedEntryPaths = null)
        {
            progress?.Report(new CleanupProgress("a.tmp", 40, liveBytes));
            await Task.Delay(100, cancellationToken);
            if (fail)
                throw new InvalidOperationException("boom");

            return new CleanupOutcome(finalBytes, 1, 0);
        }
    }

    /// <summary>A helper stand-in that reports freed bytes for the first clean job and then drops the pipe.</summary>
    private sealed class DroppingWorker : IDisposable
    {
        private readonly AnonymousPipeServerStream _workerToApp = new(PipeDirection.In);
        private readonly AnonymousPipeServerStream _appToWorker = new(PipeDirection.Out);
        private readonly StreamWriter _replies;
        private readonly StreamReader _commands;
        private readonly long _freed;

        public DroppingWorker(long freed)
        {
            _freed = freed;
            var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, _workerToApp.ClientSafePipeHandle);
            _replies = new StreamWriter(workerEnd, new UTF8Encoding(false)) { AutoFlush = true };
            _commands = new StreamReader(
                new AnonymousPipeClientStream(PipeDirection.In, _appToWorker.ClientSafePipeHandle), Encoding.UTF8);
            _ = Task.Run(ServeAsync);
        }

        public void AttachTo(ElevatedHost host) => host.AttachSessionForTest(
            new StreamReader(_workerToApp, Encoding.UTF8),
            new StreamWriter(_appToWorker, new UTF8Encoding(false)) { AutoFlush = true });

        private async Task ServeAsync()
        {
            try
            {
                while (await _commands.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.GetProperty("kind").GetString() == "control")
                        continue;

                    var id = root.GetProperty("id").GetString()!;
                    if (root.GetProperty("payload").GetProperty("command").GetString() != "clean")
                    {
                        _replies.WriteLine($"{{\"id\":\"{id}\",\"payload\":{{\"type\":\"done\"}}}}");
                        continue;
                    }

                    _replies.WriteLine(
                        $"{{\"id\":\"{id}\",\"payload\":{{\"type\":\"progress\",\"percent\":50,\"freed\":{_freed},\"current\":\"a\"}}}}");
                    await Task.Delay(300);
                    _replies.Dispose();
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the test is over
            }
        }

        public void Dispose()
        {
            _replies.Dispose();
            _workerToApp.Dispose();
            _appToWorker.Dispose();
        }
    }

    private static (CleanupViewModel ViewModel, CleanupItemViewModel Item) CreateRun(
        CleanupService service, DroppingWorker worker)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;
        var logging = new LoggingService();
        var host = new ElevatedHost(logging);
        worker.AttachTo(host);
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-fallback-s-").FullName, "settings.json"));
        var history = new CleanupHistoryService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-fallback-h-").FullName, "history.json"));

        CleanupViewModel? viewModel = null;
        WpfHarness.OnUiThread(() =>
        {
            viewModel = new CleanupViewModel(service, logging, settings, history: history, elevated: host);
            WpfHarness.WaitForCleanupCatalog(viewModel);
            viewModel.ComponentStoreResult = new ComponentStoreAnalysis(1_000_000_000, null, null, null);
        });

        var item = viewModel!.Items.First(i => i.Target.Kind == CleanupKind.Directories && i.Target.NeedsElevation);
        WpfHarness.OnUiThread(() =>
        {
            foreach (var other in viewModel.Items)
                other.IsSelected = ReferenceEquals(other, item);
        });

        return (viewModel, item);
    }

    private static Task OnUi(Func<Task> start)
    {
        Task? task = null;
        WpfHarness.OnUiThread(() => task = start());
        return task!;
    }

    [Fact]
    public async Task What_the_helper_freed_before_dropping_counts_once_when_the_local_pass_finishes()
    {
        using var worker = new DroppingWorker(1_500);
        var (viewModel, item) = CreateRun(new LocalPassService(new LoggingService(), 200, 200), worker);

        await OnUi(() => viewModel.RunCleanupAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1_700, viewModel.FreedBytes);
        Assert.Equal(1_700, item.FreedBytes);
    }

    [Fact]
    public async Task A_failing_local_pass_after_the_helper_dropped_leaves_nothing_counted()
    {
        using var worker = new DroppingWorker(1_500);
        var (viewModel, item) = CreateRun(new LocalPassService(new LoggingService(), 200, 0, fail: true), worker);

        await OnUi(() => viewModel.RunCleanupAsync()).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(CleanupTargetState.Skipped, item.RunState);
        Assert.Equal(0, viewModel.FreedBytes);
    }
}
