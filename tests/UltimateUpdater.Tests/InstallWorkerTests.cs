using System.Text;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevated worker is trusted by one UAC prompt for its whole session, so it must never
/// install whatever an unelevated job file happens to name. These cover the per-item
/// validation: source id, field length, and batch size cap.
/// </summary>
public class InstallWorkerTests
{
    private static (StreamWriter Writer, MemoryStream Buffer) NewWriter()
    {
        var buffer = new MemoryStream();
        var writer = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        return (writer, buffer);
    }

    private static string ReadAll(MemoryStream buffer)
    {
        buffer.Position = 0;
        return new StreamReader(buffer).ReadToEnd();
    }

    [Fact]
    public async Task A_source_that_does_not_need_elevation_is_rejected_per_item_and_never_resolved()
    {
        var (writer, buffer) = NewWriter();
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "pip", ItemId = "black", Name = "black" }],
        };

        var stillConnected = await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None);

        Assert.True(stillConnected);
        var output = ReadAll(buffer);
        Assert.Contains("\"type\":\"result\"", output);
        Assert.Contains("\"success\":false", output);
        Assert.Contains("\"type\":\"done\"", output);
    }

    /// <summary>
    /// This fix's belt-and-braces guard: an id that passes the elevation allowlist (so it is not caught by
    /// <see cref="A_source_that_does_not_need_elevation_is_rejected_per_item_and_never_resolved"/>) but
    /// names no source at all in the set this worker actually resolved against - the shape a compromised or
    /// buggy unelevated sender, or a mock row sharing a real id, would produce. The worker refuses the job
    /// rather than guessing what to do with it, one layer past the bare id-string check above.
    /// </summary>
    [Fact]
    public async Task A_source_id_absent_from_the_resolved_registry_is_rejected_rather_than_guessed()
    {
        var (writer, buffer) = NewWriter();
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "winget", ItemId = "git", Name = "git" }],
        };

        // The elevation allowlist accepts "winget", but the resolved set handed to this worker names no
        // such source at all - the empty list stands in for "SourceRegistry never produced one under
        // this id", the exact gap a mock row sharing a real id could otherwise fall through.
        var stillConnected = await InstallWorker.RunJobAsync(
            job, writer, "job1", new LoggingService(), CancellationToken.None, sources: []);

        Assert.True(stillConnected);
        var output = ReadAll(buffer);
        Assert.Contains("\"type\":\"result\"", output);
        Assert.Contains("\"success\":false", output);
        Assert.Contains("Unknown source", output);
    }

    [Fact]
    public async Task An_oversized_field_is_rejected_per_item_and_never_resolved()
    {
        var (writer, buffer) = NewWriter();
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "winget", ItemId = new string('a', 1000), Name = "x" }],
        };

        var stillConnected = await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None);

        Assert.True(stillConnected);
        var output = ReadAll(buffer);
        Assert.Contains("\"type\":\"result\"", output);
        Assert.Contains("\"success\":false", output);
    }

    [Theory]
    [InlineData("itemId")]
    [InlineData("name")]
    [InlineData("sourceId")]
    public async Task A_null_required_field_rejects_that_item_and_the_valid_one_still_runs(string nullField)
    {
        var (writer, buffer) = NewWriter();
        var source = new ConcurrencyTrackingSource("chocolatey", TimeSpan.FromMilliseconds(1));
        var bad = new InstallJobItem
        {
            SourceId = nullField == "sourceId" ? null! : "chocolatey",
            ItemId = nullField == "itemId" ? null! : "bad",
            Name = nullField == "name" ? null! : "bad",
        };
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "chocolatey", ItemId = "good", Name = "good" }, bad],
        };

        var stillConnected = await InstallWorker.RunJobAsync(
            job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.True(stillConnected);
        var lines = ReadAll(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var results = lines.Where(l => l.Contains("\"type\":\"result\"")).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, l => l.Contains("\"itemId\":\"good\"") && l.Contains("\"success\":true"));
        Assert.Contains(results, l => l.Contains("\"success\":false") && l.Contains("Rejected: a required field is missing."));
        Assert.Contains(lines, l => l.Contains("\"type\":\"done\""));
    }

    [Fact]
    public async Task A_null_item_in_the_list_ends_the_job_with_done_only()
    {
        var (writer, buffer) = NewWriter();
        var job = new InstallJobFile { Items = [null!] };

        var stillConnected = await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None);

        Assert.True(stillConnected);
        var output = ReadAll(buffer);
        Assert.DoesNotContain("\"type\":\"result\"", output);
        Assert.Contains("\"type\":\"done\"", output);
    }

    [Fact]
    public async Task A_batch_over_the_item_cap_is_rejected_outright()
    {
        var (writer, buffer) = NewWriter();
        var items = Enumerable.Range(0, 501)
            .Select(i => new InstallJobItem { SourceId = "winget", ItemId = $"pkg{i}", Name = "x" })
            .ToList();
        var job = new InstallJobFile { Items = items };

        var stillConnected = await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None);

        Assert.True(stillConnected);
        Assert.DoesNotContain("\"type\":\"result\"", ReadAll(buffer));
    }

    /// <summary>Every non-winget source still keeps a single global install state (npm, pip, scoop, ...);
    /// two of its own items must never run at the same time even when the budget would allow it.</summary>
    [Fact]
    public async Task Two_items_of_the_same_non_winget_source_never_overlap()
    {
        var (writer, _) = NewWriter();
        var source = new ConcurrencyTrackingSource("chocolatey", TimeSpan.FromMilliseconds(150));
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 5,
            Items =
            [
                new InstallJobItem { SourceId = "chocolatey", ItemId = "a", Name = "a" },
                new InstallJobItem { SourceId = "chocolatey", ItemId = "b", Name = "b" },
            ],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Equal(1, source.MaxObserved);
    }

    /// <summary>winget is the one source allowed to run several of its own items at once, when the budget
    /// allows it.</summary>
    [Fact]
    public async Task Two_winget_items_overlap_when_the_budget_allows_it()
    {
        var (writer, _) = NewWriter();
        var source = new ConcurrencyTrackingSource("winget", TimeSpan.FromMilliseconds(150));
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 2,
            Items =
            [
                new InstallJobItem { SourceId = "winget", ItemId = "a", Name = "a" },
                new InstallJobItem { SourceId = "winget", ItemId = "b", Name = "b" },
            ],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Equal(2, source.MaxObserved);
    }

    /// <summary>The worker never trusts the job's own budget claim; it clamps to the hard 1-4 cap itself
    /// and the number actually in flight never exceeds it.</summary>
    [Fact]
    public async Task The_number_in_flight_never_exceeds_the_clamped_budget()
    {
        var (writer, _) = NewWriter();
        var source = new ConcurrencyTrackingSource("winget", TimeSpan.FromMilliseconds(150));
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 999, // the worker must clamp this to 5, never trust it as-is
            Items = Enumerable.Range(0, 6)
                .Select(i => new InstallJobItem { SourceId = "winget", ItemId = $"pkg{i}", Name = "x" })
                .ToList(),
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Equal(5, source.MaxObserved);
    }

    /// <summary>
    /// Windows Installer refuses a second concurrent MSI transaction (1618); with several installs able to
    /// run at once, that is now an expected outcome the queue retries once, alone, rather than a hard
    /// failure. This item fails its first attempt exactly the way winget's own pass-through would, then
    /// succeeds.
    /// </summary>
    [Fact]
    public async Task An_item_refused_because_another_installation_is_running_is_retried_once_alone()
    {
        var (writer, buffer) = NewWriter();
        var source = new FlakyMsiSource("winget", failFirstAttemptsOf: ["flaky"]);
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 2,
            Items =
            [
                new InstallJobItem { SourceId = "winget", ItemId = "flaky", Name = "flaky" },
                new InstallJobItem { SourceId = "winget", ItemId = "ok", Name = "ok" },
            ],
        };

        var stillConnected = await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.True(stillConnected);
        var results = ReadAll(buffer).Split('\n')
            .Where(l => l.Contains("\"type\":\"result\"") && l.Contains("\"itemId\":\"flaky\""))
            .ToList();

        // Reported exactly once: the first, MSI-conflict attempt is queued for the serial retry instead of
        // being sent as a failure, so only the retry's own outcome ever reaches the app.
        Assert.Single(results);
        Assert.Contains("\"success\":true", results[0]);
        Assert.Equal(2, source.AttemptsFor("flaky"));
    }

    /// <summary>A SECOND refusal for the same reason is a genuine failure and is reported normally,
    /// carrying the explanation the queue's Details view shows.</summary>
    [Fact]
    public async Task A_second_refusal_for_the_same_reason_is_reported_as_a_real_failure()
    {
        var (writer, buffer) = NewWriter();
        var source = new FlakyMsiSource("winget", alwaysFailWith: ["stuck"]);
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 1,
            Items = [new InstallJobItem { SourceId = "winget", ItemId = "stuck", Name = "stuck" }],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        var results = ReadAll(buffer).Split('\n')
            .Where(l => l.Contains("\"type\":\"result\"") && l.Contains("\"itemId\":\"stuck\""))
            .ToList();

        Assert.Single(results);
        Assert.Contains("\"success\":false", results[0]);
        Assert.Contains("exit 0x00000652", results[0]);
        Assert.Equal(2, source.AttemptsFor("stuck"));
    }

    /// <summary>
    /// The app's own per-item stop reaches this worker as an entry in <c>itemCancellations</c>
    /// being cancelled (the actual pipe control message is <see cref="ElevatedWorker"/>'s own concern,
    /// covered by its own tests); this proves the worker side of the contract, that cancelling ONE entry
    /// stops only the item registered under that key while every other item in the same job runs to its
    /// own, unrelated conclusion.
    /// </summary>
    [Fact]
    public async Task Cancelling_one_item_stops_only_that_item_while_the_rest_of_the_job_keeps_running()
    {
        var (writer, buffer) = NewWriter();
        // Both source ids must need elevation: this worker rejects any item whose source does not
        // (InstallQueue.NeedsElevation), a real "npm"/"pip" item never reaches it at all.
        var itemCancellations = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>();
        var blockedSource = new BlockingSource("chocolatey");
        var fastSource = new FlakyMsiSource("winget");
        var job = new InstallJobFile
        {
            MaxParallelInstalls = 2,
            Items =
            [
                new InstallJobItem { SourceId = "chocolatey", ItemId = "blocked", Name = "blocked" },
                new InstallJobItem { SourceId = "winget", ItemId = "fast", Name = "fast" },
            ],
        };

        var runTask = InstallWorker.RunJobAsync(
            job, writer, "job1", new LoggingService(), CancellationToken.None,
            [blockedSource, fastSource], itemCancellations);

        await blockedSource.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(itemCancellations.TryGetValue("chocolatey:blocked", out var itemCts));
        itemCts.Cancel();

        Assert.True(await runTask);

        var output = ReadAll(buffer);
        var blockedResult = output.Split('\n').Single(l => l.Contains("\"type\":\"result\"") && l.Contains("\"itemId\":\"blocked\""));
        Assert.Contains("\"isCancelled\":true", blockedResult);
        Assert.Contains("\"success\":false", blockedResult);

        var fastResult = output.Split('\n').Single(l => l.Contains("\"type\":\"result\"") && l.Contains("\"itemId\":\"fast\""));
        Assert.Contains("\"success\":true", fastResult);

        // Deregistered once the item is done, an entry a later item of the same name (very unlikely,
        // but this is a dictionary keyed on a composite id) could otherwise mistake for still running.
        Assert.False(itemCancellations.ContainsKey("chocolatey:blocked"));
    }

    /// <summary>Installs nothing real; blocks until its OWN cancellation token fires, signalling
    /// <see cref="Started"/> once the install has actually begun, so a test can cancel it mid-flight rather
    /// than racing a fixed delay.</summary>
    private sealed class BlockingSource(string id) : IUpdateSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

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

    /// <summary>Fails its FIRST attempt for every item named in <paramref name="failFirstAttemptsOf"/> the
    /// way winget's own MSI-conflict pass-through reads, or every attempt for every item named in
    /// <paramref name="alwaysFailWith"/>; every other item, and every later attempt, succeeds.</summary>
    private sealed class FlakyMsiSource(
        string id, IReadOnlyCollection<string>? failFirstAttemptsOf = null, IReadOnlyCollection<string>? alwaysFailWith = null) : IUpdateSource
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _attempts = new();

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public int AttemptsFor(string itemId) => _attempts.GetValueOrDefault(itemId);

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            var attempt = _attempts.AddOrUpdate(item.Id, 1, (_, count) => count + 1);

            var alwaysFails = alwaysFailWith?.Contains(item.Id) == true;
            var failsFirstOnly = attempt == 1 && failFirstAttemptsOf?.Contains(item.Id) == true;

            if (alwaysFails || failsFirstOnly)
                throw new InvalidOperationException(
                    $"winget install failed for '{item.Id}' (exit 0x00000652). Another installation is already in progress.");

            progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A single slow item used to sit on its own log lines for its whole duration: the buffer was only
    /// drained at three fixed points, none of them while an item was actually running. This item enqueues
    /// a log line the moment its install starts and then runs long enough for the periodic flush to have
    /// fired at least once before it finishes, proving the line reaches the app well before the item's own
    /// completion (its "after" drain) would have delivered it anyway.
    /// </summary>
    [Fact]
    public async Task A_log_line_queued_during_a_slow_item_is_flushed_before_the_item_finishes()
    {
        while (ElevatedWorker.TryDequeueLog(out _))
        {
            // drain anything a differently-ordered test run might have left behind
        }

        var (writer, buffer) = NewWriter();
        var source = new SlowLoggingSource("winget", TimeSpan.FromSeconds(3));
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "winget", ItemId = "slow", Name = "slow" }],
        };

        var runTask = InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        // Comfortably past the 1-second periodic flush, comfortably short of the item's own 3-second
        // install: if the line is visible here, the timer delivered it, not the item's own completion.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var midFlightOutput = ReadAll(buffer);
        Assert.Contains("\"type\":\"log\"", midFlightOutput);
        Assert.Contains("during the slow install", midFlightOutput);
        Assert.DoesNotContain("\"type\":\"result\"", midFlightOutput);

        await runTask;
    }

    /// <summary>Installs nothing real; only enqueues one log line as soon as its own install starts, then
    /// blocks for <paramref name="delay"/> so a test can observe whether that line was flushed WHILE the
    /// install was still running.</summary>
    private sealed class SlowLoggingSource(string id, TimeSpan delay) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            ElevatedWorker.CollectLog("a line logged during the slow install");
            await Task.Delay(delay, cancellationToken);
            progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
        }
    }

    /// <summary>Installs nothing real; only tracks how many of its own items were in flight at once, the
    /// same shape <see cref="InstallQueueTests"/>'s stub sources use for their own concurrency proof.</summary>
    /// <summary>
    /// This fix's elevated-path re-check: null, unchanged, changed, same three cases the in-process path
    /// (InstallQueueTests) covers, but through the pipe protocol this worker actually speaks.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("1.0", true)]
    [InlineData("2.0", false)]
    public async Task An_install_recheck_reports_versionUnchanged_only_on_an_actual_match(
        string? recheckedVersion, bool expectedUnchanged)
    {
        var (writer, buffer) = NewWriter();
        var source = new VersionRecheckSource("chocolatey", recheckedVersion);
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "chocolatey", ItemId = "git", Name = "git", InstalledVersion = "1.0", AvailableVersion = "2.0" }],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        var output = ReadAll(buffer);
        Assert.Contains("\"success\":true", output);
        Assert.Equal(expectedUnchanged, output.Contains("\"versionUnchanged\":true", StringComparison.Ordinal));
    }

    /// <summary>The re-check is optional: a tool lookup that fails afterwards (a refused path, a timeout)
    /// must not turn the finished install into a failure.</summary>
    [Fact]
    public async Task A_failing_recheck_keeps_the_install_a_success()
    {
        var (writer, buffer) = NewWriter();
        var source = new VersionRecheckSource("chocolatey", "1.0", failIfAsked: true);
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "chocolatey", ItemId = "git", Name = "git", InstalledVersion = "1.0", AvailableVersion = "2.0" }],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        var output = ReadAll(buffer);
        Assert.Contains("\"success\":true", output);
        Assert.DoesNotContain("\"versionUnchanged\":true", output);
    }

    /// <summary>An uninstall has no "installed version" to re-check; the re-check must never even run.</summary>
    [Fact]
    public async Task An_uninstall_never_triggers_the_version_recheck()
    {
        var (writer, buffer) = NewWriter();
        var source = new VersionRecheckSource("chocolatey", "1.0", failIfAsked: true);
        var job = new InstallJobFile
        {
            Action = InstallAction.Uninstall,
            Items = [new InstallJobItem { SourceId = "chocolatey", ItemId = "git", Name = "git", InstalledVersion = "1.0" }],
        };

        await InstallWorker.RunJobAsync(job, writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        var output = ReadAll(buffer);
        Assert.Contains("\"success\":true", output);
        Assert.DoesNotContain("\"versionUnchanged\":true", output);
    }

    private sealed class VersionRecheckSource(string id, string? recheckedVersion, bool failIfAsked = false) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken) =>
            failIfAsked
                ? throw new InvalidOperationException("the re-check must not run for an uninstall")
                : Task.FromResult(recheckedVersion);
    }

    private sealed class ConcurrencyTrackingSource(string id, TimeSpan delay) : IUpdateSource
    {
        private readonly object _lock = new();
        private int _current;

        public int MaxObserved { get; private set; }

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.DeveloperTools;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<UpdateItem>();

        public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _current++;
                if (_current > MaxObserved)
                    MaxObserved = _current;
            }

            await Task.Delay(delay, cancellationToken);

            lock (_lock)
                _current--;
        }
    }
}
