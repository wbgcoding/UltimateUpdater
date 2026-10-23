using System.IO;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Thrown by <see cref="CliUpdateSource.EnsureScanCompleted"/> when a scan's exit code lands in the OS
/// crash range (<see cref="ProcessExitCodes.IsCrash"/>) instead of an ordinary nonzero failure: the
/// process aborted rather than reporting an error on purpose, most often a native runtime mismatch
/// underneath a wrapper script (Android's sdkmanager.bat aborting under a Java
/// runtime older than its cmdline-tools require). Kept distinct from the generic scan-failed exception
/// so the pill's tooltip can name the likely cause instead of a bare exit code number.
/// </summary>
public sealed class ExternalToolCrashedException(string toolPath, int exitCode)
    : Exception(string.Format(
        LocalizationService.Instance["ScanFailedToolCrashed"], toolPath, $"0x{unchecked((uint)exitCode):X8}"))
{
    public string ToolPath { get; } = toolPath;

    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Shared skeleton for the package managers that are driven through their CLI. Everything the tools
/// print is treated as hostile input (SECURITY.md): parsing lives in separate pure parsers, output is
/// size-capped by ProcessRunner, and a malformed line is skipped rather than thrown.
/// </summary>
public abstract class CliUpdateSource : IUpdateSource
{
    protected static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMinutes(2);
    protected static readonly TimeSpan DefaultInstallTimeout = TimeSpan.FromMinutes(30);

    protected CliUpdateSource(LoggingService logging)
    {
        Logging = logging;
    }

    protected LoggingService Logging { get; }

    public abstract string Id { get; }

    public abstract UpdateCategory Category { get; }

    public SourceTier Tier => SourceTier.A;

    /// <summary>Executable used both for the availability probe and for the commands.</summary>
    protected abstract string Executable { get; }

    protected virtual TimeSpan ScanTimeout => DefaultScanTimeout;

    protected virtual TimeSpan InstallTimeout => DefaultInstallTimeout;

    /// <summary>
    /// How long the SCAN call below may go with no stdout/stderr activity at all before it is judged
    /// hung. Only the scan helper <see cref="RunAsync"/> passes this; the post-install version re-check
    /// in <see cref="GetInstalledVersionAsync"/> is unrelated and stays on <see cref="ScanTimeout"/>
    /// alone, its callers do not catch a silence timeout. A subclass overrides this only with a real,
    /// measured reason, the same rule <see cref="ScanTimeout"/> follows.
    /// </summary>
    protected virtual TimeSpan ScanIdleTimeout => ExternalCallTimeouts.ScanIdle;

    public virtual Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists(Executable));

    /// <summary>
    /// Failures deliberately propagate. The orchestrator catches them per source, so the pill shows an
    /// error while every other source carries on. Swallowing them here made a broken
    /// tool indistinguishable from a source that genuinely had nothing to report.
    /// </summary>
    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = await ScanCoreAsync(cancellationToken);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public virtual async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        GuardPackageId(item.Id);

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var executable = ExecutableFor(item);

        // Streamed so tools that print a download bar (pip especially) drive live progress on the row;
        // the rest simply produce no samples and fall back to the 0 → 100 endpoints.
        var result = await RunStreamedAsync(executable, InstallArguments(item), progress, item.Name, cancellationToken);

        // A failure a different command can fix; pip retried as a per-user install when the
        // machine-wide interpreter's site-packages needs admin; rather than simply reported.
        if (!result.TimedOut && !IsInstallSuccess(result)
            && RetryInstallArguments(item, result) is { } retryArguments)
        {
            result = await RunStreamedAsync(executable, retryArguments, progress, item.Name, cancellationToken);
        }

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: install timed out for '{item.Id}'.");

        if (!IsInstallSuccess(result))
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: install failed for '{item.Id}'. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>
    /// The executable this item's install/retry runs through: the source's own by default. A source
    /// overrides this for the rare case where the SAFE way to run a command differs by item, e.g. pip
    /// upgrading itself must run through the interpreter (`python -m pip`) rather than through its own
    /// locked launcher file.
    /// </summary>
    protected virtual string ExecutableFor(UpdateItem item) => Executable;

    /// <summary>
    /// Extra environment variables an install or removal runs with, for a tool that only works with the
    /// same environment its scan was given (sdkmanager.bat needs the JAVA_HOME the scan resolved). Null:
    /// the inherited environment as is.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string>? ProcessEnvironment => null;

    private Task<ProcessResult> RunStreamedAsync(
        string executable, IReadOnlyList<string> arguments, IProgress<Models.InstallProgress> progress, string name, CancellationToken cancellationToken) =>
        ProcessRunner.RunStreamingAsync(
            executable, arguments, InstallTimeout, segment => ReportSegment(progress, name, segment), cancellationToken,
            ProcessEnvironment);

    /// <summary>Hands one streamed output segment to the progress parser, reporting only real readings.</summary>
    private protected static void ReportSegment(IProgress<Models.InstallProgress> progress, string name, string segment)
    {
        if (ProgressParser.ToInstallProgress(segment, name) is { } update)
            progress.Report(update);
    }

    /// <summary>The command that removes the package, or null when this manager offers no removal.</summary>
    protected virtual IReadOnlyList<string>? UninstallArguments(UpdateItem item) => null;

    public virtual bool CanUninstall(UpdateItem item) => UninstallArguments(item) is not null;

    /// <summary>
    /// Implements the interface's enrichment hook for every CLI source (default: nothing to add), so a
    /// subclass can simply <c>override</c> it. Declared here rather than left to the interface's own
    /// default so that override actually takes part in interface dispatch.
    /// </summary>
    public virtual Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<UpdateEnrichment?>(null);

    /// <summary>
    /// The re-check, shared by every CLI source instead of each repeating its own guard/try/catch:
    /// a subclass overrides <see cref="VersionCheckArguments"/> (and <see cref="ParseInstalledVersion"/>
    /// when the output needs more than a plain value) to answer through the SAME executable the scan
    /// already uses. Null arguments (the default) means this source does not implement the re-check,
    /// same "no statement" answer the interface default gives.
    /// </summary>
    public virtual async Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var id = item.Id;
        if (!ProcessRunner.IsSafePositionalArgument(id))
            return null;

        if (VersionCheckArguments(id) is not { } arguments)
            return null;

        try
        {
            var result = await ProcessRunner.RunAsync(Executable, arguments, ScanTimeout, cancellationToken);
            return result.TimedOut || result.ExitCode != 0 ? null : ParseInstalledVersion(result.StandardOutput, id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logging.Warn($"{Id}: could not re-check the installed version of '{id}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Arguments for a one-shot "what version is installed now" query, or null (the default) to
    /// keep the base "no statement" answer.</summary>
    protected virtual IReadOnlyList<string>? VersionCheckArguments(string id) => null;

    /// <summary>Parses <see cref="VersionCheckArguments"/>'s output. Only called when arguments were
    /// actually given, so the default body is never reached in practice.</summary>
    protected virtual string? ParseInstalledVersion(string stdout, string id) => null;

    public virtual async Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        GuardPackageId(item.Id);

        var arguments = UninstallArguments(item)
            ?? throw new NotSupportedException($"{Id} cannot uninstall '{item.Id}'.");

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            Executable,
            arguments,
            InstallTimeout,
            segment => ReportSegment(progress, item.Name, segment),
            cancellationToken,
            ProcessEnvironment);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: uninstall timed out for '{item.Id}'.");

        if (!IsInstallSuccess(result))
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: uninstall failed for '{item.Id}'. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    protected abstract Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken);

    protected abstract IReadOnlyList<string> InstallArguments(UpdateItem item);

    /// <summary>
    /// Arguments to retry a failed install with when a different command can succeed where the first
    /// could not (pip: a per-user install when the machine-wide site-packages needs admin). Returning
    /// null (the default) reports the failure without retrying.
    /// </summary>
    protected virtual IReadOnlyList<string>? RetryInstallArguments(UpdateItem item, ProcessResult firstAttempt) => null;

    protected virtual bool IsInstallSuccess(ProcessResult result) => result.ExitCode == 0;

    protected UpdateItem CreateItem(
        string id, string name, string? installed, string? available, IReadOnlyDictionary<string, string>? meta = null) => new()
    {
        Id = id,
        SourceId = Id,
        Category = Category,
        Name = name,
        InstalledVersion = installed,
        AvailableVersion = available,
        IsCritical = SecurityRelevance.IsSecurityRelevant(id, name),
        Meta = meta ?? new Dictionary<string, string>(),
    };

    /// <summary>
    /// True when a version taken from a tool's output can be pinned in an install argument such as
    /// "pkg@1.2.3": a plain release number (an optional leading 'v', then a digit, then only letters,
    /// digits, '.', '+' and '-'). Anything else (a dist-tag, a git spec, a path) is refused so the
    /// version can never turn the argument into a different install source.
    /// </summary>
    internal static bool IsPlainVersion(string? version)
    {
        if (!ProcessRunner.IsSafePositionalArgument(version))
            return false;

        var start = version![0] is 'v' or 'V' ? 1 : 0;
        return version.Length > start
            && char.IsAsciiDigit(version[start])
            && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');
    }

    protected async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null) =>
        await ProcessRunner.RunAsync(Executable, arguments, ScanTimeout, cancellationToken, environment, ScanIdleTimeout);

    /// <summary>
    /// Which exit codes mean the scan actually RAN. Zero only, unless the tool has a documented convention
    /// of its own (npm and chocolatey both signal "found outdated packages" with a non-zero code) in
    /// which case the source overrides this and says which codes it accepts and why.
    /// </summary>
    protected virtual bool IsAcceptedScanExitCode(int exitCode) => exitCode == 0;

    /// <summary>
    /// A tool that timed out or failed has NOT reported "no updates": checking only the timeout, not the
    /// exit code, would let a tool that exited non-zero with empty or garbage output get parsed into an
    /// empty list, and the scan would look perfectly clean; exactly how a broken source hides. The message names the tool
    /// and its code, plus a short tail of whatever it printed; redacted the same way the log is, since this reaches
    /// the pill's hover tooltip directly and never passes through `LoggingService.Write` to pick that up.
    /// </summary>
    protected internal ProcessResult EnsureScanCompleted(ProcessResult result)
    {
        if (result.TimedOut)
            throw new TimeoutException($"{Id}: scan timed out after {ScanTimeout.TotalSeconds:0} seconds.");

        if (!IsAcceptedScanExitCode(result.ExitCode))
        {
            if (ProcessExitCodes.IsCrash(result.ExitCode))
                throw new ExternalToolCrashedException(Executable, result.ExitCode);

            var reason = LoggingService.Redact(LastNonEmptyLine(result.StandardError) ?? LastNonEmptyLine(result.StandardOutput) ?? string.Empty);
            var suffix = reason.Length > 0 ? $" {Truncate(reason, 300)}" : string.Empty;
            throw new InvalidOperationException($"{Id}: scan failed; '{Executable}' exited with code {result.ExitCode}.{suffix}");
        }

        return result;
    }

    /// <summary>The last line with real content; usually the actual error, past whatever progress noise came before it.</summary>
    private static string? LastNonEmptyLine(string? text) =>
        text?.Split('\n').Select(line => line.Trim('\r', ' ', '\t')).LastOrDefault(line => line.Length > 0);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// Package names come out of the tools' own output, and an argument array stops shell injection
    /// but not option injection: a package called "--index-url=http://…" reaches pip as a flag and
    /// redirects where it downloads from.
    /// </summary>
    protected void GuardPackageId(string packageId)
    {
        if (!ProcessRunner.IsSafePositionalArgument(packageId))
            throw new InvalidOperationException($"{Id}: refusing to install '{packageId}': that is not a plain package name.");
    }
}
