using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstall run: lanes, order, stop, and the outcome of each entry, all against fake executors.</summary>
public class UninstallRunnerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static InstalledProgramEntry Entry(string name) =>
        new() { Key = $@"{FakeUninstallRegistry.Hklm64}\{name}", Name = name };

    private static UninstallPlan MachinePlan() =>
        new() { Method = UninstallMethod.QuietString, Executable = @"C:\Program Files\X\u.exe", Elevated = true };

    private static UninstallPlan UserPlan() =>
        new() { Method = UninstallMethod.SilentSwitch, Executable = @"C:\Users\x\u.exe", Elevated = false };

    private static UninstallPlan StorePlan() =>
        new() { Method = UninstallMethod.StoreApp, Executable = "powershell.exe", Elevated = false };

    private static UninstallPlan InteractivePlan(bool elevated = false) =>
        new() { Method = UninstallMethod.Interactive, Executable = @"C:\Program Files\X\u.exe", Elevated = elevated, Interactive = true };

    private static UninstallPlan RefusedPlan(UninstallRefusal why = UninstallRefusal.NoCommand) =>
        new() { Method = UninstallMethod.Refused, Refusal = why };

    private static string Lookup(string key) => key == "UninstallErrorCode" ? "code {0}" : key;

    /// <summary>An executor whose behaviour a test sets; it counts how many runs overlap and what ran in which order.</summary>
    private sealed class FakeExecutor : IUninstallExecutor
    {
        private readonly object _gate = new();
        private int _current;

        public Func<InstalledProgramEntry, CancellationToken, Task<UninstallExecutionResult>> Handler { get; set; } =
            (_, _) => Task.FromResult(new UninstallExecutionResult(0));

        public int MaxConcurrent { get; private set; }

        public List<string> Started { get; } = [];

        public async Task<UninstallExecutionResult> RunAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken ct)
        {
            lock (_gate)
            {
                _current++;
                MaxConcurrent = Math.Max(MaxConcurrent, _current);
                Started.Add(entry.Name);
            }

            try
            {
                return await Handler(entry, ct);
            }
            finally
            {
                lock (_gate)
                    _current--;
            }
        }
    }

    private sealed class Harness
    {
        public FakeExecutor Machine { get; } = new();

        public FakeExecutor User { get; } = new();

        public Dictionary<string, UninstallPlan> Plans { get; } = new();

        public HashSet<string> Gone { get; } = [];

        public int GoneChecks;

        public UninstallRunner Runner { get; }

        public Harness(TimeSpan? lateWindow = null)
        {
            Runner = new UninstallRunner(
                entry => Plans[entry.Name],
                Machine,
                User,
                (entry, _) =>
                {
                    Interlocked.Increment(ref GoneChecks);
                    return Task.FromResult(Gone.Contains(entry.Name));
                },
                Lookup,
                lateWindow ?? TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(10));
        }

        public Harness Add(string name, UninstallPlan plan, bool gone = true)
        {
            Plans[name] = plan;
            if (gone)
                Gone.Add(name);
            return this;
        }

        public Task<IReadOnlyList<UninstallEntryResult>> RunAsync(
            Action<UninstallEntryUpdate>? onUpdate = null, CancellationToken token = default, params string[] names) =>
            Runner.RunAsync(names.Select(Entry).ToList(), onUpdate, token);
    }

    private static Task Delay() => Task.Delay(15);

    [Fact]
    public async Task Machine_removals_run_one_after_the_other_in_the_order_given()
    {
        var h = new Harness();
        foreach (var n in new[] { "A", "B", "C", "D" })
            h.Add(n, MachinePlan());
        h.Machine.Handler = async (_, _) =>
        {
            await Delay();
            return new UninstallExecutionResult(0);
        };

        var results = await h.RunAsync(null, default, "A", "B", "C", "D");

        Assert.Equal(1, h.Machine.MaxConcurrent);
        Assert.Equal(["A", "B", "C", "D"], h.Machine.Started);
        Assert.All(results, r => Assert.Equal(UninstallOutcome.Removed, r.Outcome));
        Assert.Equal(["A", "B", "C", "D"], results.Select(r => r.Entry.Name));
    }

    [Fact]
    public async Task User_setups_run_one_after_the_other()
    {
        var h = new Harness();
        foreach (var n in new[] { "A", "B", "C" })
            h.Add(n, UserPlan());
        h.User.Handler = async (_, _) =>
        {
            await Delay();
            return new UninstallExecutionResult(0);
        };

        await h.RunAsync(null, default, "A", "B", "C");

        Assert.Equal(1, h.User.MaxConcurrent);
        Assert.Equal(["A", "B", "C"], h.User.Started);
    }

    [Fact]
    public async Task A_setup_lane_and_the_store_lane_run_side_by_side()
    {
        var h = new Harness();
        h.Add("M", MachinePlan()).Add("S", StorePlan());

        // Both removals wait until the other has started: sequential lanes would never get there.
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<UninstallExecutionResult> Barrier(InstalledProgramEntry e, CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(Patience, ct);
            return new UninstallExecutionResult(0);
        }

        h.Machine.Handler = Barrier;
        h.User.Handler = Barrier;

        var results = await h.RunAsync(null, default, "M", "S");

        Assert.All(results, r => Assert.Equal(UninstallOutcome.Removed, r.Outcome));
    }

    [Fact]
    public async Task A_machine_removal_and_a_user_setup_never_run_at_the_same_time()
    {
        var h = new Harness();
        h.Add("M1", MachinePlan()).Add("M2", MachinePlan()).Add("U1", UserPlan()).Add("U2", UserPlan());

        var running = 0;
        var most = 0;
        async Task<UninstallExecutionResult> Slow(InstalledProgramEntry e, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            lock (h)
                most = Math.Max(most, now);
            await Delay();
            Interlocked.Decrement(ref running);
            return new UninstallExecutionResult(0);
        }

        h.Machine.Handler = Slow;
        h.User.Handler = Slow;

        var results = await h.RunAsync(null, default, "M1", "M2", "U1", "U2");

        Assert.Equal(1, most);
        Assert.All(results, r => Assert.Equal(UninstallOutcome.Removed, r.Outcome));
        Assert.DoesNotContain(results, r => r.Reason == "UninstallBusy");
    }

    [Fact]
    public async Task Store_apps_run_three_at_a_time()
    {
        var h = new Harness();
        var names = new[] { "A", "B", "C", "D", "E" };
        foreach (var n in names)
            h.Add(n, StorePlan());

        var running = 0;
        var three = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.User.Handler = async (_, ct) =>
        {
            if (Interlocked.Increment(ref running) == 3)
                three.TrySetResult();
            await three.Task.WaitAsync(Patience, ct);
            await Delay();
            Interlocked.Decrement(ref running);
            return new UninstallExecutionResult(0);
        };

        var results = await h.RunAsync(null, default, names);

        Assert.Equal(UninstallRunner.MaxParallelStoreApps, h.User.MaxConcurrent);
        Assert.Equal(3, UninstallRunner.MaxParallelStoreApps);
        Assert.All(results, r => Assert.Equal(UninstallOutcome.Removed, r.Outcome));
    }

    [Fact]
    public async Task Vendor_window_removals_wait_for_every_silent_one_and_then_run_alone()
    {
        var h = new Harness();
        h.Add("Win1", InteractivePlan()).Add("Win2", InteractivePlan(elevated: true))
            .Add("M", MachinePlan()).Add("U", UserPlan()).Add("S", StorePlan());

        var events = new List<string>();
        FakeExecutor Hook(FakeExecutor executor)
        {
            executor.Handler = async (e, _) =>
            {
                lock (events)
                    events.Add($"start:{e.Name}");
                await Delay();
                lock (events)
                    events.Add($"end:{e.Name}");
                return new UninstallExecutionResult(0);
            };
            return executor;
        }

        Hook(h.Machine);
        Hook(h.User);

        await h.RunAsync(null, default, "Win1", "M", "Win2", "U", "S");

        var firstWindow = events.FindIndex(e => e.StartsWith("start:Win"));
        foreach (var silent in new[] { "M", "U", "S" })
            Assert.True(events.IndexOf($"end:{silent}") < firstWindow, $"{silent} must end before any vendor window starts");

        // One at a time: the first window ends before the second starts, in the order given.
        Assert.Equal(["start:Win1", "end:Win1", "start:Win2", "end:Win2"], events.Skip(firstWindow).ToArray());
    }

    [Fact]
    public async Task A_stop_during_a_removal_that_still_finishes_starts_no_other_entry()
    {
        var h = new Harness();
        h.Add("A", MachinePlan()).Add("B", MachinePlan()).Add("C", MachinePlan()).Add("Win", InteractivePlan());
        h.Machine.Handler = (_, _) =>
        {
            h.Runner.Stop();
            return Task.FromResult(new UninstallExecutionResult(0));
        };
        h.User.Handler = h.Machine.Handler;

        var results = await h.RunAsync(null, default, "A", "B", "C", "Win");

        Assert.True(h.Runner.StopRequested);
        Assert.Equal(["A"], h.Machine.Started);
        Assert.Empty(h.User.Started);
        Assert.Equal(UninstallOutcome.Removed, results[0].Outcome);
        Assert.All(results.Skip(1), r =>
        {
            Assert.Equal(UninstallOutcome.NotStarted, r.Outcome);
            Assert.Equal("UninstallNotStarted", r.Reason);
        });
    }

    [Fact]
    public async Task A_stop_cancels_the_token_of_the_running_removal_and_starts_no_other_entry()
    {
        var h = new Harness();
        h.Add("A", MachinePlan()).Add("B", MachinePlan());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancel = false;
        h.Machine.Handler = async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Patience, ct);
            }
            catch (OperationCanceledException)
            {
                sawCancel = true;
                throw;
            }

            return new UninstallExecutionResult(0);
        };

        var run = h.RunAsync(null, default, "A", "B");
        await started.Task.WaitAsync(Patience);
        h.Runner.Stop();
        var results = await run.WaitAsync(Patience);

        Assert.True(sawCancel, "the running removal never saw its token cancelled");
        Assert.Equal(UninstallOutcome.Failed, results[0].Outcome);
        Assert.Equal("UninstallCancelled", results[0].Reason);
        Assert.Equal(UninstallOutcome.NotStarted, results[1].Outcome);
        Assert.Equal(["A"], h.Machine.Started);
        Assert.Equal(0, h.GoneChecks);
    }

    [Fact]
    public async Task A_removal_that_sat_waiting_for_input_fails_with_that_reason_and_is_not_looked_up_as_gone()
    {
        var h = new Harness().Add("A", MachinePlan(), gone: false);
        h.Machine.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(null, "tool: stopped", Stalled: true));

        var result = Assert.Single(await h.RunAsync(null, default, "A"));

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal("UninstallWaitedForInput", result.Reason);
        Assert.Equal(0, h.GoneChecks);
    }

    [Fact]
    public async Task A_stop_before_the_run_starts_no_entry_at_all()
    {
        var h = new Harness();
        h.Add("A", MachinePlan()).Add("B", UserPlan());
        h.Runner.Stop();

        var results = await h.RunAsync(null, default, "A", "B");

        Assert.Empty(h.Machine.Started);
        Assert.Empty(h.User.Started);
        Assert.All(results, r => Assert.Equal(UninstallOutcome.NotStarted, r.Outcome));
    }

    [Fact]
    public async Task The_hard_token_ends_the_running_removal_and_leaves_the_rest_not_started()
    {
        var h = new Harness();
        h.Add("A", MachinePlan()).Add("B", MachinePlan());
        using var cts = new CancellationTokenSource();
        h.Machine.Handler = async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Patience, ct);
            return new UninstallExecutionResult(0);
        };

        var results = await h.RunAsync(null, cts.Token, "A", "B");

        Assert.Equal(UninstallOutcome.Failed, results[0].Outcome);
        Assert.Equal("UninstallCancelled", results[0].Reason);
        Assert.Equal(UninstallOutcome.NotStarted, results[1].Outcome);
        Assert.Equal(["A"], h.Machine.Started);
    }

    [Theory]
    [InlineData(0, true, UninstallOutcome.Removed, null)]
    [InlineData(0, false, UninstallOutcome.Failed, "UninstallStillPresent")]
    [InlineData(1, true, UninstallOutcome.Removed, null)]
    [InlineData(1, false, UninstallOutcome.Failed, "code 1")]
    [InlineData(-1073741510, false, UninstallOutcome.Failed, "code -1073741510")]
    [InlineData(3010, false, UninstallOutcome.NeedsRestart, "UninstallNeedsRestart")]
    [InlineData(3010, true, UninstallOutcome.NeedsRestart, "UninstallNeedsRestart")]
    [InlineData(1641, true, UninstallOutcome.NeedsRestart, "UninstallNeedsRestart")]
    [InlineData(1602, false, UninstallOutcome.Failed, "UninstallCancelled")]
    [InlineData(1223, false, UninstallOutcome.Failed, "UninstallCancelled")]
    [InlineData(1618, false, UninstallOutcome.Failed, "UninstallBusy")]
    [InlineData(1625, false, UninstallOutcome.Failed, "UninstallBlockedByPolicy")]
    public async Task The_entry_being_gone_decides_and_the_exit_code_only_explains_a_failure(
        int exitCode, bool gone, UninstallOutcome outcome, string? reason)
    {
        var h = new Harness().Add("A", MachinePlan(), gone);
        h.Machine.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(exitCode, "tool output"));

        var result = Assert.Single(await h.RunAsync(null, default, "A"));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tool output", result.Detail);
    }

    [Fact]
    public async Task A_run_with_no_exit_code_could_not_start_and_is_never_looked_up_as_gone()
    {
        var h = new Harness().Add("A", MachinePlan());
        h.Machine.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(null, "helper missing"));

        var result = Assert.Single(await h.RunAsync(null, default, "A"));

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal("UninstallCouldNotStart", result.Reason);
        Assert.Equal("helper missing", result.Detail);
        Assert.Equal(0, h.GoneChecks);
    }

    [Fact]
    public async Task A_cancelled_result_is_a_cancelled_failure_and_is_not_looked_up_as_gone()
    {
        var h = new Harness().Add("A", MachinePlan());
        h.Machine.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(null, null, Cancelled: true));

        var result = Assert.Single(await h.RunAsync(null, default, "A"));

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal("UninstallCancelled", result.Reason);
        Assert.Equal(0, h.GoneChecks);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(TimeoutException))]
    public async Task An_executor_that_throws_fails_its_entry_and_the_run_goes_on(Type exceptionType)
    {
        var h = new Harness().Add("A", MachinePlan()).Add("B", MachinePlan());
        h.Machine.Handler = (e, _) => e.Name == "A"
            ? throw (Exception)Activator.CreateInstance(exceptionType, "boom")!
            : Task.FromResult(new UninstallExecutionResult(0));

        var results = await h.RunAsync(null, default, "A", "B");

        Assert.Equal(UninstallOutcome.Failed, results[0].Outcome);
        Assert.Equal("UninstallCouldNotStart", results[0].Reason);
        Assert.Equal("boom", results[0].Detail);
        Assert.Equal(UninstallOutcome.Removed, results[1].Outcome);
    }

    [Fact]
    public async Task An_executor_cancelled_by_the_token_fails_with_the_cancelled_reason()
    {
        var h = new Harness().Add("A", UserPlan());
        h.User.Handler = (_, _) => throw new OperationCanceledException();

        var result = Assert.Single(await h.RunAsync(null, default, "A"));

        Assert.Equal(UninstallOutcome.Failed, result.Outcome);
        Assert.Equal("UninstallCancelled", result.Reason);
    }

    [Fact]
    public async Task A_refused_plan_fails_at_once_and_never_reaches_an_executor()
    {
        var h = new Harness()
            .Add("None", RefusedPlan(UninstallRefusal.NoCommand), gone: false)
            .Add("Evil", RefusedPlan(UninstallRefusal.UntrustedLocation), gone: false)
            .Add("Ok", MachinePlan());

        var results = await h.RunAsync(null, default, "None", "Evil", "Ok");

        Assert.Equal("UninstallNotRemovable", results[0].Reason);
        Assert.Equal("UninstallRefusedUntrusted", results[1].Reason);
        Assert.Equal(UninstallOutcome.Failed, results[0].Outcome);
        Assert.Equal(UninstallOutcome.Failed, results[1].Outcome);
        Assert.Equal(["Ok"], h.Machine.Started);
        Assert.Empty(h.User.Started);
    }

    [Fact]
    public async Task A_machine_plan_goes_to_the_machine_executor_and_every_other_plan_to_the_user_executor()
    {
        var h = new Harness().Add("M", MachinePlan()).Add("U", UserPlan()).Add("S", StorePlan())
            .Add("WinUser", InteractivePlan(elevated: false)).Add("WinMachine", InteractivePlan(elevated: true));

        await h.RunAsync(null, default, "M", "U", "S", "WinUser", "WinMachine");

        Assert.Equal(["M", "WinMachine"], h.Machine.Started.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(["S", "U", "WinUser"], h.User.Started.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Every_entry_reports_waiting_then_running_then_done()
    {
        var h = new Harness().Add("A", MachinePlan()).Add("B", UserPlan()).Add("None", RefusedPlan(), gone: false);
        var updates = new List<UninstallEntryUpdate>();

        var results = await h.RunAsync(u =>
        {
            lock (updates)
                updates.Add(u);
        }, default, "A", "B", "None");

        foreach (var name in new[] { "A", "B" })
        {
            var states = updates.Where(u => u.Entry.Name == name).Select(u => u.State).ToArray();
            Assert.Equal([UninstallEntryState.Waiting, UninstallEntryState.Running, UninstallEntryState.Done], states);
        }

        // A refused entry goes from waiting straight to done.
        Assert.Equal([UninstallEntryState.Waiting, UninstallEntryState.Done],
            updates.Where(u => u.Entry.Name == "None").Select(u => u.State).ToArray());
        Assert.All(updates.Where(u => u.State == UninstallEntryState.Done), u => Assert.NotNull(u.Result));
        Assert.All(updates.Where(u => u.State != UninstallEntryState.Done), u => Assert.Null(u.Result));
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void A_winget_failure_without_a_known_message_falls_back_to_the_bare_code()
    {
        var runner = new UninstallRunner(_ => MachinePlan(), new FakeExecutor(), new FakeExecutor(), (_, _) => Task.FromResult(false), Lookup);
        var plan = new UninstallPlan { Method = UninstallMethod.Winget, Executable = "winget", Elevated = true };

        Assert.Equal("code 7", runner.FailureReason(plan, 7, "nothing recognisable here"));
    }

    // ---- the gone check ----

    private sealed class ScriptedRegistry(params string[][] listings) : IRegistryView
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> SubKeyNames(string keyPath)
        {
            var listing = listings[Math.Min(Calls, listings.Length - 1)];
            Calls++;
            return listing;
        }

        public string? Value(string keyPath, string valueName) => null;
    }

    private sealed class FakeAppx(IReadOnlyList<InstalledProgramEntry>? apps) : IAppxPackageSource
    {
        public Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(apps);
    }

    private static InstalledProgramEntry StoreEntry(string fullName) => new()
    {
        Key = $"appx:{fullName}", Name = "App", Kind = UninstallEntryKind.StoreApp, PackageFullName = fullName,
    };

    [Fact]
    public async Task A_setup_whose_entry_disappears_after_it_ended_is_removed_when_it_is_recognised_by_its_files()
    {
        var h = new Harness(TimeSpan.FromSeconds(10));
        h.Add("A", UserPlan(), gone: false);
        h.User.Handler = (_, _) =>
        {
            // The entry goes a moment after the root process has exited.
            _ = Task.Delay(150).ContinueWith(_ => { lock (h.Gone) h.Gone.Add("A"); });
            return Task.FromResult(new UninstallExecutionResult(0));
        };

        var results = await h.RunAsync(null, default, "A");

        Assert.Equal(UninstallOutcome.Removed, results[0].Outcome);
        Assert.True(h.GoneChecks > 1);
    }

    [Fact]
    public async Task A_vendor_window_setup_is_given_the_same_time_to_finish_the_removal()
    {
        var h = new Harness(TimeSpan.FromSeconds(10));
        h.Add("A", InteractivePlan(), gone: false);
        h.User.Handler = (_, _) =>
        {
            _ = Task.Delay(150).ContinueWith(_ => { lock (h.Gone) h.Gone.Add("A"); });
            return Task.FromResult(new UninstallExecutionResult(0));
        };

        var results = await h.RunAsync(null, default, "A");

        Assert.Equal(UninstallOutcome.Removed, results[0].Outcome);
    }

    [Fact]
    public async Task A_setup_that_never_removes_the_entry_fails_after_the_window()
    {
        var h = new Harness(TimeSpan.FromMilliseconds(100));
        h.Add("A", UserPlan(), gone: false);

        var results = await h.RunAsync(null, default, "A");

        Assert.Equal(UninstallOutcome.Failed, results[0].Outcome);
        Assert.True(h.GoneChecks > 1);
    }

    [Fact]
    public async Task A_removal_that_is_not_a_setup_recognised_by_its_files_is_checked_once()
    {
        var h = new Harness(TimeSpan.FromSeconds(10));
        h.Add("Q", MachinePlan(), gone: false).Add("S", StorePlan(), gone: false);
        h.Machine.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(1));
        h.User.Handler = (_, _) => Task.FromResult(new UninstallExecutionResult(1));

        var results = await h.RunAsync(null, default, "Q", "S");

        Assert.All(results, r => Assert.Equal(UninstallOutcome.Failed, r.Outcome));
        Assert.Equal(2, h.GoneChecks);
    }

    private static UninstallGoneCheck Check(IRegistryView registry, IAppxPackageSource? appx = null, int attempts = 3) =>
        new(registry, appx ?? new FakeAppx([]), attempts, TimeSpan.Zero);

    [Fact]
    public async Task A_program_whose_key_is_still_listed_is_not_gone_after_every_attempt()
    {
        var registry = new ScriptedRegistry(["Foo", "Other"]);

        Assert.False(await Check(registry).IsGoneAsync(Entry("Foo"), default));
        Assert.Equal(3, registry.Calls);
    }

    [Fact]
    public async Task A_program_whose_key_is_no_longer_listed_is_gone_at_once()
    {
        var registry = new ScriptedRegistry(["Other"]);

        Assert.True(await Check(registry).IsGoneAsync(Entry("Foo"), default));
        Assert.Equal(1, registry.Calls);
    }

    [Fact]
    public async Task A_setup_that_finishes_after_it_exits_is_awaited()
    {
        var registry = new ScriptedRegistry(["Foo"], ["Foo"], ["Other"]);

        Assert.True(await Check(registry).IsGoneAsync(Entry("Foo"), default));
        Assert.Equal(3, registry.Calls);
    }

    [Fact]
    public async Task The_key_is_compared_without_regard_to_case()
    {
        var registry = new ScriptedRegistry(["FOO"]);

        Assert.False(await Check(registry, attempts: 1).IsGoneAsync(Entry("foo"), default));
    }

    [Fact]
    public async Task An_entry_key_without_a_parent_is_never_gone()
    {
        var entry = new InstalledProgramEntry { Key = "NoParent", Name = "X" };

        Assert.False(await Check(new ScriptedRegistry([]), attempts: 1).IsGoneAsync(entry, default));
    }

    [Fact]
    public async Task A_store_app_is_gone_when_it_is_missing_from_the_package_list()
    {
        var present = new FakeAppx([StoreEntry("Foo_1.0.0.0_x64__abc")]);
        var absent = new FakeAppx([StoreEntry("Other_1.0.0.0_x64__abc")]);

        Assert.False(await Check(new ScriptedRegistry(), present, attempts: 1).IsGoneAsync(StoreEntry("foo_1.0.0.0_x64__ABC"), default));
        Assert.True(await Check(new ScriptedRegistry(), absent, attempts: 1).IsGoneAsync(StoreEntry("Foo_1.0.0.0_x64__abc"), default));
    }

    [Fact]
    public async Task A_package_list_that_cannot_be_read_never_counts_as_gone()
    {
        var unreadable = new FakeAppx(null);

        Assert.False(await Check(new ScriptedRegistry(), unreadable, attempts: 2).IsGoneAsync(StoreEntry("Foo_1.0.0.0_x64__abc"), default));
    }
}
