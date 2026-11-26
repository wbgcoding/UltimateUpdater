using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>A removal that waits for input nobody can give (a vendor window in an elevated session) is ended and explained.</summary>
[Trait("Layer", "Integration")]
public class UninstallStallTests
{
    private static readonly StallWatchOptions Quick =
        new(TimeSpan.FromMilliseconds(300), 0.01, TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task A_silent_removal_is_ended_with_its_whole_tree_and_says_it_waited_for_input()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), 100, 3, true);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);

            var thrown = await Assert.ThrowsAsync<InstallerStalledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick, stallIsUninstall: true)
                .WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, child.KillCalls);
            Assert.True(thrown.IsUninstall);
            Assert.Contains(InstallerStalledException.UninstallWaitedMarker, thrown.Message);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_stop_ends_a_removal_whose_tree_sits_idle_and_says_it_waited_for_input()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), 100, 3, true);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            // The window is far away: only the stop can end it.
            var thrown = await Assert.ThrowsAsync<InstallerStalledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick with { Window = TimeSpan.MaxValue },
                stallIsUninstall: true, stopWhenIdle: stop.Token)
                .WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, child.KillCalls);
            Assert.True(thrown.IsUninstall);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_stop_leaves_a_removal_that_is_still_working_running()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ulong io = 0;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), io += 1000, 3, true);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource();
            using var hard = new CancellationTokenSource();

            var run = ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), hard.Token, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick with { Window = TimeSpan.MaxValue },
                stallIsUninstall: true, stopWhenIdle: stop.Token);
            await stop.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.False(run.IsCompleted, "a stop must not end a removal that still does I/O");
            Assert.Equal(0, child.KillCalls);

            await hard.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
        }
    }

    /// <summary>The work of an MSI removal runs in the Windows Installer service, outside the tool's tree: a tree
    /// that only waits for it reads as idle although the removal is mid-transaction.</summary>
    [Fact]
    public async Task A_stop_leaves_an_idle_tree_alone_while_the_windows_installer_is_busy()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), 100, 3, true);
        var installerBusy = true;
        ProcessRunner.InstallerBusyProbeForTest.Value = () => Volatile.Read(ref installerBusy);
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource();

            var run = ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick with { Window = TimeSpan.MaxValue },
                stallIsUninstall: true, stopWhenIdle: stop.Token);
            await stop.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.False(run.IsCompleted, "a stop ended a removal while the Windows Installer was busy");
            Assert.Equal(0, child.KillCalls);

            // Once the transaction is over the tree is idle for real and the stop takes effect.
            Volatile.Write(ref installerBusy, false);
            await Assert.ThrowsAsync<InstallerStalledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(1, child.KillCalls);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
            ProcessRunner.InstallerBusyProbeForTest.Value = null;
        }
    }

    [Fact]
    public async Task The_stall_window_of_a_removal_does_not_run_out_while_the_windows_installer_is_busy()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), 100, 3, true);
        ProcessRunner.InstallerBusyProbeForTest.Value = () => true;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var hard = new CancellationTokenSource();

            var run = ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), hard.Token, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick, stallIsUninstall: true);
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            Assert.False(run.IsCompleted, "the stall window ended a removal while the Windows Installer was busy");
            Assert.Equal(0, child.KillCalls);

            await hard.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
            ProcessRunner.InstallerBusyProbeForTest.Value = null;
        }
    }

    /// <summary>A hung installer with a visible-nowhere dialog also holds the mutex, so an installation keeps the
    /// plain window rule.</summary>
    [Fact]
    public async Task An_installation_stall_ignores_the_windows_installer_mutex()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        ProcessRunner.TreeSamplerForTest.Value = _ => new TreeActivity(TimeSpan.FromSeconds(10), 100, 3, true);
        ProcessRunner.InstallerBusyProbeForTest.Value = () => true;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);

            await Assert.ThrowsAsync<InstallerStalledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), CancellationToken.None, null, null, null, null,
                elevated: false, scrubEnvironment: null, stallWatch: Quick, stallIsUninstall: false)
                .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
            ProcessRunner.TreeSamplerForTest.Value = null;
            ProcessRunner.InstallerBusyProbeForTest.Value = null;
        }
    }

    [Fact]
    public void External_life_restarts_the_idle_count_of_a_tree()
    {
        var detector = new StallDetector(Quick);
        var idle = new TreeActivity(TimeSpan.FromSeconds(1), 10, 2, true);
        detector.Observe(0, idle);
        detector.Observe(200, idle, externalLife: true);

        Assert.Equal(TimeSpan.Zero, detector.IdleFor(200));
        Assert.False(detector.Observe(400, idle, externalLife: true));
        Assert.True(detector.Observe(800, idle), "without the external work the window runs out again");
    }

    [Fact]
    public void A_tree_counts_as_idle_for_a_stop_from_its_last_sign_of_life()
    {
        var detector = new StallDetector(Quick);
        detector.Observe(0, new TreeActivity(TimeSpan.FromSeconds(1), 10, 2, true));
        detector.Observe(100, new TreeActivity(TimeSpan.FromSeconds(1), 10, 2, true));

        Assert.Equal(TimeSpan.FromMilliseconds(100), detector.IdleFor(100));
        Assert.Equal(TimeSpan.FromMilliseconds(150), Quick.StopIdle);
        Assert.Equal(TimeSpan.MaxValue, StallWatchOptions.StopOnly.Window);
    }

    [Fact]
    public void The_row_of_a_removal_that_waited_for_input_names_the_cause_and_the_way_out()
    {
        var message = new InstallerStalledException("winget", uninstall: true).Message;

        Assert.Equal("UninstallWaitedForInput", InstallErrorExplainer.RowText("winget", message, key => key));
        Assert.Equal(
            "InstallStalled", InstallErrorExplainer.RowText("winget", new InstallerStalledException("winget").Message, key => key));
    }

    [Fact]
    public void The_uninstall_window_has_the_same_length_as_the_install_window()
    {
        Assert.Equal(StallWatchOptions.ForInstall, StallWatchOptions.ForUninstall);
    }

    [Theory]
    [InlineData("winget: the uninstaller waited for input and was stopped.", true)]
    [InlineData("winget: the installer stopped responding and was stopped.", false)]
    [InlineData("access denied", false)]
    [InlineData(null, false)]
    public void The_elevated_executor_recognises_a_stall_the_worker_reports(string? error, bool expected)
    {
        Assert.Equal(expected, ElevatedUninstallExecutor.ReportsStall(error));
    }
}
