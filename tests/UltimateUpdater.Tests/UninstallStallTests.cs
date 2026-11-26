using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>A removal that waits for input nobody can give (a vendor window in an elevated session) is ended and explained.</summary>
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
