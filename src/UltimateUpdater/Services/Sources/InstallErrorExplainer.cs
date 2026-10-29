using System.Globalization;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The three buckets a failure sorts into: "behebbar" (Recoverable), "Neustart nötig"
/// (RestartNeeded) and "Handarbeit" (ManualWork). <see cref="InstallErrorExplanation.AllowsInteractiveRetry"/>
/// reads Recoverable as "interactively maybe fixable": the one bucket whose retry button is offered for.
/// </summary>
public enum InstallErrorClassification
{
    /// <summary>Something the user (or this app's own interactive-retry flow) can plausibly fix and try again.</summary>
    Recoverable,

    /// <summary>The install already went through; a Windows restart is what finishes it, not a retry.</summary>
    RestartNeeded,

    /// <summary>Needs the user's own action outside this app (its log, a manual install, waiting it out): a retry will not help.</summary>
    ManualWork,
}

/// <summary>
/// One entry from <see cref="InstallErrorExplainer"/>: a "what happened" sentence and a "what now"
/// sentence, each a resource key plus its format arguments so the caller localizes them.
/// </summary>
public sealed record InstallErrorExplanation(
    string WhatHappenedKey,
    object[] WhatHappenedArgs,
    string WhatNowKey,
    object[] WhatNowArgs,
    InstallErrorClassification Classification,
    bool AllowsAutomaticRetry = false)
{
    /// <summary>The interactive-retry eligibility test, deliberately narrow, see InstallErrorExplainer's own table.</summary>
    public bool AllowsInteractiveRetry => Classification == InstallErrorClassification.Recoverable;

    /// <summary>
    /// The automatic-retry eligibility, narrower still than <see cref="AllowsInteractiveRetry"/>:
    /// true only for the causes the batch's own second-attempt round may retry WITHOUT asking anyone -
    /// another installation was already running, or the tool itself reported being busy/timed out. False
    /// by default, including for every cause this file does not (yet) recognize at all: an automatic
    /// retry is the one kind nobody watches, so it only fires where retrying blindly is actually safe.
    /// </summary>

    /// <summary>Both sentences, localized and joined: what the row's tooltip and the error window show.</summary>
    public string Format(Func<string, string> lookup) =>
        $"{string.Format(lookup(WhatHappenedKey), WhatHappenedArgs)} {string.Format(lookup(WhatNowKey), WhatNowArgs)}";
}

/// <summary>
/// Plain-language explanation for a failed install/uninstall, built from the RAW tool output text. Pure
/// and testable, no I/O. From a real report: winget downloads and hash-verifies an installer, runs it
/// silently, and the VENDOR installer's own exit code (not winget's) is what actually failed, and winget only
/// passes that code through in its own message text, in whichever language the system runs
/// ("Installation fehlgeschlagen mit Exitcode: 8006" / "Installer failed with exit code: 8006"). There is
/// no structured field for it anywhere in this app's pipeline; the two regexes below are the only way in.
/// Codes are matched by NUMBER, not by source id or package id. 8001-8006 are Visual Studio Setup's own
/// range regardless of which package triggered them, and the same principle applies to the MSI/winget
/// tables. Every winget-specific code is recognized by calling the SAME helpers <see cref="WingetSource"/>
/// already uses for its own retry/reboot/crash logic (<see cref="WingetSource.IsVendorInstallerCrash"/>,
/// <see cref="WingetSource.IsRebootRequired"/>, <see cref="WingetSource.IsInapplicableOutcome"/>,
/// <see cref="WingetSource.IsDifferentInstallTechnology"/>, <see cref="WingetSource.IsNoUninstallInfo"/>,
/// <see cref="WingetSource.NeedsUserScopeRetry"/>).
/// never re-hardcoded here, so the two places cannot drift.
/// </summary>
public static class InstallErrorExplainer
{
    private static readonly Regex VendorExitCodeDe =
        new(@"Installation fehlgeschlagen mit Exitcode:\s*(-?\d+)", RegexOptions.Compiled);

    private static readonly Regex VendorExitCodeEn =
        new(@"Installer failed with exit code:\s*(-?\d+)", RegexOptions.Compiled);

    private static readonly Regex HexExitCode = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    // Windows Update's own codes are typed COM values known directly at the throw site
    // (WindowsUpdateSource.VerifySucceeded), not extracted from a vendor's free-form text: so they are
    // matched against that fixed English exception wording instead of the vendor-exit-code regexes above.
    private static readonly Regex WuResultCodeRegex =
        new(@"Windows Update install returned result code (\d+)", RegexOptions.Compiled);

    // Every CLI-driven source (chocolatey, npm, pip, scoop, ollama, pyenv-win, volta, the PowerShell
    // module wrapper…) reports a failed install/uninstall through ToolOutput.Summarize's own wording .
    // "... (exit code N)" when it captured output, "exited with code N and produced no output" when it
    // didn't. Or, for the one source that builds its own message instead (the PowerShell module
    // wrapper in PackageManagerSources), the bare "(exit N)". None of these carry a bucket-worthy code of
    // their own; they fall into the same GenericExplanation as an unrecognized vendor/hex code
    // (Explain's own "a code WAS found but matches nothing above" fallback), just recognized as decimal
    // codes in the first place. Each pattern requires the digits to be immediately followed by the
    // closing token that phrase always uses, so a winget hex code like "(exit 0x8007...)" can never match
    // here: the "0x" breaks the digit run before it reaches that closing token.
    private static readonly Regex CliExitCodeRegex = new(@"\(exit(?: code)? (-?\d+)\)", RegexOptions.Compiled);

    private static readonly Regex CliNoOutputExitCodeRegex =
        new(@"exited with code (-?\d+) and produced no output", RegexOptions.Compiled);

    // nvidia-driver's own installer-exit-code wording: distinct from all of the above, so it needs its
    // own anchor rather than reusing the CLI patterns.
    private static readonly Regex NvidiaExitCodeRegex =
        new(@"NVIDIA driver install returned exit code (-?\d+)\.", RegexOptions.Compiled);

    // Shared wording every source's package/model-id safety guard uses (CliUpdateSource.GuardPackageId,
    // OllamaSource, WingetSource). matched by the phrase itself rather than scoped by source id, since
    // "is not a plain <noun> <name/id>" is specific enough that no other source's text could collide with
    // it by accident.
    private static readonly Regex PackageIdRefusalRegex =
        new(@"is not a plain \w+ (name|id)", RegexOptions.Compiled);

    /// <summary>
    /// The full pipeline: source id (only scopes the winget-specific bucket: every other
    /// bucket is keyed on the code alone) and the raw tool output. Null when no code could be found at all .
    /// genuinely nothing to say, which is more honest than guessing.
    /// </summary>
    public static InstallErrorExplanation? Explain(string sourceId, string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return null;

        // A failed winget ladder lists every rung ("[upgrade] … (exit 0x8A15002B)", then "[install] …").
        // WingetSource.LadderException classifies on the LAST rung only, and so must this: the first hex code
        // in the text belongs to the first rung, not to whatever actually stopped the ladder. The explanation
        // sentence LadderFailure appends after the last rung is kept.
        if (string.Equals(sourceId, "winget", StringComparison.OrdinalIgnoreCase)
            && WingetLadderRung.Match(outputText) is { Success: true, Index: > 0 } lastRung)
        {
            outputText = outputText[lastRung.Index..];
        }

        // pip's own "cannot install" wording (PipSource.InstallAsync) leads the text and carries the package,
        // version and dependency; it comes before any code lookup, which would read the trailing "(exit code 1)".
        if (string.Equals(sourceId, "pip", StringComparison.OrdinalIgnoreCase)
            && PackageManagers.PipInstallFailure.TryParseMessage(outputText, out var pipPackage, out var pipVersion, out var pipFailure))
        {
            return pipFailure.Kind == PackageManagers.PipFailureKind.MissingDependency
                ? new InstallErrorExplanation(
                    "InstallErrorPipMissingDependencyWhat", [pipPackage, pipVersion, pipFailure.Dependency ?? string.Empty],
                    "InstallErrorPipBlockedNow", [], InstallErrorClassification.ManualWork)
                : new InstallErrorExplanation(
                    "InstallErrorPipConflictWhat", [pipPackage, pipVersion],
                    "InstallErrorPipBlockedNow", [], InstallErrorClassification.ManualWork);
        }

        var vendorCode = ExtractVendorDecimalCode(outputText);
        var hexCode = TryExtractHexCode(outputText);

        if (vendorCode is { } decimalCode)
        {
            if (VisualStudioTable.TryGetValue(decimalCode, out var vs))
                return vs;
            if (MsiTable.TryGetValue(decimalCode, out var msi))
                return msi;
        }

        // A crashed vendor installer (NTSTATUS) or a bare MSI reboot code (0x8A150109) can show up
        // regardless of which source's own wrapper text carries it, so these two stay unscoped by source id.
        if (hexCode is { } code)
        {
            if (WingetSource.IsVendorInstallerCrash(code))
                return CrashExplanation;
            if (WingetSource.IsRebootRequired(code))
                return RebootExplanation;
        }

        // APPINSTALLER_CLI_ERROR_* is winget's own error namespace; scoped to it so an unrelated tool's
        // text can never accidentally match one of these hex codes or wordings.
        var isWinget = string.Equals(sourceId, "winget", StringComparison.OrdinalIgnoreCase);
        if (isWinget && hexCode is { } wingetHex && WingetSource.IsInapplicableOutcome(wingetHex))
            return NoApplicableExplanation;
        if (isWinget && hexCode is { } techHex && WingetSource.IsDifferentInstallTechnology(techHex))
            return DifferentInstallTechnologyExplanation;
        if (isWinget && hexCode is { } noUninstallHex && WingetSource.IsNoUninstallInfo(noUninstallHex))
            return NoUninstallInfoExplanation;
        if (isWinget && WingetSource.NeedsUserScopeRetry(outputText))
            return UserScopeExplanation;

        var isWindowsUpdate = string.Equals(sourceId, "windowsupdate", StringComparison.OrdinalIgnoreCase);
        if (isWindowsUpdate)
        {
            if (outputText.Contains("Windows Update is still busy", StringComparison.Ordinal))
                return WuBusyExplanation;
            if (outputText.Contains("A restart is pending before this update", StringComparison.Ordinal))
                return WuRebootPendingExplanation;
            if (hexCode is { } wuHresult && WuHresultTable.TryGetValue(wuHresult, out var wuHresultExplanation))
                return wuHresultExplanation;

            var wuMatch = WuResultCodeRegex.Match(outputText);
            if (wuMatch.Success && int.TryParse(wuMatch.Groups[1].Value, out var wuCode))
            {
                // Codes 0/1/4/5 have their own entry; the rest (2/3 should never reach here, anything
                // else is unknown) still get the honest generic sentence naming the code, never silence.
                return WuResultCodeTable.TryGetValue(wuCode, out var wuCodeExplanation)
                    ? wuCodeExplanation
                    : GenericExplanation(wuCode.ToString());
            }

            // COM session/searcher/search/collection setup failures (WindowsUpdateSource.Install). none
            // carry a code, all mean the same thing to the user: WUA itself did not cooperate this time.
            if (outputText.Contains("Windows Update session unavailable", StringComparison.Ordinal)
                || outputText.Contains("Windows Update searcher unavailable", StringComparison.Ordinal)
                || outputText.Contains("Windows Update search failed", StringComparison.Ordinal)
                || outputText.Contains("Could not create update collection", StringComparison.Ordinal))
                return WuInternalFailureExplanation;

            if (outputText.Contains("is no longer offered", StringComparison.Ordinal))
                return WuNoLongerOfferedExplanation;
        }

        // Package/model-id safety refusal: a real reported case involved a package name from a source's
        // own listing that could have been read as a command option; this app refuses to run it rather
        // than guess, and the user should be told that's WHY it stopped, not just shown the raw refusal.
        if (PackageIdRefusalRegex.IsMatch(outputText))
            return PackageIdRefusalExplanation;

        if (outputText.Contains("missing install target for", StringComparison.Ordinal))
            return MissingInstallTargetExplanation;

        var isPip = string.Equals(sourceId, "pip", StringComparison.OrdinalIgnoreCase);
        if (isPip && outputText.Contains("has been repaired automatically", StringComparison.Ordinal))
            return PipRepairedExplanation;

        // The Android CLI exits with 127 whatever happened; a locked file is only visible in the Java stack
        // trace it prints, so that is what is matched (before the generic exit-code sentence).
        var isAndroidSdk = string.Equals(sourceId, "android-sdk", StringComparison.OrdinalIgnoreCase);
        if (isAndroidSdk && outputText.Contains("AccessDeniedException", StringComparison.Ordinal))
            return AndroidLockedExplanation;

        // nvidia-driver's own security refusals (signature, redirect, size) and lifecycle failures. Each
        // is a distinct, real failure mode of NvidiaDriverSource.InstallAsync, none carrying a bucket-worthy
        // code of its own.
        var isNvidiaDriver = string.Equals(sourceId, "nvidia-driver", StringComparison.OrdinalIgnoreCase);
        if (isNvidiaDriver)
        {
            if (outputText.Contains("is not NVIDIA-signed", StringComparison.Ordinal))
                return NvidiaNotSignedExplanation;
            if (outputText.Contains("redirected off the vendor's domain", StringComparison.Ordinal))
                return NvidiaOffDomainExplanation;
            if (outputText.Contains("exceeds the size limit", StringComparison.Ordinal)
                || outputText.Contains("exceeded the size limit mid-download", StringComparison.Ordinal))
                return NvidiaSizeLimitExplanation;
            if (outputText.Contains("Elevation for the NVIDIA installer was declined", StringComparison.Ordinal))
                return NvidiaElevationDeclinedExplanation;
            if (outputText.Contains("the installer did not start", StringComparison.Ordinal))
                return NvidiaDidNotStartExplanation;
            if (outputText.Contains("no longer reports its progress", StringComparison.Ordinal))
                return NvidiaStillRunningExplanation;
        }

        // A code WAS found but matches nothing above: an honest generic sentence naming it, never a
        // fabricated cause. Never invent an explanation without evidence for it.
        if (vendorCode is { } unmatchedDecimal)
            return GenericExplanation(unmatchedDecimal.ToString());

        if (hexCode is { } unmatchedHex)
            return GenericExplanation($"0x{unmatchedHex:X8}");

        // The broadest net, tried last: every CLI-driven source times its install/uninstall out in its
        // own wording ("install timed out for", "upgrading Flutter timed out", "pulling '...' timed
        // out"…) and none of it carries a code: this is the honest "it ran out of time" sentence instead
        // of silence, for whichever of those wordings reached here unmatched by anything more specific.
        if (outputText.Contains("timed out", StringComparison.Ordinal))
            return TimeoutExplanation;

        return null;
    }

    private static readonly Regex[] DecimalExitCodePatterns =
        [VendorExitCodeDe, VendorExitCodeEn, CliExitCodeRegex, CliNoOutputExitCodeRegex, NvidiaExitCodeRegex];

    private static int? ExtractVendorDecimalCode(string text)
    {
        // Each wording is tried until one yields a code that actually PARSES, rather than stopping at the
        // first one that merely matched: a match whose digits do not convert must not end
        // extraction, or a later wording carrying a readable code would never be reached.
        foreach (var pattern in DecimalExitCodePatterns)
        {
            if (pattern.Match(text) is { Success: true } match && TryParseExitCode(match.Groups[1].Value, out var code))
                return code;
        }

        return null;
    }

    /// <summary>
    /// An exit code reaches this app in either shape. Every wrapper in this project formats
    /// <c>Process.ExitCode</c>, a SIGNED int that is negative for a crashed installer, while a vendor
    /// printing the same NTSTATUS writes it unsigned (3221226505 for 0xC0000409). Both are the same 32
    /// bits, so a value past int's range is read as unsigned and cast back the way every other exit-code
    /// check here already does: the alternative was no explanation at all, which is what this whole
    /// class exists to avoid.
    /// </summary>
    private static bool TryParseExitCode(string value, out int code)
    {
        if (int.TryParse(value, out code))
            return true;

        if (uint.TryParse(value, out var unsigned))
        {
            code = unchecked((int)unsigned);
            return true;
        }

        return false;
    }

    private static readonly Regex WingetLadderRung =
        new(@"(?m)^\[(?:upgrade|install)[^\]\r\n]*\] ", RegexOptions.Compiled | RegexOptions.RightToLeft);

    private static int? TryExtractHexCode(string text)
    {
        var match = HexExitCode.Match(text);
        if (!match.Success)
            return null;

        // The hex text is always 8 digits (FailureDetail's "X8" format / winget's own APPINSTALLER codes),
        // so it parses as an unsigned 32-bit value and is cast back the same way every exit-code check in
        // WingetSource already does.
        return uint.TryParse(match.Value.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? unchecked((int)value)
            : null;
    }

    private static InstallErrorExplanation GenericExplanation(string code) => new(
        "InstallErrorGenericWhat", [code],
        "InstallErrorGenericNow", [],
        InstallErrorClassification.Recoverable);

    // ---- Visual Studio Setup, 8001-8006 (verified against Microsoft's own error-code reference). Only the
    // six official one-line labels are translated below; nothing beyond them is invented for 8001-8005, and
    // 8006's own remedy points straight at the interactive-retry blocker flow, which is the actual fix. ----

    private static readonly Dictionary<int, InstallErrorExplanation> VisualStudioTable = new()
    {
        [8001] = new(
            "InstallErrorVs8001What", [], "InstallErrorVsGenericNow", [], InstallErrorClassification.ManualWork),
        [8002] = new(
            "InstallErrorVs8002What", [], "InstallErrorVsGenericNow", [], InstallErrorClassification.ManualWork),
        [8003] = new(
            "InstallErrorVs8003What", [], "InstallErrorVsGenericNow", [], InstallErrorClassification.ManualWork),
        [8004] = new(
            "InstallErrorVs8004What", [], "InstallErrorVsGenericNow", [], InstallErrorClassification.ManualWork),
        [8005] = new(
            "InstallErrorVs8005What", [], "InstallErrorVsGenericNow", [], InstallErrorClassification.ManualWork),
        [8006] = new(
            "InstallErrorVs8006What", [], "InstallErrorVs8006Now", [], InstallErrorClassification.Recoverable),
    };

    private static readonly InstallErrorExplanation Msi1619Explanation = new(
        "InstallErrorMsi1619What", [], "InstallErrorMsi1619Now", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation RebootExplanation = new(
        "InstallErrorRebootWhat", [], "InstallErrorRebootNow", [], InstallErrorClassification.RestartNeeded);

    private static readonly Dictionary<int, InstallErrorExplanation> MsiTable = new()
    {
        [1602] = new(
            "InstallErrorMsi1602What", [], "InstallErrorMsi1602Now", [], InstallErrorClassification.Recoverable),
        [1603] = new(
            "InstallErrorMsi1603What", [], "InstallErrorMsi1603Now", [], InstallErrorClassification.ManualWork),
        // Recoverable, not ManualWork: with several installs now able to run at once, this code
        // is an expected side effect of that, not a real failure, and the queue already retries it once,
        // serially, on its own (WingetSource.IsAnotherInstallationRunning).
        [1618] = new(
            "InstallErrorMsi1618What", [], "InstallErrorMsi1618Now", [], InstallErrorClassification.Recoverable,
            AllowsAutomaticRetry: true),
        // 1619 (package could not be opened) and 1620 (invalid package) are ONE catalogue entry .
        // both mean "this file is not a usable package".
        [1619] = Msi1619Explanation,
        [1620] = Msi1619Explanation,
        [1638] = new(
            "InstallErrorMsi1638What", [], "InstallErrorMsi1638Now", [], InstallErrorClassification.ManualWork),
        // 1641/3010 are ERROR_SUCCESS_REBOOT_INITIATED/REQUIRED. SUCCESS codes in this app's own winget
        // path (WingetSource.IsInstallSuccess), so a failed ladder should never actually carry one as its
        // LAST rung's code. Kept in the table anyway for the rare case they show up in the middle of a
        // longer message (an earlier rung, a different tool's own wording).
        [1641] = RebootExplanation,
        [3010] = RebootExplanation,
    };

    // ---- winget's own codes: thin wrappers, see the class doc comment. ----

    private static readonly InstallErrorExplanation CrashExplanation = new(
        "InstallErrorCrashWhat", [], "InstallErrorCrashNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation NoApplicableExplanation = new(
        "InstallErrorWingetNoApplicableWhat", [], "InstallErrorWingetNoApplicableNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation UserScopeExplanation = new(
        "InstallErrorWingetUserScopeWhat", [], "InstallErrorWingetUserScopeNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation DifferentInstallTechnologyExplanation = new(
        "InstallErrorWingetDifferentTechWhat", [], "InstallErrorWingetDifferentTechNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation NoUninstallInfoExplanation = new(
        "InstallErrorWingetNoUninstallWhat", [], "InstallErrorWingetNoUninstallNow", [], InstallErrorClassification.ManualWork);

    // ---- Windows Update's own codes/HRESULTs, matched by fixed English wording and hex value
    // instead of the vendor-exit-code regexes above; see WuResultCodeRegex's own doc comment. ----

    private static readonly InstallErrorExplanation WuBusyExplanation = new(
        "InstallErrorWuBusyWhat", [], "InstallErrorWuBusyNow", [], InstallErrorClassification.Recoverable,
        AllowsAutomaticRetry: true);

    private static readonly InstallErrorExplanation WuRebootPendingExplanation = new(
        "InstallErrorWuRebootPendingWhat", [], "InstallErrorWuRebootPendingNow", [], InstallErrorClassification.RestartNeeded);

    private static readonly Dictionary<int, InstallErrorExplanation> WuResultCodeTable = new()
    {
        [0] = new("InstallErrorWuNotStartedWhat", [], "InstallErrorWuNotStartedNow", [], InstallErrorClassification.Recoverable),
        [1] = new("InstallErrorWuInProgressWhat", [], "InstallErrorWuInProgressNow", [], InstallErrorClassification.Recoverable),
        [4] = new("InstallErrorWuFailedWhat", [], "InstallErrorWuFailedNow", [], InstallErrorClassification.Recoverable),
        [5] = new("InstallErrorWuAbortedWhat", [], "InstallErrorWuAbortedNow", [], InstallErrorClassification.Recoverable),
    };

    private static readonly Dictionary<int, InstallErrorExplanation> WuHresultTable = new()
    {
        [unchecked((int)0x80240016)] = new(
            "InstallErrorWuHresult80240016What", [], "InstallErrorWuHresult80240016Now", [], InstallErrorClassification.Recoverable,
            AllowsAutomaticRetry: true),
        [unchecked((int)0x80240044)] = new(
            "InstallErrorWuHresult80240044What", [], "InstallErrorWuHresult80240044Now", [], InstallErrorClassification.ManualWork),
        [unchecked((int)0x80070422)] = new(
            "InstallErrorWuHresult80070422What", [], "InstallErrorWuHresult80070422Now", [], InstallErrorClassification.ManualWork),
        [unchecked((int)0x8024002D)] = new(
            "InstallErrorWuHresult8024002DWhat", [], "InstallErrorWuHresult8024002DNow", [], InstallErrorClassification.ManualWork),
        [unchecked((int)0x80240022)] = new(
            "InstallErrorWuHresult80240022What", [], "InstallErrorWuHresult80240022Now", [], InstallErrorClassification.Recoverable),
    };

    private static readonly InstallErrorExplanation WuInternalFailureExplanation = new(
        "InstallErrorWuInternalWhat", [], "InstallErrorWuInternalNow", [], InstallErrorClassification.Recoverable);

    private static readonly InstallErrorExplanation WuNoLongerOfferedExplanation = new(
        "InstallErrorWuNoLongerOfferedWhat", [], "InstallErrorWuNoLongerOfferedNow", [], InstallErrorClassification.ManualWork);

    // ---- Shared across every source whose own guard produces this wording (see PackageIdRefusalRegex,
    // CliExitCodeRegex's own doc comment, and CliUpdateSource.GuardPackageId). ----

    private static readonly InstallErrorExplanation PackageIdRefusalExplanation = new(
        "InstallErrorPackageIdRefusedWhat", [], "InstallErrorPackageIdRefusedNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation MissingInstallTargetExplanation = new(
        "InstallErrorMissingTargetWhat", [], "InstallErrorMissingTargetNow", [], InstallErrorClassification.Recoverable);

    private static readonly InstallErrorExplanation PipRepairedExplanation = new(
        "InstallErrorPipRepairedWhat", [], "InstallErrorPipRepairedNow", [], InstallErrorClassification.Recoverable);

    private static readonly InstallErrorExplanation AndroidLockedExplanation = new(
        "InstallErrorAndroidLockedWhat", [], "InstallErrorAndroidLockedNow", [], InstallErrorClassification.Recoverable);

    // ---- nvidia-driver's own security refusals and lifecycle failures: see NvidiaDriverSource.InstallAsync. ----

    private static readonly InstallErrorExplanation NvidiaNotSignedExplanation = new(
        "InstallErrorNvidiaNotSignedWhat", [], "InstallErrorNvidiaNotSignedNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation NvidiaOffDomainExplanation = new(
        "InstallErrorNvidiaOffDomainWhat", [], "InstallErrorNvidiaOffDomainNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation NvidiaSizeLimitExplanation = new(
        "InstallErrorNvidiaSizeLimitWhat", [], "InstallErrorNvidiaSizeLimitNow", [], InstallErrorClassification.ManualWork);

    private static readonly InstallErrorExplanation NvidiaElevationDeclinedExplanation = new(
        "InstallErrorNvidiaElevationDeclinedWhat", [], "InstallErrorNvidiaElevationDeclinedNow", [], InstallErrorClassification.Recoverable);

    private static readonly InstallErrorExplanation NvidiaDidNotStartExplanation = new(
        "InstallErrorNvidiaDidNotStartWhat", [], "InstallErrorNvidiaDidNotStartNow", [], InstallErrorClassification.Recoverable);

    private static readonly InstallErrorExplanation NvidiaStillRunningExplanation = new(
        "InstallErrorNvidiaStillRunningWhat", [], "InstallErrorNvidiaStillRunningNow", [], InstallErrorClassification.ManualWork);

    // ---- The broadest, last-resort bucket: see Explain's own "timed out" check. ----

    private static readonly InstallErrorExplanation TimeoutExplanation = new(
        "InstallErrorTimeoutWhat", [], "InstallErrorTimeoutNow", [], InstallErrorClassification.Recoverable,
        AllowsAutomaticRetry: true);

    /// <summary>
    /// Both sentences, already localized and joined: the row's failure tooltip and the error window's
    /// leading line both call this rather than formatting <see cref="InstallErrorExplanation"/> themselves,
    /// so the two surfaces can never drift apart. Null exactly when <see cref="Explain"/> is.
    /// </summary>
    public static string? Describe(string sourceId, string? outputText, Func<string, string> lookup) =>
        Explain(sourceId, outputText)?.Format(lookup);

    /// <summary>
    /// The raw numeric code <see cref="Explain"/> itself would extract from this text (vendor decimal
    /// tried first, then the broad 8-digit hex form), independent of whether any table recognizes it.
    /// Used by <c>ErrorReportBuilder</c> as the report's own "exit code" field, so the report
    /// and the classification read the same number out of the same text rather than parsing it twice.
    /// </summary>
    public static int? TryExtractCode(string? outputText) =>
        string.IsNullOrWhiteSpace(outputText) ? null : ExtractVendorDecimalCode(outputText) ?? TryExtractHexCode(outputText);

    // ---- The broader extraction, deliberately separate from every phrase-anchored regex
    // above. By the time Explain() itself returns null, neither a vendor decimal wording nor an 8-digit
    // hex token is present (either one falls through to GenericExplanation instead) - so these two exist
    // purely to give the "what does this mean" button something to send to Windows' own
    // FormatMessage/Win32Exception, for a code shaped some OTHER way than this class already recognizes:
    // a hex token of a different digit count, or a decimal number introduced by wording this class does
    // not know. ----

    private static readonly Regex LooseHexCodeRegex = new(@"0x[0-9A-Fa-f]{1,8}", RegexOptions.Compiled);

    private static readonly Regex LooseDecimalCodeRegex =
        new(@"(?:code|error|status)\D{0,12}?(-?\d{1,10})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A best-effort code for the offline "what does this mean" lookup, tried only once <see cref="Explain"/>
    /// has already found nothing at all. Hex first (any digit count), then the loosest decimal form - a
    /// number sitting next to the word "code"/"error"/"status" so an unrelated number elsewhere in the
    /// text (a size, a version, a timestamp) is not picked up by accident. Null when neither shape appears.
    /// </summary>
    public static (int Code, bool IsHresult)? TryFindUnknownCode(string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
            return null;

        var hexMatch = LooseHexCodeRegex.Match(outputText);
        if (hexMatch.Success
            && uint.TryParse(hexMatch.Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue))
        {
            return (unchecked((int)hexValue), true);
        }

        var decimalMatch = LooseDecimalCodeRegex.Match(outputText);
        return decimalMatch.Success && TryParseExitCode(decimalMatch.Groups[1].Value, out var decimalValue)
            ? (decimalValue, false)
            : null;
    }
}
