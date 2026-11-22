using System.Diagnostics;
using System.IO;
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

        public void TreeEnds() => _tree.TrySetResult();

        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WaitForTreeAsync(CancellationToken cancellationToken) => _tree.Task.WaitAsync(cancellationToken);

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

    [Fact]
    public void An_administrator_run_gets_the_protected_system_temp_folder()
    {
        var windows = Path.Combine(Path.GetTempPath(), "win");
        var environment = new Dictionary<string, string> { ["UU_X"] = "1", ["temp"] = @"C:\Users\x\AppData\Local\Temp" };

        var result = UninstallExecution.WithProtectedTemp(environment, windows, _ => true);

        Assert.Equal(Path.Combine(windows, "SystemTemp"), result["TEMP"]);
        Assert.Equal(Path.Combine(windows, "SystemTemp"), result["TMP"]);
        Assert.Equal("1", result["UU_X"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Without_a_protected_system_temp_folder_the_administrator_run_is_refused()
    {
        Assert.Throws<InvalidOperationException>(
            () => UninstallExecution.WithProtectedTemp(null, @"C:\Windows", _ => false));
    }

    [Fact]
    public void The_system_temp_folder_of_this_windows_is_what_the_trust_check_judges()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var asked = new List<string>();

        UninstallExecution.WithProtectedTemp(null, windows, folder => { asked.Add(folder); return true; });

        Assert.Equal([Path.Combine(windows, "SystemTemp")], asked);
    }
}
