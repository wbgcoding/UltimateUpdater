using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The one pre-flight window shown before a batch starts, in place of a per-group
/// <see cref="Dialogs.ConfirmThreeWay"/> dialog. <see cref="MainViewModel.InstallPreflightOverride"/> is
/// the test seam (same shape as <see cref="MainViewModel.RetryInteractiveOverride"/>), and the injected
/// <see cref="Services.Install.InstallBlockers"/> reads a FAKE process list, never whatever genuinely
/// happens to be running on the machine that runs these tests.
/// </summary>
[Collection("wpf")]
public class InstallPreflightTests
{
    private static UpdateItem Item(string id, string name, string sourceId, long? downloadSize = null) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = name,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
        DownloadSize = downloadSize,
    };

    /// <summary>Nothing to warn or ask about: the window must never open, and the batch queues exactly
    /// as it did before this fix existed.</summary>
    [Fact]
    public void No_lines_means_no_window_and_the_same_queue_as_today()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var opened = false;
            viewModel.InstallPreflightOverride = _ => { opened = true; return true; };

            var row = new UpdateRowViewModel(Item("requests", "requests", "pip"));
            viewModel.Updates.Add(row);
            row.IsSelected = true;

            ExecuteInstallAndWait(viewModel);

            Assert.False(opened, "nothing was blocked and no warning applies, the window must not open");
            Assert.Contains(row, viewModel.InstallBatch);
        });
    }

    /// <summary>A blocked row produces exactly one line (one group, one blocker), and Start with "try
    /// anyway" queues the row it stands for.</summary>
    [Fact]
    public void A_blocked_row_shows_one_line_and_start_queues_it_on_try_anyway()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
                var viewModel = Create(installBlockers: blockers);

                var row = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                var opened = false;
                viewModel.InstallPreflightOverride = preflight =>
                {
                    opened = true;
                    var line = Assert.Single(preflight.Lines);
                    Assert.True(line.IsBlocker);
                    Assert.True(preflight.HasBlockers);
                    preflight.StartTryingAnywayCommand.Execute(null); // "try anyway" starts at once
                    return preflight.Started;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.True(opened);
                Assert.Contains(row, viewModel.InstallBatch);
                Assert.NotEqual(RowInstallState.Idle, row.InstallState);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>Cancel queues nothing at all, not even the row nothing was wrong with.</summary>
    [Fact]
    public void Cancel_queues_nothing()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
                var viewModel = Create(installBlockers: blockers);

                var blockedRow = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                var cleanRow = new UpdateRowViewModel(Item("requests", "requests", "pip"));
                viewModel.Updates.Add(blockedRow);
                viewModel.Updates.Add(cleanRow);
                blockedRow.IsSelected = true;
                cleanRow.IsSelected = true;

                viewModel.InstallPreflightOverride = _ => false; // Cancel, without answering the line

                ExecuteInstallAndWait(viewModel);

                Assert.Empty(viewModel.InstallBatch);
                Assert.Equal(RowInstallState.Idle, blockedRow.InstallState);
                Assert.Equal(RowInstallState.Idle, cleanRow.InstallState);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>Two blocked groups and one free row: the window asks once, Skip answers both groups
    /// together and only the free row is queued.</summary>
    [Fact]
    public void Skip_answers_every_blocked_group_at_once_and_queues_the_rest()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv", "Discord"]);
                var viewModel = Create(installBlockers: blockers);

                var vsRow = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                var discordRow = new UpdateRowViewModel(Item("Discord.Discord", "Discord", "winget"));
                var cleanRow = new UpdateRowViewModel(Item("requests", "requests", "pip"));
                foreach (var row in new[] { vsRow, discordRow, cleanRow })
                {
                    viewModel.Updates.Add(row);
                    row.IsSelected = true;
                }

                var opened = 0;
                viewModel.InstallPreflightOverride = preflight =>
                {
                    opened++;
                    Assert.Equal(2, preflight.Lines.Count(l => l.IsBlocker));
                    preflight.StartSkippingBlockedCommand.Execute(null);
                    return preflight.Started;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.Equal(1, opened);
                Assert.Contains(cleanRow, viewModel.InstallBatch);
                // skipped rows are listed in the strip (as cancelled), but never queued
                Assert.Contains(vsRow, viewModel.InstallBatch);
                Assert.Contains(discordRow, viewModel.InstallBatch);
                Assert.Equal(RowInstallState.Cancelled, vsRow.InstallState);
                Assert.Equal(RowInstallState.Cancelled, discordRow.InstallState);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>A metered connection with at least one known, non-zero download size adds a
    /// warning line quoting the batch's total; starting still queues the row, nothing here blocks it.</summary>
    [Fact]
    public void A_metered_connection_with_a_known_size_adds_a_warning_line()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create();
                viewModel.IsMeteredOverride = () => true;

                var row = new UpdateRowViewModel(Item("requests", "requests", "pip", downloadSize: 5_000_000L));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                viewModel.InstallPreflightOverride = preflight =>
                {
                    var line = Assert.Single(preflight.Lines);
                    Assert.False(line.IsBlocker); // a plain warning, not a blocker line
                    return true;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.Contains(row, viewModel.InstallBatch);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>Many sources report no size at scan time and only the enrichment pass finds one; the
    /// pre-flight reads the row's own figure, so that size counts toward the metered warning too.</summary>
    [Fact]
    public void A_metered_connection_counts_a_size_found_only_by_enrichment()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create();
                viewModel.IsMeteredOverride = () => true;

                var row = new UpdateRowViewModel(Item("requests", "requests", "pip"));
                row.ApplyEnrichment(null, 5_000_000L);
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                var sawLine = false;
                viewModel.InstallPreflightOverride = preflight =>
                {
                    sawLine = preflight.Lines.Count == 1;
                    return false;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.True(sawLine);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>"Skip blocked" leaves the blocked row out, but not silently: it reads Cancelled with the
    /// reason naming the program, the batch lists it and the history gets one cancelled entry.</summary>
    [Fact]
    public void Skipping_a_blocked_row_marks_it_cancelled_with_the_reason_and_records_history()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var (viewModel, history) = CreateWithHistory(
                    new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]));
                var row = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                viewModel.InstallPreflightOverride = preflight =>
                {
                    preflight.StartSkippingBlockedCommand.Execute(null);
                    return preflight.Started;
                };

                ExecuteInstallAndWait(viewModel);

                AssertSkippedStillOpen(row, history);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>"Close and continue" with the force close declined (or a survivor) leaves the row out the
    /// same way as Skip: Cancelled, the reason, one cancelled history entry.</summary>
    [Fact]
    public void A_declined_force_close_marks_the_blocked_row_cancelled_with_the_reason()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var (viewModel, history) = CreateWithHistory(
                    new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]));
                var row = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                viewModel.CloseBlockingProcessesOverride = _ => Task.FromResult(false);
                viewModel.InstallPreflightOverride = preflight =>
                {
                    preflight.StartClosingProgramsCommand.Execute(null);
                    return preflight.Started;
                };

                ExecuteInstallAndWait(viewModel);

                AssertSkippedStillOpen(row, history);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    private static void AssertSkippedStillOpen(UpdateRowViewModel row, HistoryService history)
    {
        Assert.Equal(RowInstallState.Cancelled, row.InstallState);
        var expected = string.Format(
            LocalizationService.Instance["InstallBlockerSkippedStillOpen"], "Visual Studio");
        Assert.Equal(expected, row.InstallStatusText);

        var entry = Assert.Single(history.Read());
        Assert.True(entry.IsCancelled);
        Assert.False(entry.IsManualHandoff);
        Assert.Equal(row.Item.Id, entry.ItemId);
    }

    /// <summary>The restore point line is only for a batch that can really create one: an admin row in
    /// it and the setting on. A user scope only batch, or the setting off, hides it.</summary>
    [Theory]
    [InlineData("pip", true, false)]
    [InlineData("winget", true, true)]
    [InlineData("winget", false, false)]
    public void The_restore_point_row_shows_only_for_an_admin_batch_with_the_setting_on(
        string sourceId, bool createRestorePoint, bool expectedVisible)
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create(createRestorePoint: createRestorePoint);
                viewModel.IsMeteredOverride = () => true; // any line opens the window

                var row = new UpdateRowViewModel(Item("requests", "requests", sourceId, downloadSize: 5_000_000L));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                bool? shown = null;
                viewModel.InstallPreflightOverride = preflight =>
                {
                    shown = preflight.HasRestorePointText;
                    return false;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.Equal(expectedVisible, shown);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>A low, genuinely-on-battery reading adds a warning line with the whole percent;
    /// starting still queues the row.</summary>
    [Fact]
    public void A_low_battery_reading_adds_a_warning_line()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create();
                viewModel.PowerStatusOverride = () => (System.Windows.Forms.PowerLineStatus.Offline, 0.09f);

                var row = new UpdateRowViewModel(Item("requests", "requests", "pip"));
                viewModel.Updates.Add(row);
                row.IsSelected = true;

                viewModel.InstallPreflightOverride = preflight =>
                {
                    var line = Assert.Single(preflight.Lines);
                    Assert.Contains("9", line.Text); // 0.09f -> "9 percent"
                    return true;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.Contains(row, viewModel.InstallBatch);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>Unattended (the suite's default, see WpfHarness): never opens the window, keeps skipping
    /// a blocked row automatically with its reason recorded, exactly as before this fix.</summary>
    [Fact]
    public void Unattended_never_opens_the_window_and_still_auto_skips_a_blocked_row()
    {
        WpfHarness.OnUiThread(() =>
        {
            var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
            var viewModel = Create(installBlockers: blockers);

            var row = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
            viewModel.Updates.Add(row);
            row.IsSelected = true;

            var opened = false;
            viewModel.InstallPreflightOverride = _ => { opened = true; return true; };

            ExecuteInstallAndWait(viewModel);

            Assert.False(opened);
            // SkipBlockedRowUnattended marks the row failed with its reason and still surfaces it in the
            // strip, rather than dropping it with no trace.
            Assert.Contains(row, viewModel.InstallBatch);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
        });
    }

    /// <summary>A skipped blocked row belongs to the strip of the run it was skipped for: not wiped by that
    /// run's clear (mixed batch), not appended to the previous run's leftovers (everything skipped).</summary>
    [Fact]
    public void Skipped_blocked_rows_appear_in_the_strip_of_the_current_run()
    {
        WpfHarness.OnUiThread(() =>
        {
            var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
            var viewModel = Create(installBlockers: blockers);

            var stale = new UpdateRowViewModel(Item("old", "old", "pip"));
            viewModel.InstallBatch.Add(stale);

            var blocked = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
            var clean = new UpdateRowViewModel(Item("requests", "requests", "pip"));
            viewModel.Updates.Add(blocked);
            viewModel.Updates.Add(clean);
            blocked.IsSelected = true;
            clean.IsSelected = true;

            ExecuteInstallAndWait(viewModel);

            Assert.Contains(blocked, viewModel.InstallBatch);
            Assert.Contains(clean, viewModel.InstallBatch);
            Assert.DoesNotContain(stale, viewModel.InstallBatch);
        });
    }

    [Fact]
    public void A_run_where_every_row_is_skipped_does_not_extend_the_previous_strip()
    {
        WpfHarness.OnUiThread(() =>
        {
            var blockers = new Services.Install.InstallBlockers(runningProcessNames: () => ["devenv"]);
            var viewModel = Create(installBlockers: blockers);

            var stale = new UpdateRowViewModel(Item("old", "old", "pip"));
            viewModel.InstallBatch.Add(stale);

            var blocked = new UpdateRowViewModel(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio", "winget"));
            viewModel.Updates.Add(blocked);
            blocked.IsSelected = true;

            ExecuteInstallAndWait(viewModel);

            Assert.Same(blocked, Assert.Single(viewModel.InstallBatch));
        });
    }

    /// <summary>The blocker check takes one process-name snapshot for the whole batch, not one per row: a
    /// counting fake proves it is read exactly once even with several blocked candidate rows in one install
    /// call, where the old per-row <c>Detect(item)</c> overload would have read it once per row.</summary>
    [Fact]
    public void Blocker_detection_reads_the_process_snapshot_once_per_batch_not_per_row()
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var snapshotReads = 0;
                var blockers = new Services.Install.InstallBlockers(runningProcessNames: () =>
                {
                    Interlocked.Increment(ref snapshotReads);
                    return ["devenv"];
                });
                var viewModel = Create(installBlockers: blockers);

                var rows = Enumerable.Range(0, 5)
                    .Select(i => new UpdateRowViewModel(
                        Item($"Microsoft.VisualStudio.Product.{i}", $"Visual Studio {i}", "winget")))
                    .ToList();
                foreach (var row in rows)
                {
                    viewModel.Updates.Add(row);
                    row.IsSelected = true;
                }

                viewModel.InstallPreflightOverride = preflight =>
                {
                    preflight.StartTryingAnywayCommand.Execute(null); // "try anyway", once for every group
                    return preflight.Started;
                };

                ExecuteInstallAndWait(viewModel);

                Assert.Equal(1, snapshotReads);
                foreach (var row in rows)
                    Assert.Contains(row, viewModel.InstallBatch);
            });
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    /// <summary>
    /// The pre-flight window's own "last restore point" line, read back from the same timestamp
    /// file <see cref="Services.InstallQueue"/> writes after actually creating one. The dialog seam proves
    /// the button never has to pop a real system window for this to be testable.
    /// </summary>
    [Fact]
    public void RestorePointText_reads_the_shared_timestamp_and_the_open_command_uses_its_test_seam()
    {
        var logging = new LoggingService();
        var timestampPath = System.IO.Path.Combine(AppPaths.DataDirectory, "restore-point.json");
        if (System.IO.File.Exists(timestampPath))
            System.IO.File.Delete(timestampPath);

        var withoutOne = new InstallPreflightViewModel([], logging, true);
        Assert.Equal(LocalizationService.Instance["RestorePointNone"], withoutOne.RestorePointText);

        RestorePointThrottle.TryWriteCreated(logging, DateTimeOffset.UtcNow);
        var withOne = new InstallPreflightViewModel([], logging, true);

        // Whatever language happens to be active in this process (LocalizationService is a shared
        // singleton, and another test earlier in the same run may have switched it), not a hardcoded
        // English literal: the prefix up to the format placeholder is enough to prove the shared
        // timestamp actually reached the string, without pinning the test to one language.
        var expectedPrefix = LocalizationService.Instance["RestorePointLast"].Split("{0}")[0];
        Assert.StartsWith(expectedPrefix, withOne.RestorePointText);

        var opened = false;
        withOne.OpenRestorePointDialogOverride = () => opened = true;
        withOne.OpenRestorePointDialogCommand.Execute(null);

        Assert.True(opened, "the seam must run instead of the real rstrui.exe launch");
    }

    /// <summary>
    /// Runs the install command and waits for it to actually finish: blocker detection now takes its
    /// process snapshot off the UI thread, so <c>Execute</c> alone returns before the batch is built. A
    /// nested <see cref="System.Windows.Threading.DispatcherFrame"/> keeps this thread's own dispatcher
    /// pumping while the command's background work runs, the same pattern <see
    /// cref="WpfHarness.WaitForCleanupCatalog"/> already uses for an async view model operation.
    /// </summary>
    private static void ExecuteInstallAndWait(MainViewModel viewModel)
    {
        var command = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)viewModel.InstallSelectedCommand;
        command.Execute(null);
        var task = command.ExecutionTask;
        if (task is null || task.IsCompleted)
            return;

        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static (MainViewModel ViewModel, HistoryService History) CreateWithHistory(
        Services.Install.InstallBlockers installBlockers)
    {
        var path = System.IO.Path.Combine(
            System.IO.Directory.CreateTempSubdirectory("wu-preflight-history-").FullName, "history.jsonl");
        var history = new HistoryService(new LoggingService(), path);
        return (Create(installBlockers: installBlockers, history: history), history);
    }

    private static MainViewModel Create(
        IReadOnlyList<IUpdateSource>? sources = null,
        Services.Install.InstallBlockers? installBlockers = null,
        bool createRestorePoint = true,
        HistoryService? history = null)
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, System.IO.Path.Combine(
            System.IO.Directory.CreateTempSubdirectory("wu-preflight-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        settings.Current.Install.CreateRestorePoint = createRestorePoint;

        var allSources = sources ?? MockUpdateSource.CreateAll();
        history ??= new HistoryService(logging);

        return new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(allSources, logging),
            new IconCacheService(logging), new InstallQueue(allSources, settings, history, logging), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new Services.SelfUpdate.SelfUpdateService(
                new System.Net.Http.HttpClient(), logging, new Services.SelfUpdate.PublicUpdateChannel(logging)),
            allSources, installBlockers: installBlockers);
    }

    /// <summary>A row the user stopped shows Retry, so installing it again must actually queue it, the
    /// same as a failed row.</summary>
    [Fact]
    public void A_cancelled_row_can_be_installed_again()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create();
            var row = new UpdateRowViewModel(Item("requests", "requests", "pip"));
            viewModel.Updates.Add(row);
            row.ApplyInstallStatus(new InstallItemStatus("pip", "requests", 0, null, null, null) { IsCancelled = true });
            Assert.Equal(RowInstallState.Cancelled, row.InstallState);
            Assert.True(row.CanInstall);

            row.IsSelected = true;
            ExecuteInstallAndWait(viewModel);

            Assert.Contains(row, viewModel.InstallBatch);
        });
    }

    /// <summary>A blocking program that is still open after the grace period and whose force-kill is
    /// declined (headless dialogs always decline) must report "not cleared", so its rows are not queued.</summary>
    [Fact]
    public async Task Closing_blockers_reports_a_survivor_when_the_force_kill_is_declined()
    {
        Dialogs.SuppressInteractiveDialogs = true;
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        var pid = child.Id;
        try
        {
            var cleared = await MainViewModel.CloseBlockingProcessesAsync(
                ["ping-test"],
                _ => [System.Diagnostics.Process.GetProcessById(pid)],
                TimeSpan.FromMilliseconds(300));

            Assert.False(cleared);
        }
        finally
        {
            try { child.Kill(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Nothing running under the blocking name means there is nothing left to wait for.</summary>
    [Fact]
    public async Task Closing_blockers_reports_cleared_when_nothing_is_running()
    {
        var cleared = await MainViewModel.CloseBlockingProcessesAsync(["ping-test"], _ => [], TimeSpan.FromMilliseconds(300));

        Assert.True(cleared);
    }
}
