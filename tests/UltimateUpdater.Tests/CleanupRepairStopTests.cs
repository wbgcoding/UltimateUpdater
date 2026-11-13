using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Stop and a system repair: a repair that has not begun (its job still waits for the helper) must never
/// start afterwards and must not claim "the running repair has to finish"; a repair that is really running
/// keeps running to its end and says so.
/// </summary>
[Collection("UiLanguage")]
public class CleanupRepairStopTests
{
    /// <summary>
    /// A helper stand-in for the repair job. With <c>begins</c> it reports its first step at once (the repair
    /// is running); without it the job just sits queued. Either way it answers a stop the way the real worker
    /// does: a queued job ends with only "done", a running repair finishes first and then sends its result.
    /// </summary>
    private sealed class RepairWorker : IDisposable
    {
        private readonly AnonymousPipeServerStream _workerToApp = new(PipeDirection.In);
        private readonly AnonymousPipeServerStream _appToWorker = new(PipeDirection.Out);
        private readonly StreamWriter _replies;
        private readonly StreamReader _commands;
        private readonly bool _begins;
        private string? _repairJobId;

        public TaskCompletionSource RepairJobSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The test lets a running repair end by completing this.</summary>
        public TaskCompletionSource FinishRepair { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool RepairBegan;
        public volatile bool StopReceived;

        public RepairWorker(bool begins)
        {
            _begins = begins;
            var workerEnd = new AnonymousPipeClientStream(PipeDirection.Out, _workerToApp.ClientSafePipeHandle);
            _replies = new StreamWriter(workerEnd, new UTF8Encoding(false)) { AutoFlush = true };
            _commands = new StreamReader(
                new AnonymousPipeClientStream(PipeDirection.In, _appToWorker.ClientSafePipeHandle), Encoding.UTF8);
            _ = Task.Run(ServeAsync);
        }

        public void AttachTo(ElevatedHost host) => host.AttachSessionForTest(
            new StreamReader(_workerToApp, Encoding.UTF8),
            new StreamWriter(_appToWorker, new UTF8Encoding(false)) { AutoFlush = true });

        private void Reply(string id, string payload) =>
            _replies.WriteLine($"{{\"id\":\"{id}\",\"payload\":{payload}}}");

        private async Task ServeAsync()
        {
            try
            {
                while (await _commands.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var kind = root.GetProperty("kind").GetString();
                    var payload = root.GetProperty("payload");

                    if (kind == "control")
                    {
                        if (payload.GetProperty("action").GetString() == "cancel"
                            && payload.GetProperty("jobId").GetString() == _repairJobId)
                        {
                            StopReceived = true;
                            _ = Task.Run(() => AnswerStopAsync(_repairJobId!));
                        }

                        continue;
                    }

                    var id = root.GetProperty("id").GetString()!;
                    if (kind != "cleanup-repair")
                    {
                        Reply(id, "{\"type\":\"done\"}");
                        continue;
                    }

                    _repairJobId = id;
                    if (_begins)
                    {
                        RepairBegan = true;
                        Reply(id, "{\"type\":\"progress\",\"percent\":0,\"freed\":0,\"current\":\"DISM\"}");
                    }

                    RepairJobSeen.TrySetResult();
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the test is over
            }
        }

        private async Task AnswerStopAsync(string id)
        {
            if (RepairBegan)
            {
                // Never interrupted: the tool runs on, and only then does the job report.
                await FinishRepair.Task;
                Reply(id, "{\"type\":\"result\",\"id\":\"system-repair-dism\"}");
            }

            Reply(id, "{\"type\":\"done\"}");
        }

        public void Dispose()
        {
            FinishRepair.TrySetResult();
            _replies.Dispose();
            _workerToApp.Dispose();
            _appToWorker.Dispose();
        }
    }

    private static (CleanupViewModel ViewModel, CleanupItemViewModel Item) CreateRun(RepairWorker worker)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;
        var logging = new LoggingService();
        var host = new ElevatedHost(logging);
        worker.AttachTo(host);
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-repair-stop-s-").FullName, "settings.json"));
        settings.Current.Install.CreateRestorePoint = false;
        var history = new CleanupHistoryService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-repair-stop-h-").FullName, "history.json"));

        CleanupViewModel? viewModel = null;
        WpfHarness.OnUiThread(() =>
        {
            viewModel = new CleanupViewModel(new CleanupService(logging), logging, settings, history: history, elevated: host);
            WpfHarness.WaitForCleanupCatalog(viewModel);
        });

        var item = viewModel!.Items.First(i => i.Target.Id == "system-repair-dism");
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Stop_before_the_repair_began_never_starts_it_and_shows_no_must_finish_note()
    {
        using var worker = new RepairWorker(begins: false);
        var (viewModel, item) = CreateRun(worker);
        var noteEverShown = false;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupViewModel.IsStoppingRepair) && viewModel.IsStoppingRepair)
                noteEverShown = true;
        };

        Task? run = null;
        WpfHarness.OnUiThread(() => run = viewModel.RunCleanupAsync());
        await worker.RepairJobSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntil(() => item.IsCleaning);

        WpfHarness.OnUiThread(() => viewModel.StopCommand.Execute(null));
        Assert.False(viewModel.IsStoppingRepair);

        await run!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(worker.StopReceived);
        Assert.False(worker.RepairBegan);
        Assert.False(noteEverShown);
        Assert.False(viewModel.IsStoppingRepair);
        Assert.Equal(CleanupTargetState.Stopped, item.RunState);
        Assert.True(viewModel.WasStopped);
    }

    [Fact]
    public async Task Stop_while_the_repair_runs_keeps_it_going_and_shows_the_note_until_it_ends()
    {
        using var worker = new RepairWorker(begins: true);
        var (viewModel, item) = CreateRun(worker);

        Task? run = null;
        WpfHarness.OnUiThread(() => run = viewModel.RunCleanupAsync());
        await worker.RepairJobSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntil(() => item.IsCleaning);

        // The first report is posted to the UI thread; wait for it like the user would.
        await WaitUntil(() => item.DetailText == "DISM");

        WpfHarness.OnUiThread(() => viewModel.StopCommand.Execute(null));
        Assert.True(viewModel.IsStoppingRepair);

        await WaitUntil(() => worker.StopReceived);
        await Task.Delay(200);
        Assert.False(run!.IsCompleted);
        Assert.True(viewModel.IsStoppingRepair);

        worker.FinishRepair.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        // The repair ran to its end and reported; the note is gone with the run.
        Assert.False(viewModel.IsStoppingRepair);
        Assert.Equal(CleanupPhase.Finished, viewModel.Phase);
    }

    /// <summary>The worker side: a stop that is already in force when the job gets to run ends it before any
    /// repair tool is launched.</summary>
    private sealed class CountingService(LoggingService logging) : CleanupService(logging)
    {
        public int CleanCalls;

        public override Task<CleanupOutcome> CleanAsync(
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
            Interlocked.Increment(ref CleanCalls);
            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }
    }

    [Fact]
    public async Task The_worker_does_not_launch_a_repair_whose_job_was_stopped_before_it_began()
    {
        var logging = new LoggingService();
        var service = new CountingService(logging);
        var command = new CleanupCommand { Command = "clean", Language = "en", TargetIds = ["system-repair-dism"] };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };

        await CleanupWorker.RunCommandAsync(
            command, writer, "job-1", logging, cancelled.Token, new PauseGate(), service);

        Assert.Equal(0, service.CleanCalls);
    }

    [Fact]
    public async Task A_queued_repair_job_ends_when_the_stop_arrives_without_waiting_for_its_slot()
    {
        // Another repair-kind job holds the lane; the second one must leave on the stop alone.
        var id = Guid.NewGuid().ToString("N");
        var job = new ElevatedJob { Id = id, Kind = "cleanup-repair" };
        using var cts = new CancellationTokenSource();

        var held = await ElevatedWorker.AcquireJobSlotsAsync(job, () => { }, CancellationToken.None);
        try
        {
            var second = ElevatedWorker.AcquireJobSlotsAsync(job, () => { }, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        finally
        {
            await held.DisposeAsync();
        }
    }
}
