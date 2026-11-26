using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services.App;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

public class UninstallExecutionTests
{
    /// <summary>A child whose first process is already gone while the rest of its tree still runs.</summary>
    private sealed class TreeChild : IChildProcess
    {
        private readonly TaskCompletionSource _tree = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StreamReader StandardOutput { get; } = new(new MemoryStream());

        public StreamReader StandardError { get; } = new(new MemoryStream());

        public int ExitCode => 0;

        public bool HasExited => true;

        public int ProcessId => 0;

        public int KillCalls { get; private set; }

        /// <summary>What the tree wait was asked: false for a wait that must not give up on an idle leftover.</summary>
        public bool? EndWhenIdleAsked { get; private set; }

        /// <summary>What the child reports as still alive when its tree wait has returned.</summary>
        public int ActiveJobProcesses { get; set; }

        public void TreeEnds() => _tree.TrySetResult();

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitForTreeAsync(CancellationToken cancellationToken, bool endWhenIdle = true)
        {
            EndWhenIdleAsked = endWhenIdle;
            return _tree.Task.WaitAsync(cancellationToken);
        }

        public void Kill() => KillCalls++;

        public void Dispose()
        {
        }
    }

    private static Task<ProcessResult> RunCommand(TimeSpan timeout, CancellationToken token)
    {
        var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
        return UninstallExecution.RunCommandAsync(executable, [], timeout, elevated: false, null, _ => { }, token);
    }

    [Fact]
    public async Task A_registry_command_that_runs_out_of_time_is_left_running_not_killed()
    {
        var child = new LingeringChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            var result = await RunCommand(TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.TimedOut);
            Assert.Equal(0, child.KillCalls);
            child.Exit();
            await child.Disposed.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_hard_cancel_ends_the_wait_but_not_the_uninstaller()
    {
        var child = new LingeringChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RunCommand(TimeSpan.FromSeconds(60), cancel.Token).WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(0, child.KillCalls);
            child.Exit();
            await child.Disposed.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_registry_command_is_waited_for_with_everything_it_started()
    {
        var child = new TreeChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            var run = RunCommand(TimeSpan.FromSeconds(60), CancellationToken.None);

            await Task.Delay(300);
            Assert.False(run.IsCompleted, "the run returned although the tree still ran");

            child.TreeEnds();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.False(result.TimedOut);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Only_an_interactive_removal_is_waited_for_past_an_idle_leftover(bool interactive, bool expectedEndWhenIdle)
    {
        var child = new TreeChild();
        child.TreeEnds();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            var executable = new ResolvedExecutable(Environment.ProcessPath!, null);

            await UninstallExecution.RunCommandAsync(
                executable, [], TimeSpan.FromSeconds(60), elevated: false, null, _ => { }, CancellationToken.None,
                interactive: interactive).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(expectedEndWhenIdle, child.EndWhenIdleAsked);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public async Task A_run_says_when_its_tree_was_still_alive_at_the_end_of_the_wait(int alive, bool expectedLeftRunning)
    {
        var child = new TreeChild { ActiveJobProcesses = alive };
        child.TreeEnds();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            var result = await RunCommand(TimeSpan.FromSeconds(60), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.False(result.TimedOut);
            Assert.Equal(expectedLeftRunning, result.TreeLeftRunning);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void The_administrator_temp_folder_stays_while_a_program_still_runs_in_it(bool timedOut, bool treeLeftRunning, bool kept)
    {
        using var windows = new ScratchDir();
        using var programData = new ScratchDir();
        DriverBackupStaging.ProgramDataRootOverride = programData.Path;
        try
        {
            string folder;
            using (var temp = UninstallExecution.AcquireProtectedTemp(QuietLog(), windows.Path, _ => true))
            {
                folder = temp.Folder;
                UninstallExecution.KeepTempWhileRunning(
                    temp, new ProcessResult(0, "", "") { TimedOut = timedOut, TreeLeftRunning = treeLeftRunning });
            }

            Assert.Equal(kept, Directory.Exists(folder));
            if (kept)
                Directory.Delete(folder, recursive: true);
        }
        finally
        {
            DriverBackupStaging.ProgramDataRootOverride = null;
        }
    }

    [Trait("Layer", "Integration")]
    [Fact]
    public async Task An_interactive_tree_is_waited_for_although_its_leftover_sits_idle()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("start /b ping -n 7 127.0.0.1 >nul");

        SuspendedChildProcess.TreeIdleGraceForTest.Value = TimeSpan.FromSeconds(1);
        using var child = SuspendedChildProcess.Start(info, null, null);
        try
        {
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            var watch = Stopwatch.StartNew();

            await child.WaitForTreeAsync(CancellationToken.None, endWhenIdle: false).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.True(watch.Elapsed > TimeSpan.FromSeconds(3), $"the wait gave up on the idle leftover after {watch.Elapsed}");
            Assert.Equal(0, child.ActiveJobProcesses);
        }
        finally
        {
            SuspendedChildProcess.TreeIdleGraceForTest.Value = null;
            child.Kill();
        }
    }

    [Trait("Layer", "Integration")]
    [Fact]
    public async Task A_job_tells_when_a_process_the_first_one_started_is_still_running()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("start /b ping -n 4 127.0.0.1 >nul");

        using var child = SuspendedChildProcess.Start(info, null, null);
        var watch = Stopwatch.StartNew();
        await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var exited = watch.Elapsed;
        var activeAfterExit = child.ActiveJobProcesses;

        await child.WaitForTreeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(activeAfterExit > 0, "the job lost the ping that cmd started");
        Assert.True(watch.Elapsed - exited > TimeSpan.FromSeconds(1), $"the tree wait ended too early ({watch.Elapsed - exited})");
        Assert.Equal(0, child.ActiveJobProcesses);
    }

    [Trait("Layer", "Integration")]
    [Fact]
    public async Task An_idle_leftover_does_not_hold_the_wait_for_the_whole_timeout()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("start /b ping -n 40 127.0.0.1 >nul");

        SuspendedChildProcess.TreeIdleGraceForTest.Value = TimeSpan.FromSeconds(1);
        using var child = SuspendedChildProcess.Start(info, null, null);
        try
        {
            var watch = Stopwatch.StartNew();
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

            await child.WaitForTreeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the wait took {watch.Elapsed}");
            Assert.Equal(0, child.ExitCode);
            Assert.True(child.ActiveJobProcesses > 0, "the long-lived leftover was gone already");
        }
        finally
        {
            SuspendedChildProcess.TreeIdleGraceForTest.Value = null;
            child.Kill();
        }
    }

    private sealed class ScratchDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "uu-temp-" + Guid.NewGuid().ToString("N"));

        public ScratchDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Scratch only; the next run uses another name.
            }
        }
    }

    private static LoggingService QuietLog() => new(_ => { });

    [Fact]
    public void An_administrator_run_gets_the_protected_system_temp_folder()
    {
        using var windows = new ScratchDir();
        Directory.CreateDirectory(Path.Combine(windows.Path, "SystemTemp"));
        var environment = new Dictionary<string, string> { ["UU_X"] = "1", ["temp"] = @"C:\Users\x\AppData\Local\Temp" };

        using var temp = UninstallExecution.AcquireProtectedTemp(QuietLog(), windows.Path, _ => true);
        var result = UninstallExecution.WithProtectedTemp(environment, temp.Folder);

        Assert.Equal(Path.Combine(windows.Path, "SystemTemp"), result["TEMP"]);
        Assert.Equal(Path.Combine(windows.Path, "SystemTemp"), result["TMP"]);
        Assert.Equal("1", result["UU_X"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void A_system_temp_folder_that_is_not_protected_refuses_the_administrator_run()
    {
        using var windows = new ScratchDir();
        Directory.CreateDirectory(Path.Combine(windows.Path, "SystemTemp"));

        Assert.Throws<InvalidOperationException>(
            () => UninstallExecution.AcquireProtectedTemp(QuietLog(), windows.Path, _ => false));
    }

    [Fact]
    public void The_system_temp_folder_of_this_windows_is_what_the_trust_check_judges()
    {
        using var windows = new ScratchDir();
        var systemTemp = Path.Combine(windows.Path, "SystemTemp");
        Directory.CreateDirectory(systemTemp);
        var asked = new List<string>();

        using var temp = UninstallExecution.AcquireProtectedTemp(
            QuietLog(), windows.Path, folder => { asked.Add(folder); return true; });

        Assert.Equal([systemTemp], asked);
    }

    [Fact]
    public void Without_a_system_temp_folder_the_run_gets_an_administrator_only_folder_of_its_own()
    {
        using var windows = new ScratchDir();
        using var programData = new ScratchDir();
        DriverBackupStaging.ProgramDataRootOverride = programData.Path;
        try
        {
            string folder;
            var asked = new List<string>();
            using (var temp = UninstallExecution.AcquireProtectedTemp(
                QuietLog(), windows.Path, f => { asked.Add(f); return true; }))
            {
                folder = temp.Folder;
                var environment = UninstallExecution.WithProtectedTemp(null, folder);

                Assert.Equal(folder, environment["TEMP"]);
                Assert.Equal(Path.Combine(programData.Path, "UltimateUpdater", "Temp"), Path.GetDirectoryName(folder));
                Assert.Equal([folder], asked);
                Assert.True(Directory.Exists(folder));

                var sids = new DirectoryInfo(folder).GetAccessControl()
                    .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                    .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                    .Select(rule => rule.IdentityReference.Value)
                    .ToList();
                Assert.NotEmpty(sids);
                // Under the test root the unelevated test user owns the folder and is the one extra entry the
                // staging allows; the production rules (an elevated owner) leave only these two.
                var owner = System.Security.Principal.WindowsIdentity.GetCurrent().Owner?.Value;
                Assert.All(sids, sid => Assert.Contains(sid, new[] { "S-1-5-18", "S-1-5-32-544", owner }));
                Assert.Contains("S-1-5-18", sids);
                Assert.Contains("S-1-5-32-544", sids);
                Assert.DoesNotContain("S-1-5-32-545", sids);
            }

            Assert.False(Directory.Exists(folder), "the folder made for the run was left behind");
        }
        finally
        {
            DriverBackupStaging.ProgramDataRootOverride = null;
        }
    }

    [Fact]
    public void A_fallback_folder_that_fails_the_trust_check_refuses_the_run()
    {
        using var windows = new ScratchDir();
        using var programData = new ScratchDir();
        DriverBackupStaging.ProgramDataRootOverride = programData.Path;
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => UninstallExecution.AcquireProtectedTemp(QuietLog(), windows.Path, _ => false));
        }
        finally
        {
            DriverBackupStaging.ProgramDataRootOverride = null;
        }
    }
}
