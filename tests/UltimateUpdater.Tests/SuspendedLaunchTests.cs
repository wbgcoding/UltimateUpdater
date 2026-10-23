using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevated route for a verified alias: started suspended, the mapped image compared with the pinned
/// target, resumed only on a match. The generic tests use cmd.exe as a stand-in image; the host test runs
/// the real winget alias through the same code.
/// </summary>
public class SuspendedLaunchTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static ProcessStartInfo CmdStart(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Cmd,
            WorkingDirectory = Environment.SystemDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string ImageOf(string path)
    {
        using var handle = File.OpenHandle(path);
        return SuspendedChildProcess.PlainFinalPath(handle)!;
    }

    [Fact]
    public async Task A_matching_image_runs_and_its_output_and_exit_code_come_back()
    {
        using var child = SuspendedChildProcess.Start(CmdStart("/c", "echo out-text & echo err-text 1>&2 & exit 3"), ImageOf(Cmd), expectedPackage: null);

        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync(CancellationToken.None);

        Assert.Contains("out-text", await stdout);
        Assert.Contains("err-text", await stderr);
        Assert.Equal(3, child.ExitCode);
        Assert.True(child.HasExited);
    }

    /// <summary>cmd resolves the temp folder itself, so no path travels through the argument list and a temp
    /// folder with spaces needs no quoting.</summary>
    private static (string Name, string Marker) NewMarker()
    {
        var name = "wu-marker-" + Guid.NewGuid().ToString("N");
        return (name, Path.Combine(Path.GetTempPath(), name));
    }

    [Fact]
    public async Task A_different_image_is_refused_before_it_runs_anything()
    {
        var (name, marker) = NewMarker();
        try
        {
            Assert.Throws<UntrustedToolException>(() =>
                SuspendedChildProcess.Start(
                    CmdStart("/c", "cd /d %TEMP% & md " + name), @"C:\Windows\System32\notepad.exe", expectedPackage: null));

            await Task.Delay(500);
            Assert.False(Directory.Exists(marker), "the refused process ran");

            using var child = SuspendedChildProcess.Start(
                CmdStart("/c", "cd /d %TEMP% & md " + name), ImageOf(Cmd), expectedPackage: null);
            await child.WaitForExitAsync(CancellationToken.None);
            Assert.True(Directory.Exists(marker), "the matching process did not run");
        }
        finally
        {
            try { Directory.Delete(marker); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Through_the_runner_the_alias_route_returns_output_and_honours_the_timeout()
    {
        ProcessRunner.UnpackagedImageForTest.Value = true;
        using var targetPin = new FileStream(Cmd, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var aliasStandIn = File.OpenHandle(Cmd);
        using var executable = new ResolvedExecutable(Cmd, null, targetPin, aliasStandIn);

        var echoed = await ProcessRunner.RunResolvedAsync(
            executable, ["/c", "echo hello"], TimeSpan.FromSeconds(30), CancellationToken.None, null, null, null, null, elevated: true);
        Assert.Equal(0, echoed.ExitCode);
        Assert.Contains("hello", echoed.StandardOutput);

        var watch = Stopwatch.StartNew();
        var slow = await ProcessRunner.RunResolvedAsync(
            executable, ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromSeconds(1), CancellationToken.None, null, null, null, null, elevated: true);
        Assert.True(slow.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the timeout kill took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_stop_request_ends_the_alias_route_child()
    {
        ProcessRunner.UnpackagedImageForTest.Value = true;
        using var targetPin = new FileStream(Cmd, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var aliasStandIn = File.OpenHandle(Cmd);
        using var executable = new ResolvedExecutable(Cmd, null, targetPin, aliasStandIn);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var (name, marker) = NewMarker();

        try
        {
            // The child waits about three seconds and only then creates the marker: a child that was not
            // really ended by the stop request would still get there.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunResolvedAsync(
                executable, ["/c", "cd /d %TEMP% & ping -n 4 127.0.0.1 >nul & md " + name],
                TimeSpan.FromSeconds(60), stop.Token, null, null, null, null, elevated: true));

            await Task.Delay(TimeSpan.FromSeconds(6));
            Assert.False(Directory.Exists(marker), "the child kept running after the stop request");
        }
        finally
        {
            try { Directory.Delete(marker); } catch (IOException) { }
        }
    }

    private const string Image = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\winget.exe";
    private static readonly (string Family, string Aumid) Winget =
        (AppExecutionAlias.WingetPackageFamily, AppExecutionAlias.WingetAppUserModelId);

    [Theory]
    [InlineData(true, Image, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget")]
    [InlineData(true, Image, "microsoft.desktopappinstaller_8wekyb3d8bbwe", "microsoft.desktopappinstaller_8wekyb3d8bbwe!WINGET")]
    [InlineData(false, @"C:\Other\winget.exe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget")]
    [InlineData(false, null, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget")]
    [InlineData(false, Image, "Evil.Package_abcdefghijklm", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget")]
    [InlineData(false, Image, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!other")]
    [InlineData(false, Image, null, null)]
    [InlineData(false, Image, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", null)]
    public void MayResume_requires_image_family_and_application_id_when_a_package_is_expected(
        bool expected, string? mapped, string? family, string? aumid) =>
        Assert.Equal(expected, SuspendedChildProcess.MayResume(mapped, Image, family, aumid, Winget));

    [Theory]
    [InlineData(true, Image, null, null)]
    [InlineData(false, Image, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe!winget")]
    [InlineData(false, Image, "Some.Package_abcdefghijklm", null)]
    [InlineData(false, Image, null, "Some.Package_abcdefghijklm!app")]
    [InlineData(false, @"C:\Other\x.exe", null, null)]
    public void MayResume_requires_no_identity_at_all_when_none_is_expected(
        bool expected, string? mapped, string? family, string? aumid) =>
        Assert.Equal(expected, SuspendedChildProcess.MayResume(mapped, Image, family, aumid, expected: null));

    [Fact]
    public void A_packaged_expectation_refuses_an_unpackaged_image_before_it_runs()
    {
        Assert.Throws<UntrustedToolException>(() =>
            SuspendedChildProcess.Start(CmdStart("/c", "exit 0"), ImageOf(Cmd), Winget));
    }

    /// <summary>A launch through <see cref="Process.Start()"/> while a suspended launch holds inheritable
    /// pipe ends must not inherit them: the reader of the suspended child would otherwise wait for output
    /// until the unrelated child ends.</summary>
    [Fact]
    public async Task A_parallel_launch_does_not_keep_the_suspended_childs_output_pipe_open()
    {
        using var executable = new ResolvedExecutable(Cmd, null);
        using var cancel = new CancellationTokenSource();
        Task? other = null;

        SuspendedChildProcess.AfterPipesCreatedForTest.Value = () =>
        {
            other = Task.Run(() => ProcessRunner.RunResolvedAsync(
                executable, ["/c", "ping -n 15 127.0.0.1 >nul"], TimeSpan.FromSeconds(60), cancel.Token,
                null, null, null, null, elevated: false));

            // Long enough for the parallel start to happen inside this window if nothing orders the two.
            Thread.Sleep(500);
        };

        try
        {
            using var child = SuspendedChildProcess.Start(CmdStart("/c", "echo x"), ImageOf(Cmd), expectedPackage: null);

            var output = child.StandardOutput.ReadToEndAsync();
            var finished = await Task.WhenAny(output, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(finished == output, "the suspended child's output never ended: a parallel launch inherited its pipe");
            Assert.Contains("x", await output);
        }
        finally
        {
            SuspendedChildProcess.AfterPipesCreatedForTest.Value = null;
            cancel.Cancel();
            if (other is not null)
            {
                try { await other; } catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public void The_childs_job_allows_breakaway_but_not_silent_breakaway()
    {
        using var child = SuspendedChildProcess.Start(CmdStart("/c", "exit 0"), ImageOf(Cmd), expectedPackage: null);

        Assert.NotEqual(0u, child.JobLimitFlags & 0x00000800);
        Assert.Equal(0u, child.JobLimitFlags & 0x00001000);
    }

    [Fact]
    public void Arguments_are_quoted_so_they_read_back_unchanged()
    {
        var line = new StringBuilder();
        SuspendedChildProcess.AppendArgument(line, @"C:\Program Files\x.exe");
        SuspendedChildProcess.AppendArgument(line, "plain");
        SuspendedChildProcess.AppendArgument(line, "two words");
        SuspendedChildProcess.AppendArgument(line, "say \"hi\"");
        SuspendedChildProcess.AppendArgument(line, @"trailing\");
        SuspendedChildProcess.AppendArgument(line, @"with space\");
        SuspendedChildProcess.AppendArgument(line, string.Empty);

        Assert.Equal(
            "\"C:\\Program Files\\x.exe\" plain \"two words\" \"say \\\"hi\\\"\" trailing\\ \"with space\\\\\" \"\"",
            line.ToString());
    }

    /// <summary>The real thing on a machine that has winget: the alias passes the elevated resolve with both
    /// pins set, and the suspended start runs it. Returns early where winget is absent.</summary>
    [Fact]
    public async Task The_real_winget_alias_resolves_and_runs_through_the_suspended_start()
    {
        if (ProcessRunner.Resolve("winget") is null)
            return;

        using var executable = ProcessRunner.ResolveForPrivilege("winget", elevated: true);

        Assert.NotNull(executable.AliasPin);
        Assert.NotNull(executable.ShimTargetPin);
        Assert.Null(executable.Pin);

        var result = await ProcessRunner.RunResolvedAsync(
            executable, ["--version"], TimeSpan.FromSeconds(60), CancellationToken.None, null, null, null, null, elevated: true);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("v", result.StandardOutput.Trim());
    }

    [Fact]
    public void Package_roots_include_every_volume_store_but_only_plain_drive_paths()
    {
        var roots = AppExecutionAlias.PackageRootsFrom(
            @"C:\Program Files", @"D:\WindowsApps",
            [@"D:\WindowsApps", @"E:\WindowsApps", @"\\server\share\WindowsApps", @"\\?\Volume{1}\WindowsApps", "", null, @"relative\WindowsApps"]);

        Assert.Equal([@"C:\Program Files\WindowsApps", @"D:\WindowsApps", @"E:\WindowsApps"], roots);
    }

    [Fact]
    public void A_link_into_a_second_volumes_store_is_accepted_when_that_store_is_listed()
    {
        var link = new AppExecutionAlias.Link(
            AppExecutionAlias.WingetPackageFamily, AppExecutionAlias.WingetPackageFamily + "!winget",
            @"E:\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe");

        Assert.False(AppExecutionAlias.IsWingetLink(link, [@"C:\Program Files\WindowsApps"]));
        Assert.True(AppExecutionAlias.IsWingetLink(
            link, AppExecutionAlias.PackageRootsFrom(@"C:\Program Files", null, [@"E:\WindowsApps"])));
    }
}
