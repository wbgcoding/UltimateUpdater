using System.IO;
using System.Runtime.InteropServices;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Runs the "older Python versions" row's actual uninstall, in EITHER process this app has: the elevated
/// worker for a machine-wide interpreter, or the ordinary unelevated app itself for a per-user one. Always
/// re-runs <see cref="PythonCleanupDiscovery"/> against a FRESH registry read and only ever launches an
/// uninstall command this pass re-derived itself, never a string the unelevated app sent over the pipe.
///
/// The split by <see cref="PythonVersionCandidate.PerUser"/> below is the security boundary, not a
/// convenience: a per-user interpreter's own Uninstall registry entry lives under HKCU, which the SAME
/// unprivileged user this app already runs as can write to. If the elevated worker ever launched a
/// command read from there, a same-user process could plant a fake "Python 3.x" entry (any
/// DisplayName/QuietUninstallString it likes) and have ITS command run with administrator rights the
/// moment this row is cleaned, a same-user-to-admin privilege escalation, not a Python bug at all. A
/// per-user interpreter's real uninstaller also never needed admin in the first place (python.org's
/// per-user installer registers and uninstalls entirely without elevation), so nothing is lost by refusing
/// it here: <see cref="CleanupService.CleanOldPythonVersionsAsync"/> calls this SAME method again, always,
/// from the ordinary unelevated app, and that second call is where every per-user row actually gets
/// uninstalled. Each interpreter's own registered uninstaller does the actual removal either way; this
/// never deletes an install folder by hand.
/// </summary>
public static class PythonVersionCleanupExecutor
{
    public static async Task<CleanupOutcome> CleanAsync(
        IRegistryView registry, IReadOnlyCollection<string> selectedPythonExePaths, LoggingService logging,
        CancellationToken cancellationToken)
    {
        if (selectedPythonExePaths.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        var matched = PythonCleanupDiscovery.DiscoverOldVersions(registry)
            .Where(c => selectedPythonExePaths.Contains(c.PythonExe, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // An app started as administrator has no unelevated pass after this one, so it gets the elevated
        // worker's allow-list: a per-user command read from HKCU must never run with administrator rights.
        var isElevatedWorker = ElevationService.IsElevatedWorkerProcess;
        var runsElevated = isElevatedWorker || ElevationService.IsElevated;
        var eligible = runsElevated
            ? CandidatesEligibleForElevatedWorker(matched)
            : CandidatesEligibleUnelevated(matched);

        var deferred = matched.Count - eligible.Count;
        var deleted = 0;
        var skipped = 0;

        if (runsElevated && deferred > 0)
        {
            if (isElevatedWorker)
            {
                logging.Warn(
                    $"Python version cleanup: the elevated worker left {deferred} per-user interpreter(s) untouched; " +
                    "those are uninstalled by the app itself, never with administrator rights.");
            }
            else
            {
                // Nothing else will pick these up in this run, so they are reported rather than dropped.
                skipped = deferred;
                logging.Warn(
                    $"Python version cleanup: {deferred} per-user interpreter(s) were skipped because the app runs " +
                    "as administrator; start it normally to remove them.");
            }
        }

        foreach (var candidate in eligible)
        {
            // Returned, not thrown: the caller discards everything from a cancelled call, and the versions
            // already removed are real work the user must be told about.
            if (cancellationToken.IsCancellationRequested)
                return new CleanupOutcome(0, deleted, skipped, WasStopped: true);

            if (await RunUninstallAsync(candidate, logging, cancellationToken))
                deleted++;
            else
                skipped++;
        }

        return new CleanupOutcome(0, deleted, skipped);
    }

    /// <summary>
    /// The elevated worker's own allow-list: never a per-user (HKCU-sourced) candidate. Pure and public
    /// (well, internal) precisely so the security property is directly provable without an elevated
    /// process, a real registry, or a real uninstaller: see the class doc comment above for why this
    /// split exists at all.
    /// </summary>
    internal static IReadOnlyList<PythonVersionCandidate> CandidatesEligibleForElevatedWorker(
        IReadOnlyList<PythonVersionCandidate> candidates) =>
        candidates.Where(c => !c.PerUser).ToList();

    /// <summary>The unelevated app's own half: exactly the per-user rows the elevated worker above must
    /// never touch.</summary>
    internal static IReadOnlyList<PythonVersionCandidate> CandidatesEligibleUnelevated(
        IReadOnlyList<PythonVersionCandidate> candidates) =>
        candidates.Where(c => c.PerUser).ToList();

    /// <summary>Test seam for the process launch (<see cref="ProcessRunner.RunAsync"/> is static). Per async
    /// flow, so parallel tests never see each other's runner.</summary>
    internal static readonly AsyncLocal<Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>>?> RunnerForTest = new();

    /// <summary>A real uninstaller (a large bundle, an antivirus scan of its files) can run far past the
    /// generic five minutes; killing it half way leaves a broken install behind.</summary>
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(30);

    internal static async Task<bool> RunUninstallAsync(
        PythonVersionCandidate candidate, LoggingService logging, CancellationToken cancellationToken)
    {
        var split = SplitCommand(candidate.UninstallCommand);
        if (split is null)
        {
            logging.Warn($"Python version cleanup: could not parse the uninstall command for '{candidate.DisplayLabel}'.");
            return false;
        }

        var (executable, arguments) = split.Value;
        if (!File.Exists(executable))
        {
            logging.Warn($"Python version cleanup: uninstaller for '{candidate.DisplayLabel}' was not found at '{executable}'.");
            return false;
        }

        try
        {
            // Stop is honoured between candidates only: killing an uninstaller half way leaves a broken,
            // half-removed Python install behind, so a started one always runs to its own end.
            var run = RunnerForTest.Value
                ?? ((file, args, timeout, token) => ProcessRunner.RunAsync(file, args, timeout, token));
            var result = await run(executable, arguments, UninstallTimeout, CancellationToken.None);
            if (result.TimedOut)
            {
                logging.Warn($"Python version cleanup: uninstalling '{candidate.DisplayLabel}' was cut off after {UninstallTimeout.TotalMinutes:0} minutes.");
                return false;
            }

            if (result.ExitCode == 0)
                return true;

            logging.Warn($"Python version cleanup: uninstalling '{candidate.DisplayLabel}' exited with code {result.ExitCode}.");
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException
                                       or UntrustedToolException)
        {
            // An unsigned uninstaller is refused for this one version; the others still get their turn.
            logging.Warn($"Python version cleanup: could not uninstall '{candidate.DisplayLabel}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Splits a registry uninstall command into an executable plus its arguments. A quoted path is the
    /// easy case; the harder one, and the shape <see cref="UltimateUpdater.Services.Sources.PackageManagers.PythonInterpreters.
    /// FindUninstallCommand"/> actually returns for a real python.org entry, is an UNQUOTED path that can
    /// itself contain spaces ("C:\Program Files\Python312\uninstall.exe /quiet"): naively splitting on
    /// whitespace would cut the path itself apart. The executable is instead read up to and including the
    /// first ".exe" (case-insensitive), whitespace-bounded on the right, which is exactly where a real
    /// Windows executable path ends regardless of how many spaces came before it.
    /// </summary>
    internal static (string Executable, IReadOnlyList<string> Arguments)? SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();

        if (trimmed[0] == '"')
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing < 0)
                return null;

            var quotedExecutable = trimmed[1..closing];
            var quotedRest = trimmed[(closing + 1)..].TrimStart();
            return (quotedExecutable, SplitArguments(quotedRest));
        }

        // The first ".exe" that ends a token (followed by whitespace or the end of the string): a folder such
        // as "my.exes" or "tools.exe-old" merely contains the text and must not cut the path short.
        var exeEnd = -1;
        for (var from = 0; from < trimmed.Length;)
        {
            var marker = trimmed.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                break;

            var end = marker + 4;
            if (end == trimmed.Length || char.IsWhiteSpace(trimmed[end]))
            {
                exeEnd = end;
                break;
            }

            from = marker + 1;
        }

        if (exeEnd < 0)
            return null;

        var executable = trimmed[..exeEnd];
        var rest = trimmed[exeEnd..].TrimStart();
        return (executable, SplitArguments(rest));
    }

    /// <summary>
    /// Splits the argument part with Windows' own command-line rules (<c>CommandLineToArgvW</c>), so a quoted
    /// argument with a space stays one argument and backslashes before a quote mean what they mean to the
    /// program that will parse them. The API treats the first token as the program name with its own
    /// quoting rules, hence the placeholder in front, which is dropped again.
    /// </summary>
    private static IReadOnlyList<string> SplitArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var argv = CommandLineToArgvW("x " + arguments, out var count);
        if (argv == IntPtr.Zero)
            return [];

        try
        {
            var result = new List<string>(Math.Max(0, count - 1));
            for (var i = 1; i < count; i++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty);
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
