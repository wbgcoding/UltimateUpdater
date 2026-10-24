using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>Blocks the FIRST install it receives until the test releases it and records every item that
    /// reaches <c>InstallAsync</c>, so a test can prove which queued rows really ran and in what order.</summary>
    private sealed class FirstInstallGatedSource(string id = "gated-first-source") : IUpdateSource
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public List<string> Installed { get; } = [];

        public Task FirstStarted => _firstStarted.Task;

        public void Release() => _gate.TrySetResult();

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            lock (Installed)
                Installed.Add(item.Id);

            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstStarted.TrySetResult();
                await _gate.Task;
            }

            progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
        }
    }

    [Fact]
    public void A_row_queued_during_a_running_batch_can_be_removed_before_it_runs()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FirstInstallGatedSource();
            var viewModel = Create(sources: [source]);
            var rows = new[] { "a", "b", "c", "d" }
                .Select(id => new UpdateRowViewModel(Item(id, id.ToUpperInvariant(), source.Id))).ToList();
            viewModel.Updates.AddRange(rows);

            InvokeEnqueueInstall(viewModel, [rows[0]]);
            Pump(source.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10)));

            InvokeEnqueueInstall(viewModel, [rows[1], rows[2]]);
            InvokeEnqueueInstall(viewModel, [rows[3]]);
            Assert.Contains(rows[3], viewModel.QueueRows);

            viewModel.RemoveFromQueueCommand.Execute(rows[3]);

            Assert.DoesNotContain(rows[3], viewModel.QueueRows);
            Assert.Equal(RowInstallState.Idle, rows[3].InstallState);

            source.Release();
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, TimeSpan.FromSeconds(10)));

            Assert.Equal(["a", "b", "c"], source.Installed.OrderBy(id => id, StringComparer.Ordinal));
        });
    }

    /// <summary>A row the table folded two sources into queues both installations but shows one queue
    /// line; removing that line must take the hidden copy out of the queue too, not leave it to install
    /// on its own.</summary>
    [Fact]
    public void Removing_a_folded_row_from_the_queue_also_removes_its_hidden_copy()
    {
        WpfHarness.OnUiThread(() =>
        {
            // Both lanes busy, so the folded row's two copies wait in the queue instead of starting at once.
            var source = new FirstInstallGatedSource();
            var pip = new FirstInstallGatedSource("pip");
            var viewModel = Create(sources: [source, pip]);
            var first = new UpdateRowViewModel(Item("a", "A", source.Id));
            var pipBusy = new UpdateRowViewModel(Item("busy", "Busy", "pip"));
            var copies = new[] { new UpdateRowViewModel(Item("d", "D", source.Id)), new UpdateRowViewModel(Item("d", "D", "pip")) };
            viewModel.Updates.AddRange([first, pipBusy, .. copies]);

            InvokeEnqueueInstall(viewModel, [first, pipBusy]);
            Pump(source.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10)));
            Pump(pip.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10)));

            var shown = Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>(), r => copies.Contains(r));
            var hidden = copies.Single(r => r != shown);
            InvokeEnqueueInstall(viewModel, [shown]);
            Assert.Equal(RowInstallState.Queued, hidden.InstallState);
            Assert.DoesNotContain(hidden, viewModel.QueueRows);

            viewModel.RemoveFromQueueCommand.Execute(shown);

            Assert.Equal(RowInstallState.Idle, shown.InstallState);
            Assert.Equal(RowInstallState.Idle, hidden.InstallState);

            source.Release();
            pip.Release();
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, TimeSpan.FromSeconds(10)));

            Assert.Equal(["a"], source.Installed);
            Assert.Equal(["busy"], pip.Installed);
            Assert.Equal(RowInstallState.Idle, hidden.InstallState);
        });
    }

    [Fact]
    public void Reordering_the_queue_panel_reorders_rows_that_are_still_pending()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new FirstInstallGatedSource();
            var viewModel = Create(sources: [source]);
            var rows = new[] { "a", "b", "c" }
                .Select(id => new UpdateRowViewModel(Item(id, id.ToUpperInvariant(), source.Id))).ToList();
            viewModel.Updates.AddRange(rows);

            InvokeEnqueueInstall(viewModel, [rows[0]]);
            Pump(source.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10)));
            InvokeEnqueueInstall(viewModel, [rows[1], rows[2]]);

            var cIndex = viewModel.QueueRows.IndexOf(rows[2]);
            var bIndex = viewModel.QueueRows.IndexOf(rows[1]);
            viewModel.MoveQueueRow(cIndex, bIndex);

            source.Release();
            Pump(WaitUntilAsync(() => !viewModel.IsInstalling, TimeSpan.FromSeconds(10)));

            Assert.Equal(["a", "c", "b"], source.Installed);
        });
    }
}
