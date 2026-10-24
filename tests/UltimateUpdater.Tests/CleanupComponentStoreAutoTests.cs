using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The component store analysis starts on its own wherever administrator rights are already there: in the
/// startup prescan, as soon as a window's scan has them, and when the helper starts after a prescan. It never
/// asks for them. The elevated helper is a scripted stand-in on a real pipe, so the tests see exactly which
/// jobs reach it; the view model lives on the harness's UI thread, like the real one.
/// </summary>
[Collection("UiLanguage")]
public class CleanupComponentStoreAutoTests
{
    private sealed record Call(string Command, string[] TargetIds, bool BackgroundPriority);

    /// <summary>A stand-in for the elevated worker: records every cleanup job and answers it with "done" once
    /// the matching gate (open by default) lets it.</summary>
    private sealed class FakeWorker : IDisposable
    {
        private readonly AnonymousPipeServerStream _workerToApp = new(PipeDirection.In);
        private readonly AnonymousPipeServerStream _appToWorker = new(PipeDirection.Out);
        private readonly StreamWriter _replies;
        private readonly StreamReader _commands;
        private readonly Lock _replyLock = new();

        public ConcurrentQueue<Call> Calls { get; } = new();

        private volatile TaskCompletionSource _scanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>From now on, scan jobs are only answered after <see cref="ReleaseScans"/>.</summary>
        public void HoldScans() => _scanGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseScans() => _scanGate.TrySetResult();

        private volatile TaskCompletionSource _analyzeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>From now on, analyze jobs are only answered after <see cref="ReleaseAnalyses"/>.</summary>
        public void HoldAnalyses() => _analyzeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseAnalyses() => _analyzeGate.TrySetResult();

        public FakeWorker()
        {
            _scanGate.SetResult();
            _analyzeGate.SetResult();
            var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, _workerToApp.ClientSafePipeHandle);
            _replies = new StreamWriter(workerEnd, new UTF8Encoding(false)) { AutoFlush = true };
            _commands = new StreamReader(
                new AnonymousPipeClientStream(PipeDirection.In, _appToWorker.ClientSafePipeHandle), Encoding.UTF8);
            _ = Task.Run(ServeAsync);
        }

        public void AttachTo(ElevatedHost host) => host.AttachSessionForTest(
            new StreamReader(_workerToApp, Encoding.UTF8),
            new StreamWriter(_appToWorker, new UTF8Encoding(false)) { AutoFlush = true });

        public bool Saw(string command) => Calls.Any(c => c.Command == command);

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
                    var payload = root.GetProperty("payload");
                    var command = payload.GetProperty("command").GetString()!;
                    var targets = payload.GetProperty("targetIds").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    var background = payload.TryGetProperty("backgroundPriority", out var flag) && flag.GetBoolean();
                    Calls.Enqueue(new Call(command, targets, background));
                    _ = Task.Run(() => AnswerAsync(id, command));
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the test is over
            }
        }

        private async Task AnswerAsync(string id, string command)
        {
            if (command == "scan")
                await _scanGate.Task;
            else if (command == "analyze")
                await _analyzeGate.Task;

            lock (_replyLock)
            {
                try
                {
                    _replies.WriteLine($"{{\"id\":\"{id}\",\"payload\":{{\"type\":\"done\"}}}}");
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // the test is over
                }
            }
        }

        public void Dispose()
        {
            ReleaseScans();
            ReleaseAnalyses();
            _replies.Dispose();
            _workerToApp.Dispose();
            _appToWorker.Dispose();
        }
    }

    private sealed record Rig(
        CleanupViewModel ViewModel, ElevatedHost Host, FakeWorker Worker, CleanupPrescanTests.RecordingCleanupService Service,
        Func<int> StartRequests) : IDisposable
    {
        public void Dispose() => Worker.Dispose();
    }

    /// <summary>The view model on the harness's UI thread. <paramref name="helperRunning"/> attaches the
    /// stand-in before the view model exists; otherwise the helper is down and a test attaches it later.
    /// Every call that would start a helper is counted; the stand-in is never "started", only attached.</summary>
    private static Rig CreateRig(bool helperRunning)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var service = new CleanupPrescanTests.RecordingCleanupService(logging);
        var host = new ElevatedHost(logging);
        var worker = new FakeWorker();
        var requests = 0;
        host.EnsureStartedOverrideForTest = _ =>
        {
            if (!host.IsRunning)
                Interlocked.Increment(ref requests);
            return Task.FromResult(host.IsRunning);
        };

        if (helperRunning)
            worker.AttachTo(host);

        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-cs-auto-").FullName, "settings.json"));
        CleanupViewModel? viewModel = null;
        WpfHarness.OnUiThread(() =>
        {
            viewModel = new CleanupViewModel(service, logging, settings, elevated: host);
            WpfHarness.WaitForCleanupCatalog(viewModel);
        });

        return new Rig(viewModel!, host, worker, service, () => Volatile.Read(ref requests));
    }

    private static Task OnUi(Func<Task> start)
    {
        Task? task = null;
        WpfHarness.OnUiThread(() => task = start());
        return task!;
    }

    private static void OnUi(Action action) => WpfHarness.OnUiThread(action);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), what);
    }

    private static string[] AdminIds(CleanupViewModel viewModel) =>
        viewModel.Items.Where(i => i.Target.NeedsElevation).Select(i => i.Target.Id).OrderBy(x => x).ToArray();

    [Fact]
    public async Task The_prescan_analyzes_the_component_store_when_the_helper_already_runs()
    {
        using var rig = CreateRig(helperRunning: true);

        await OnUi(() => rig.ViewModel.PrescanAsync());

        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");
        Assert.True(rig.Worker.Saw("analyze"));
        Assert.Equal(0, rig.StartRequests());
        Assert.False(rig.Host.WasDeclined);
    }

    [Fact]
    public async Task The_prescan_does_not_analyze_again_when_there_is_a_result()
    {
        using var rig = CreateRig(helperRunning: true);
        OnUi(() => rig.ViewModel.ComponentStoreResult = new ComponentStoreAnalysis(1, null, null, null));

        await OnUi(() => rig.ViewModel.PrescanAsync());
        await Task.Delay(300);

        Assert.False(rig.Worker.Saw("analyze"));
        Assert.True(rig.Worker.Saw("scan"));
    }

    [Fact]
    public async Task The_prescan_without_a_helper_neither_analyzes_nor_asks()
    {
        using var rig = CreateRig(helperRunning: false);

        await OnUi(() => rig.ViewModel.PrescanAsync());
        await Task.Delay(300);

        Assert.Null(rig.ViewModel.ComponentStoreResult);
        Assert.Equal(0, rig.StartRequests());
        Assert.False(rig.Host.IsRunning);
        Assert.False(rig.Host.WasDeclined);
        Assert.Empty(rig.Worker.Calls);
    }

    [Fact]
    public async Task A_window_scan_starts_the_analysis_before_the_scan_has_finished()
    {
        using var rig = CreateRig(helperRunning: true);
        rig.Worker.HoldScans();
        OnUi(() => rig.ViewModel.WindowAttached());

        var scan = OnUi(() => rig.ViewModel.StartOpenScanAsync());
        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");

        // The admin scan is still being held at the door: the analysis did not wait for it.
        Assert.False(scan.IsCompleted);
        Assert.True(rig.Worker.Saw("scan"));
        rig.Worker.ReleaseScans();
        await scan.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, rig.StartRequests());
    }

    [Fact]
    public async Task A_helper_that_starts_after_the_prescan_gets_its_targets_measured_and_the_analysis_run()
    {
        using var rig = CreateRig(helperRunning: false);
        await OnUi(() => rig.ViewModel.PrescanAsync());
        var admin = AdminIds(rig.ViewModel);
        Assert.NotEmpty(admin);
        Assert.Empty(rig.Worker.Calls);
        Assert.Null(rig.ViewModel.ComponentStoreResult);

        rig.Worker.AttachTo(rig.Host);

        await WaitUntil(
            () => rig.Worker.Saw("analyze") && rig.Worker.Calls.Any(c => c.Command == "scan"),
            "the late pass reached the helper");
        var scan = rig.Worker.Calls.Single(c => c.Command == "scan");
        Assert.Equal(admin, scan.TargetIds.OrderBy(x => x).ToArray());
        Assert.True(scan.BackgroundPriority, "nobody is looking: the late pass stays quiet");
        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");
        Assert.Equal(0, rig.StartRequests());
        Assert.False(rig.Host.WasDeclined);
    }

    [Fact]
    public async Task A_helper_that_starts_with_a_window_open_gets_the_analysis_but_no_late_pass()
    {
        using var rig = CreateRig(helperRunning: false);
        await OnUi(() => rig.ViewModel.PrescanAsync());
        OnUi(() => rig.ViewModel.WindowAttached());

        rig.Worker.AttachTo(rig.Host);

        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");
        await Task.Delay(300);
        Assert.False(rig.Worker.Saw("scan"), "an open window runs its own pass");
        Assert.Equal(0, rig.StartRequests());
    }

    [Fact]
    public async Task A_helper_that_starts_without_a_prescan_gets_the_analysis_only()
    {
        using var rig = CreateRig(helperRunning: false);

        rig.Worker.AttachTo(rig.Host);

        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");
        await Task.Delay(300);
        Assert.False(rig.Worker.Saw("scan"));
        Assert.Equal(0, rig.StartRequests());
    }

    [Fact]
    public async Task A_quiet_analysis_that_cannot_reach_the_helper_asks_for_nothing()
    {
        using var rig = CreateRig(helperRunning: false);

        // Reports as running, but there is no session behind it: the job cannot be sent.
        rig.Host.SetRunningForTest(true);

        await Task.Delay(600);
        Assert.Null(rig.ViewModel.ComponentStoreResult);
        Assert.False(rig.Host.WasDeclined);
    }

    [Fact]
    public async Task A_running_analysis_is_busy_and_marks_only_the_component_row()
    {
        using var rig = CreateRig(helperRunning: true);
        rig.Worker.HoldAnalyses();

        await OnUi(() => rig.ViewModel.PrescanAsync());
        await WaitUntil(() => rig.ViewModel.IsAnalyzingComponentStore, "the analysis started");

        Assert.True(rig.ViewModel.IsBusy);
        Assert.True(rig.ViewModel.Items.Single(i => i.IsComponentCleanup).IsAnalyzingComponentStore);
        Assert.True(rig.ViewModel.Items.Single(i => i.IsComponentCleanup).HasSizeCell);
        Assert.DoesNotContain(rig.ViewModel.Items.Where(i => !i.IsComponentCleanup), i => i.IsAnalyzingComponentStore);

        rig.Worker.ReleaseAnalyses();
        await WaitUntil(() => rig.ViewModel.ComponentStoreResult is not null, "the analysis finished");

        Assert.False(rig.ViewModel.IsAnalyzingComponentStore);
        Assert.False(rig.ViewModel.Items.Single(i => i.IsComponentCleanup).IsAnalyzingComponentStore);
    }
}
