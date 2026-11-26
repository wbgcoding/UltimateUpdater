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
    public async Task A_stop_ends_a_running_winget_style_removal_through_its_tree()
    {
        var child = new InstallStallWatchTests.HangingChild();
        ProcessRunner.ChildFactoryForTest.Value = () => child;
        try
        {
            using var executable = new ResolvedExecutable(Environment.ProcessPath!, null);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunResolvedAsync(
                executable, [], TimeSpan.FromMinutes(30), stop.Token, null, null, null, null, elevated: false)
                .WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(1, child.KillCalls);
        }
        finally
        {
            ProcessRunner.ChildFactoryForTest.Value = null;
        }
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
