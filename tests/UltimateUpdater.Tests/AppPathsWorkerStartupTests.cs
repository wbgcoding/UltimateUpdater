using System.Diagnostics;

namespace UltimateUpdater.Tests;

/// <summary>
/// This fix (follow-up of 176): the elevated worker must never touch <see cref="UltimateUpdater.Services.AppPaths"/>
/// during startup. The earlier test for this
/// (<c>SecurityFixTests.Constructing_the_worker_log_sink_never_touches_AppPaths</c>) only proves that
/// constructing the worker's <c>LoggingService</c> alone is clean, in a process where countless other
/// tests already touched <see cref="UltimateUpdater.Services.AppPaths"/> long before this one runs, a
/// before/after diff, not a "never touched" proof. Proving "never touched" needs a genuinely fresh
/// process: the real app, launched exactly as the elevated worker is (<c>--elevated-worker</c>), reports
/// through its own stdout whether <see cref="UltimateUpdater.Services.AppPathsTouchTracker"/> ever saw a
/// touch by the time it exits. The worker args here name a pipe that cannot exist, so the run fails
/// deterministically fast (an unparsable process id, in <c>App.OnStartup</c>'s own arg check) without
/// needing a real elevated pipe partner.
/// </summary>
public class AppPathsWorkerStartupTests
{
    [Fact]
    public async Task Worker_mode_startup_never_touches_AppPaths()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "UltimateUpdater.exe");
        Assert.True(File.Exists(exePath), $"expected the app exe next to the test output at '{exePath}'");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                // "--elevated-worker" enters worker mode; "not-a-pipe" / "not-a-number" fail App.OnStartup's
                // own arg check immediately, before any pipe connection is attempted; the 4th argument asks
                // the run to report the touch tracker on the way out.
                ArgumentList = { "--elevated-worker", "not-a-pipe", "not-a-number", "--report-apppaths-touch" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(20_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("the worker-mode process did not exit within 20 seconds");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Contains(
            "AppPathsTouched=False", stdout,
            StringComparison.Ordinal);
        Assert.True(
            !stdout.Contains("AppPathsTouched=True", StringComparison.Ordinal),
            $"worker-mode startup touched AppPaths; stdout:\n{stdout}\nstderr:\n{stderr}");
    }

    /// <summary>
    /// The test above only proves the ARGUMENT CHECK path is clean, because
    /// "not-a-number" fails <c>App.xaml.cs</c>'s own arg parsing before <c>ElevatedWorker.RunAsync</c> is
    /// ever called. A valid parent process id (this test process' own) with a pipe name nobody is serving
    /// instead passes that check and actually reaches <c>RunAsync</c>'s pipe-connect path, which fails on
    /// its own 30-second connect timeout, proving the tracker stays untouched on THAT path too.
    /// </summary>
    [Fact]
    public async Task Worker_mode_connect_path_never_touches_AppPaths()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "UltimateUpdater.exe");
        Assert.True(File.Exists(exePath), $"expected the app exe next to the test output at '{exePath}'");

        var pipeName = "wu-nonexistent-pipe-" + Guid.NewGuid().ToString("N");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                ArgumentList =
                {
                    "--elevated-worker", pipeName, Environment.ProcessId.ToString(), "--report-apppaths-touch",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // ElevatedWorker.RunAsync's own pipe-connect timeout is 30s; a loaded machine can start the process
        // late, so the wait leaves a wide margin past that.
        var exited = process.WaitForExit(90_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail(
                "the worker-mode process did not exit within 90 seconds. stdout: "
                + await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5))
                + " stderr: "
                + await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Contains(
            "AppPathsTouched=False", stdout,
            StringComparison.Ordinal);
        Assert.True(
            !stdout.Contains("AppPathsTouched=True", StringComparison.Ordinal),
            $"worker-mode connect-path startup touched AppPaths; stdout:\n{stdout}\nstderr:\n{stderr}");
    }
}
