using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevation boundary: an elevated process taking an executable path from a directory the user can
/// write, an elevated worker accepting a pipe served by someone else, and unbounded reads on the
/// self-update metadata.
/// </summary>
public class SecurityFixTests
{
    [Fact]
    public void A_windows_binary_is_recognised_as_signed()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        Assert.True(File.Exists(notepad));
        Assert.True(TrustedBinary.IsSigned(notepad));
    }

    [Fact]
    public void Anything_planted_is_not_signed()
    {
        var planted = Path.Combine(Directory.CreateTempSubdirectory("wu-trust-").FullName, "winget.exe");
        File.WriteAllBytes(planted, [0x4D, 0x5A, 0x90, 0x00]);   // just enough to look like a PE

        Assert.False(TrustedBinary.IsSigned(planted));
    }

    /// <summary>
    /// The check has to answer for the bytes that will actually run, not for whatever the path resolves to
    /// at the moment it is asked: a signed file in a directory this user can write could otherwise be
    /// swapped for an unsigned payload in the moment between passing the check and being launched. Handing
    /// the verification the handle the caller is holding open is what removes that gap, so a mismatched
    /// handle must make the check say "not signed" even though the PATH alone verifies perfectly.
    /// </summary>
    [Fact]
    public void The_trust_check_answers_for_the_pinned_handle_not_for_the_path()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        var unsigned = Path.Combine(Directory.CreateTempSubdirectory("wu-pin-").FullName, "payload.exe");
        File.WriteAllBytes(unsigned, [0x4D, 0x5A, 0x90, 0x00]);

        using var pin = new FileStream(unsigned, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(TrustedBinary.IsSigned(notepad), "the path on its own verifies");
        Assert.False(TrustedBinary.IsSigned(notepad, pin), "the pinned bytes are what decides");
    }

    /// <summary>
    /// The handle is taken BEFORE the check and kept until the process has started: FileShare.Read denies
    /// write, delete and rename for as long as it lives, so nothing can re-point the path at other bytes
    /// once the file has been accepted.
    /// </summary>
    [Fact]
    public void An_accepted_elevated_target_stays_held_open_until_it_is_released()
    {
        var copy = Path.Combine(AppContext.BaseDirectory, $"wu-pin-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), copy);

        try
        {
            // Nobody else holds it, so the probe below says something about our own pin and nothing else.
            using (var free = TryOpenExclusive(copy))
                Assert.NotNull(free);

            var target = ProcessRunner.ResolveForPrivilege(copy, elevated: true, TrustAnyLocation);
            try
            {
                Assert.Equal(copy, target.Path);

                using var blocked = TryOpenExclusive(copy);
                Assert.Null(blocked);
            }
            finally
            {
                target.Dispose();
            }

            using var released = TryOpenExclusive(copy);
            Assert.NotNull(released);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>
    /// A target that cannot be held open cannot be vouched for, so it is refused rather than launched on
    /// the strength of its path, with exactly one exception, the app-execution alias below, which is a
    /// zero-length reparse point that cannot be opened by anyone and is recognised by that very fact.
    /// </summary>
    [Fact]
    public void An_elevated_launch_refuses_a_signed_tool_it_cannot_hold_open()
    {
        // A copy of a catalog-signed binary, outside the user profile (the test's own output directory is)
        //, so the only thing this test varies is whether the file can be held open.
        var copy = Path.Combine(AppContext.BaseDirectory, $"wu-pin-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), copy);

        try
        {
            using (var accepted = ProcessRunner.ResolveForPrivilege(copy, elevated: true, TrustAnyLocation))
                Assert.Equal(copy, accepted.Path);

            using var exclusive = TryOpenExclusive(copy);
            Assert.NotNull(exclusive);

            Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(copy, elevated: true, TrustAnyLocation));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>These tests vary the pin and the signature, not the folder: the test output directory is
    /// user-writable, which the location rule rightly refuses (ElevatedPathTrustTests covers that rule).</summary>
    private static bool TrustAnyLocation(string path, FileStream pin) => true;

    private static FileStream? TryOpenExclusive(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one that matters: winget resolves through %LOCALAPPDATA%\Microsoft\WindowsApps on a normal
    /// machine, and that directory is writable without admin. Elevated, a planted stand-in there must
    /// not be launched with the token the user granted to this app.
    /// </summary>
    [Fact]
    public void An_elevated_launch_refuses_an_unsigned_tool()
    {
        var planted = Path.Combine(Directory.CreateTempSubdirectory("wu-trust-").FullName, "winget.exe");
        File.WriteAllBytes(planted, [0x4D, 0x5A, 0x90, 0x00]);

        var refused = Assert.Throws<UntrustedToolException>(
            () => ProcessRunner.ResolveForPrivilege(planted, elevated: true));

        Assert.Equal(planted, refused.Path);

        // Unelevated the same call is fine: the tools reached there include.cmd shims, which carry
        // no signature and gain nothing from one.
        using var unelevated = ProcessRunner.ResolveForPrivilege(planted, elevated: false);
        Assert.Equal(planted, unelevated.Path);
    }

    [Fact]
    public void An_elevated_launch_accepts_a_signed_tool()
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

        using var target = ProcessRunner.ResolveForPrivilege(powershell, elevated: true);
        Assert.Equal(powershell, target.Path);
    }

    /// <summary>
    /// The production case, and the one that would break the app rather than expose it: winget is
    /// reached through an app-execution alias, which is a reparse point, not a plain file. If the
    /// check cannot read it the elevated worker would refuse to install anything through winget at
    /// all. Skipped where winget is not installed.
    /// </summary>
    [Fact]
    public void The_real_winget_alias_passes_the_check()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");

        if (!File.Exists(alias))
            return;

        using var target = ProcessRunner.ResolveForPrivilege(alias, elevated: true);
        Assert.Equal(alias, target.Path);
    }

    /// <summary>
    /// The attack itself, in the real directory: anyone running as this user can drop a file next to
    /// the alias. An ordinary file there is readable and unsigned, and must be refused even though it
    /// sits exactly where the genuine winget alias lives.
    /// </summary>
    [Fact]
    public void A_binary_planted_next_to_the_alias_is_still_refused()
    {
        var aliasDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");

        if (!Directory.Exists(aliasDirectory))
            return;

        var planted = Path.Combine(aliasDirectory, $"wu-plant-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), planted);
        try
        {
            Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(planted, elevated: true));
        }
        finally
        {
            File.Delete(planted);
        }
    }

    /// <summary>
    /// The attack a signature check does NOT stop, and the reason the rule is about location rather
    /// than about being signed at all: catalog signing covers the file's hash, so a copy of any signed
    /// binary keeps a valid signature under any name. Windows' own notepad.exe, copied and called
    /// winget.exe, verifies, and a signed host next to an attacker's DLL is code execution as admin.
    /// </summary>
    [Fact]
    public void A_signed_binary_copied_into_the_user_profile_is_refused()
    {
        var planted = Path.Combine(Directory.CreateTempSubdirectory("wu-signed-plant-").FullName, "winget.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), planted);

        // the signature genuinely does still verify, that is exactly why it cannot be the whole rule
        Assert.True(TrustedBinary.IsSigned(planted));

        Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(planted, elevated: true));
    }

    /// <summary>
    /// When the app gives up on the pipe, the name becomes free while the elevated worker is still
    /// trying to connect, so any process running as this user could serve it and hand an elevated
    /// worker its own list of packages. The worker checks who is serving before it reads a job.
    /// </summary>
    [Fact]
    public async Task The_worker_refuses_a_pipe_served_by_someone_else()
    {
        var pipeName = $"UltimateUpdater-test-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // process id 0 is never a real server, so this stands in for "anyone but the app"
        var worker = ElevatedWorker.RunAsync(pipeName, expectedServerProcessId: 0, new LoggingService());

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(5, await worker.WaitAsync(TimeSpan.FromSeconds(20)));

        // and it must have refused BEFORE asking for a job
        Assert.False(server.IsConnected && server.CanRead && HasPendingJobRequest(server));
    }

    private static bool HasPendingJobRequest(NamedPipeServerStream server)
    {
        try
        {
            return server.ReadByte() >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Oversized_update_metadata_is_refused()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[5 * 1024 * 1024]),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y/releases"),
        };

        Assert.Null(await UpdateMetadata.ReadLimitedAsync(response, new LoggingService(), CancellationToken.None));
    }

    [Fact]
    public async Task Normal_update_metadata_is_read_whole()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Mozilla.Firefox  1.2.3"),
            // RequestMessage is what a real SendAsync leaves on the response, allowlisted or not, set by
            // hand here since this test never actually sends the request.
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y/releases"),
        };

        Assert.Equal(
            "Mozilla.Firefox  1.2.3",
            await UpdateMetadata.ReadLimitedAsync(response, new LoggingService(), CancellationToken.None));
    }

    /// <summary>The allowlist re-check inside ReadLimitedAsync (UpdateChannels.cs) is what closes the
    /// redirect gap: a response whose RequestMessage ended up off-allowlist must be refused even though
    /// its size is fine.</summary>
    [Fact]
    public async Task Update_metadata_that_redirected_off_the_allowlist_is_refused()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Mozilla.Firefox  1.2.3"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example.com/releases"),
        };

        Assert.Null(await UpdateMetadata.ReadLimitedAsync(response, new LoggingService(), CancellationToken.None));
    }

    /// <summary>
    /// The app id is interpolated into a steam:// URI that goes to the shell, and it comes out of an
    /// ACF file in a library folder that need not belong to this user. Install and Uninstall never reach
    /// the shell at all (a Steam row is information only), so the one path that does, Start, which the
    /// user picks deliberately, carries the numeric guard.
    /// </summary>
    [Fact]
    public async Task A_non_numeric_steam_app_id_never_reaches_the_shell()
    {
        var source = new SteamSource(new LoggingService());
        var item = new UpdateItem
        {
            Id = "420/../../evil",
            SourceId = "steam",
            Category = UpdateCategory.Games,
            Name = "Not a game",
        };

        Assert.Null(source.LaunchTarget(item));

        var progress = new RecordingProgress();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.UninstallAsync(item, progress, CancellationToken.None));

        // the launch was preceded by a progress report, so nothing reported means nothing launched
        Assert.False(progress.Reported);
    }

    /// <summary>Synchronous on purpose: Progress&lt;T&gt; would post the callback and race the assertion.</summary>
    private sealed class RecordingProgress : IProgress<InstallProgress>
    {
        public bool Reported { get; private set; }

        public void Report(InstallProgress value) => Reported = true;
    }

    /// <summary>
    /// The elevated worker's log sink used to read <see cref="AppPaths.LogsDirectory"/> in its
    /// constructor, which runs the WHOLE static initializer of <see cref="AppPaths"/> (data directory
    /// resolution, following the same-user-writable datapath.txt pointer, folder creation) as admin. This
    /// mirrors exactly what App.xaml.cs does for a worker process (<c>new LoggingService(ElevatedWorker.CollectLog)</c>)
    /// and asserts, through the access-count seam on <see cref="AppPaths"/>, that constructing it touches
    /// nothing on that type. <see cref="AppPaths.AccessCount"/> is process-wide and non-zero by the time this
    /// test runs (other tests already touched it), so the assertion is a before/after diff around the one
    /// call under test, not a check that the type was never initialized.
    /// </summary>
    [Fact]
    public void Constructing_the_worker_log_sink_never_touches_AppPaths()
    {
        var before = AppPaths.AccessCount;

        var lines = new List<string>();
        var logging = new LoggingService(lines.Add);
        logging.Info("worker started");
        logging.Error("worker failed", new InvalidOperationException("boom"));
        logging.Flush();

        Assert.Equal(before, AppPaths.AccessCount);
        Assert.Equal(2, lines.Count);
    }

    /// <summary>The pip blocked-version store used to resolve its default path (AppPaths) before its own
    /// worker check; in a worker-mode source that must stay untouched, and nothing is written.</summary>
    [Fact]
    public void A_worker_mode_pip_install_failure_never_touches_AppPaths()
    {
        // The worker's logger takes a sink and never reads AppPaths; the default one does.
        var source = new PipSource(new LoggingService(_ => { }), new HttpClient()) { RunsInElevatedWorker = true };
        var before = AppPaths.AccessCount;
        const string python = @"C:\Python315\python.exe";

        source.BlockedStore.Add(python, "pymupdf", "2.0");
        var (visible, hidden) = source.BlockedStore.Apply(python, [new OutdatedPackage("pymupdf", "pymupdf", "1.0", "2.0")]);

        Assert.Equal(before, AppPaths.AccessCount);
        Assert.Single(visible);
        Assert.Empty(hidden);
    }
}
