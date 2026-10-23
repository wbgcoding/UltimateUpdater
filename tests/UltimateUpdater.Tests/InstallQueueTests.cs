using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Covers two real failure modes: rows stranded on "installing" when a batch was cancelled, and a
/// worker that would have kept installing with admin rights after the app stopped listening. Nothing
/// here installs anything real, the sources are stubs and the elevated worker is driven over a pipe
/// inside the test process.
/// </summary>
public class InstallQueueTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// This fix's own decision function: skip a NEW restore point while one from within the last 24 hours
    /// is already on record, create one once that record is stale (or missing entirely).
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(23, false)]
    [InlineData(25, true)]
    public void RestorePointThrottle_skips_within_24_hours_and_creates_past_it(double hoursAgo, bool expectedCreate)
    {
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var lastCreated = now - TimeSpan.FromHours(hoursAgo);

        Assert.Equal(expectedCreate, RestorePointThrottle.ShouldCreate(lastCreated, now));
    }

    /// <summary>No record at all (never created, or the timestamp file could not be read) always says
    /// yes: the safe direction to be wrong in is an extra restore point, not a silently skipped one.</summary>
    [Fact]
    public void RestorePointThrottle_creates_when_nothing_was_ever_recorded()
    {
        Assert.True(RestorePointThrottle.ShouldCreate(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_successful_install_is_reported_and_recorded()
    {
        var (queue, history, statuses) = Create(new StubSource("npm"));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        // Contains, not Last: Progress<T> has no synchronization context here, so its callbacks can
        // land after the final status. In the app they are marshalled to the UI thread and stay in order.
        Assert.Contains(statuses, s => s.ItemId == "eslint" && s.Success == true);
        Assert.Contains(history.Read(), e => e.ItemId == "eslint" && e.Success);
    }

    /// <summary>
    /// A mock session never elevates, even for a source id that <see cref="InstallQueue.NeedsElevation"/>
    /// names (winget/chocolatey/windowsupdate) - MockUpdateSource deliberately reuses those exact ids, so the
    /// only thing that can tell a mock row apart from a real one is the session itself. No custom
    /// <c>ElevatedHost</c> is given here on purpose: if the admin batch were reached at all, this test would
    /// hang or fail trying to talk to a real elevated worker that was never started; StubSource's own
    /// AttemptCount reaching 1 is only possible through the in-process, user-scope half.
    /// </summary>
    [Fact]
    public async Task A_mock_session_never_elevates_an_elevation_needing_source_id()
    {
        var logging = new LoggingService();
        var temp = Directory.CreateTempSubdirectory("wu-mock-session-test-").FullName;
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        var source = new StubSource("winget");
        var queue = new InstallQueue([source], settings, history, logging, isMockSession: true);
        var statusesLock = new object();
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += s =>
        {
            lock (statusesLock)
                statuses.Add(s);
        };

        var finished = await Task.WhenAny(
            queue.RunAsync([Item("git", "winget")], CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(10)));
        await finished;

        Assert.Equal(1, source.AttemptCount);
        Assert.Contains(statuses, s => s.ItemId == "git" && s.Success == true);
    }

    /// <summary>
    /// A successful install closes its own download bar to full in the SAME status update that
    /// reports the row finished, rather than leaving it to a later progress line a successful install never
    /// sends. Before the fix the terminal status carried no bytes at all, so the row's own BytesReceived
    /// stayed at whatever the last progress report happened to be.
    /// </summary>
    [Fact]
    public async Task A_successful_install_closes_its_own_download_bar_in_the_same_status()
    {
        var (queue, _, statuses) = Create(new DownloadingSource("npm", receivedAtEnd: 50, total: 100));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var finished = statuses.Last(s => s.ItemId == "eslint" && s.Success == true);
        Assert.Equal(finished.BytesTotal, finished.BytesReceived);
        Assert.Equal(100, finished.BytesTotal);
    }

    /// <summary>
    /// A download that fails partway through must keep its real partial byte count, never be
    /// stamped as if it had finished. Before the fix, <c>CloseTransfer</c> filled BytesReceived/BytesTotal
    /// from the tracked total for EVERY terminal status carrying no bytes of its own, success or not, so a
    /// failed 40/80 MB download read "80.0 / 80.0 MB" once it closed.
    /// </summary>
    [Fact]
    public async Task A_failed_download_keeps_its_partial_byte_count_instead_of_reading_as_complete()
    {
        var (queue, _, statuses) = Create(new FailingDownloadSource("npm", receivedBeforeFailure: 40, total: 80));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var failed = statuses.Last(s => s.ItemId == "eslint" && s.Success == false);
        Assert.Equal(80, failed.BytesTotal);
        Assert.Equal(40, failed.BytesReceived);
        Assert.NotEqual(failed.BytesTotal, failed.BytesReceived);
    }

    /// <summary>
    /// A retry of the same item in the same session (the automatic retry round here) must show
    /// real progress again. Before the fix, the first attempt's own transfer entry stayed marked
    /// <c>Finished</c> in <c>InstallQueue._transfers</c> forever, so every progress report from the SECOND
    /// attempt was read as stale by <c>Report</c>'s own "already finished" branch and silently dropped -
    /// the row never moved off 0% on the retry that actually succeeded.
    /// </summary>
    [Fact]
    public async Task A_retried_download_reports_progress_again_instead_of_staying_at_zero()
    {
        var source = new RetryableDownloadSource("npm", total: 80);
        var (queue, _, statuses) = Create(source);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Equal(2, source.AttemptCount);

        // A real, non-dropped progress report from the SECOND attempt: proves the retry's transfer was not
        // silently rejected as stale.
        Assert.Contains(statuses, s => s.ItemId == "eslint" && s.Status == "downloading attempt 2" && s.BytesReceived == 40);

        var finished = statuses.Last(s => s.ItemId == "eslint" && s.Success == true);
        Assert.Equal(80, finished.BytesTotal);
        Assert.Equal(80, finished.BytesReceived);
    }

    /// <summary>
    /// A null <c>items</c> payload (an explicit JSON <c>"items": null</c>, not merely an empty
    /// array) must end the job cleanly rather than throwing a <see cref="NullReferenceException"/> and
    /// dropping the whole job with no reply.
    /// </summary>
    [Fact]
    public async Task RunJobAsync_treats_a_null_items_payload_as_an_empty_job()
    {
        var job = JsonSerializer.Deserialize<InstallJobFile>("""{"action":0,"items":null}""", Json);
        var logging = new LoggingService();
        using var stream = new MemoryStream();
        await using var writer = new StreamWriter(stream) { AutoFlush = true };

        var completed = await InstallWorker.RunJobAsync(job, writer, "job-1", logging, CancellationToken.None);

        Assert.True(completed);
    }

    /// <summary>
    /// A progress line that is still in flight when the item's own result already went out (the
    /// same race the previous test's neighbour works around with Assert.Contains, see its own comment) must
    /// be dropped rather than applied: applying it would pull a row that already reads "done" back to a
    /// part-filled bar.
    /// </summary>
    [Fact]
    public async Task A_progress_line_that_arrives_after_completion_is_dropped()
    {
        var source = new ProgressCapturingSource("npm");
        var (queue, _, statuses) = Create(source);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var countBeforeLateReport = statuses.Count;
        Assert.Contains(statuses, s => s.ItemId == "eslint" && s.Success == true);

        // Fired directly against the same IProgress<T> the queue already handed the source, after the
        // item's own success has been recorded: exactly the late callback this fix measured.
        source.CapturedProgress!.Report(new Models.InstallProgress
        {
            PercentComplete = 60, BytesReceived = 60, BytesTotal = 100, StatusText = "late",
        });

        // Progress<T> has no synchronization context here either, so the late report's own delivery is
        // itself posted through the thread pool; waited out rather than asserted on immediately, the same
        // reason the neighbouring test above reads Contains instead of Last.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && statuses.Count == countBeforeLateReport)
            await Task.Delay(20);

        Assert.DoesNotContain(statuses, s => s.ItemId == "eslint" && s.Status == "late");
        var last = statuses.Last(s => s.ItemId == "eslint");
        Assert.True(last.Success);
    }

    /// <summary>
    /// This fix's own regression, straight from the measured log: the download's own "done" line must be
    /// written before the install's own "ok" line, not after, so a finished row's log never again reads as
    /// if the install ended while its download was still open.
    /// </summary>
    [Fact]
    public async Task The_download_finished_line_is_logged_before_the_install_succeeded_line()
    {
        var temp = Directory.CreateTempSubdirectory("wu-install-log-test-").FullName;
        var logging = new LoggingService(temp);
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        var queue = new InstallQueue([new DownloadingSource("npm", receivedAtEnd: 50, total: 100)], settings, history, logging);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var logFile = Path.Combine(temp, $"{DateTime.Now:yyyy-MM-dd}.log");
        var lines = File.ReadAllLines(logFile);

        var downloadDoneIndex = Array.FindIndex(lines, l => l.Contains(" dl ") && l.Contains("done npm:eslint"));
        var installOkIndex = Array.FindIndex(lines, l => l.Contains(" inst ") && l.Contains("ok update npm:eslint"));

        Assert.True(downloadDoneIndex >= 0, "expected a 'dl done' line for npm:eslint");
        Assert.True(installOkIndex >= 0, "expected an 'inst ok' line for npm:eslint");
        Assert.True(downloadDoneIndex < installOkIndex, "the download's own finished line must precede the install's own succeeded line");
    }

    /// <summary>
    /// This fix's history-entry shape: a pinned, rollback item is otherwise just a plain install as far as
    /// this queue is concerned (its own version-pinned attempt is WingetSource's own concern, exercised in
    /// WingetInstallLadderTests), but AppendHistory must still read IsRollback straight off the item that
    /// actually ran, and ToVersion must be the pinned OLDER version, never a version this queue invents.
    /// </summary>
    [Fact]
    public async Task A_rollback_install_records_the_pinned_version_and_the_rollback_flag()
    {
        var (queue, history, _) = Create(new StubSource("npm"));

        var item = new UpdateItem
        {
            Id = "eslint",
            SourceId = "npm",
            Category = UpdateCategory.DeveloperTools,
            Name = "eslint",
            InstalledVersion = "2.0",
            AvailableVersion = "1.0",
            PinnedVersion = "1.0",
            IsRollback = true,
        };

        await queue.RunAsync([item], CancellationToken.None);

        var entry = history.Read().First(e => e.ItemId == "eslint");
        Assert.True(entry.Success);
        Assert.True(entry.IsRollback);
        Assert.Equal("2.0", entry.FromVersion);
        Assert.Equal("1.0", entry.ToVersion);
    }

    /// <summary>An ordinary install (no PinnedVersion, this fix's own default shape) must not somehow read
    /// as a rollback; IsRollback only ever follows an explicit, deliberate UpdateItem.IsRollback.</summary>
    [Fact]
    public async Task An_ordinary_install_is_never_recorded_as_a_rollback()
    {
        var (queue, history, _) = Create(new StubSource("npm"));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.False(history.Read().First(e => e.ItemId == "eslint").IsRollback);
    }

    /// <summary>
    /// A source that cannot answer the re-check (the default, no callback given) makes no
    /// statement at all - the row stays a plain success, no quiet note, and the history entry is not
    /// flagged either.
    /// </summary>
    [Fact]
    public async Task Install_version_recheck_null_makes_no_statement()
    {
        var (queue, history, statuses) = Create(new StubSource("npm"));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        // Success == true, not a bare ItemId match. StubSource's plain progress line (no bytes,
        // reported just before it returns) has no ordering guarantee against the terminal status in a test
        // with no synchronization context (see the note on A_successful_install_is_reported_and_recorded);
        // only the terminal status itself is what "final" means here.
        var final = statuses.Last(s => s.ItemId == "eslint" && s.Success == true);
        Assert.True(final.Success);
        Assert.False(final.VersionUnchanged);
        Assert.False(history.Read().First(e => e.ItemId == "eslint").VersionUnchanged);
    }

    /// <summary>
    /// The re-check reports back the SAME version the item had before this run - a real success
    /// (the tool exited 0), but nothing actually changed, so the row keeps Success=true and gains the
    /// quiet note, and the history entry carries the same flag.
    /// </summary>
    [Fact]
    public async Task Install_version_recheck_unchanged_keeps_success_and_adds_a_note()
    {
        var (queue, history, statuses) = Create(new StubSource("npm", getInstalledVersion: _ => "1.0"));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        // Success == true, see the note on Install_version_recheck_null_makes_no_statement above.
        var final = statuses.Last(s => s.ItemId == "eslint" && s.Success == true);
        Assert.True(final.Success);
        Assert.True(final.VersionUnchanged);
        Assert.True(history.Read().First(e => e.ItemId == "eslint").VersionUnchanged);
    }

    /// <summary>A re-check that reports the NEW version is an ordinary success, no note.</summary>
    [Fact]
    public async Task Install_version_recheck_changed_reports_plain_success()
    {
        var (queue, history, statuses) = Create(new StubSource("npm", getInstalledVersion: _ => "2.0"));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        // Success == true, see the note on Install_version_recheck_null_makes_no_statement above.
        var final = statuses.Last(s => s.ItemId == "eslint" && s.Success == true);
        Assert.True(final.Success);
        Assert.False(final.VersionUnchanged);
        Assert.False(history.Read().First(e => e.ItemId == "eslint").VersionUnchanged);
    }

    /// <summary>A re-check that throws makes no statement either: the install already finished, so the
    /// row stays a success and its history entry, which a later rollback is built from, is kept.</summary>
    [Fact]
    public async Task Install_version_recheck_that_throws_keeps_the_success()
    {
        var (queue, history, statuses) = Create(new StubSource("npm",
            getInstalledVersion: _ => throw new InvalidOperationException("tool lookup refused")));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Contains(statuses, s => s.ItemId == "eslint" && s.Success == true);
        Assert.DoesNotContain(statuses, s => s.ItemId == "eslint" && s.Success == false);
        Assert.True(history.Read().Single(e => e.ItemId == "eslint").Success);
    }

    /// <summary>This fix's shared comparison: null from the source is "no statement", never "unchanged".</summary>
    [Theory]
    [InlineData(null, "1.0", false)]
    [InlineData("1.0", "1.0", true)]
    [InlineData("2.0", "1.0", false)]
    [InlineData("1.0", null, false)]
    public void IsVersionUnchanged_reads_null_as_no_statement(string? recheck, string? previous, bool expected) =>
        Assert.Equal(expected, InstallQueue.IsVersionUnchanged(previous, recheck));

    // ---- this fix's own automatic retry round ----

    /// <summary>MSI 1618 ("another installation was already running") is one of the explainer table's
    /// safe-to-retry-blindly causes; a first failure with that code gets exactly one automatic second
    /// attempt, which here succeeds.</summary>
    [Fact]
    public async Task A_retryable_failure_is_retried_exactly_once_and_can_still_succeed()
    {
        var source = new StubSource("npm", throwsByAttempt: attempt =>
            attempt == 1 ? new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)") : null);
        var (queue, history, statuses) = Create(source);
        var secondAttemptFired = 0;
        queue.SecondAttemptStarted += () => secondAttemptFired++;

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Equal(2, source.AttemptCount);
        Assert.Equal(1, secondAttemptFired);
        // Success == true, see the note on Install_version_recheck_null_makes_no_statement above.
        Assert.True(statuses.Last(s => s.ItemId == "eslint" && s.Success == true).Success);
        Assert.Equal(2, history.Read().Count(e => e.ItemId == "eslint"));
    }

    /// <summary>A second failure in the retry round is final: no third pass, whatever the cause.</summary>
    [Fact]
    public async Task A_retryable_failure_that_fails_again_is_final_with_no_third_pass()
    {
        var source = new StubSource("npm", throwsByAttempt: _ =>
            new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)"));
        var (queue, history, statuses) = Create(source);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Equal(2, source.AttemptCount);
        Assert.False(statuses.Last(s => s.ItemId == "eslint").Success);
        Assert.Equal(2, history.Read().Count(e => e.ItemId == "eslint"));
    }

    /// <summary>A cause the explainer table does not mark safe to retry blindly is never retried at all.</summary>
    [Fact]
    public async Task A_non_retryable_failure_is_never_retried()
    {
        var source = new StubSource("npm", throwsByAttempt: _ => new InvalidOperationException("stub failure"));
        var (queue, _, _) = Create(source);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Equal(1, source.AttemptCount);
    }

    /// <summary>A row the user stopped with its own stop button is never part of the retry
    /// round, even though it never actually "succeeded".</summary>
    [Fact]
    public async Task A_row_the_user_stopped_is_never_part_of_the_retry_round()
    {
        var blocked = new BlockingSource("scoop");
        var (queue, history, statuses) = Create(blocked);

        var runTask = queue.RunAsync([Item("stopped", "scoop")], CancellationToken.None);

        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        queue.CancelItem("scoop", "stopped");

        await runTask;

        Assert.Single(history.Read(), e => e.ItemId == "stopped");
        Assert.Contains(statuses, s => s.ItemId == "stopped" && s.IsCancelled);
    }

    /// <summary>A first failure the retry round will run again is not the row's outcome yet. Reported as
    /// Failed, the row could be retried by hand while the queue retried it too, and both failures would
    /// count toward the three-strikes tally. Only the second attempt's result is terminal.</summary>
    [Fact]
    public async Task A_failure_awaiting_the_retry_round_is_not_reported_as_final()
    {
        var source = new StubSource("npm", throwsByAttempt: _ =>
            new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)"));
        var (queue, _, statuses) = Create(source);

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        Assert.Equal(2, source.AttemptCount);
        Assert.Single(statuses, s => s.ItemId == "eslint" && (s.IsTerminal || s.Error is not null));
        Assert.Contains(statuses, s => s.ItemId == "eslint" && !s.IsTerminal && s.Error is null
            && s.Status == LocalizationService.Instance["InstallRetryRound"]);
    }

    /// <summary>A batch stopped between the first round and the retry round never gives the waiting row
    /// its second attempt; the row must still end on a terminal status rather than read "in progress".</summary>
    [Fact]
    public async Task A_failure_awaiting_the_retry_round_is_closed_out_when_the_batch_stops_first()
    {
        var source = new StubSource("npm", throwsByAttempt: _ =>
            new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)"));
        var (queue, _, statuses) = Create(source);
        using var cts = new CancellationTokenSource();
        queue.SecondAttemptStarted += cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync([Item("eslint", "npm")], cts.Token));

        Assert.Equal(1, source.AttemptCount);
        Assert.True(statuses.Last(s => s.ItemId == "eslint").IsCancelled);
    }

    /// <summary>Stop on a row that waits for its automatic retry settles the row as stopped and the retry
    /// round skips it, instead of the click being ignored and the package installing again.</summary>
    [Fact]
    public async Task Stopping_a_row_that_waits_for_the_retry_skips_the_second_attempt()
    {
        // "eslint" fails with a retryable cause and then waits while the next row of the same source
        // runs; the user presses its stop button during that wait.
        InstallQueue? queue = null;
        var source = new StubSource(
            "npm",
            onInstall: item =>
            {
                if (item.Id == "typescript")
                    queue!.CancelItem("npm", "eslint");
            },
            throwsByAttempt: attempt => attempt == 1
                ? new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)")
                : null);
        (queue, _, var statuses) = Create(source);
        var secondAttemptFired = 0;
        queue.SecondAttemptStarted += () => secondAttemptFired++;

        await queue.RunAsync([Item("eslint", "npm"), Item("typescript", "npm")], CancellationToken.None);

        Assert.Equal(2, source.AttemptCount);
        Assert.Equal(0, secondAttemptFired);
        Assert.Single(statuses, s => s.ItemId == "eslint" && s.IsCancelled);
        Assert.True(statuses.Last(s => s.ItemId == "eslint").IsCancelled);
    }

    /// <summary>The elevated half's version of the rule above: a retryable failure from the helper is
    /// enrolled for the retry round without a Failed status and without being counted as accounted for.</summary>
    [Fact]
    public void A_retryable_failure_from_the_helper_is_not_reported_as_final_before_the_retry_round()
    {
        var (queue, history, _) = Create();
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += statuses.Add;

        var runStateType = typeof(InstallQueue).GetNestedType("RunState", BindingFlags.NonPublic)!;
        var run = Activator.CreateInstance(runStateType, nonPublic: true)!;
        var handleWorkerLine = typeof(InstallQueue)
            .GetMethod("HandleWorkerLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var item = Item("eslint", "npm");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var line = JsonSerializer.Serialize(
            new
            {
                type = "result", sourceId = item.SourceId, itemId = item.Id, success = false,
                error = "failed", detail = "npm: install failed for 'eslint'. (exit code 1618)",
            }, Json);

        handleWorkerLine.Invoke(queue, [line, new List<UpdateItem> { item }, InstallAction.Install, reported, new List<UpdateItem>(), run, false]);

        var candidates = (System.Collections.IDictionary)runStateType.GetField("AutomaticRetryCandidates")!.GetValue(run)!;
        Assert.Single(candidates);
        Assert.Empty(reported);
        Assert.DoesNotContain(statuses, s => s.IsTerminal || s.Error is not null);
        Assert.Single(history.Read(), e => e.ItemId == "eslint" && !e.Success);
    }

    /// <summary>
    /// A source that can only hand an action off to an external program (Steam's own uninstall confirm)
    /// must never be recorded as a completed success OR a failure, neither is a claim this app can make.
    /// </summary>
    [Fact]
    public async Task A_manual_handoff_is_reported_and_recorded_as_neither_success_nor_failure()
    {
        var (queue, history, statuses) = Create(
            new StubSource("npm", throws: new ManualHandoffException("opened the vendor's own dialog")));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var final = statuses.Last(s => s.ItemId == "eslint");
        Assert.True(final.IsManualHandoff);

        var entry = history.Read().Single(e => e.ItemId == "eslint");
        Assert.True(entry.IsManualHandoff);
        Assert.False(entry.Success);
    }

    /// <summary>
    /// winget's 0x8A15002B means it has no version that fits THIS installation, nothing
    /// was changed and the user did nothing wrong, so the row must not read like a failed install. Its own
    /// terminal outcome, like the handoff above.
    /// </summary>
    [Fact]
    public async Task A_package_with_no_fitting_version_is_reported_as_neither_success_nor_failure()
    {
        var (queue, history, statuses) = Create(
            new StubSource("npm", throws: new NotApplicableException("no applicable upgrade")));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var final = statuses.Last(s => s.ItemId == "eslint");
        Assert.True(final.IsNotApplicable);
        Assert.Null(final.Success);

        var entry = history.Read().Single(e => e.ItemId == "eslint");
        Assert.True(entry.IsNotApplicable);
        Assert.False(entry.Success);
    }

    /// <summary>The source's own text for a package with no fitting version is English diagnostic output
    /// (winget's whole command ladder); the row and the history get the translated line, the raw text
    /// stays behind the Details click, the same split a failure uses.</summary>
    [Fact]
    public async Task A_package_with_no_fitting_version_shows_the_translated_line_and_keeps_the_raw_text_as_detail()
    {
        var (queue, history, statuses) = Create(
            new StubSource("npm", throws: new NotApplicableException("winget ladder: no applicable upgrade")));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var final = statuses.Last(s => s.ItemId == "eslint");
        Assert.Equal(LocalizationService.Instance["RowStuckDetail"], final.Error);
        Assert.Equal("winget ladder: no applicable upgrade", final.Detail);

        var entry = history.Read().Single(e => e.ItemId == "eslint");
        Assert.Equal(LocalizationService.Instance["RowStuckDetail"], entry.Error);
        Assert.Equal("winget ladder: no applicable upgrade", entry.Detail);
    }

    /// <summary>
    /// The manual-handoff and not-applicable flags must survive the pipe, they are what keeps an
    /// admin-side handoff from arriving as a plain red failure, and a message serialized before these
    /// flags existed must still deserialize cleanly, with both flags defaulting to false.
    /// </summary>
    [Fact]
    public void The_result_message_carries_both_terminal_non_failures_across_the_pipe()
    {
        var handoff = new InstallProgressMessage
        {
            Type = "result", SourceId = "winget", ItemId = "x", Success = false, IsManualHandoff = true,
        };

        var wire = JsonSerializer.Serialize(handoff, Json);
        var back = JsonSerializer.Deserialize<InstallProgressMessage>(wire, Json)!;

        Assert.True(back.IsManualHandoff);
        Assert.False(back.IsNotApplicable);

        var old = JsonSerializer.Deserialize<InstallProgressMessage>(
            """{"type":"result","sourceId":"winget","itemId":"x","success":true}""", Json)!;

        Assert.False(old.IsManualHandoff);
        Assert.False(old.IsNotApplicable);
    }

    /// <summary>The raw exception is log material: it is English and routinely contains user paths.</summary>
    [Fact]
    public async Task A_failing_install_shows_a_translated_line_not_the_tool_output()
    {
        var (queue, history, statuses) = Create(
            new StubSource("npm", throws: new InvalidOperationException(@"npm ERR! C:\Users\someone\AppData\npm-cache")));

        await queue.RunAsync([Item("eslint", "npm")], CancellationToken.None);

        var final = statuses.Last(s => s.ItemId == "eslint");
        Assert.False(final.Success);
        Assert.Equal(LocalizationService.Instance["InstallFailed"], final.Error);
        Assert.DoesNotContain(@"C:\Users", final.Error!);
        // Detail is the one place the raw tool output IS allowed (opened deliberately, never shown by default).
        Assert.False(string.IsNullOrWhiteSpace(final.Detail));
        Assert.NotEqual(final.Error, final.Detail);
        Assert.Contains(@"C:\Users", final.Detail!);

        var entry = history.Read().Single(e => e.ItemId == "eslint");
        Assert.False(entry.Success);
        Assert.DoesNotContain(@"C:\Users", entry.Error!);
    }

    /// <summary>
    /// The user-scope half bounds its total concurrency by the same setting the elevated worker's own
    /// budget comes from, but two DIFFERENT sources must still be able to run at the same time as before,
    /// the whole reason the class doc gives for not serializing everything.
    /// </summary>
    [Fact]
    public async Task Two_items_of_different_sources_overlap()
    {
        var shared = new SharedConcurrencyCounter();
        var npm = new ConcurrencyTrackingSource("npm", TimeSpan.FromMilliseconds(150), shared);
        var pip = new ConcurrencyTrackingSource("pip", TimeSpan.FromMilliseconds(150), shared);
        var (queue, _, _) = Create(npm, pip);

        await queue.RunAsync([Item("a", "npm"), Item("b", "pip")], CancellationToken.None);

        Assert.Equal(2, shared.Max);
    }

    /// <summary>
    /// The bug this fix fixes: <c>RunAsync</c> used to assign its own snapshot to one shared <c>_pending</c>
    /// field and null it out again in its own <c>finally</c>, so a second concurrent call on the SAME
    /// queue instance overwrote the first call's list out from under it, and the first call's own
    /// <c>finally</c> could then null a field the second call still needed. Each call now owns its own
    /// run state; both calls' own items must still be found, installed and recorded, not silently lost.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_RunAsync_calls_on_the_shared_queue_keep_their_own_item_lists()
    {
        var (queue, history, statuses) = Create(new StubSource("npm"), new StubSource("pip"));

        var callA = queue.RunAsync([Item("a", "npm")], CancellationToken.None);
        var callB = queue.RunAsync([Item("b", "pip")], CancellationToken.None);

        await Task.WhenAll(callA, callB);

        Assert.Contains(statuses, s => s.ItemId == "a" && s.Success == true);
        Assert.Contains(statuses, s => s.ItemId == "b" && s.Success == true);
        Assert.Contains(history.Read(), e => e.ItemId == "a" && e.Success);
        Assert.Contains(history.Read(), e => e.ItemId == "b" && e.Success);
    }

    /// <summary>A package installed per user makes the elevated helper refuse; the row is retried in this
    /// process. Until that retry answers, the refusal must not show up as a failure: no Failed status, no
    /// done count, nothing recorded against the three-strike rule.</summary>
    [Fact]
    public void A_user_scope_refusal_from_the_helper_is_not_reported_as_a_failure_before_the_retry()
    {
        var (queue, _, _) = Create();
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += statuses.Add;

        var runStateType = typeof(InstallQueue).GetNestedType("RunState", BindingFlags.NonPublic)!;
        var run = Activator.CreateInstance(runStateType, nonPublic: true)!;
        var handleWorkerLine = typeof(InstallQueue)
            .GetMethod("HandleWorkerLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var item = Item("bitcoin", "winget");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retries = new List<UpdateItem>();
        var line = JsonSerializer.Serialize(
            new
            {
                type = "result", sourceId = item.SourceId, itemId = item.Id, success = false,
                error = "failed", detail = "winget exit 0x8A15007D",
            }, Json);

        handleWorkerLine.Invoke(queue, [line, new List<UpdateItem> { item }, InstallAction.Install, reported, retries, run, false]);

        Assert.Single(retries);
        Assert.DoesNotContain(statuses, s => s.Success == false);
        Assert.Empty(reported);
    }

    /// <summary>A progress report that follows a transfer reset for the same item starts a fresh
    /// transfer instead of throwing. The race this guards is only reachable with two threads, so this is a
    /// contrast test of the single-threaded sequence.</summary>
    [Fact]
    public void A_progress_report_after_a_transfer_reset_starts_a_new_transfer()
    {
        var (queue, _, statuses) = Create();
        var report = typeof(InstallQueue).GetMethod("Report", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var reset = typeof(InstallQueue).GetMethod("ResetTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var item = Item("pkg", "npm");
        var progress = new InstallItemStatus("npm", "pkg", 10, "downloading", null, null) { BytesTotal = 100, BytesReceived = 10 };

        report.Invoke(queue, [progress]);
        reset.Invoke(queue, [item]);
        report.Invoke(queue, [progress with { BytesReceived = 20 }]);

        Assert.Equal(2, statuses.Count);
    }

    /// <summary>The in-process retry of a user scope refusal is the item's second attempt. When it fails
    /// with a cause the explainer marks safe to retry, that failure is final: the item must not be
    /// enrolled in the automatic retry pass, which would give it a third attempt.</summary>
    [Fact]
    public async Task A_user_scope_retry_that_fails_is_not_enrolled_for_a_third_attempt()
    {
        var source = new StubSource("npm", throwsByAttempt: _ =>
            new InvalidOperationException("npm: install failed for 'eslint'. (exit code 1618)"));
        var (queue, _, _) = Create(source);

        var runStateType = typeof(InstallQueue).GetNestedType("RunState", BindingFlags.NonPublic)!;
        var run = Activator.CreateInstance(runStateType, nonPublic: true)!;
        var retry = typeof(InstallQueue)
            .GetMethod("RetryUserScopeInProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var throttle = new SemaphoreSlim(1, 1);

        var task = (Task)retry.Invoke(queue,
            [new List<UpdateItem> { Item("eslint", "npm") }, InstallAction.Install,
             new HashSet<string>(StringComparer.OrdinalIgnoreCase), throttle, run, CancellationToken.None])!;
        await task;

        var candidates = (System.Collections.IDictionary)runStateType.GetField("AutomaticRetryCandidates")!.GetValue(run)!;
        Assert.Equal(1, source.AttemptCount);
        Assert.Empty(candidates);
    }

    /// <summary>The in-process retry of a user scope refusal is one more in-process install, so it waits
    /// for a slot of the shared parallel-install budget instead of running on top of a full set.</summary>
    [Fact]
    public async Task A_user_scope_retry_waits_for_a_slot_of_the_parallel_install_budget()
    {
        var source = new StubSource("npm");
        var (queue, _, _) = Create(source);

        var runStateType = typeof(InstallQueue).GetNestedType("RunState", BindingFlags.NonPublic)!;
        var run = Activator.CreateInstance(runStateType, nonPublic: true)!;
        var retry = typeof(InstallQueue)
            .GetMethod("RetryUserScopeInProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var throttle = new SemaphoreSlim(0, 1);

        var task = (Task)retry.Invoke(queue,
            [new List<UpdateItem> { Item("eslint", "npm") }, InstallAction.Install,
             new HashSet<string>(StringComparer.OrdinalIgnoreCase), throttle, run, CancellationToken.None])!;

        await Task.Delay(200);
        Assert.False(task.IsCompleted);
        Assert.Equal(0, source.AttemptCount);

        throttle.Release();
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, source.AttemptCount);
        Assert.Equal(1, throttle.CurrentCount);
    }

    /// <summary>
    /// The elevated pipe's own reader thread runs <c>HandleWorkerLine</c>, which adds to the SAME
    /// <c>reported</c> set the in-process half writes to (under <c>_reportLock</c>) while both halves run
    /// concurrently within one <see cref="InstallQueue.RunAsync"/> call. A plain <see cref="HashSet{T}"/>
    /// is not thread-safe, so a writer that skips the lock corrupts or drops entries the other writer added
    /// under it. Driving a REAL admin batch needs a real UAC prompt (not available here), so this drives
    /// <c>HandleWorkerLine</c> directly via reflection, racing it against a directly-locked writer that
    /// mirrors the in-process half exactly (same field, same lock object).
    /// </summary>
    [Fact]
    public void HandleWorkerLine_shares_the_reported_set_with_the_in_process_half_without_losing_entries()
    {
        var (queue, _, _) = Create();

        var reportLock = typeof(InstallQueue)
            .GetField("_reportLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(queue)!;
        var runStateType = typeof(InstallQueue).GetNestedType("RunState", BindingFlags.NonPublic)!;
        var run = Activator.CreateInstance(runStateType, nonPublic: true)!;
        var handleWorkerLine = typeof(InstallQueue)
            .GetMethod("HandleWorkerLine", BindingFlags.NonPublic | BindingFlags.Instance)!;

        const int perSide = 500;
        var elevatedItems = Enumerable.Range(0, perSide).Select(i => Item($"elevated-{i}", "winget")).ToList();

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userScopeRetries = new List<UpdateItem>();

        Exception? failure = null;
        using var barrier = new Barrier(2);

        // HandleWorkerLine does real work per call (JSON parse, logging, a history-file append under the
        // same lock), so it is far slower per item than a bare locked Add. A fixed count on the in-process
        // side would race the elevated side for only its first few milliseconds and then run on alone,
        // uncontended, for the rest, exactly the flaky-negative shape this test must avoid. Instead the
        // in-process side keeps adding, as fast as it can, for as long as the elevated side is still
        // running, so the two are guaranteed to overlap for the ENTIRE elevated run, not just its start.
        var elevatedDone = new ManualResetEventSlim(false);
        var inProcessCount = 0;

        var elevatedThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                foreach (var item in elevatedItems)
                {
                    var line = JsonSerializer.Serialize(
                        new { type = "result", sourceId = item.SourceId, itemId = item.Id, success = true }, Json);
                    handleWorkerLine.Invoke(
                        queue, [line, elevatedItems, InstallAction.Install, reported, userScopeRetries, run, false]);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                elevatedDone.Set();
            }
        });

        var inProcessThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                while (!elevatedDone.IsSet)
                {
                    lock (reportLock)
                        reported.Add($"npm:in-process-{inProcessCount}");
                    inProcessCount++;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        elevatedThread.Start();
        inProcessThread.Start();
        elevatedThread.Join();
        inProcessThread.Join();

        Assert.Null(failure);
        Assert.Equal(perSide + inProcessCount, reported.Count);
        Assert.All(elevatedItems, item => Assert.Contains(Key(item), reported));
        for (var i = 0; i < inProcessCount; i++)
            Assert.Contains($"npm:in-process-{i}", reported);
    }

    /// <summary>Mirrors <see cref="InstallQueue"/>'s own private key format, for the concurrency test above.</summary>
    private static string Key(UpdateItem item) => $"{item.SourceId}:{item.Id}";

    /// <summary>
    /// The user-scope and admin halves are two such concurrent calls on the SAME queue instance
    /// (<see cref="MainViewModel"/>'s install loop, this fix): a slow one must never hold up a fast one
    /// that started alongside it. Driving a REAL admin/elevated call needs a real UAC prompt, so this
    /// proves the queue-level mechanism with two ordinary calls instead, one slow, one fast.
    /// </summary>
    [Fact]
    public async Task A_fast_concurrent_call_finishes_before_a_slow_one_on_the_same_queue()
    {
        var order = new List<string>();
        var orderLock = new object();
        var slowSource = new TimedStubSource("pip", TimeSpan.FromMilliseconds(200), order, orderLock);
        var fastSource = new TimedStubSource("npm", TimeSpan.FromMilliseconds(20), order, orderLock);
        var (queue, _, _) = Create(slowSource, fastSource);

        var slowCall = queue.RunAsync([Item("slow", "pip")], CancellationToken.None);
        var fastCall = queue.RunAsync([Item("fast", "npm")], CancellationToken.None);

        await Task.WhenAll(slowCall, fastCall);

        Assert.Equal(["fast", "slow"], order);
    }

    [Fact]
    public async Task One_failing_item_does_not_stop_the_rest()
    {
        var (queue, _, statuses) = Create(
            new StubSource("npm", failFor: "broken"));

        await queue.RunAsync([Item("broken", "npm"), Item("fine", "npm")], CancellationToken.None);

        Assert.Contains(statuses, s => s.ItemId == "broken" && s.Success == false);
        Assert.Contains(statuses, s => s.ItemId == "fine" && s.Success == true);
    }

    /// <summary>
    /// This fix's own stop button: <see cref="InstallQueue.CancelItem"/> stops just the ONE row named,
    /// while a different source's item running in the very same <c>RunAsync</c> call keeps going and
    /// finishes normally, both in the reported statuses and in the history file.
    /// </summary>
    [Fact]
    public async Task CancelItem_stops_only_the_named_row_while_the_rest_of_the_batch_keeps_running()
    {
        // Neither source needs elevation, so both run through the in-process, user-scope half; a
        // source that DID need elevation would instead go to the elevated worker over a real UAC prompt,
        // which is the elevated worker's own concern (InstallWorkerTests covers cancellation there).
        var blocked = new BlockingSource("scoop");
        var (queue, history, statuses) = Create(blocked, new StubSource("npm"));

        var runTask = queue.RunAsync([Item("stopped", "scoop"), Item("fine", "npm")], CancellationToken.None);

        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        queue.CancelItem("scoop", "stopped");

        await runTask;

        Assert.Contains(statuses, s => s.ItemId == "stopped" && s.IsCancelled && s.Success == null);
        Assert.Contains(statuses, s => s.ItemId == "fine" && s.Success == true);

        var entry = history.Read().Single(e => e.ItemId == "stopped");
        Assert.True(entry.IsCancelled);
        Assert.False(entry.Success);
        Assert.Contains(history.Read(), e => e.ItemId == "fine" && e.Success);
    }

    /// <summary>
    /// Calling <see cref="InstallQueue.CancelItem"/> for a row that already finished, or one that was never
    /// running at all, is a plain no-op, the same "unknown key, not an error" rule the elevated worker's own
    /// per-item cancellation follows (a click and the item finishing can race in the real UI).
    /// </summary>
    [Fact]
    public async Task CancelItem_for_an_unknown_or_finished_row_is_a_no_op()
    {
        var (queue, _, statuses) = Create(new StubSource("npm"));

        await queue.RunAsync([Item("done", "npm")], CancellationToken.None);

        var exception = Record.Exception(() => queue.CancelItem("npm", "done"));
        Assert.Null(exception);

        var recordedBeforeNoOp = statuses.Count;
        queue.CancelItem("npm", "never-existed");
        Assert.Equal(recordedBeforeNoOp, statuses.Count);
    }

    /// <summary>
    /// Cancelling a batch must close out every remaining row instead of leaving it on "installing" for
    /// ever, but NOT write those rows into the history as failures. The elevated worker installs each
    /// package with an untokened cancellation on purpose, so the one it was on has most likely finished,
    /// and the history is the only record there is of what actually happened.
    /// </summary>
    [Fact]
    public async Task Cancelling_closes_out_every_item_that_never_ran_without_claiming_it_failed()
    {
        using var cts = new CancellationTokenSource();
        var source = new StubSource("npm", onInstall: _ => cts.Cancel());
        var (queue, history, statuses) = Create(source);

        var items = new[] { Item("first", "npm"), Item("second", "npm"), Item("third", "npm") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync(items, cts.Token));

        foreach (var id in new[] { "second", "third" })
        {
            var final = statuses.Last(s => s.ItemId == id);
            Assert.True(final.IsCancelled);
            Assert.Null(final.Success);
            Assert.Equal(LocalizationService.Instance["InstallCancelled"], final.Error);
            // Same rule as the unknown-source case above: a row closed out this way must still open its
            // error-detail button on something more specific than the translated summary sentence.
            Assert.False(string.IsNullOrWhiteSpace(final.Detail));
            Assert.NotEqual(final.Error, final.Detail);
            Assert.DoesNotContain(history.Read(), e => e.ItemId == id);
        }
    }

    /// <summary>
    /// 22.08.2026: this error state used to report no Detail at all, nothing ever failed to run a real
    /// tool here, so the generic catch that normally fills Detail from the exception never runs, and the
    /// row's error-detail button opened on an empty window. Every path that reports Success==false must
    /// carry a Detail that says more than the row's own translated summary line.
    /// </summary>
    [Fact]
    public async Task An_unknown_source_is_reported_rather_than_silently_skipped()
    {
        var (queue, _, statuses) = Create(new StubSource("npm"));

        await queue.RunAsync([Item("mystery", "nosuchsource")], CancellationToken.None);

        var final = statuses.Last(s => s.ItemId == "mystery");
        Assert.False(final.Success);
        Assert.False(string.IsNullOrWhiteSpace(final.Detail));
        Assert.NotEqual(final.Error, final.Detail);
        Assert.Contains("nosuchsource", final.Detail);
    }

    /// <summary>
    /// The queue panel's drag-and-drop must actually change install order, not just the visual list
    ///, reordering while the first item is still running has to change which of the
    /// two REMAINING items goes next.
    /// </summary>
    [Fact]
    public async Task Reordering_mid_batch_changes_which_item_installs_next()
    {
        InstallQueue? queueRef = null;
        var source = new StubSource("npm", onInstall: item =>
        {
            if (item.Id == "first")
                queueRef!.Reorder([("npm", "third"), ("npm", "second")]);
        });
        var (queue, _, statuses) = Create(source);
        queueRef = queue;

        await queue.RunAsync(
            [Item("first", "npm"), Item("second", "npm"), Item("third", "npm")], CancellationToken.None);

        var order = statuses.Where(s => s.Success == true).Select(s => s.ItemId).ToList();
        Assert.Equal(["first", "third", "second"], order);
    }

    /// <summary>An item removed from the queue before its turn never installs and leaves no history.</summary>
    [Fact]
    public async Task A_removed_item_never_installs_and_is_not_recorded()
    {
        InstallQueue? queueRef = null;
        var source = new StubSource("npm", onInstall: item =>
        {
            if (item.Id == "first")
                Assert.True(queueRef!.Remove("npm", "second"));
        });
        var (queue, history, statuses) = Create(source);
        queueRef = queue;

        await queue.RunAsync([Item("first", "npm"), Item("second", "npm")], CancellationToken.None);

        Assert.DoesNotContain(statuses, s => s.ItemId == "second");
        Assert.DoesNotContain(history.Read(), e => e.ItemId == "second");
    }

    /// <summary>Reordering or removing outside any running batch is a harmless no-op, not a crash.</summary>
    [Fact]
    public void Reorder_and_remove_are_safe_when_nothing_is_queued()
    {
        var (queue, _, _) = Create(new StubSource("npm"));

        queue.Reorder([("npm", "whatever")]);
        Assert.False(queue.Remove("npm", "whatever"));
    }

    /// <summary>Unwraps one reply envelope into the job id it belongs to and the message inside it.</summary>
    private static (string Id, InstallProgressMessage? Message) ReadReply(string line)
    {
        var reply = JsonSerializer.Deserialize<ElevatedReply>(line, Json)!;
        return (reply.Id, reply.Payload.Deserialize<InstallProgressMessage>(Json));
    }

    /// <summary>
    /// The whole elevated protocol without UAC: the worker connects back, is handed its job over the
    /// pipe, reports a result per item and finishes with "done".
    /// </summary>
    [Fact]
    public async Task The_worker_receives_its_job_over_the_pipe_and_reports_back()
    {
        var pipeName = $"UltimateUpdater-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var worker = ElevatedWorker.RunAsync(pipeName, Environment.ProcessId, new LoggingService());

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));

        // Not "using": the worker outlives the job now, so the test disconnects the pipe to end it, and a
        // scope-end flush onto a disconnected pipe throws. Both are disposed by hand, before that.
        var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        // CreateRestorePoint stays FALSE: this runs on a real machine and must change nothing.
        var job = new InstallJobFile
        {
            CreateRestorePoint = false,
            Language = "en",
            Items = [new InstallJobItem { SourceId = "nosuchsource", ItemId = "mystery", Name = "Mystery" }],
        };

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new { id = "job-1", kind = "install", exclusive = true, payload = job }, Json));

        var messages = new List<InstallProgressMessage>();
        while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) is { } line)
        {
            var (id, message) = ReadReply(line);

            // Every reply is tagged with the job it belongs to; that is what lets several run at once.
            Assert.Equal("job-1", id);

            if (message is null)
                continue;

            messages.Add(message);
            if (message.Type == "done")
                break;
        }

        Assert.Contains(messages, m => m.Type == "result" && m.ItemId == "mystery" && !m.Success);
        Assert.Equal("done", messages[^1].Type);

        // One worker now serves the whole session, so it waits for the next job until the pipe closes.
        // The stream wrappers go first: flushing them onto a disconnected pipe throws.
        await writer.DisposeAsync();
        reader.Dispose();
        server.Disconnect();

        Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    /// <summary>
    /// Two jobs in flight at once, each answered under its own id. This is what lets a cleanup measurement
    /// run alongside an install, and, just as importantly, it is what stops one conversation's leftovers
    /// being read as the next one's answers, which left the queue stuck after a stop.
    /// </summary>
    [Fact]
    public async Task Replies_are_tagged_so_two_jobs_can_be_in_flight_at_once()
    {
        var pipeName = $"UltimateUpdater-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var worker = ElevatedWorker.RunAsync(pipeName, Environment.ProcessId, new LoggingService());
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        static object Job(string id, string itemId) => new
        {
            id,
            kind = "install",
            exclusive = true,
            payload = new InstallJobFile
            {
                CreateRestorePoint = false,
                Language = "en",
                Items = [new InstallJobItem { SourceId = "nosuchsource", ItemId = itemId, Name = itemId }],
            },
        };

        // Both go out before either is answered.
        await writer.WriteLineAsync(JsonSerializer.Serialize(Job("a", "first"), Json));
        await writer.WriteLineAsync(JsonSerializer.Serialize(Job("b", "second"), Json));

        var doneIds = new List<string>();
        var resultsById = new Dictionary<string, string>(StringComparer.Ordinal);

        while (doneIds.Count < 2
               && await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
        {
            var (id, message) = ReadReply(line);
            if (message is null)
                continue;

            if (message.Type == "result" && message.ItemId is { } itemId)
                resultsById[id] = itemId;
            else if (message.Type == "done")
                doneIds.Add(id);
        }

        Assert.Equal(["a", "b"], doneIds.Order());

        // Each job's result came back under ITS id, not the other's.
        Assert.Equal("first", resultsById["a"]);
        Assert.Equal("second", resultsById["b"]);

        await writer.DisposeAsync();
        reader.Dispose();
        server.Disconnect();

        Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    /// <summary>An empty job must still be answered, or the app waits on a conversation that never starts.</summary>
    [Fact]
    public async Task The_worker_answers_an_empty_job()
    {
        var pipeName = $"UltimateUpdater-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var worker = ElevatedWorker.RunAsync(pipeName, Environment.ProcessId, new LoggingService());
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));

        // Not "using": the worker outlives the job now, so the test disconnects the pipe to end it, and a
        // scope-end flush onto a disconnected pipe throws. Both are disposed by hand, before that.
        var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                id = "empty-1",
                kind = "install",
                exclusive = true,
                payload = new InstallJobFile { CreateRestorePoint = false, Language = "en", Items = [] },
            },
            Json));

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.NotNull(line);
        var (emptyId, emptyMessage) = ReadReply(line!);
        Assert.Equal("empty-1", emptyId);
        Assert.Equal("done", emptyMessage!.Type);

        // The worker now stays alive for further jobs; closing the pipe is what ends it.
        await writer.DisposeAsync();
        reader.Dispose();
        server.Disconnect();

        Assert.Equal(0, await worker.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    private static (InstallQueue Queue, HistoryService History, List<InstallItemStatus> Statuses) Create(
        params IUpdateSource[] sources)
    {
        var logging = new LoggingService();
        var temp = Directory.CreateTempSubdirectory("wu-install-test-").FullName;
        var settings = new SettingsService(logging, Path.Combine(temp, "settings.json"));

        // An explicit path of its own, never the shared HistoryService.EffectivePath: that static fallback
        // is also what WpfHarness.RedirectUserDataForTest reassigns for every UI test on its own thread,
        // and other non-UI test classes read the very same default when no UI test has redirected it yet.
        // A private file per Create() call removes that shared mutable state outright.
        var history = new HistoryService(logging, Path.Combine(temp, "history.jsonl"));
        var queue = new InstallQueue(sources, settings, history, logging);

        // Reproduced under load: InstallQueue.StatusChanged fires from whichever thread finishes an item,
        // and several can run at once by design (different source ids run concurrently, and a Progress<T>
        // built with no captured SynchronizationContext delivers its callback on a ThreadPool thread, not
        // on the caller's own async continuation, so a stray progress tick can land on a different thread
        // than the item's own terminal report). The real subscriber, MainViewModel.OnInstallStatusChanged,
        // already takes its own lock before touching shared state; this fake one used a bare List<T>, whose
        // Add is not thread-safe under concurrent callers and silently lost entries under real contention.
        // That is what made CancelItem_stops_only_the_named_row_while_the_rest_of_the_batch_keeps_running
        // and Reordering_mid_batch_changes_which_item_installs_next fail once each in a full run and pass
        // every time alone: only a loaded run has enough genuinely overlapping threads to hit it.
        var statusesLock = new object();
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += s =>
        {
            lock (statusesLock)
                statuses.Add(s);
        };

        return (queue, history, statuses);
    }

    private static UpdateItem Item(string id, string sourceId) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.DeveloperTools,
        Name = id,
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    };

    /// <summary>Counts how many <see cref="ConcurrencyTrackingSource"/> installs, across however many
    /// instances share it, are in flight at once.</summary>
    private sealed class SharedConcurrencyCounter
    {
        private readonly object _lock = new();
        private int _current;

        public int Max { get; private set; }

        public void Enter()
        {
            lock (_lock)
            {
                _current++;
                if (_current > Max)
                    Max = _current;
            }
        }

        public void Exit()
        {
            lock (_lock)
                _current--;
        }
    }

    /// <summary>Installs nothing real; only proves how many installs (of this source, and across every
    /// source sharing the same <see cref="SharedConcurrencyCounter"/>) were in flight at once.</summary>
    private sealed class ConcurrencyTrackingSource(string id, TimeSpan delay, SharedConcurrencyCounter shared) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            shared.Enter();
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                shared.Exit();
            }
        }
    }

    /// <summary>Installs nothing real; waits out its own delay, then records its item id into a list
    /// shared across sources, so two concurrent calls' relative finishing order can be proven.</summary>
    private sealed class TimedStubSource(string id, TimeSpan delay, List<string> finishOrder, object orderLock) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            lock (orderLock)
                finishOrder.Add(item.Id);
        }
    }

    /// <summary>Installs nothing real; blocks until its OWN cancellation token fires, signalling
    /// <see cref="Started"/> once the install has actually begun, so a test can cancel it mid-flight
    /// rather than racing a fixed delay.</summary>
    private sealed class BlockingSource(string id) : IUpdateSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    /// <summary>
    /// Reports one progress line carrying bytes, then succeeds without reporting any bytes of
    /// its own on the terminal status, the same shape every real source uses (see
    /// <see cref="StubSource.InstallAsync"/> for the plain no-bytes case this only adds a download to).
    /// </summary>
    private sealed class DownloadingSource(string id, long receivedAtEnd, long total) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new Models.InstallProgress
            {
                PercentComplete = 50, BytesReceived = receivedAtEnd, BytesTotal = total, StatusText = "downloading",
            });

            // Progress<T> has no captured synchronization context in a test (see the note on
            // A_successful_install_is_reported_and_recorded above) so the report above is only queued, not
            // yet delivered, when this method would otherwise return; without this wait the terminal status
            // right after InstallAsync can arrive before the queue has any transfer tracked to close.
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>
    /// Reports real progress partway through a download, then fails outright - the same shape
    /// as a network error mid-transfer. Used to prove <see cref="InstallQueue.CloseTransfer"/> keeps the
    /// last REAL received count on a failure rather than stamping the row (and the batch total) as if the
    /// whole file had arrived.
    /// </summary>
    private sealed class FailingDownloadSource(string id, long receivedBeforeFailure, long total) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(new Models.InstallProgress
            {
                PercentComplete = 50, BytesReceived = receivedBeforeFailure, BytesTotal = total, StatusText = "downloading",
            });

            // Same reason as DownloadingSource's own wait: the report above must actually be tracked by
            // the queue's Report() before the failure closes the transfer, or there is nothing to close.
            await Task.Delay(200, cancellationToken);

            throw new InvalidOperationException($"{id}: download failed partway through.");
        }
    }

    /// <summary>
    /// Fails on its FIRST attempt with a message <see cref="Sources.InstallErrorExplainer"/>
    /// marks as safe to retry automatically (same message shape as
    /// <see cref="A_retryable_failure_is_retried_exactly_once_and_can_still_succeed"/>'s own source), after
    /// reporting real download progress, then SUCCEEDS on the retry round with its own real progress too -
    /// proving the retry's progress is not read as stale by a transfer entry the first, failed attempt left
    /// behind marked finished.
    /// </summary>
    private sealed class RetryableDownloadSource(string id, long total) : IUpdateSource
    {
        private int _attempts;

        public int AttemptCount => _attempts;

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);

            progress.Report(new Models.InstallProgress
            {
                PercentComplete = 50, BytesReceived = total / 2, BytesTotal = total, StatusText = $"downloading attempt {attempt}",
            });
            await Task.Delay(200, cancellationToken);

            if (attempt == 1)
                throw new InvalidOperationException($"{id}: install failed for '{item.Id}'. (exit code 1618)");

            progress.Report(new Models.InstallProgress
            {
                PercentComplete = 100, BytesReceived = total, BytesTotal = total, StatusText = "done",
            });
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>Same shape as <see cref="DownloadingSource"/>, keeping the <see cref="IProgress{T}"/> the
    /// queue handed it so a test can report through it again after the install already finished.</summary>
    private sealed class ProgressCapturingSource(string id) : IUpdateSource
    {
        public IProgress<Models.InstallProgress>? CapturedProgress { get; private set; }

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            CapturedProgress = progress;
            progress.Report(new Models.InstallProgress
            {
                PercentComplete = 50, BytesReceived = 50, BytesTotal = 100, StatusText = "downloading",
            });

            // Same wait as DownloadingSource: the queue must have this transfer tracked BEFORE the
            // terminal status fires, or the later test-fired "late" report would look like a brand new
            // transfer starting rather than a stale one being dropped.
            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>Installs nothing; it only plays the part so the queue's own behaviour can be seen.</summary>
    private sealed class StubSource(
        string id,
        Exception? throws = null,
        string? failFor = null,
        Action<UpdateItem>? onInstall = null,
        Func<string, string?>? getInstalledVersion = null,
        Func<int, Exception?>? throwsByAttempt = null) : IUpdateSource
    {
        private int _attempts;

        /// <summary>This fix's own proof: how many times InstallAsync actually ran for this source, across
        /// the first round AND any automatic retry round.</summary>
        public int AttemptCount => _attempts;

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            onInstall?.Invoke(item);
            cancellationToken.ThrowIfCancellationRequested();

            // throwsByAttempt (this fix's tests) decides per call; the plain throws/failFor pair (every
            // pre-existing test) behaves exactly as before when it is not given.
            var exception = throwsByAttempt is not null
                ? throwsByAttempt(attempt)
                : throws is not null || item.Id == failFor ? throws ?? new InvalidOperationException("stub failure") : null;

            if (exception is not null)
                throw exception;

            progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
            return Task.CompletedTask;
        }

        /// <summary>This fix's re-check; null (the default, unset) means this stub does not implement it,
        /// same "no statement" answer the interface default gives.</summary>
        public Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken) =>
            Task.FromResult(getInstalledVersion?.Invoke(item.Id));
    }
}

/// <summary>
/// --mock-sources must be able to exercise what a real session can, not only the happy
/// path. A plain <see cref="IProgress{T}"/> recorder is used here rather than <see cref="Progress{T}"/>,
/// which has no captured synchronization context in a test and can deliver out of order (the same note
/// several <see cref="InstallQueueTests"/> carry); recording synchronously sidesteps that entirely for
/// these source-level tests, which never go through <see cref="InstallQueue"/> at all.
/// </summary>
public class MockUpdateSourceTests
{
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }

    /// <summary>The count this fix promises to keep: two existing rows gained a deterministic outcome,
    /// none was added or removed.</summary>
    [Fact]
    public void CreateAll_still_returns_exactly_eight_sources()
    {
        Assert.Equal(8, MockUpdateSource.CreateAll().Count);
    }

    /// <summary>This fix's restart prompt had no deterministic source before this: every mock row finished
    /// clean. windowsupdate's own install now asks for a reboot on its LAST progress line only, matching
    /// how NeedsReboot reads a real source's own final report.</summary>
    [Fact]
    public async Task The_windowsupdate_mock_source_asks_for_a_reboot_only_on_its_last_report()
    {
        var source = MockUpdateSource.CreateAll().Single(s => s.Id == "windowsupdate");
        var progress = new SyncProgress<Models.InstallProgress>();
        var item = new UpdateItem { Id = "x", SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers, Name = "x" };

        await source.InstallAsync(item, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Reports);
        Assert.True(progress.Reports[^1].RebootRequired);
        Assert.DoesNotContain(progress.Reports.Take(progress.Reports.Count - 1), r => r.RebootRequired);
    }

    /// <summary>The non-fatal failure path had no deterministic source either: scoop's own mock failure is
    /// a SCAN failure with nothing installable behind it. pip's install now fails partway through instead
    /// of completing, a real Exception InstallQueue reports as an ordinary Success == false row, not a
    /// terminal non-failure.</summary>
    [Fact]
    public async Task The_pip_mock_source_fails_partway_through_its_own_install()
    {
        var source = MockUpdateSource.CreateAll().Single(s => s.Id == "pip");
        var progress = new SyncProgress<Models.InstallProgress>();
        var item = new UpdateItem { Id = "numpy", SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = "numpy" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.InstallAsync(item, progress, CancellationToken.None));

        // It fails PARTWAY, not before doing anything at all: a row that jumps straight from 0 % to
        // "failed" with no visible progress in between would look identical to every already-covered
        // "never started" outcome, not a new one.
        Assert.NotEmpty(progress.Reports);
    }

    /// <summary>The other installable mock row (Tier A, not touched by this item) is unaffected: still a
    /// plain, deterministic success with no reboot asked for, the existing behaviour every other test that
    /// installs a mock row relies on.</summary>
    [Fact]
    public async Task Winget_mock_source_still_succeeds_plainly_with_no_reboot()
    {
        var source = MockUpdateSource.CreateAll().Single(s => s.Id == "winget");
        var progress = new SyncProgress<Models.InstallProgress>();
        var item = new UpdateItem { Id = "Git.Git", SourceId = "winget", Category = UpdateCategory.Software, Name = "Git" };

        await source.InstallAsync(item, progress, CancellationToken.None);

        Assert.NotEmpty(progress.Reports);
        Assert.DoesNotContain(progress.Reports, r => r.RebootRequired);
    }
}
