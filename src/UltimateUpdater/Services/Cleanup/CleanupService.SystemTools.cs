using System.IO;
using System.Text;

namespace UltimateUpdater.Services.Cleanup;

public partial class CleanupService
{
    /// <summary>
    /// The exact command line <see cref="AnalyzeComponentStoreAsync"/> runs, split out so a test can
    /// assert on it without launching DISM. <c>/English</c> forces DISM's message text to English on any
    /// Windows display language; the German labels <see cref="ComponentStoreAnalysisParser"/> carries are
    /// only a defensive fallback.
    /// </summary>
    internal static readonly IReadOnlyList<string> AnalyzeComponentStoreArguments =
        ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"];

    /// <summary>How long DISM may take to analyze the store before it is stopped. On a PC with a large or
    /// busy store the read-only analysis runs well past five minutes, which only produced a timeout.</summary>
    internal static readonly TimeSpan ComponentStoreAnalysisTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Read-only measurement of the WinSxS component store via DISM's own AnalyzeComponentStore switch.
    /// Needs administrator rights like every other DISM call in this file; the caller is
    /// responsible for only ever running this from the elevated worker. Unlike
    /// <see cref="RunSystemRepairAsync"/> this writes nothing to the component store, so cancelling it
    /// mid-run is fine and no <see cref="PauseGate"/> is offered, there is nothing to pause.
    /// </summary>
    public async Task<ComponentStoreAnalysis> AnalyzeComponentStoreAsync(CancellationToken cancellationToken)
    {
        var dism = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Dism.exe");
        if (!File.Exists(dism))
        {
            logging.Warn("Cleanup: component store analysis skipped; Dism.exe was not found.");
            return ComponentStoreAnalysis.Unknown;
        }

        try
        {
            // Decoded losslessly as Latin1 (1:1 byte-to-char), not the UTF-8 every other
            // ProcessRunner caller gets, because a real German-language run's bytes turned out to be the
            // machine's own OEM code page, and recovering THAT shape needs the original bytes, which a
            // UTF-8 decode already destroys (every OEM byte becomes an unrecoverable U+FFFD). See
            // OemTextRecovery's own doc comment for the full story.
            var result = await ProcessRunner.RunAsync(
                dism, AnalyzeComponentStoreArguments,
                ComponentStoreAnalysisTimeout, cancellationToken, outputEncoding: Encoding.Latin1);

            // A killed-after-the-deadline DISM must read as "this timed out", never as a run that "finished"
            // with a stray -1 exit code (result.ExitCode is meaningless once TimedOut is set, the process
            // was killed rather than exiting) or as one that quietly reported nothing at all.
            if (result.TimedOut)
            {
                logging.Warn("Cleanup: component store analysis timed out after 15 minutes and was stopped.");
                return ComponentStoreAnalysis.TimedOutResult;
            }

            if (result.ExitCode != 0)
                logging.Info($"Cleanup: component store analysis finished with exit code {result.ExitCode}.");

            // DISM writes UTF-16LE once it notices its output is redirected (the shape RunStreamedStepAsync
            // already recovers). This whole-output capture needs the same recovery, or every label match
            // fails silently and the card shows dashes. Both recovery shapes are safe to apply
            // unconditionally: an ordinary ASCII run trips neither heuristic and passes through untouched.
            var output = result.StandardOutput;
            if (Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(output))
                output = Utf16RedirectRecovery.Recover(output);
            else if (OemTextRecovery.NeedsRecovery(output))
                output = OemTextRecovery.Recover(output);

            var analysis = ComponentStoreAnalysisParser.Parse(output);

            logging.Info(analysis.ActualSizeBytes is null && analysis.SharedWithWindowsBytes is null
                ? "Cleanup: component store analysis completed, but DISM reported no sizes."
                : $"Cleanup: component store analysis completed, actual={analysis.ActualSizeBytes?.ToString() ?? "?"} " +
                  $"shared={analysis.SharedWithWindowsBytes?.ToString() ?? "?"} " +
                  $"recommended={analysis.CleanupRecommended?.ToString() ?? "?"}.");

            return analysis;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logging.Warn($"Cleanup: component store analysis could not be run: {ex.Message}");
            return ComponentStoreAnalysis.Unknown;
        }
    }

    /// <summary>Test seams: another cleanmgr path and another way to write the selection profile, so the
    /// "tool missing" and "profile refused" outcomes can be proven without touching the machine.</summary>
    internal string? CleanmgrExecutable { get; init; }

    internal Func<bool>? SagesetProfileWriter { get; init; }

    /// <summary>Test seams for the repair and defrag steps: a shorter time limit and another defrag path.</summary>
    internal TimeSpan RepairTimeout { get; init; } = RepairStepTimeout;

    internal TimeSpan DefragTimeout { get; init; } = DefragLimit;

    internal string? DefragExecutable { get; init; }

    /// <summary>How long drive optimisation may run before it is ended.</summary>
    internal static readonly TimeSpan DefragLimit = TimeSpan.FromHours(6);

    /// <summary>
    /// Windows' own Disk Cleanup, run WITHOUT its dialogs. It can only run unattended from a saved
    /// profile, so the profile is written first: each handler under VolumeCaches gets a StateFlags value
    /// for our own sageset number, and `/sagerun` then processes exactly those. Only handlers as safe as
    /// the rest of this feature are enabled (temporary files, caches, thumbnails, error reports, delivery
    /// optimisation), NEVER anything that removes user data or a rollback path (no Downloads handler, no
    /// previous-Windows installation, no recycle bin: that is its own opt-in target).
    /// Writing the profile needs admin. Without it the target is skipped with a log line: starting
    /// cleanmgr with its own UI would leave the dialog waiting on a window it cannot cancel.
    /// </summary>
    private async Task<CleanupOutcome> RunDiskCleanupAsync(CancellationToken cancellationToken)
    {
        var cleanmgr = CleanmgrExecutable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cleanmgr.exe");
        if (!File.Exists(cleanmgr))
        {
            logging.Warn("Cleanup: cleanmgr.exe was not found.");
            return CleanupOutcome.Failure();
        }

        // Without admin the profile cannot be written, and starting cleanmgr with its OWN dialogs would
        // leave this dialog waiting on a window the user did not ask for, and unable to cancel it.
        // Skip the target instead and say why.
        var outcome = await RunUnderSagesetProfileAsync(SagesetProfileWriter ?? TryWriteSagesetProfile, RemoveSagesetProfile, async () =>
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cleanmgr,
                    // /sagerun processes the saved profile with no prompts; /verylowdisk is not used because it
                    // silently includes handlers we did not choose.
                    Arguments = $"/sagerun:{SagesetNumber}",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });

                // Bounded AND cancellable: closing the dialog must not leave it waiting for the full limit.
                if (process is not null)
                {
                    var waited = await WaitForDiskCleanupAsync(process, DiskCleanupLimit, cancellationToken);
                    if (waited is { } early)
                        return early;
                }

                // cleanmgr reports nothing about what it freed, and guessing a figure would be a lie.
                return new CleanupOutcome(0, 0, 0);
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logging.Warn($"Cleanup: Disk Cleanup could not be started: {ex.Message}");
                return CleanupOutcome.Failure();
            }
        });

        if (outcome is null)
        {
            // The selection profile could not be written, so cleanmgr was never started.
            logging.Warn("Cleanup: Disk Cleanup was not started: its selection profile could not be written.");
            return CleanupOutcome.Failure();
        }

        return outcome.Value;
    }

    /// <summary>
    /// Keeps the machine-wide profile only for the run itself: a flag left behind would make any later
    /// <c>cleanmgr /sagerun:4711</c> (by hand or another tool) silently run the whole selection again. It is
    /// removed however the run ends (finished, stopped, timed out, failed to start). Returns null when the
    /// profile could not be written, so the caller skips the target.
    /// </summary>
    internal static async Task<CleanupOutcome?> RunUnderSagesetProfileAsync(
        Func<bool> writeProfile, Action removeProfile, Func<Task<CleanupOutcome>> run)
    {
        try
        {
            if (!writeProfile())
                return null;

            return await run();
        }
        finally
        {
            removeProfile();
        }
    }

    /// <summary>Takes our flag off every handler it may have been set on: the counterpart of the write, so
    /// this code stays the only writer of the profile.</summary>
    private void RemoveSagesetProfile()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        var flagName = $"StateFlags{SagesetNumber:0000}";

        try
        {
            using var caches = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root, writable: true);
            if (caches is null)
                return;

            foreach (var handler in SafeVolumeCaches)
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    key?.DeleteValue(flagName, throwOnMissingValue: false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    logging.Info($"Cleanup: could not remove the Disk Cleanup selection from \"{handler}\": {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logging.Info($"Cleanup: could not remove the Disk Cleanup profile: {ex.Message}");
        }
    }

    /// <summary>How long Disk Cleanup may run. "Update Cleanup" alone can take a long while on a machine with
    /// many superseded updates, so the cap is generous; it only exists so a hung cleanmgr cannot hold the
    /// run for ever.</summary>
    internal static readonly TimeSpan DiskCleanupLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Waits for cleanmgr to end. Returns null when it did; otherwise the outcome that ends the target early
    /// (cleanmgr is killed first either way).
    /// </summary>
    internal async Task<CleanupOutcome?> WaitForDiskCleanupAsync(
        System.Diagnostics.Process process, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return null;
        }
        catch (OperationCanceledException)
        {
            // Told apart from the caller's own token: the time limit is a failure, a Stop is not.
            var timedOut = !cancellationToken.IsCancellationRequested;

            // Stop means stop: leaving cleanmgr running after Stop would make the dialog say the run
            // had ended while Windows carried on deleting. Ending it is safe: each file it removes
            // is removed completely, so an abandoned run leaves no half state. The time limit
            // ends it the same way.
            logging.Info(timedOut
                ? $"Cleanup: Disk Cleanup ran longer than {limit.TotalMinutes:0} minutes and was ended."
                : "Cleanup: Disk Cleanup was stopped.");

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                // it finished between the check and the kill, or Windows refused; either way it is gone
            }

            // Caught here rather than left to propagate, because this method still has cleanmgr to
            // kill first. WasStopped tells the caller not to mark the row "done" for a run the user
            // just stopped; TimedOut tells it the run failed instead.
            return new CleanupOutcome(0, 0, 0, WasStopped: !timedOut, TimedOut: timedOut);
        }
    }

    /// <summary>
    /// Windows' own repair pass, run in whatever order the catalogue lists its steps; DISM before sfc
    /// today, because DISM repairs the component store that sfc restores files FROM. Both need admin
    /// and both take a while, so each is run with its own generous timeout and reported as it goes.
    /// They free no measurable space; a repair is not a deletion, and claiming freed bytes here would
    /// be a lie.
    /// </summary>
    private async Task<CleanupOutcome> RunSystemRepairAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
        PauseGate? pause)
    {
        var steps = target.RepairSteps;
        if (steps.Count == 0)
            return new CleanupOutcome(0, 0, 0);

        var done = 0;
        var share = 100.0 / steps.Count;

        // What the tools leave behind that tells how the check ended; see SystemRepairResults. Only the two
        // check targets feed it: the component-store cleanup steps say nothing about the system's health.
        var judgesSfc = target.Id == "system-repair-sfc";
        var judgesDism = target.Id == "system-repair-dism";
        var cbsPath = SystemRepairResults.DefaultCbsLogPath;
        var cbsStart = judgesSfc ? SystemRepairResults.CbsLength(cbsPath) : null;
        var exitCodes = new int?[steps.Count];
        var sfcFinished = false;
        var timedOut = false;
        var couldNotRun = false;
        int? failingExit = null;

        foreach (var (exe, args, label) in steps)
        {
            // Held and stopped BETWEEN steps only; see the note on the run itself below.
            pause?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var stepBase = done * share;
            progress?.Report(new CleanupProgress(label, stepBase, 0));

            if (!File.Exists(exe))
            {
                logging.Warn($"Cleanup: {label} skipped; {Path.GetFileName(exe)} was not found.");
                couldNotRun = true;
                done++;
                continue;
            }

            try
            {
                // CancellationToken.None on purpose: cancelling ProcessRunner KILLS the process tree, and
                // killing DISM mid-write can leave the component store inconsistent; Microsoft says so
                // explicitly. The loop above checks the token BETWEEN steps instead (unlike the
                // defrag step, which passes its real token straight through). The timeout kills too, so it
                // is set far beyond any healthy run and only ever ends a step that is truly stuck.
                var result = await RunStreamedStepAsync(
                    exe, args, label, stepBase, share, progress, RepairTimeout, CancellationToken.None);

                if (result.TimedOut)
                {
                    logging.Warn($"Cleanup: {label} timed out.");
                    timedOut = true;
                }
                else if (result.ExitCode != 0)
                {
                    logging.Info($"Cleanup: {label} finished with exit code {result.ExitCode}.");
                    // 3010 is "done, restart needed": a success. The first other code is the one reported.
                    if (result.ExitCode != RestartRequiredExitCode)
                        failingExit ??= result.ExitCode;
                }
                else
                    logging.Info($"Cleanup: {label} completed.");

                if (!result.TimedOut)
                {
                    exitCodes[done] = result.ExitCode;
                    sfcFinished = true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Without admin these refuse to start at all; expected, and not worth failing the run over.
                logging.Warn($"Cleanup: {label} could not be run: {ex.Message}");
                couldNotRun = true;
            }

            done++;
        }

        progress?.Report(new CleanupProgress(string.Empty, 100, 0));

        RepairResult? repairResult = null;
        if (judgesDism && exitCodes.Length == 3)
        {
            repairResult = SystemRepairResults.ClassifyDism(exitCodes[0], exitCodes[1], exitCodes[2]);
        }
        else if (judgesSfc && sfcFinished && cbsStart is { } startOffset
            && SystemRepairResults.ReadCbsLinesSince(cbsPath, startOffset) is { } lines)
        {
            var verdict = SystemRepairResults.ClassifyCbsLines(lines);
            // A failed run (non-zero exit) that left no trace in the log is unknown, not "OK".
            repairResult = verdict == RepairResult.Ok && exitCodes[0] != 0 ? null : verdict;
        }

        // A check that reached a verdict (all clear, or repaired) is not a failure even though the tool's
        // own exit code said "found something"; every other bad ending is.
        var verdictIsFine = repairResult is RepairResult.Ok or RepairResult.Repaired;
        var failed = couldNotRun || (failingExit is not null && !verdictIsFine);
        return new CleanupOutcome(
            0, 0, 0, TimedOut: timedOut, RepairResult: repairResult,
            Failed: failed && !timedOut, ExitCode: failed && !timedOut ? failingExit : null);
    }

    /// <summary>DISM's "completed, restart required": counts as success.</summary>
    private const int RestartRequiredExitCode = 3010;

    /// <summary>
    /// Windows' own drive optimisation: defragmentation on a rotational disk, retrim on an SSD, the SAME
    /// command for both (`defrag &lt;drive&gt;: /O` decides from the drive's media type). Deliberately no
    /// scheduled task: started directly through the elevated worker like the repair steps and Disk
    /// Cleanup, so a run never outlives the app. Frees no measurable space. Unlike sfc/DISM the real
    /// cancellation token is passed straight through: interrupting drive optimisation is its ordinary way
    /// of being stopped (a shutdown, or Stop in the Optimize Drives UI).
    /// </summary>
    private async Task<CleanupOutcome> RunDefragAsync(
        CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (target.DriveLetter is null)
        {
            logging.Warn("Cleanup: drive optimisation skipped; the target named no drive.");
            return new CleanupOutcome(0, 0, 0);
        }

        var defrag = DefragExecutable ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "defrag.exe");
        if (!File.Exists(defrag))
        {
            logging.Warn("Cleanup: drive optimisation skipped; defrag.exe was not found.");
            return CleanupOutcome.Failure();
        }

        var label = string.Format(LocalizationService.Instance["CleanupDefragName"], target.DriveLetter);

        using var runMarker = DefragRunState.Enter();

        var result = await RunStreamedStepAsync(
            defrag, [target.DriveLetter, "/O", "/U", "/V"], label, stepBase: 0, share: 100, progress,
            DefragTimeout, cancellationToken);

        progress?.Report(new CleanupProgress(string.Empty, 100, 0));

        if (result.TimedOut)
        {
            logging.Warn("Cleanup: drive optimisation timed out.");
            return new CleanupOutcome(0, 0, 0, TimedOut: true);
        }

        if (result.ExitCode != 0)
        {
            logging.Info($"Cleanup: drive optimisation finished with exit code {result.ExitCode}.");
            return CleanupOutcome.Failure(result.ExitCode);
        }

        logging.Info("Cleanup: drive optimisation completed.");
        return new CleanupOutcome(0, 0, 0);
    }

    /// <summary>A RestoreHealth on a slow disk or a slow Windows Update source can pass 45 minutes and is
    /// still healthy; a kill at that point is the mid-write corruption the no-cancel rule exists to avoid.</summary>
    internal static readonly TimeSpan RepairStepTimeout = TimeSpan.FromHours(4);

    /// <summary>
    /// One externally-progressed tool, shared by every repair step and by the defrag command: reports
    /// its percentage scaled into [<paramref name="stepBase"/>, <paramref name="stepBase"/> +
    /// <paramref name="share"/>], applies the UTF-16-redirect recovery, and falls back to the honest
    /// "no progress" hint once <see cref="ShouldShowNoProgressHint"/> says the tool never reports one.
    /// Argument arrays, no shell: the same rule every other external tool here follows.
    /// </summary>
    private static Task<ProcessResult> RunStreamedStepAsync(
        string exe, IReadOnlyList<string> args, string label, double stepBase, double share,
        IProgress<CleanupProgress>? progress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reached = 0.0;
        var hasReadableProgress = false;
        var lastReport = -1.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var nextTextAt = TimeSpan.Zero;
        var nextHintAt = NoProgressHintDelay;

        return ProcessRunner.RunStreamingAsync(
            exe,
            args,
            timeout,
            segment =>
            {
                var line = DecodeStepSegment(segment).Trim();
                if (line.Length == 0)
                    return;

                if (ProgressParser.Parse(line)?.Percent is { } percent)
                {
                    hasReadableProgress = true;

                    // Never backwards: DISM restarts its bar for each internal phase, and a ring that jumps
                    // back looks like the run came undone.
                    reached = Math.Max(reached, percent);

                    // Throttled to whole percent: a redrawn bar arrives many times a second and every
                    // report crosses a dispatcher (and, elevated, a pipe).
                    if (Math.Floor(reached) <= lastReport)
                        return;

                    lastReport = Math.Floor(reached);
                    progress?.Report(new CleanupProgress(
                        $"{label}: {PercentFormat.Format(reached)}", stepBase + (reached / 100.0 * share), 0));
                }
                else if (clock.Elapsed >= nextTextAt)
                {
                    // A plain output line is what the step is currently saying; shown at most a few times a
                    // second so a chatty tool cannot flood the UI.
                    nextTextAt = clock.Elapsed + TimeSpan.FromMilliseconds(300);
                    progress?.Report(new CleanupProgress(
                        $"{label}: {line}", stepBase + (reached / 100.0 * share), 0));
                }

                // A step that has said nothing readable for a while is not "stuck at 0 %",
                // it simply never reports one; the honest running state replaces the plain-text line above
                // once ShouldShowNoProgressHint says so (throttled the same way the plain-text line is),
                // and this keeps checking for the rest of the step, so a tool that starts reporting late
                // still switches straight back.
                if (ShouldShowNoProgressHint(hasReadableProgress, clock.Elapsed, nextHintAt))
                {
                    nextHintAt = clock.Elapsed + NoProgressHintRepeat;
                    progress?.Report(new CleanupProgress(
                        $"{label}: {LocalizationService.Instance["CleanupNoProgressHint"]}", stepBase, 0));
                }
            },
            cancellationToken,
            outputEncoding: Encoding.Latin1);
    }

    /// <summary>
    /// One output segment of a repair or defrag step, read as Latin1 so every byte survives. Sfc writes
    /// UTF-16LE once its output is redirected (a character, then a NUL, over and over; for the Latin1 range
    /// dropping the NULs is the exact text). DISM and defrag write the machine's OEM code page, one byte per
    /// accented letter, which a UTF-8 read turned into a replacement sign ("f?r" instead of "für"). Plain ASCII
    /// is the same text in every one of these and is left alone.
    /// </summary>
    internal static string DecodeStepSegment(string latin1Segment)
    {
        if (Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(latin1Segment))
            return Utf16RedirectRecovery.Recover(latin1Segment);

        return OemTextRecovery.NeedsRecovery(latin1Segment) ? OemTextRecovery.Recover(latin1Segment) : latin1Segment;
    }

    /// <summary>
    /// How long a repair step is given before "no percentage yet" turns into "this step apparently never
    /// reports one". Long enough that DISM's own multi-second startup, or a step that is
    /// simply slow to print its first line, does not flash the hint needlessly.
    /// </summary>
    private static readonly TimeSpan NoProgressHintDelay = TimeSpan.FromSeconds(8);

    /// <summary>Once shown, re-shown at most this often, the same throttling reason the plain-text line
    /// above already has: every report crosses a dispatcher and, elevated, a pipe.</summary>
    private static readonly TimeSpan NoProgressHintRepeat = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pure so the decision is directly testable without running a real process for 8 real seconds: never
    /// once a real percentage has been read, and not sooner than <paramref name="nextHintAt"/> (the caller's
    /// own throttle, seeded at <see cref="NoProgressHintDelay"/> and advanced by
    /// <see cref="NoProgressHintRepeat"/> each time this returns true).
    /// </summary>
    internal static bool ShouldShowNoProgressHint(bool hasReadableProgress, TimeSpan elapsed, TimeSpan nextHintAt) =>
        !hasReadableProgress && elapsed >= nextHintAt;

    /// <summary>Our own profile slot. High enough not to collide with a profile the user saved themselves.</summary>
    private const int SagesetNumber = 4711;

    /// <summary>
    /// The cleanmgr handlers we are willing to run unattended. Deliberately a short allow-list: everything
    /// here is a cache or a temporary file. Anything that could remove user data or a rollback path is
    /// absent on purpose, and absence is the safe default: an unlisted handler simply does not run.
    /// </summary>
    private static readonly string[] SafeVolumeCaches =
    [
        "Temporary Files",
        "Temporary Setup Files",
        "Downloaded Program Files",
        "Internet Cache Files",
        "Thumbnail Cache",
        "Delivery Optimization Files",
        "Windows Error Reporting Files",
        "Windows Error Reporting Queue Files",
        "Windows Error Reporting System Queue Files",
        "Old ChkDsk Files",
        "Setup Log Files",
        "Update Cleanup",
    ];

    /// <summary>
    /// Makes the profile slot belong to the allow-list alone: a handler outside it that already carries our
    /// flag (left by the user's own <c>cleanmgr /sageset</c> or another tool) would otherwise run with the
    /// rest, e.g. the Downloads folder or the recycle bin. Each such flag is cleared; if one cannot be, the
    /// result is false and no unattended run may start. Pure over the registry facts so a test never touches HKLM.
    /// </summary>
    /// <param name="handlers">Every subkey name under VolumeCaches and whether it carries the flag.</param>
    /// <param name="allowed">The handlers that may keep the flag (and get it set afterwards).</param>
    /// <param name="tryClearFlag">Removes the flag from one handler; true when it is gone.</param>
    /// <param name="failedHandler">The first handler whose flag could not be cleared, else null.</param>
    internal static bool ClearForeignHandlerFlags(
        IEnumerable<(string Name, bool HasFlag)> handlers, IReadOnlyCollection<string> allowed,
        Func<string, bool> tryClearFlag, out string? failedHandler)
    {
        failedHandler = null;
        foreach (var (name, hasFlag) in handlers)
        {
            if (!hasFlag || allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!tryClearFlag(name))
                failedHandler ??= name;
        }

        return failedHandler is null;
    }

    private bool TryWriteSagesetProfile()
    {
        const string root = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
        var flagName = $"StateFlags{SagesetNumber:0000}";
        var written = 0;

        try
        {
            using var caches = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root, writable: true);
            if (caches is null)
                return false;

            var handlers = caches.GetSubKeyNames().Select(name =>
            {
                try
                {
                    using var key = caches.OpenSubKey(name, writable: false);
                    return (Name: name, HasFlag: key?.GetValue(flagName) is not null);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // unreadable: assume it carries the flag, so a clear is attempted and a failure fails closed
                    return (Name: name, HasFlag: true);
                }
            }).ToList();

            var clean = ClearForeignHandlerFlags(handlers, SafeVolumeCaches, handler =>
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    key?.DeleteValue(flagName, throwOnMissingValue: false);
                    return true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    return false;
                }
            }, out var stuckHandler);

            if (!clean)
            {
                logging.Info($"Cleanup: no unattended Disk Cleanup profile: the handler \"{stuckHandler}\" carries a foreign selection that could not be cleared.");
                return false;
            }

            foreach (var handler in SafeVolumeCaches)
            {
                try
                {
                    using var key = caches.OpenSubKey(handler, writable: true);
                    if (key is null)
                        continue;       // handler not present on this Windows build

                    // 2 = include in this sageset run; every other handler is left untouched (and so off).
                    key.SetValue(flagName, 2, Microsoft.Win32.RegistryValueKind.DWord);
                    written++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // one handler refusing is not worth abandoning the rest
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logging.Info($"Cleanup: no unattended Disk Cleanup profile (needs admin): {ex.Message}");
            return false;
        }

        if (written == 0)
            logging.Info("Cleanup: Disk Cleanup skipped: no handler could be selected for an unattended profile.");

        return written > 0;
    }
}
