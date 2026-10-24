using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>
    /// The same update from two sources is ONE row, and the better-ranked source keeps it (real case,
    /// 2026-07-26: graphify through pip and through uv's tools at the same time). Both sources are named on the row that
    /// stays, so nothing is hidden.
    /// </summary>
    [Fact]
    public void The_same_update_from_two_sources_becomes_one_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("graphify", "graphify", "pip")),
                new UpdateRowViewModel(Item("graphify", "graphify", "uv-tool")),
            ]);

            var visible = viewModel.UpdatesView.Cast<UpdateRowViewModel>().ToList();

            var row = Assert.Single(visible);
            Assert.Equal("uv-tool", row.SourceId);          // uv owns the tools it installed
            Assert.True(row.HasAlternateSources);
            Assert.Equal("uv tools · pip", row.AllSourcesText);
        });
    }

    /// <summary>
    /// A folded row stands for a REAL installation per source, so updating it must update every one of
    /// them, updating only the best-ranked source would leave the other copy behind, and the next scan
    /// would show the same version again from the source that was skipped.
    /// </summary>
    [Fact]
    public void Installing_a_merged_row_installs_every_source_it_stands_for()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("graphify", "graphify", "pip")),
                new UpdateRowViewModel(Item("graphify", "graphify", "uv-tool")),
            ]);

            var row = Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
            row.IsSelected = true;

            // Blocker detection now takes its process snapshot off the UI thread, so the command's own
            // ExecuteAsync must be pumped to completion rather than assumed done once Execute returns.
            Pump(((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)viewModel.InstallSelectedCommand).ExecuteAsync(null));

            // BOTH real installations are updated, they are two separate copies of the package. Asserted as
            // "no longer idle" rather than "queued": the mock source finishes instantly, so a row may already
            // have run to completion by the time this reads it.
            var touched = viewModel.Updates
                .Where(r => r.InstallState != RowInstallState.Idle)
                .Select(r => r.SourceId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["pip", "uv-tool"], touched);

            // … but the strip shows ONE line, the same one the table shows.
            var shown = Assert.Single(viewModel.InstallBatch);
            Assert.Equal("uv-tool", shown.SourceId);
        });
    }

    /// <summary>
    /// Only the rows handed to ONE call are sorted among themselves, critical first; a STABLE
    /// sort, so two rows tied on IsCritical keep whatever order they arrived in. Invoked directly
    /// (reflection, same style already used elsewhere in this file for a private method) so the assert
    /// runs on the deterministic enqueue step, before the fire-and-forget install loop can touch anything.
    /// </summary>
    [Fact]
    public void A_mixed_batch_puts_critical_rows_first_and_keeps_relative_order_otherwise()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create(sources: [new InstantSource()]);
            const string sourceId = "instant-test-source";
            var a = new UpdateRowViewModel(Item("a", "A", sourceId));
            var b = new UpdateRowViewModel(Item("b", "B", sourceId, critical: true));
            var c = new UpdateRowViewModel(Item("c", "C", sourceId));
            var d = new UpdateRowViewModel(Item("d", "D", sourceId, critical: true));
            viewModel.Updates.AddRange([a, b, c, d]);

            InvokeEnqueueInstall(viewModel, [a, b, c, d]);

            Assert.Equal(["b", "d", "a", "c"], viewModel.InstallBatch.Select(r => r.Item.Id));
        });
    }

    /// <summary>A row queued while a batch is already running only appends behind whatever is
    /// already there - nothing about an already-running batch is ever re-sorted. "Already running" is
    /// simulated directly on the private flag (reflection): the real flag only actually flips once
    /// <c>RunInstallLoopAsync</c>'s own fire-and-forget task gets scheduled, which is not deterministic
    /// enough for this assert to race against.</summary>
    [Fact]
    public void A_row_added_while_a_batch_is_running_only_appends_behind_it()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create(sources: [new InstantSource()]);
            const string sourceId = "instant-test-source";
            var running = new UpdateRowViewModel(Item("running", "Running", sourceId, critical: false));
            var joining = new UpdateRowViewModel(Item("joining", "Joining", sourceId, critical: true));
            viewModel.Updates.AddRange([running, joining]);

            InvokeEnqueueInstall(viewModel, [running]);
            Assert.Equal(["running"], viewModel.InstallBatch.Select(r => r.Item.Id));

            SetInstallActivityRunning(viewModel, true);
            try
            {
                // A critical row joining a batch that is already under way must still land AFTER the row
                // already running, never jump ahead of it just because it outranks it.
                InvokeEnqueueInstall(viewModel, [joining]);

                Assert.Equal(["running", "joining"], viewModel.InstallBatch.Select(r => r.Item.Id));
            }
            finally
            {
                SetInstallActivityRunning(viewModel, false);
            }
        });
    }

    private static void SetInstallActivityRunning(MainViewModel viewModel, bool value) =>
        typeof(MainViewModel).GetField("_installActivityRunning", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, value);

    /// <summary>Reflection seam for <c>MainViewModel.EnqueueInstallAsync</c> (private), same style already
    /// used for <c>TrackPersistentFailure</c> elsewhere in this file.</summary>
    private static void InvokeEnqueueInstall(MainViewModel viewModel, IReadOnlyList<UpdateRowViewModel> rows)
    {
        var method = typeof(MainViewModel).GetMethod("EnqueueInstallAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(viewModel, [rows])!;

        // Blocker detection now takes its process snapshot off the UI thread: a blocking GetResult() here,
        // on the same dispatcher thread the async continuation needs to resume on, would deadlock. Pumping
        // a nested frame lets that continuation actually run while this call waits for it.
        Pump(task);
    }

    /// <summary>
    /// The user-scope and admin halves used to go out as two SEPARATE <c>InstallQueue.RunAsync</c> calls,
    /// each with its own automatic retry round, so a fast user-scope failure could retry before the admin
    /// half of the SAME batch had even finished its first attempt, even though the spec ("one automatic
    /// second attempt at the end of a stapel") means batch end, not half end. Built with a mock-session
    /// queue so a row whose source id LOOKS admin-needing ("winget", per the queue panel's own static
    /// <see cref="InstallQueue.NeedsElevation"/> split) still runs entirely in-process here, with no real
    /// UAC prompt, <see cref="InstallQueueTests.A_mock_session_never_elevates_an_elevation_needing_source_id"/>
    /// is the same technique. The admin row blocks on a gate the test controls; the user-scope row fails
    /// once with a retryable code. Before the fix, the user-scope retry fires and finishes while the admin
    /// row is still blocked on its own first attempt. After the fix, both rows go out in ONE call, so
    /// InstallQueue's own retry round cannot start until BOTH halves have finished their first attempt,
    /// the admin row's gate release must come before the user-scope retry, not after.
    /// </summary>
    [Fact]
    public void A_user_scope_retry_waits_for_the_admin_halfs_first_attempt_to_finish()
    {
        WpfHarness.OnUiThread(() =>
        {
            var wingetGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wingetAttemptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var npmAttempt2Done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var order = new List<string>();
            var orderLock = new object();
            void Record(string what)
            {
                lock (orderLock)
                    order.Add(what);
            }

            var winget = new GatedAdminSource("winget", wingetAttemptStarted, wingetGate, Record);
            var npm = new RetryOnceSource("npm-retry-test", Record, npmAttempt2Done);

            var logging = new LoggingService();
            var temp = Directory.CreateTempSubdirectory("wu-batch-retry-test-").FullName;
            var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
            settings.Current.Ui.Language = "en";
            var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
            var sources = new IUpdateSource[] { winget, npm };
            var installQueue = new InstallQueue(sources, settings, history, logging, isMockSession: true);

            var viewModel = new MainViewModel(
                settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
                new IconCacheService(logging), installQueue, history,
                new AutostartService(logging), new NotificationService(settings, logging),
                new Services.SelfUpdate.SelfUpdateService(
                    new HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
                sources);

            var wingetRow = new UpdateRowViewModel(Item("winget-pkg", "Winget Package", "winget"));
            var npmRow = new UpdateRowViewModel(Item("npm-pkg", "Npm Package", "npm-retry-test"));
            viewModel.Updates.AddRange([wingetRow, npmRow]);

            InvokeEnqueueInstall(viewModel, [wingetRow, npmRow]);

            // The admin row's own first attempt must have genuinely started before this proves anything.
            Pump(wingetAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            // The real, timing-independent guard: winget's own admin row is still blocked on its first
            // attempt here (wingetGate is not yet released), so npm's retry cannot have recorded its
            // second attempt yet either, since a correct implementation only starts the retry round once
            // BOTH halves of the same RunAsync call have finished their first pass. A prior version of
            // this check polled for up to 30 dispatcher ticks and asserted npmAttempt2Done was still
            // pending, which is exactly the kind of margin that can flake under a loaded machine without
            // the production code being wrong; the order assertion below needs no such margin, because it
            // only inspects events that already happened by the time both tasks are awaited to completion.
            Assert.DoesNotContain("npm-attempt-2", order);

            wingetGate.SetResult();
            Pump(npmAttempt2Done.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Contains("winget-done", order);
            Assert.Contains("npm-attempt-2", order);
            Assert.True(
                order.IndexOf("winget-done") < order.IndexOf("npm-attempt-2"),
                $"expected winget-done before npm-attempt-2, got: {string.Join(", ", order)}");
        });
    }

    /// <summary>A row queued while another row is still installing starts at once on its own lane; it
    /// used to wait for the whole first run (retry round included) before it even began.</summary>
    [Fact]
    public void A_row_queued_while_another_installs_starts_at_once_on_its_own_lane()
    {
        WpfHarness.OnUiThread(() =>
        {
            var slowGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            void Record(string what)
            {
                lock (order)
                    order.Add(what);
            }

            var slow = new GatedAdminSource("slow-test-source", slowStarted, slowGate, Record);
            var viewModel = Create(sources: [slow, new InstantSource()]);
            var slowRow = new UpdateRowViewModel(Item("slow", "Slow", "slow-test-source"));
            var fastRow = new UpdateRowViewModel(Item("fast", "Fast", "instant-test-source"));
            viewModel.Updates.AddRange([slowRow, fastRow]);

            var fastDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fastRow.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UpdateRowViewModel.InstallState) && fastRow.InstallState == RowInstallState.Succeeded)
                    fastDone.TrySetResult();
            };

            InvokeEnqueueInstall(viewModel, [slowRow]);
            Pump(slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            InvokeEnqueueInstall(viewModel, [fastRow]);
            try
            {
                Pump(fastDone.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.DoesNotContain("winget-done", order);
                Assert.NotEqual(RowInstallState.Succeeded, slowRow.InstallState);
            }
            finally
            {
                slowGate.TrySetResult();
            }

            Pump(Task.Run(async () =>
            {
                while (viewModel.IsInstalling)
                    await Task.Delay(20);
            }).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("winget-done", order);
        });
    }

    /// <summary>Windows' own OpenSSH and WSL share Git's short process names; closing for Git's setup
    /// leaves anything that runs from the Windows folder alone.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\OpenSSH\ssh.exe", true)]
    [InlineData(@"C:\Windows\System32\bash.exe", true)]
    [InlineData(@"C:\Program Files\Git\usr\bin\ssh.exe", false)]
    [InlineData(@"C:\WindowsApps\bash.exe", false)]
    [InlineData(null, false)]
    public void Only_processes_from_the_windows_folder_are_exempt_from_closing(string? path, bool exempt) =>
        Assert.Equal(exempt, MainViewModel.IsUnderWindowsFolder(path, @"C:\Windows"));

    /// <summary>A process that cannot be queried is not proof that it is gone: the blocker check fails
    /// closed so the row stays blocked instead of installing over a live program.</summary>
    [Fact]
    public void A_process_that_cannot_be_queried_counts_as_still_running()
    {
        Assert.False(MainViewModel.SafeHasExited(() => throw new System.ComponentModel.Win32Exception(5)));
        Assert.False(MainViewModel.SafeHasExited(() => throw new InvalidOperationException()));
        Assert.True(MainViewModel.SafeHasExited(() => true));
        Assert.False(MainViewModel.SafeHasExited(() => false));
    }

    /// <summary>Blocks on its own gate before reporting success, and tells the test when its one and only
    /// attempt actually started.</summary>
    private sealed class GatedAdminSource(
        string id, TaskCompletionSource started, TaskCompletionSource gate, Action<string> record) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            record("winget-start");
            started.TrySetResult();
            await gate.Task;
            record("winget-done");
            progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
        }
    }

    /// <summary>Fails its first attempt with a code the explainer table marks safe to retry blindly, then
    /// succeeds on the automatic retry round, signalling the test once that second attempt is done.</summary>
    private sealed class RetryOnceSource(string id, Action<string> record, TaskCompletionSource attempt2Done) : IUpdateSource
    {
        private int _attempts;

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                record("npm-attempt-1");
                throw new InvalidOperationException("npm: install failed for 'npm-pkg'. (exit code 1618)");
            }

            record("npm-attempt-2");
            progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
            attempt2Done.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Two installations at DIFFERENT versions are two different pieces of work and keep their own rows,
    /// folding them would leave one of them silently behind.
    /// </summary>
    [Fact]
    public void Two_installations_at_different_versions_keep_their_rows()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();

            var choco = Item("git", "git", "chocolatey");
            var scoop = new UpdateItem
            {
                Id = "git",
                SourceId = "scoop",
                Category = UpdateCategory.Software,
                Name = "git",
                InstalledVersion = "1.5",       // a different installation, at its own version
                AvailableVersion = "2.0",
            };

            viewModel.Updates.AddRange([new UpdateRowViewModel(choco), new UpdateRowViewModel(scoop)]);

            Assert.Equal(2, viewModel.UpdatesView.Cast<UpdateRowViewModel>().Count());
        });
    }

    /// <summary>
    /// A source switched off must not win a group from off-screen and take the visible row with it: the
    /// remaining source keeps its own line.
    /// </summary>
    [Fact]
    public void A_disabled_source_does_not_swallow_the_row_that_is_still_shown()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            viewModel.Updates.AddRange(
            [
                new UpdateRowViewModel(Item("graphify", "graphify", "pip")),
                new UpdateRowViewModel(Item("graphify", "graphify", "uv-tool")),
            ]);

            viewModel.ToggleSourceCommand.Execute("uv-tool");

            var row = Assert.Single(viewModel.UpdatesView.Cast<UpdateRowViewModel>());
            Assert.Equal("pip", row.SourceId);
            Assert.False(row.HasAlternateSources);
        });
    }

    /// <summary>
    /// A source that reports the SAME update twice, the second time with a different version, which is
    /// what Windows Update does often enough to matter. The row created for the first report is not yet
    /// in Updates when the second arrives, so an update path that only removes-then-re-adds from Updates
    /// would miss it, leaving both copies in the table.
    /// </summary>
    [Fact]
    public void An_update_reported_twice_in_one_scan_produces_one_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new TwiceSource()]);

            // Everything stays on the dispatcher thread: the table is a CollectionView, which refuses to be
            // changed from anywhere else. The frame keeps pumping until the scan's own finish has drained
            // what the source reported.
            var scan = viewModel.ScanCommand.ExecuteAsync(null);
            var frame = new System.Windows.Threading.DispatcherFrame();
            _ = scan.ContinueWith(
                _ => frame.Continue = false,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.FromCurrentSynchronizationContext());
            System.Windows.Threading.Dispatcher.PushFrame(frame);

            Assert.Single(viewModel.Updates, r => r.SourceId == "windowsupdate");
            Assert.Equal("2.1", viewModel.Updates.Single(r => r.SourceId == "windowsupdate").AvailableVersion);
        });
    }

    private sealed class TwiceSource : IUpdateSource
    {
        public string Id => "windowsupdate";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Item("KB5041585", "Security update", "windowsupdate");

            yield return new UpdateItem
            {
                Id = "KB5041585",
                SourceId = "windowsupdate",
                Category = UpdateCategory.Software,
                Name = "Security update",
                InstalledVersion = "1.0",
                AvailableVersion = "2.1",
            };

            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Scan, tick a row, stop, scan again straight away: a cancelled scan is abandoned rather than
    /// waited for, so its provider could still be running when the next scan starts the SAME source
    /// again. Each source is scanned one run at a time and every event carries the scan it belongs to,
    /// so a straggler's finds cannot land in the new scan's table.
    /// </summary>
    [Fact]
    public void A_rescan_started_before_the_previous_one_settles_still_produces_one_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new SlowWindowsUpdateSource();
            var viewModel = Create([source]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            // The user picks the row, the state the duplicate always appeared next to.
            var row = Assert.Single(viewModel.Updates);
            row.IsSelected = true;

            // Stop, then scan again immediately: the previous run is still inside its own untokened wait.
            var stopped = viewModel.ScanCommand.ExecuteAsync(null);
            viewModel.CancelScanCommand.Execute(null);
            var rescan = viewModel.ScanCommand.ExecuteAsync(null);

            Pump(Task.WhenAll(stopped, rescan));

            Assert.Single(viewModel.Updates);
            Assert.Single(viewModel.Updates, r => r.SourceId == "windowsupdate");

            // and the source itself was never asked to scan twice at once
            Assert.Equal(1, source.MaxConcurrentRuns);
        });
    }

    /// <summary>
    /// FinishScanUi runs inside ScanAsync's finally block, but used to call _scanGate.Release() as a
    /// separate statement after it: a throw anywhere inside FinishScanUi (here, a ScanCompleted
    /// subscriber) propagated straight past that Release() call, leaving the gate held forever and
    /// hanging every scan after this one.
    /// </summary>
    [Fact]
    public void A_throwing_ScanCompleted_handler_still_releases_the_scan_gate()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            void ThrowOnCompleted() => throw new InvalidOperationException("scan completed handler failure");
            viewModel.ScanCompleted += ThrowOnCompleted;

            var scan = viewModel.ScanCommand.ExecuteAsync(null);
            Pump(scan);

            Assert.True(scan.IsFaulted);
            viewModel.ScanCompleted -= ThrowOnCompleted;

            var gateField = typeof(MainViewModel).GetField("_scanGate", BindingFlags.NonPublic | BindingFlags.Instance);
            var gate = (SemaphoreSlim)gateField!.GetValue(viewModel)!;
            Assert.Equal(1, gate.CurrentCount);
        });
    }

    /// <summary>A source whose scan finishes in one shot: its Done status arrives on the very same
    /// Flush tick as its only item, which is the case the enrichment ordering bug depended on.</summary>
    private sealed class EnrichOnDoneSource : IUpdateSource
    {
        public string Id => "enrich-test";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Item("some-package", "Some Package", "enrich-test");
            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
            Task.FromResult<UpdateEnrichment?>(
                new UpdateEnrichment(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), 4096));
    }

    /// <summary>
    /// Flush() used to hand the drained rows to Updates only AFTER FlushProviderStatuses ran
    /// EnrichSource, so a source reporting Done on the same tick as its last items got EnrichSource
    /// called against an empty slice of Updates, and the once-per-scan guard meant it never got a
    /// second chance. Rows must still gain their release date and download size.
    /// </summary>
    [Fact]
    public void A_source_that_finishes_in_its_first_flush_tick_still_gets_enriched()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new EnrichOnDoneSource()]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var row = Assert.Single(viewModel.Updates, r => r.SourceId == "enrich-test");

            // The scan's own Flush() already ran synchronously; enrichment itself runs on a background
            // Task.Run and marshals its result back via the dispatcher, so give it a bounded chance to land.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!row.HasPublishDate && DateTime.UtcNow < deadline)
                Pump(Task.Delay(10));

            Assert.True(row.HasPublishDate, "the row's own source finished scanning without ever being enriched");
            Assert.True(row.HasDownloadSize);
        });
    }

    /// <summary>
    /// RefreshLocalizedText must announce both CategoryDisplay and ChildCountText, leaving either out
    /// would strand a category group header or a Steam-style parent row's "3 games" sub-list count in
    /// the old language until something unrelated (a rescan, a filter change) happened to re-raise them.
    /// </summary>
    [Fact]
    public void RefreshLocalizedText_announces_CategoryDisplay_and_ChildCountText()
    {
        WpfHarness.OnUiThread(() =>
        {
            var row = new UpdateRowViewModel(new UpdateItem
            {
                Id = "steam", SourceId = "steam", Category = UpdateCategory.Games, Name = "Steam",
                Children = [new UpdateItem { Id = "game-a", SourceId = "steam", Category = UpdateCategory.Games, Name = "Game A" }],
            });

            var raisedCategory = false;
            var raisedChildCount = false;
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(UpdateRowViewModel.CategoryDisplay))
                    raisedCategory = true;
                if (args.PropertyName == nameof(UpdateRowViewModel.ChildCountText))
                    raisedChildCount = true;
            };

            row.RefreshLocalizedText();

            Assert.True(raisedCategory);
            Assert.True(raisedChildCount);
        });
    }

    /// <summary>
    /// A row's download size can arrive later via background enrichment rather than up front from the
    /// scan, so DownloadSizeBytes must announce its OWN change (not just the other properties enrichment
    /// touches), otherwise nothing summing it across selected rows has a signal to react to.
    /// </summary>
    [Fact]
    public void Selecting_a_row_then_enriching_its_size_updates_the_selection_summary()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new SizelessSource()]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var row = Assert.Single(viewModel.Updates);
            row.IsSelected = true;
            var beforeSize = viewModel.SelectionSummary;

            var raised = false;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SelectionSummary))
                    raised = true;
            };

            row.ApplyEnrichment(date: null, size: 5_000_000);

            Assert.True(raised, "SelectionSummary must announce its own change once the size arrives");
            Assert.NotEqual(beforeSize, viewModel.SelectionSummary);
        });
    }

    private sealed class SizelessSource : IUpdateSource
    {
        public string Id => "windowsupdate";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return Item("KB5041585", "Security update", "windowsupdate");
            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Only the LAST of several scan requests queued behind a running scan's gate may actually run once
    /// the gate frees up, letting every queued waiter run would fire the first click's cancel-and-restart
    /// plus a second, entirely redundant scan. A third request queued behind the first two must supersede
    /// the second.
    /// </summary>
    [Fact]
    public void A_superseded_queued_scan_request_steps_aside_instead_of_running_redundantly()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new GatedSource();
            var viewModel = Create([source]);

            // Holds the gate; cancelled the moment the second request arrives, but the orchestrator
            // abandons a cancelled scan rather than waiting for it (INVARIANTS § Sources+scanning), so
            // this returns quickly without needing the source itself to unblock.
            var first = viewModel.ScanCommand.ExecuteAsync(null);

            // Queues behind the first (still finishing its cancellation), then is itself immediately
            // superseded by the third before it can ever reach the gate.
            var second = viewModel.ScanCommand.ExecuteAsync(null);
            var third = viewModel.ScanCommand.ExecuteAsync(null);

            // Only now let a source invocation actually complete, whichever request ends up owning the
            // real run needs this to finish.
            source.Release();

            Pump(Task.WhenAll(first, second, third));

            // The second request must never have run its own scan, only the first (cancelled, still
            // counts as an attempt) and the third (the one that actually completes) may have.
            Assert.True(source.ScanCount <= 2, $"expected at most 2 scan attempts, got {source.ScanCount}");
            Assert.Single(viewModel.Updates);
        });
    }

    private sealed class GatedSource : IUpdateSource
    {
        private readonly TaskCompletionSource _gate = new();

        public int ScanCount;

        public void Release() => _gate.TrySetResult();

        public string Id => "windowsupdate";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ScanCount);
            await _gate.Task.WaitAsync(cancellationToken);
            yield return Item("KB5041585", "Security update", "windowsupdate");
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Install-status reports must be matched against child rows too, not just top-level Updates, a
    /// report for one of Steam's children carries its own id, never "steam", so matching only the top
    /// level would leave a child's install/uninstall progress or manual-handoff result unreported.
    /// </summary>
    [Fact]
    public void FindRow_locates_a_child_row_not_only_top_level_rows()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var parent = new UpdateRowViewModel(new UpdateItem
            {
                Id = "steam", SourceId = "steam", Category = UpdateCategory.Games, Name = "Steam",
                Children =
                [
                    new UpdateItem { Id = "game-a", SourceId = "steam", Category = UpdateCategory.Games, Name = "Game A" },
                ],
            });
            viewModel.Updates.Add(parent);

            var found = viewModel.FindRow("steam", "game-a");

            Assert.NotNull(found);
            Assert.Equal("game-a", found!.Item.Id);
        });
    }

    /// <summary>
    /// Steam's parent row carries no version of its own (both InstalledVersion and AvailableVersion are
    /// null), so a reconcile that compares versions alone would see null-vs-null, decide nothing changed,
    /// and keep the row's ORIGINAL children forever. A second scan reporting a different set of children
    /// must replace them.
    /// </summary>
    [Fact]
    public void A_parent_row_with_no_version_of_its_own_still_picks_up_changed_children_on_a_rescan()
    {
        WpfHarness.OnUiThread(() =>
        {
            var source = new SteamLikeSource();
            var viewModel = Create([source]);

            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);
            Assert.Single(row.Children);
            Assert.Equal("game-a", row.Children[0].Item.Id);

            source.SecondScanHasTwoGames = true;
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var afterRescan = Assert.Single(viewModel.Updates);
            Assert.Equal(2, afterRescan.Children.Count);
            Assert.Contains(afterRescan.Children, c => c.Item.Id == "game-b");
        });
    }

    private sealed class SteamLikeSource : IUpdateSource
    {
        public bool SecondScanHasTwoGames { get; set; }

        public string Id => "steam";

        public UpdateCategory Category => UpdateCategory.Games;

        public SourceTier Tier => SourceTier.C;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public async IAsyncEnumerable<UpdateItem> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var children = new List<UpdateItem>
            {
                new()
                {
                    Id = "game-a", SourceId = "steam", Category = UpdateCategory.Games, Name = "Game A",
                    InstalledVersion = "1", AvailableVersion = "2",
                },
            };

            if (SecondScanHasTwoGames)
            {
                children.Add(new UpdateItem
                {
                    Id = "game-b", SourceId = "steam", Category = UpdateCategory.Games, Name = "Game B",
                    InstalledVersion = "1", AvailableVersion = "2",
                });
            }

            yield return new UpdateItem
            {
                Id = "steam", SourceId = "steam", Category = UpdateCategory.Games, Name = "Steam",
                Children = children,
            };

            await Task.CompletedTask;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
