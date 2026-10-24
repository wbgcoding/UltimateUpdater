using System.Collections.Concurrent;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// An uninstall started while an install batch runs is its own concurrent queue run: it neither
/// cancels the batch nor waits for it, and the shared "installing" state ends only with the last of both.
/// </summary>
public partial class MainViewModelTests
{
    private static readonly TimeSpan ConcurrentWait = TimeSpan.FromSeconds(5);

    /// <summary>A source whose installs and uninstalls each wait on a gate the test opens (or fails) by
    /// hand, so "the batch is still running" is a state a test can hold for as long as it needs.</summary>
    private sealed class HoldSource : IUpdateSource
    {
        public const string SourceId = "gated-test-source";

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public string Id => SourceId;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public bool CanUninstall(UpdateItem item) => true;

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            RunGatedAsync("install", item, progress, cancellationToken);

        public Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            RunGatedAsync("uninstall", item, progress, cancellationToken);

        public int Calls(string action, string id) => _calls.GetValueOrDefault($"{action}:{id}");

        public void Release(string action, string id) => Gate(action, id).TrySetResult();

        public void Fail(string action, string id) =>
            Gate(action, id).TrySetException(new Exception("mock tool (exit code 1618)"));

        private TaskCompletionSource Gate(string action, string id) =>
            _gates.GetOrAdd($"{action}:{id}", _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private async Task RunGatedAsync(
            string action, UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            _calls.AddOrUpdate($"{action}:{item.Id}", 1, (_, n) => n + 1);
            progress.Report(new InstallProgress { PercentComplete = 5, StatusText = item.Name });
            // Progress<T> posts its report to the UI thread: a gate that is already open or faulted (a retry
            // round) must not finish before that report lands, or it would overwrite the final status.
            await Task.Delay(150, cancellationToken);
            await Gate(action, item.Id).Task.WaitAsync(cancellationToken);
        }
    }

    private static (MainViewModel ViewModel, HoldSource Source) CreateGated()
    {
        var source = new HoldSource();
        var viewModel = Create([source]);
        viewModel.UninstallConfirmOverride = () => true;
        return (viewModel, source);
    }

    private static UpdateRowViewModel AddGatedRow(MainViewModel viewModel, string id)
    {
        var row = new UpdateRowViewModel(Item(id, id, HoldSource.SourceId));
        viewModel.Updates.Add(row);
        return row;
    }

    private static bool InstallActivityRunning(MainViewModel viewModel) =>
        (bool)typeof(MainViewModel).GetField("_installActivityRunning", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;

    /// <summary>Starts a one-row batch and waits until the source is actually inside its install.</summary>
    private static void StartGatedBatch(MainViewModel viewModel, HoldSource source, UpdateRowViewModel row)
    {
        ((IRowActions)viewModel).InstallSingle(row);
        Pump(WaitUntilAsync(() => source.Calls("install", row.Item.Id) == 1, ConcurrentWait));
        Pump(WaitUntilAsync(() => row.InstallState == RowInstallState.Running, ConcurrentWait));
    }

    private static Task StartGatedUninstall(MainViewModel viewModel, HoldSource source, UpdateRowViewModel row)
    {
        var task = viewModel.UninstallRowAsync(row);
        Pump(WaitUntilAsync(() => source.Calls("uninstall", row.Item.Id) == 1, ConcurrentWait));
        Pump(WaitUntilAsync(() => row.InstallState == RowInstallState.Running, ConcurrentWait));
        return task;
    }

    [Fact]
    public void An_uninstall_during_a_running_batch_starts_and_finishes_while_the_batch_keeps_running()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var batchRow = AddGatedRow(viewModel, "batch.app");
            var removeRow = AddGatedRow(viewModel, "remove.app");

            StartGatedBatch(viewModel, source, batchRow);
            var uninstall = StartGatedUninstall(viewModel, source, removeRow);

            Assert.Equal(RowInstallState.Running, batchRow.InstallState);

            source.Release("uninstall", "remove.app");
            RunPumped(uninstall);

            Assert.Equal(RowInstallState.Succeeded, removeRow.InstallState);
            Assert.True(removeRow.WasUninstalled);
            // The batch was neither cancelled nor finished by the uninstall coming and going.
            Assert.Equal(RowInstallState.Running, batchRow.InstallState);
            Assert.True(viewModel.IsInstalling);
            Assert.Equal(0, source.Calls("uninstall", "batch.app"));

            source.Release("install", "batch.app");
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));
            Assert.Equal(RowInstallState.Succeeded, batchRow.InstallState);
        });
    }

    [Fact]
    public void An_uninstall_of_a_row_that_is_already_queued_or_running_is_refused()
    {
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var (viewModel, source) = CreateGated();
                var batchRow = AddGatedRow(viewModel, "batch.app");
                var removeRow = AddGatedRow(viewModel, "remove.app");
                var refusal = LocalizationService.Instance["UninstallBlockedByActivity"];

                StartGatedBatch(viewModel, source, batchRow);
                var uninstall = StartGatedUninstall(viewModel, source, removeRow);

                // The very row the uninstall is already working on.
                RunPumped(viewModel.UninstallRowAsync(removeRow));
                // The very row the batch is installing right now.
                RunPumped(viewModel.UninstallRowAsync(batchRow));

                Assert.Equal([refusal, refusal], warnings);
                Assert.Equal(1, source.Calls("uninstall", "remove.app"));
                Assert.Equal(0, source.Calls("uninstall", "batch.app"));
                Assert.Equal(RowInstallState.Running, batchRow.InstallState);

                source.Release("uninstall", "remove.app");
                RunPumped(uninstall);
                source.Release("install", "batch.app");
                Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
        }
    }

    [Fact]
    public void A_failing_batch_install_does_not_touch_the_uninstall_result()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var batchRow = AddGatedRow(viewModel, "batch.app");
            var removeRow = AddGatedRow(viewModel, "remove.app");

            StartGatedBatch(viewModel, source, batchRow);
            var uninstall = StartGatedUninstall(viewModel, source, removeRow);

            source.Release("uninstall", "remove.app");
            RunPumped(uninstall);
            Assert.Equal(RowInstallState.Succeeded, removeRow.InstallState);

            source.Fail("install", "batch.app");
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));

            Pump(WaitUntilAsync(() => batchRow.InstallState == RowInstallState.Failed, ConcurrentWait));
            Assert.Equal(RowInstallState.Failed, batchRow.InstallState);
            Assert.Equal(RowInstallState.Succeeded, removeRow.InstallState);
            Assert.True(viewModel.IsSecondAttemptActive, "the batch's own retry round is still announced");
        });
    }

    [Fact]
    public void A_failing_uninstall_does_not_touch_the_batch_result_or_announce_a_batch_retry()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var batchRow = AddGatedRow(viewModel, "batch.app");
            var removeRow = AddGatedRow(viewModel, "remove.app");

            StartGatedBatch(viewModel, source, batchRow);
            var uninstall = StartGatedUninstall(viewModel, source, removeRow);

            source.Fail("uninstall", "remove.app");
            RunPumped(uninstall);
            Pump(WaitUntilAsync(() => removeRow.InstallState == RowInstallState.Failed, ConcurrentWait));
            Assert.Equal(RowInstallState.Failed, removeRow.InstallState);
            Assert.Equal(RowInstallState.Running, batchRow.InstallState);

            source.Release("install", "batch.app");
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));

            Assert.Equal(RowInstallState.Succeeded, batchRow.InstallState);
            Assert.Equal(RowInstallState.Failed, removeRow.InstallState);
            // The uninstall's own retry round is not the batch's "second attempt".
            Assert.False(viewModel.IsSecondAttemptActive);
        });
    }

    [Fact]
    public void The_installing_state_stays_until_the_uninstall_ends_when_it_outlasts_the_batch()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var batchRow = AddGatedRow(viewModel, "batch.app");
            var removeRow = AddGatedRow(viewModel, "remove.app");
            var laterRow = AddGatedRow(viewModel, "later.app");

            StartGatedBatch(viewModel, source, batchRow);
            var uninstall = StartGatedUninstall(viewModel, source, removeRow);

            // The batch ends first; the uninstall is still going.
            source.Release("install", "batch.app");
            Pump(WaitUntilAsync(() => batchRow.InstallState == RowInstallState.Succeeded, ConcurrentWait));
            Pump(Task.Delay(300));

            Assert.True(viewModel.IsInstalling);
            Assert.True(InstallActivityRunning(viewModel));
            Assert.Equal(RowInstallState.Running, removeRow.InstallState);

            // A click that lands now queues behind the remaining work instead of racing it.
            ((IRowActions)viewModel).InstallSingle(laterRow);
            Pump(WaitUntilAsync(() => laterRow.InstallState == RowInstallState.Queued, ConcurrentWait));
            Pump(Task.Delay(300));
            Assert.Equal(0, source.Calls("install", "later.app"));
            Assert.True(viewModel.IsInstalling);

            // Only the end of the last piece of work clears the state and hands the queued row on.
            source.Release("uninstall", "remove.app");
            RunPumped(uninstall);
            Pump(WaitUntilAsync(() => source.Calls("install", "later.app") == 1, ConcurrentWait));
            Assert.True(viewModel.IsInstalling);

            source.Release("install", "later.app");
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));
            Assert.False(InstallActivityRunning(viewModel));
            Assert.Equal(RowInstallState.Succeeded, removeRow.InstallState);
            Assert.Equal(RowInstallState.Succeeded, laterRow.InstallState);
        });
    }

    [Fact]
    public void The_installing_state_stays_until_the_batch_ends_when_the_uninstall_finishes_first()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, source) = CreateGated();
            var batchRow = AddGatedRow(viewModel, "batch.app");
            var removeRow = AddGatedRow(viewModel, "remove.app");

            StartGatedBatch(viewModel, source, batchRow);
            var uninstall = StartGatedUninstall(viewModel, source, removeRow);
            source.Release("uninstall", "remove.app");
            RunPumped(uninstall);

            Assert.True(viewModel.IsInstalling);
            Assert.True(InstallActivityRunning(viewModel));

            source.Release("install", "batch.app");
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, ConcurrentWait));
            Assert.False(InstallActivityRunning(viewModel));
        });
    }
}
