using System.IO;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Tier A: winget. Two scan paths, both driving the SYSTEM winget (always current, nothing bundled):
/// 1. the `winget upgrade` CLI table, parsed defensively (see WingetUpgradeParser), is the authority
/// because the module misses packages winget's own upgrade list shows;
/// 2. the official Microsoft.WinGet.Client PowerShell module (structured, locale-independent objects)
/// is the fallback when the CLI cannot be started.
/// The winget COM API is not used: an unpackaged, unelevated process cannot marshal its objects (no
/// package identity), and the in-proc alternative measured 190 s/scan with a private 244 MB index and
/// missed results, versus 5 s (module) / 16 s (CLI) against the system winget.
/// Installs always run through winget itself, so its manifest hash verification is untouched.
/// </summary>
public sealed class WingetSource : IUpdateSource
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ShowTimeout = ExternalCallTimeouts.Long;

    private readonly LoggingService _logging;

    public WingetSource(LoggingService logging)
    {
        _logging = logging;
    }

    public string Id => "winget";

    public UpdateCategory Category => UpdateCategory.Software;

    public SourceTier Tier => SourceTier.A;

    /// <summary>The only source with a proven version-pinned install path
    /// (<see cref="VersionPinnedInstallArguments"/>, already exercised inside the retry ladder below).</summary>
    public bool SupportsVersionPinning => true;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists("winget"));

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The CLI is the authority, NOT the PowerShell module; the module misses packages winget's own
        // upgrade list shows.
        IReadOnlyList<WingetUpgradeEntry>? entries = null;

        try
        {
            entries = await ScanViaCliAsync(cancellationToken);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logging.Info($"winget: CLI unavailable ({ex.Message}); trying the PowerShell module.");
        }

        entries ??= await ScanViaModuleAsync(cancellationToken)
                    ?? throw new InvalidOperationException("winget: neither the CLI nor the PowerShell module could be read.");

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new UpdateItem
            {
                Id = entry.Id,
                SourceId = Id,
                Category = Category,
                Name = entry.Name,
                InstalledVersion = NormalizeVersion(entry.InstalledVersion),
                AvailableVersion = NormalizeVersion(entry.AvailableVersion),
                IsCritical = SecurityRelevance.IsSecurityRelevant(entry.Id, entry.Name),
            };
        }
    }

    public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        // package ids come from winget's own output; one starting with '-' would arrive as an option
        if (!ProcessRunner.IsSafePositionalArgument(item.Id, allowCmdMetacharacters: true))
throw new InvalidOperationException($"winget: refusing to install '{item.Id}'; that is not a plain package id.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        // A deliberate rollback bypasses the whole upgrade ladder below - that ladder exists to
        // find winget's OWN idea of the best upgrade, exactly what a pinned downgrade must NOT do. One
        // hash-verified attempt at the exact requested version, same command the ladder's own last-resort
        // rung already uses; a failure here is a real failure, never escalated further.
        if (item.PinnedVersion is { } pinnedVersion)
        {
            // The rollback target comes from history the unelevated user can edit, and this can run in
            // the elevated worker: a value shaped like an option must never reach winget's command line.
            if (!ProcessRunner.IsSafePositionalArgument(pinnedVersion, allowCmdMetacharacters: true))
                throw new InvalidOperationException($"winget: refusing to install '{item.Id}' at '{pinnedVersion}'; that is not a plain version.");

            var pinnedResult = await ProcessRunner.RunStreamingAsync(
                "winget",
                VersionPinnedInstallArguments(item.Id, pinnedVersion),
                InstallTimeout,
                segment => Report(progress, item.Name, segment),
                cancellationToken);

            if (pinnedResult.TimedOut)
                throw new InvalidOperationException($"winget install timed out for '{item.Id}'.");

            if (!IsInstallSuccess(pinnedResult.ExitCode))
                throw new InvalidOperationException(FailureDetail("install", item.Id, pinnedResult));

            progress.Report(new InstallProgress
            {
                PercentComplete = 100,
                StatusText = item.Name,
                RebootRequired = IsRebootRequired(pinnedResult.ExitCode),
            });
            return;
        }

        ProcessResult? last = null;

// Every rung's verdict, not just the last: the queue decides retry-unelevated from these codes.
        var attempts = new List<string>();

        // The ladder, not a single command: winget refuses an upgrade for several different reasons and
        // NAMES the remedy in its own message each time. Each rung is tried only when the
        // previous failure is one this rung actually addresses, so an ordinary failure still fails at once.
        foreach (var attempt in InstallLadder(item.Id))
        {
            if (last is not null && !ShouldEscalate(last))
                break;

            cancellationToken.ThrowIfCancellationRequested();

            // Streamed: winget repaints a percentage and a "x MB / y MB" line as it downloads, so the row
            // shows live throughput instead of jumping 0 → 100.
            var result = await ProcessRunner.RunStreamingAsync(
                "winget",
                attempt.Arguments,
                InstallTimeout,
                segment => Report(progress, item.Name, segment),
                cancellationToken);

            if (result.TimedOut)
                throw new InvalidOperationException($"winget install timed out for '{item.Id}'.");

            if (IsInstallSuccess(result.ExitCode))
            {
                if (last is not null)
                    _logging.Info($"winget: '{item.Id}' went through on the '{attempt.Label}' attempt.");

                progress.Report(new InstallProgress
                {
                    PercentComplete = 100,
                    StatusText = item.Name,
                    RebootRequired = IsRebootRequired(result.ExitCode),
                });

                return;
            }

            last = result;
            attempts.Add($"[{attempt.Label}] {FailureDetail("install", item.Id, result)}");

// No rung can help a user-scope package while running elevated; stop and hand the queue the
            // refusal it needs to retry unelevated.
            if (ElevationService.IsElevated && NeedsUserScopeRetry(attempts[^1]))
                break;
        }

// Every ordinary rung refused with "no applicable upgrade" (0x8A15002B): winget knows a newer
        // version exists but does not consider it a match for THIS installation (architecture, install
        // type, scope, or an installed version it cannot compare). One more try: read the exact version
// list winget itself offers and pin the install to the newest one, still through winget's normal
        // hash-verified path, never a `--force` fallback, which would reach the same class of package only
        // by disabling the installer hash check.
        string? offeredVersion = null;
        if (last is not null && DidNothing(last.ExitCode))
        {
            var versions = await ListVersionsAsync(item.Id, cancellationToken);
            offeredVersion = versions.FirstOrDefault();

            if (offeredVersion is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pinned = await ProcessRunner.RunStreamingAsync(
                    "winget",
                    VersionPinnedInstallArguments(item.Id, offeredVersion),
                    InstallTimeout,
                    segment => Report(progress, item.Name, segment),
                    cancellationToken);

                if (pinned.TimedOut)
                    throw new InvalidOperationException($"winget install timed out for '{item.Id}'.");

                if (IsInstallSuccess(pinned.ExitCode))
                {
                    _logging.Info($"winget: '{item.Id}' went through on the version-pinned attempt ({offeredVersion}).");
                    progress.Report(new InstallProgress
                    {
                        PercentComplete = 100,
                        StatusText = item.Name,
                        RebootRequired = IsRebootRequired(pinned.ExitCode),
                    });
                    return;
                }

                last = pinned;
                attempts.Add($"[install --version {offeredVersion}] {FailureDetail("install", item.Id, pinned)}");
            }
        }

        throw LadderException(attempts, last!.ExitCode, offeredVersion);
    }

/// <summary>The exact versions winget knows about for `id`, newest first, or empty on any failure:
    /// best effort, same fail-closed contract as everywhere else this app shells out to winget.</summary>
    private async Task<IReadOnlyList<string>> ListVersionsAsync(string packageId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                "winget",
                ["show", "--id", packageId, "--exact", "--versions", "--disable-interactivity", "--accept-source-agreements"],
                ShowTimeout,
                cancellationToken);

            return result.TimedOut || result.ExitCode != 0
                ? []
                : WingetUpgradeParser.ParseVersionsList(result.StandardOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logging.Warn($"winget: could not list versions for '{packageId}': {ex.Message}");
            return [];
        }
    }

    /// <summary>The re-check, one local call, no download: `winget list --id &lt;id&gt; --exact`.</summary>
    public async Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var id = item.Id;
        if (!ProcessRunner.IsSafePositionalArgument(id, allowCmdMetacharacters: true))
            return null;

        try
        {
            var result = await ProcessRunner.RunAsync(
                "winget",
                ["list", "--id", id, "--exact", "--disable-interactivity", "--accept-source-agreements"],
                ShowTimeout,
                cancellationToken);

            return result.TimedOut || result.ExitCode != 0
                ? null
                : WingetUpgradeParser.ParseListVersion(result.StandardOutput, id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logging.Warn($"winget: could not re-check the installed version of '{id}': {ex.Message}");
            return null;
        }
    }

/// <summary>Still hash-verified, with no --force, unlike the rung this replaced.</summary>
    internal static IReadOnlyList<string> VersionPinnedInstallArguments(string packageId, string version) =>
    [
        "install", "--id", packageId, "--exact", "--version", version,
        "--silent", "--disable-interactivity",
        "--accept-package-agreements", "--accept-source-agreements",
    ];

    /// <summary>
    /// What an exhausted ladder throws. "No applicable upgrade" (0x8A15002B) is NOT a failed install: winget
    /// has no version that fits this installation, nothing was changed and the user did nothing wrong, so it
    /// travels as its own terminal outcome and the row reads "stuck" in caution rather than red. Every other
    /// exit code stays an ordinary failure. Classification is keyed ONLY on the LAST rung's own code: an
    /// earlier rung's 0x8A15002B must not excuse whatever a LATER rung reports (a genuine crash, access
    /// denied), since that later rung is what actually stopped the ladder. See
    /// <see cref="IsInapplicableOutcome"/> for what does count.
    /// </summary>
    internal static Exception LadderException(IReadOnlyList<string> attempts, int lastExitCode, string? offeredVersion = null)
    {
        var message = LadderFailure(attempts, lastExitCode, offeredVersion);

        return IsInapplicableOutcome(lastExitCode)
            ? new NotApplicableException(message)
            : new InvalidOperationException(message);
    }

    /// <summary>
    /// The message a failed ladder reports: EVERY rung, in order, not just the last one. Two readers depend
    /// on it: the queue decides from these codes whether to retry the job without administrator rights, and
    /// the row's Details view shows the user exactly what was attempted. "No applicable upgrade" is spelled
    /// out because on its own it reads like success; when the version-pinned rung also ran, the message names
    /// the exact version winget offered and still refused, so it answers "why" instead of repeating a code.
    /// </summary>
    internal static string LadderFailure(IReadOnlyList<string> attempts, int lastExitCode, string? offeredVersion = null)
    {
        var detail = string.Join(Environment.NewLine, attempts);

        if (!IsInapplicableOutcome(lastExitCode))
            return detail;

        var explanation = offeredVersion is { Length: > 0 }
            ? $"winget offers no version of this package that fits this installation (architecture, install type, or scope); not even version {offeredVersion}, the newest one it knows about, which was tried and refused for the same reason."
            : "winget changed nothing; every variant this app knows how to try was refused.";

        return detail + Environment.NewLine + explanation;
    }

    /// <summary>One way of asking winget to do the upgrade, and what to call it in the log.</summary>
    internal readonly record struct WingetAttempt(string Label, IReadOnlyList<string> Arguments);

    /// <summary>
    /// The attempts, in order. Every one keeps --silent and --disable-interactivity: nothing here may ever
    /// put a window in front of the user in the middle of an unattended batch.
    /// 1. The plain upgrade, WITH --include-unknown: winget's own message asks for exactly that when a
    /// package's installed version cannot be determined. The scan passes it too, so the two agree about
    /// what is upgradable.
    /// 2. The same, pinned to the USER scope: winget reports "a newer version is available but does not
    /// apply to your system" when the version it found does not match the scope the package is installed
    /// in (Bitcoin Core, installed per-user).
    /// 3. install instead of upgrade. Some packages have no upgrade path registered but install cleanly
    /// over themselves; --include-unknown is not valid here and is left off.
    /// `--force`, which winget documents as overriding "the installer hash check as well as any other
    /// checks that might prevent the installer from proceeding", must never become a rung: for a package
    /// whose applicability gate (0x8A15002B) survives every rung above, the cost would be the installer
    /// hash check on every package this ladder reaches. InstallAsync's version-pinned attempt (below the
    /// loop this builds) reaches the same class of package without that trade: it still verifies the hash,
    /// and tells winget exactly which version to install instead of letting its applicability check refuse
    /// an upgrade it cannot place.
    /// </summary>
    internal static IReadOnlyList<WingetAttempt> InstallLadder(string packageId) =>
    [
        new("upgrade", InstallArguments(packageId)),
        new("upgrade --scope user", [.. InstallArguments(packageId), "--scope", "user"]),
        new("install", [
            "install", "--id", packageId, "--exact",
            "--silent", "--disable-interactivity",
            "--accept-package-agreements", "--accept-source-agreements",
        ]),
    ];

    /// <summary>
/// Whether the next rung is worth trying. Only for refusals another rung can actually address: winget
    /// declining to do anything, or refusing over scope/elevation. A genuine failure (a bad hash, no disk, a
    /// download that died) is reported straight away rather than repeated three times.
    /// </summary>
    private static bool ShouldEscalate(ProcessResult result) =>
        DidNothing(result.ExitCode)
        || NeedsUserScopeRetry(ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode));

    /// <summary>
    /// The item installed, but a reboot is needed to finish: winget passes the installer's own
    /// ERROR_SUCCESS_REBOOT_* codes straight through. These still count as success (IsInstallSuccess);
    /// this only tells the user a reboot is pending.
    /// </summary>
    internal static bool IsRebootRequired(int exitCode) =>
        exitCode is 1641 or 3010 || unchecked((uint)exitCode) == 0x8A150109u;

    /// <summary>
    /// `winget show --id &lt;id&gt; --exact` for the release date (see
    /// <see cref="WingetUpgradeParser.ParseShowReleaseDate"/>; this output has no installer size). Runs
    /// only AFTER the scan, one row at a time on the shared enrichment gate
    /// (<c>MainViewModel.EnrichAllAsync</c>), never during it: a `winget show` per row while scanning would
    /// multiply the scan's cost by the row count. Best effort: no manifest date, an unrecognised locale, or
    /// the command failing all return null rather than throwing.
    /// </summary>
    public async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id, allowCmdMetacharacters: true))
            return null;

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                "winget",
                ["show", "--id", item.Id, "--exact", "--disable-interactivity", "--accept-source-agreements"],
                ShowTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logging.Warn($"{Id}: enrichment for '{item.Id}' failed: {ex.Message}");
            return null;
        }

        if (result.TimedOut || result.ExitCode != 0)
            return null;

        var date = WingetUpgradeParser.ParseShowReleaseDate(result.StandardOutput);
        var (changelogBody, changelogUrl) = WingetUpgradeParser.ParseShowReleaseNotes(result.StandardOutput);
        if (date is null && changelogBody is null && changelogUrl is null)
            return null;

        return new UpdateEnrichment(date, null, ChangelogBody: changelogBody, ChangelogUrl: changelogUrl);
    }

    public bool CanUninstall(UpdateItem item) => ProcessRunner.IsSafePositionalArgument(item.Id, allowCmdMetacharacters: true);

    public async Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id, allowCmdMetacharacters: true))
throw new InvalidOperationException($"winget: refusing to uninstall '{item.Id}'; that is not a plain package id.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            "winget",
            UninstallArguments(item.Id),
            InstallTimeout,
            segment => Report(progress, item.Name, segment),
            cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"winget uninstall timed out for '{item.Id}'.");

        if (!IsInstallSuccess(result.ExitCode))
            throw new InvalidOperationException(FailureDetail("uninstall", item.Id, result));

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>
    /// winget refuses to touch a package installed for the current user while it is running as an
    /// administrator, and the elevated helper always is. The app catches this and retries in-process, the
    /// one thing that can succeed.
    /// This is NOT only about removing. The winget code for it is
    /// APPINSTALLER_CLI_ERROR_ADMIN_CONTEXT_ACTION_PROHIBITED (0x8A15007D / -1978335107): "the requested
    /// operation is not permitted from an administrator context on packages installed within the user
    /// scope", so an upgrade hits it exactly like an uninstall (Bitcoin Core is installed that way).
    /// APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION (0x8A150056) is the same situation from the
    /// other side: the installer itself refuses to run elevated. The answer is identical.
    /// Matched on the codes in both spellings AND on the stable part of the wording, so a translated
    /// Windows still hits it.
    /// </summary>
    internal static bool NeedsUserScopeRetry(string? output) =>
        output is { Length: > 0 }
        && (output.Contains("0x8A15002B", StringComparison.OrdinalIgnoreCase)
            || output.Contains("0x8A15007D", StringComparison.OrdinalIgnoreCase)
            || output.Contains("-1978335107", StringComparison.Ordinal)
            || output.Contains("0x8A150056", StringComparison.OrdinalIgnoreCase)
            || output.Contains("-1978335146", StringComparison.Ordinal)
            || output.Contains("when running with administrator", StringComparison.OrdinalIgnoreCase)
            || output.Contains("from an administrator context", StringComparison.OrdinalIgnoreCase)
            || output.Contains("cannot be run from an administrator", StringComparison.OrdinalIgnoreCase)
            || output.Contains("installed for user scope", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ERROR_INSTALL_ALREADY_RUNNING (1618, HRESULT 0x80070652): Windows Installer allows only ONE classic
    /// MSI transaction machine-wide and refuses every other one that starts alongside it. With installs
    /// running concurrently this is an expected outcome whenever two installs land on an MSI-based package
    /// at once, so the queue retries an item refused this way once, serially, instead of reporting a hard
    /// failure. Matched like <see cref="NeedsUserScopeRetry"/>: both numeric spellings winget's
    /// pass-through can produce (<see cref="FailureDetail"/>'s "exit 0x########" text), plus the
    /// installer's own wording, since a vendor installer wrapping MSI can report the condition as plain
    /// text under an unrelated, generic exit code.
    /// </summary>
    internal static bool IsAnotherInstallationRunning(string? output) =>
        output is { Length: > 0 }
        && (output.Contains("exit 0x00000652", StringComparison.OrdinalIgnoreCase)
            || output.Contains("exit 0x80070652", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Another installation is already in progress", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> UninstallArguments(string packageId) =>
    [
        "uninstall", "--id", packageId, "--exact",
        "--silent", "--disable-interactivity", "--accept-source-agreements",
    ];

    private static void Report(IProgress<InstallProgress> progress, string name, string segment)
    {
        if (ProgressParser.ToInstallProgress(segment, name) is { } update)
            progress.Report(update);
    }

    /// <summary>
    /// winget passes an installer's own exit code straight through, and several of those mean success:
/// 3010/1641 are "installed, reboot needed"; treating them as failures was a large part of why
    /// updates "mostly failed". A reboot-required update IS installed. Everything genuinely wrong still
    /// surfaces, with the real code and output carried back for the row's Details view.
    /// </summary>
    internal static bool IsInstallSuccess(int exitCode) =>
        exitCode is 0
            or 1641                          // ERROR_SUCCESS_REBOOT_INITIATED
            or 3010                          // ERROR_SUCCESS_REBOOT_REQUIRED
            || unchecked((uint)exitCode) is 0x8A150109u;  // reboot required to install (winget code)

    /// <summary>
    /// APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE (0x8A15002B): winget did NOTHING. Every command here
    /// names one exact package that the scan had just seen an upgrade for, so "nothing applicable" means
    /// winget and the scan disagree, not that the work is done. Treating it as success let Bitcoin Core
    /// report a successful update while staying at 31.0.0 (the elevated attempt could not act on a
    /// user-scope package and the row went green). It is treated as "retry unelevated once, then say so
    /// plainly".
    /// </summary>
    internal static bool DidNothing(int exitCode) => unchecked((uint)exitCode) is 0x8A15002Bu;

    /// <summary>
    /// Whether a ladder's TERMINAL rung, the one that actually stopped it, reports one of winget's own
    /// "nothing to act on" codes, as opposed to a genuine failure. Broader than <see cref="DidNothing"/> by
    /// exactly one code: APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND (0x8A150014, "no installed package
    /// found matching input criteria"). A real OpenJDK ladder reported 0x8A15002B on rung 1 and 0x8A150014
    /// on rung 2 (the terminal rung), a differently worded but equally harmless refusal; checking only
    /// rung 1, or only 0x8A15002B on rung 2, both missed it and reported the row red. Still keyed on ONE
    /// rung's own code: scanning every rung would excuse a LATER rung's genuine crash because an EARLIER
    /// one said "not applicable" first.
    /// </summary>
    internal static bool IsInapplicableOutcome(int exitCode) =>
        DidNothing(exitCode) || unchecked((uint)exitCode) is 0x8A150014u;

    /// <summary>
    /// winget's own APPINSTALLER_CLI_ERROR_INSTALL_TECHNOLOGY_DIFFERENT: a newer version is available, but
    /// it packages differently than what is already installed (MSI to MSIX and similar). winget refuses
    /// to upgrade in place, and it comes back on every scan until the old one is removed by hand.
    /// </summary>
    internal static bool IsDifferentInstallTechnology(int exitCode) =>
        unchecked((uint)exitCode) == 0x8A15008Eu;

    /// <summary>
    /// winget's own APPINSTALLER_CLI_ERROR_NO_UNINSTALL_INFO_FOUND (0x8A15002F): the update has to remove the
    /// old version first, and the old version registered no uninstall command, so there is nothing for
    /// winget to run. Only removing the old version by hand helps.
    /// </summary>
    internal static bool IsNoUninstallInfo(int exitCode) =>
        unchecked((uint)exitCode) == 0x8A15002Fu;

    /// <summary>
    /// winget passes an installer's own exit code straight through. A genuine OS-killed-the-process NTSTATUS
    /// lives in the narrow 0xC0000000-0xC0000FFF band: FACILITY_NONE, severity "error", the "0xC0000" prefix
    /// every Windows crash code shares (STATUS_ACCESS_VIOLATION 0xC0000005, STATUS_STACK_BUFFER_OVERRUN
    /// 0xC0000409, STATUS_HEAP_CORRUPTION 0xC0000374, STATUS_STACK_OVERFLOW 0xC00000FD, and the real exit
    /// code of a vendor setup that crashed after winget downloaded and hash-verified it). A plain
    /// `&gt;= 0xC0000000` range would also catch every ordinary negative exit code (-1 is 0xFFFFFFFF,
    /// InstallShield's documented -1/-3/-12), misreporting a plain failure as a crash and discarding
    /// winget's real diagnostic text for it (see <see cref="FailureDetail"/>).
    /// One code inside the band is carved back out, like 3010/1641 are carved out of "failure":
    /// STATUS_CONTROL_C_EXIT (0xC000013A) is what a process reports when it was TERMINATED (a stop, a
    /// console close, a parent tearing the tree down), not when it broke; calling that a vendor crash
    /// blames the installer for something the machine did.
    /// </summary>
    internal static bool IsVendorInstallerCrash(int exitCode)
    {
        var code = unchecked((uint)exitCode);
        return code is >= 0xC0000000u and <= 0xC0000FFFu && code != ControlCExitCode;
    }

/// <summary>STATUS_CONTROL_C_EXIT: killed from outside, not a crash of its own.</summary>
    private const uint ControlCExitCode = 0xC000013Au;

    /// <summary>Raw failure text for Details: the TAIL of winget's output plus exit code, never empty; a
    /// vendor-crash lead-in keeps the tail too.</summary>
    internal static string FailureDetail(string action, string id, ProcessResult result)
    {
        var summary = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);

        if (IsVendorInstallerCrash(result.ExitCode))
        {
            var crashSentence = string.Format(
                LocalizationService.Instance["WingetVendorInstallerCrashed"], $"0x{result.ExitCode:X8}");
            return $"{crashSentence} {summary}";
        }

        return $"winget {action} failed for '{id}' (exit 0x{result.ExitCode:X8}). {summary}";
    }

    /// <summary>
    /// --id + --exact so a package is never resolved by fuzzy name match; winget verifies the installer hash
    /// from its manifest itself and aborts on mismatch. --silent also keeps the updated
    /// application from opening itself: it hands the vendor's installer its own quiet switch, so no finish
    /// page and no "launch now" box is left ticked. --disable-interactivity stops winget prompting on top
    /// of that. Nothing here may become --interactive, --wait or --open-logs: each puts a window in front
    /// of the user in the middle of an unattended batch.
    /// </summary>
    internal static IReadOnlyList<string> InstallArguments(string packageId) =>
    [
        "upgrade", "--id", packageId, "--exact",
        "--silent", "--disable-interactivity",
        "--accept-package-agreements", "--accept-source-agreements",
        // The scan passes this too; the two commands have to agree about what counts as upgradable,
        // otherwise winget lists packages as upgradable and then refuses to upgrade them ("Die Versionsnummer dieses Pakets kann nicht bestimmt werden…",
        // PokeMMO).
        "--include-unknown",
    ];

/// <summary>The one deliberate exception to "never --interactive": a single user-triggered retry,
    /// in-process and unelevated only, no ladder.</summary>
    public async Task InstallInteractiveAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(item.Id, allowCmdMetacharacters: true))
throw new InvalidOperationException($"winget: refusing to install '{item.Id}'; that is not a plain package id.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            "winget",
            InteractiveInstallArguments(item.Id),
            InstallTimeout,
            segment => Report(progress, item.Name, segment),
            cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"winget interactive install timed out for '{item.Id}'.");

        if (!IsInstallSuccess(result.ExitCode))
            throw new InvalidOperationException(FailureDetail("install", item.Id, result));

        progress.Report(new InstallProgress
        {
            PercentComplete = 100,
            StatusText = item.Name,
            RebootRequired = IsRebootRequired(result.ExitCode),
        });
    }

    /// <summary>
/// The normal ladder's first rung, minus --silent and --disable-interactivity, plus --interactive:
    /// built by filtering <see cref="InstallArguments"/> rather than duplicating it, so the two can never
    /// drift apart (proven by <c>WingetInstallLadderTests</c>: no --silent, otherwise identical).
    /// </summary>
    internal static IReadOnlyList<string> InteractiveInstallArguments(string packageId) =>
    [
        .. InstallArguments(packageId).Where(arg => arg is not ("--silent" or "--disable-interactivity")),
        "--interactive",
    ];

    /// <summary>Returns null when the module path is unusable, so the caller can fall back.</summary>
    private async Task<IReadOnlyList<WingetUpgradeEntry>?> ScanViaModuleAsync(CancellationToken cancellationToken)
    {
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "Import-Module Microsoft.WinGet.Client; " +
            "Get-WinGetPackage | Where-Object { $_.IsUpdateAvailable } | " +
            "ForEach-Object { [pscustomobject]@{ Id=$_.Id; Name=$_.Name; " +
            "InstalledVersion=$_.InstalledVersion; AvailableVersion=($_.AvailableVersions | Select-Object -First 1) } } | " +
            "ConvertTo-Json -Compress";

        try
        {
            var result = await ProcessRunner.RunAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", script],
                ScanTimeout,
                cancellationToken);

            if (result.TimedOut || result.ExitCode != 0)
                return null;

            // A working module with nothing to report prints nothing at all; that is an empty result,
            // not a failure (treating it as one would add a roughly 16 second CLI pass to every clean scan
            // and log a misleading "module unavailable").
            if (string.IsNullOrWhiteSpace(result.StandardOutput))
                return [];

            // output that parsed to nothing is a genuinely broken response, so fall back
            var entries = WingetUpgradeParser.ParseModuleJson(result.StandardOutput);
            return entries.Count > 0 ? entries : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logging.Warn($"winget: PowerShell module path failed: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<WingetUpgradeEntry>> ScanViaCliAsync(CancellationToken cancellationToken)
    {
        var entries = await RunUpgradeListAsync(
            ["upgrade", "--include-unknown", "--disable-interactivity", "--accept-source-agreements"],
            cancellationToken);

        // The Microsoft Store is a winget source of its own, and its packages are not all in the default
// list: an app that was never matched to a winget manifest is only offered under `--source msstore`.
        // Asked separately and merged by id, so a package that IS in both appears once.
        var known = new HashSet<string>(entries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var store = await StoreUpgradesAsync(cancellationToken);
        var merged = entries.Concat(store.Where(e => known.Add(e.Id))).ToList();

        return merged;
    }

    /// <summary>
    /// The Store list is a bonus, never a reason to fail the scan: `--source msstore` prints "no installed
    /// package found" as an ERROR exit when nothing matches, and an unconfigured or refused source must not
    /// take winget's own results down with it.
    /// </summary>
    private async Task<IReadOnlyList<WingetUpgradeEntry>> StoreUpgradesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunUpgradeListAsync(
                [
                    "upgrade", "--include-unknown", "--source", "msstore",
                    "--disable-interactivity", "--accept-source-agreements",
                ],
                cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or IOException
            or InvalidOperationException)
        {
            // InvalidOperationException included on purpose: a refused or unconfigured msstore source now
            // fails its listing loudly, and that must still not take winget's own results down with it.
            _logging.Info($"winget: the Store source could not be listed ({ex.Message}); continuing without it.");
            return [];
        }
    }

    /// <summary>
    /// The only non-zero codes a LISTING may end with and still be believed:
    /// APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND (nothing matched, what `--source msstore` prints when
    /// no Store app is installed) and APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE (nothing to upgrade).
    /// Both mean "the list is empty", a real answer. Every other code means winget produced no list at
    /// all, and parsing its empty output as "no upgrades available" would make a broken winget look like a
    /// machine that is fully up to date.
    /// </summary>
    private static bool IsEmptyListExitCode(int exitCode) =>
        unchecked((uint)exitCode) is 0x8A150014u or 0x8A15002Bu;

    private async Task<IReadOnlyList<WingetUpgradeEntry>> RunUpgradeListAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("winget", arguments, ScanTimeout, cancellationToken);

// a timeout is a failed scan, not an empty one; "nothing found" would hide every winget update
        if (result.TimedOut)
            throw new TimeoutException($"winget: scan timed out after {ScanTimeout.TotalSeconds:0} seconds.");

        if (result.ExitCode != 0 && !IsEmptyListExitCode(result.ExitCode))
            throw new InvalidOperationException($"winget: the upgrade list failed (exit 0x{result.ExitCode:X8}).");

        return WingetUpgradeParser.ParseCliTable(
            result.StandardOutput,
            id => _logging.Warn($"winget: skipped a row whose Id winget shortened to '{id}'; it cannot be installed by that Id."));
    }

/// <summary>winget reports an unknown installed version as the literal "Unknown"; show nothing instead.</summary>
    private static string? NormalizeVersion(string version) =>
        string.IsNullOrWhiteSpace(version) || version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : version;
}
