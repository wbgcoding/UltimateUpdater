using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class PythonUninstallRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wu-pyuninstall-" + Guid.NewGuid());

    public PythonUninstallRunTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        PythonVersionCleanupExecutor.RunnerForTest.Value = null;
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_quoted_executable_with_a_quoted_argument_containing_a_space_splits_into_three_arguments()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(
            @"""C:\Py\python.exe"" /uninstall /log ""C:\Temp\a b.log""");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Py\python.exe", split!.Value.Executable);
        Assert.Equal(["/uninstall", "/log", @"C:\Temp\a b.log"], split.Value.Arguments);
    }

    [Fact]
    public void An_unquoted_path_with_spaces_still_keeps_its_quoted_arguments_whole()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(
            @"C:\Program Files\Python312\uninstall.exe /quiet /log ""D:\My Logs\py.log"" /x");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Program Files\Python312\uninstall.exe", split!.Value.Executable);
        Assert.Equal(["/quiet", "/log", @"D:\My Logs\py.log", "/x"], split.Value.Arguments);
    }

    [Fact]
    public void Backslashes_before_a_quote_follow_the_argv_rules()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(@"""C:\Py\python.exe"" ""C:\dir with space\\"" last");

        Assert.NotNull(split);
        Assert.Equal([@"C:\dir with space\", "last"], split!.Value.Arguments);
    }

    /// <summary>Stop pressed while an uninstaller is running must not kill it half way: the process gets a
    /// token that can never cancel, and its own exit code decides the result.</summary>
    [Fact]
    public async Task A_started_uninstall_is_awaited_and_is_never_handed_the_stop_token()
    {
        var exe = Path.Combine(_folder, "uninstall.exe");
        File.WriteAllText(exe, "x");
        using var stop = new CancellationTokenSource();
        CancellationToken seen = new(true);
        var completed = false;

        PythonVersionCleanupExecutor.RunnerForTest.Value = async (_, _, _, token) =>
        {
            seen = token;
            stop.Cancel();
            await Task.Delay(100, CancellationToken.None);
            completed = true;
            return new ProcessResult(0, "", "");
        };

        var candidate = new PythonVersionCandidate(@"C:\Py\python.exe", "Python 3.9", null, 0, 0, $"\"{exe}\" /quiet", PerUser: true);

        var ok = await PythonVersionCleanupExecutor.RunUninstallAsync(candidate, new LoggingService(), stop.Token);

        Assert.True(ok);
        Assert.True(completed);
        Assert.False(seen.CanBeCanceled);
    }
}
