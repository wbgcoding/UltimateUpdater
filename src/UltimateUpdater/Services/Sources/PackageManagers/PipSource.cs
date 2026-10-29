using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// pip, scanned through EVERY registered Python interpreter (see <see cref="PythonInterpreters"/>),
/// never through whichever one PATH happens to resolve to. A machine with two interpreters installed
/// side by side (a real case: 3.13 and 3.14, both per-user) would show only the first one's packages;
/// PATH resolution is not consulted anywhere in this class. A row's <c>Id</c> carries the
/// interpreter's own path once more than one interpreter exists (<c>"{package}#{python.exe path}"</c>),
/// so install/uninstall/re-check always hit the SAME interpreter the row was found on; a single-interpreter
/// machine keeps the plain package name it always had. The interpreter path and the plain package name
/// both also live in <see cref="UpdateItem.Meta"/> for callers that already have the item in hand.
/// </summary>
public sealed class PipSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "pip";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "pip";

    /// <summary>
    /// Python writes to a redirected stdout in the ANSI code page (cp1252 here), so the first package
    /// description with an emoji makes pip die with UnicodeEncodeError (exit 2). Measured with
    /// 'pip inspect', which prints every installed package's metadata; any pip output can carry such text.
    /// Every pip call gets UTF-8 instead, which is also what ProcessRunner decodes.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Utf8Environment = new Dictionary<string, string>
    {
        ["PYTHONIOENCODING"] = "utf-8",
        ["PYTHONUTF8"] = "1",
    };

    /// <summary>Shared by every PyPI-backed source (pip, uv-tool, pipx) so the base URL is written once.</summary>
    internal const string PyPiBaseUrl = "https://pypi.org/pypi/";

    /// <summary>
    /// The newest version PyPI lists for a package, for sources that only know what is installed (pipx,
    /// uv tool), together with how the lookup ended so the caller can tell "PyPI does not know it" from
    /// "PyPI could not be asked". The outcome is null when no lookup was made (an id that is not a safe
    /// argument). No answer means no known newer version, never an assumed one.
    /// </summary>
    internal static async Task<(string? Latest, RegistryFetch? Fetch)> LatestOnPyPiAsync(
        HttpClient httpClient, LoggingService logging, string sourceId, string packageId, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.IsSafePositionalArgument(packageId))
            return (null, null);

        var url = $"{PyPiBaseUrl}{Uri.EscapeDataString(packageId)}/json";
        var fetch = await RegistryEnrichment.FetchRawOutcomeAsync(httpClient, url, logging, sourceId, packageId, cancellationToken);

        return (fetch.Body is null ? null : PackageMetadataParsers.ParsePyPiLatest(fetch.Body).AvailableVersion, fetch);
    }

    private PipBlockedStore? _blockedStore;

    /// <summary>One store per source, created on first use so the data folder is not touched before it is
    /// needed; a test hands in a store on a temporary file instead.</summary>
    internal PipBlockedStore BlockedStore
    {
        get => LazyInitializer.EnsureInitialized(ref _blockedStore, () => new PipBlockedStore(PipBlockedStore.DefaultPath, Logging))!;
        init => _blockedStore = value;
    }

    private const string PackageMetaKey = "pipPackage";
    private const string InterpreterMetaKey = "pythonPath";
    private const int MaxParallelInterpreters = 3;

    /// <summary>Test seam: the real implementation reads the actual registry, a test hands back a
    /// fabricated interpreter set instead.</summary>
    internal Func<IReadOnlyList<PythonInterpreter>> DiscoverInterpreters { get; init; } =
        () => PythonInterpreters.Discover(WindowsRegistryView.Instance);

    /// <summary>At least one interpreter is registered. Registry-only, like the scan itself: PATH is never
    /// consulted here either. Whether an interpreter actually has pip is decided per interpreter during the
    /// scan, where a missing pip counts as an absent tool and not as a failure.</summary>
    public override Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DiscoverInterpreters().Count > 0);

    /// <summary>
    /// pip's outdated listing carries neither a date, a size nor a publisher, so all three are read
    /// afterwards from the public PyPI JSON API for the exact version being offered; read-only, no key.
    /// A package PyPI has nothing to say about the publisher for (unlisted, a private index, or simply no
    /// author/maintainer declared) falls back to the installed copy's own METADATA file
    /// (<see cref="PipDistInfoMetadata"/>), read straight off disk, no second network request for the one
    /// fact the JSON fetch above did not already carry.
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var packageName = PackageName(item);
        if (string.IsNullOrWhiteSpace(item.AvailableVersion) || !ProcessRunner.IsSafePositionalArgument(packageName))
            return null;

        var url = $"{PyPiBaseUrl}{Uri.EscapeDataString(packageName)}/{Uri.EscapeDataString(item.AvailableVersion)}/json";
        var enrichment = await RegistryEnrichment.FetchAsync(
            httpClient, url, PackageMetadataParsers.ParsePyPiRelease, Logging, Id, packageName, cancellationToken);

        if (enrichment is { Publisher: not null })
            return enrichment;

        if (PipDistInfoMetadata.ReadPublisher(PythonInterpreters.SitePackagesForExe(InterpreterPath(item)), packageName) is not { } localPublisher)
            return enrichment;

        return (enrichment ?? default) with { Publisher = localPublisher };
    }

    /// <summary>One entry per interpreter that has already logged its own missing/unavailable
    /// 'pip inspect', so a repeated scan on that same interpreter does not repeat the log line.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedMissingInspect =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var interpreters = DiscoverInterpreters();
        if (interpreters.Count == 0)
            return [];

        var multiple = interpreters.Count > 1;
        // Each interpreter is a long pip process with its own output buffer; a machine with many Pythons
        // would otherwise start them all at once.
        var perInterpreter = await BoundedFanOut.SelectAsync(
            interpreters,
            (interpreter, token) => ScanInterpreterAsync(interpreter, multiple, token),
            cancellationToken,
            limit: MaxParallelInterpreters);

        return CombineInterpreterResults(perInterpreter);
    }

    /// <summary>
    /// One interpreter failing keeps every other interpreter's rows (it is logged where it happened). When
    /// EVERY interpreter failed there is no answer at all, which must surface as a failed source and not as
    /// a machine with nothing to update: the first failure is rethrown unchanged.
    /// </summary>
    internal static IReadOnlyList<UpdateItem> CombineInterpreterResults(
        IReadOnlyList<(IReadOnlyList<UpdateItem>? Rows, Exception? Error)> results)
    {
        if (results.Count > 0 && results.All(r => r.Error is not null))
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(results[0].Error!).Throw();

        return results.SelectMany(r => r.Rows ?? []).ToList();
    }

    /// <summary>
    /// One interpreter's own scan, isolated from every other one: an interpreter whose pip is broken, too
    /// old, or simply refuses to run must not blank out every OTHER interpreter's perfectly good results,
    /// the same "one failure never kills the rest" rule <see cref="ScanOrchestrator"/> already applies
    /// across whole sources, extended here across the interpreters one source now covers.
    /// </summary>
    private async Task<(IReadOnlyList<UpdateItem>? Rows, Exception? Error)> ScanInterpreterAsync(
        PythonInterpreter interpreter, bool multiple, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            var run = await ProcessRunner.RunAsync(
                interpreter.PythonExe,
                ["-m", "pip", "list", "--outdated", "--format=json", "--disable-pip-version-check"],
                ScanTimeout, cancellationToken, Utf8Environment, ScanIdleTimeout);

            if (IsPipNotInstalled(interpreter, run, PipFoldersPresent))
            {
                Logging.Info($"pip ({interpreter.DisplayLabel}): pip is not installed for this interpreter; nothing to scan.");
                return ([], null);
            }

            result = EnsureScanCompleted(run);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ExternalToolCrashedException
                                       or ProcessIdleTimeoutException or System.ComponentModel.Win32Exception)
        {
            Logging.Warn($"pip ({interpreter.DisplayLabel}): scan failed; {ex.Message}");
            return (null, ex);
        }

        var (listed, blocked) = BlockedStore.Apply(
            interpreter.PythonExe, PackageManagerParsers.ParsePipJson(result.StandardOutput).ToList());
        foreach (var package in blocked)
            Logging.Info($"pip ({interpreter.DisplayLabel}): not offering {package.Id} {package.Available}; installing it failed here before and pip offers nothing newer.");

        var outdated = listed.ToList();
        var heldBackIds = await FindHeldBackIdsAsync(interpreter, outdated, cancellationToken);

        return (outdated
            .Where(p => !heldBackIds.Contains(p.Id))
            .Select(p => CreateItem(p, interpreter, multiple))
            .ToList(), null);
    }

    /// <summary>
    /// A Python installed without pip (installer box unticked, an embeddable or vendor-registered build)
    /// answers <c>-m pip</c> with "No module named pip". That is a tool that is not there, not a source that
    /// failed. A pip that IS on disk but fails to import stays an error: the repair path in InstallAsync
    /// handles that case, and hiding it would leave a broken pip unnoticed. The probe receives the
    /// interpreter's site-packages folder and says whether pip's folder or dist-info is in it.
    /// </summary>
    internal static bool IsPipNotInstalled(
        PythonInterpreter interpreter, ProcessResult result, Func<string?, bool> pipOnDisk)
    {
        if (result.TimedOut || result.ExitCode == 0)
            return false;

        var output = result.StandardError + " " + result.StandardOutput;
        return NoPipModule.IsMatch(output)
            && !pipOnDisk(PythonInterpreters.SitePackagesFor(interpreter));
    }

    private static readonly System.Text.RegularExpressions.Regex NoPipModule = new(
        @"No module named '?pip\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool PipFoldersPresent(string? sitePackages)
    {
        if (string.IsNullOrEmpty(sitePackages) || !Directory.Exists(sitePackages))
            return false;

        try
        {
            return Directory.Exists(Path.Combine(sitePackages, "pip"))
                || Directory.EnumerateDirectories(sitePackages, "pip-*.dist-info").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder: assume pip is there so the real error stays visible.
            return true;
        }
    }

    private UpdateItem CreateItem(OutdatedPackage package, PythonInterpreter interpreter, bool multiple)
    {
        var id = BuildId(package.Id, interpreter, multiple);
        var name = multiple ? $"{package.Name} ({interpreter.DisplayLabel})" : package.Name;

        return new UpdateItem
        {
            Id = id,
            SourceId = Id,
            Category = Category,
            Name = name,
            InstalledVersion = package.Installed,
            AvailableVersion = package.Available,
            IsCritical = SecurityRelevance.IsSecurityRelevant(package.Id, package.Name),
            Meta = new Dictionary<string, string>
            {
                [PackageMetaKey] = package.Id,
                [InterpreterMetaKey] = interpreter.PythonExe,
            },
        };
    }

    /// <summary>
    /// The row Id: the plain package name while only one interpreter exists at all (so a single-interpreter
    /// machine's rows look exactly like they always did), or "package#python.exe path" once a second
    /// interpreter is in the picture and a bare package name cannot tell two rows apart.
    /// <see cref="ParseId"/> is this function's own inverse.
    /// </summary>
    internal static string BuildId(string packageId, PythonInterpreter interpreter, bool multiple) =>
        multiple ? $"{packageId}#{interpreter.PythonExe}" : packageId;

    /// <summary>The real pip package name, whether the row's Id is the plain name (one interpreter) or a
    /// composite one (more than one): the Meta value is authoritative when present, falling back to Id
    /// for anything that predates this field or was built without it.</summary>
    internal static string PackageName(UpdateItem item) =>
        item.Meta.TryGetValue(PackageMetaKey, out var name) ? name : item.Id;

    /// <summary>The interpreter this row was found on. Falls back to <see cref="OwnPython"/> only for an
    /// item that somehow carries no interpreter metadata at all, which normal scanning never produces.</summary>
    internal static string InterpreterPath(UpdateItem item) =>
        item.Meta.TryGetValue(InterpreterMetaKey, out var path) ? path : OwnPython();

    /// <summary>The interpreter a pip install runs against, for deciding which installs may overlap: the
    /// Meta value when present, else the one in a composite Id, else empty. Pure; never probes the machine.</summary>
    internal static string LaneInterpreter(string itemId, IReadOnlyDictionary<string, string>? meta) =>
        meta is not null && meta.TryGetValue(InterpreterMetaKey, out var path) && !string.IsNullOrEmpty(path)
            ? path
            : ParseId(itemId).InterpreterPath ?? string.Empty;

    /// <summary>
    /// Parses a row Id back into its package name and (when the Id is the composite, multi-interpreter
    /// form) interpreter path. Kept for callers that only have the Id string; <see cref="GetInstalledVersionAsync"/>
    /// does not need it, since it receives the full item and reads Meta directly (a plain
    /// single-interpreter Id never carries the interpreter path).
    /// </summary>
    internal static (string PackageName, string? InterpreterPath) ParseId(string id)
    {
        var separator = id.IndexOf('#');
        return separator < 0 ? (id, null) : (id[..separator], id[(separator + 1)..]);
    }

    /// <summary>
    /// A dependent's own pin can forbid the exact upgrade pip's outdated list just offered (an
    /// installed package's own metadata says so; see <see cref="PipDependencyGuard"/>), which showed up
    /// as the same handful of packages coming back update after update: pip flips one up, the OTHER
    /// package's next scan flips it right back down, forever. Read-only, best-effort: a failure here
    /// (older pip, unexpected output) leaves the outdated list exactly as pip reported it rather than
    /// losing the scan over an enhancement.
    /// </summary>
    private async Task<IReadOnlySet<string>> FindHeldBackIdsAsync(
        PythonInterpreter interpreter, IReadOnlyList<OutdatedPackage> outdated, CancellationToken cancellationToken)
    {
        if (outdated.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        ProcessResult inspectResult;
        try
        {
            inspectResult = await ProcessRunner.RunAsync(
                interpreter.PythonExe, ["-m", "pip", "inspect", "--disable-pip-version-check"],
                ScanTimeout, cancellationToken, Utf8Environment, ScanIdleTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logging.Warn($"pip ({interpreter.DisplayLabel}): could not run 'pip inspect' to check for held-back packages: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }

        if (inspectResult.TimedOut || inspectResult.ExitCode != 0)
        {
            if (_loggedMissingInspect.TryAdd(interpreter.PythonExe, 0))
                Logging.Info(inspectResult.TimedOut
                    ? $"pip ({interpreter.DisplayLabel}): 'pip inspect' timed out; skipping the held-back-package check."
                    : $"pip ({interpreter.DisplayLabel}): 'pip inspect' failed ({ToolOutput.Summarize(null, inspectResult.StandardError, inspectResult.ExitCode, maxChars: 300)}); skipping the held-back-package check.");

            return new HashSet<string>(StringComparer.Ordinal);
        }

        var report = PipDependencyGuard.Parse(inspectResult.StandardOutput);
        if (report is null)
        {
            if (_loggedMissingInspect.TryAdd(interpreter.PythonExe, 0))
                Logging.Info($"pip ({interpreter.DisplayLabel}): 'pip inspect' did not return the expected JSON; skipping the held-back-package check.");

            return new HashSet<string>(StringComparer.Ordinal);
        }

        var candidates = outdated
            .Where(p => !string.IsNullOrEmpty(p.Available))
            .Select(p => new PipDependencyGuard.Candidate(p.Id, p.Available!))
            .ToList();

        var heldBack = PipDependencyGuard.FindHeldBack(candidates, report);
        foreach (var entry in heldBack)
            Logging.Info(entry.LogLine);

        return heldBack.Select(h => h.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every install runs through the interpreter itself (<c>python.exe -m pip</c>), never pip's own
    /// launcher file directly: the launcher stays locked while it runs, and pip upgrading ITSELF
    /// ("pip install --upgrade pip") would then try to replace the very file it is currently executing
    /// from. python.exe is not the file being replaced, so nothing is locked against itself, and pip's own
    /// documentation gives this as the safe form on Windows for exactly this reason.
    /// </summary>
    protected override string ExecutableFor(UpdateItem item) => InterpreterPath(item);

    /// <summary>
    /// The python.exe belonging to the SAME installation as whichever pip was resolved via PATH: used
    /// only as a last-resort fallback for an item that somehow carries no interpreter metadata at all.
    /// Derived from where a PATH pip resolves to; python.exe sits either right next to it (a venv's
    /// Scripts/bin) or one directory up (pip.exe in ...\Scripts, python.exe in the installation root).
    /// Falls back to the bare "python" name only when neither can be found.
    /// </summary>
    internal static string OwnPython()
    {
        if (ProcessRunner.Resolve("pip") is { } pipPath
            && Path.GetDirectoryName(pipPath) is { } pipDirectory)
        {
            var sibling = Path.Combine(pipDirectory, "python.exe");
            if (File.Exists(sibling))
                return sibling;

            if (Path.GetDirectoryName(pipDirectory) is { } installRoot)
            {
                var oneUp = Path.Combine(installRoot, "python.exe");
                if (File.Exists(oneUp))
                    return oneUp;
            }
        }

        return "python";
    }

    /// <summary>The re-check runs against the SAME interpreter the row was found on, read from the item's
    /// own metadata rather than parsed out of its Id: on a single-interpreter machine the Id is a plain
    /// package name with no "#" (see the class comment), so parsing the Id alone can never recover the
    /// interpreter path there, even though it is right there in Meta. This override entirely replaces
    /// the base's single-executable implementation, which has no way to know which of several
    /// interpreters a bare package name refers to.</summary>
    public override async Task<string?> GetInstalledVersionAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var packageName = PackageName(item);
        if (!ProcessRunner.IsSafePositionalArgument(packageName))
            return null;

        var python = InterpreterPath(item);

        try
        {
            var result = await ProcessRunner.RunAsync(
                python, ["-m", "pip", "show", packageName, "--disable-pip-version-check"], ScanTimeout, cancellationToken, Utf8Environment);
            return result.TimedOut || result.ExitCode != 0
                ? null
                : PackageManagerParsers.ParsePipShowVersion(result.StandardOutput);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Logging.Warn($"pip: could not re-check the installed version of '{packageName}': {ex.Message}");
            return null;
        }
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => InstallArgumentsFor(item);

    internal static IReadOnlyList<string> InstallArgumentsFor(UpdateItem item) =>
        ["-m", "pip", "install", "--upgrade", "--disable-pip-version-check", Requirement(item)];

    private static readonly Regex SafeVersion = new(
        @"^[0-9A-Za-z][0-9A-Za-z.+_!-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The package pinned to the exact version the scan offered. A plain "--upgrade" makes pip quietly fall
    /// back to the installed version when the newest one cannot be resolved (a dependency without a build
    /// for this Python), and it exits 0 as if the upgrade had worked; the pin turns that into the real
    /// error. A version that is missing or not a plain version string keeps the unpinned name.
    /// </summary>
    internal static string Requirement(UpdateItem item)
    {
        var name = PackageName(item);
        var version = item.AvailableVersion;
        return version is not null && SafeVersion.IsMatch(version) && ProcessRunner.IsSafePositionalArgument(version)
            ? $"{name}=={version}"
            : name;
    }

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        ["-m", "pip", "uninstall", "-y", "--disable-pip-version-check", PackageName(item)];

    /// <summary>
    /// A machine-wide Python keeps its site-packages under Program Files, so an unelevated upgrade
    /// there dies with a permission error. Pip's own advice in exactly that case is a per-user install,
    /// so the upgrade is retried with --user; no elevation, landing in the user's own site-packages.
    /// Never applies to the self-upgrade case above, which already runs safely through the interpreter,
    /// nor to a virtual environment, where pip refuses --user outright and the retry would only bury the
    /// real first error.
    /// </summary>
    protected override IReadOnlyList<string>? RetryInstallArguments(UpdateItem item, ProcessResult firstAttempt) =>
        RetryArgumentsFor(item, firstAttempt);

    internal static IReadOnlyList<string>? RetryArgumentsFor(UpdateItem item, ProcessResult firstAttempt) =>
        !IsSelfUpgrade(item) && !IsVirtualEnv(InterpreterPath(item)) && NeedsUserScopeRetry(firstAttempt)
            ? ["-m", "pip", "install", "--upgrade", "--user", "--disable-pip-version-check", Requirement(item)]
            : null;

    /// <summary>A venv marks itself with pyvenv.cfg beside its interpreter (Scripts) or one level above it.</summary>
    internal static bool IsVirtualEnv(string pythonExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(pythonExe);
            if (string.IsNullOrEmpty(dir))
                return false;

            return File.Exists(Path.Combine(dir, "pyvenv.cfg"))
                   || (Path.GetDirectoryName(dir) is { Length: > 0 } parent && File.Exists(Path.Combine(parent, "pyvenv.cfg")));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The message detail for a failed install. After a per-user retry that also failed, the first
    /// attempt's output stays in it: that is where the actual reason usually is.</summary>
    internal static string FailureDetail(ProcessResult firstAttempt, ProcessResult? retry)
    {
        var first = ToolOutput.Summarize(firstAttempt.StandardOutput, firstAttempt.StandardError, firstAttempt.ExitCode);
        return retry is null
            ? first
            : $"{first} Retrying as a per-user install also failed: {ToolOutput.Summarize(retry.StandardOutput, retry.StandardError, retry.ExitCode)}";
    }

    internal static bool IsSelfUpgrade(UpdateItem item) =>
        string.Equals(PackageName(item), "pip", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] BrokenPipSignals = ["modulenotfounderror: no module named 'pip'"];

    /// <summary>
    /// A previous broken self-upgrade; can leave pip's own package half-replaced, so EVERY pip command
    /// fails this way until pip itself is repaired, not just a self-upgrade attempt; reproduced live via
    /// a failed 'headroom-ai' install. `python -m ensurepip --upgrade`, run through the SAME interpreter
    /// the failed install itself used, reinstalls pip from Python's own bundled copy, no network needed,
    /// and is the officially documented repair for exactly this. The repair fixes pip, not the package the
    /// user actually asked for, so this reports failure with a clear explanation rather than a false
    /// success; Retry now installs the real target.
    /// </summary>
    public override async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        var packageName = PackageName(item);
        GuardPackageId(packageName);

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var executable = ExecutableFor(item);

        var result = await ProcessRunner.RunStreamingAsync(
            executable, InstallArguments(item), InstallTimeout,
            segment => ReportSegment(progress, item.Name, segment), cancellationToken, Utf8Environment);

        var firstAttempt = result;
        ProcessResult? retried = null;
        if (!result.TimedOut && !IsInstallSuccess(result) && RetryInstallArguments(item, result) is { } retryArguments)
        {
            result = retried = await ProcessRunner.RunStreamingAsync(
                executable, retryArguments, InstallTimeout,
                segment => ReportSegment(progress, item.Name, segment), cancellationToken, Utf8Environment);
        }

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: install timed out for '{packageName}'.");

        if (IsInstallSuccess(result))
        {
            progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
            return;
        }

        var detail = FailureDetail(firstAttempt, retried);

        var blocked = PipInstallFailure.Classify(AllOutput(firstAttempt, retried), packageName);
        if (blocked.Kind != PipFailureKind.None)
        {
            var version = item.AvailableVersion ?? string.Empty;
            if (!string.IsNullOrEmpty(version))
                BlockedStore.Add(InterpreterPath(item), packageName, version);

            // Nothing was changed and nothing went wrong on the user's side: this version cannot be
            // installed on this Python, so the row is "stuck", not failed.
            throw new NotApplicableException($"{PipInstallFailure.BuildMessage(packageName, version, blocked)} {detail}");
        }

        if (!IsSelfUpgrade(item) && LooksLikeBrokenPip(detail))
        {
            Logging.Warn($"pip: '{packageName}' failed because pip's own installation is broken; repairing with 'python -m ensurepip --upgrade'.");

            // Starting a process from inside this branch needs its own guard: a missing or unrunnable
            // interpreter throws Win32Exception here, and unguarded that exception would REPLACE the real
            // pip failure the user is waiting to be told about. A repair that cannot even start is simply
            // a repair that did not happen.
            ProcessResult? repair = null;
            try
            {
                repair = await ProcessRunner.RunAsync(executable, ["-m", "ensurepip", "--upgrade"], InstallTimeout, cancellationToken, Utf8Environment);
            }
            catch (Exception repairFailure) when (
                repairFailure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                Logging.Warn($"pip: the repair could not be started: {repairFailure.Message}");
            }

            if (repair is { ExitCode: 0 })
            {
                throw new InvalidOperationException(
                    $"{Id}: pip's own installation was broken and has been repaired automatically. Install '{packageName}' again now that pip works.");
            }
        }

        throw new InvalidOperationException($"{Id}: install failed for '{packageName}'. {detail}");
    }

    private static string AllOutput(ProcessResult firstAttempt, ProcessResult? retry) =>
        firstAttempt.StandardError + "\n" + firstAttempt.StandardOutput
        + (retry is null ? "" : "\n" + retry.StandardError + "\n" + retry.StandardOutput);

    internal static bool LooksLikeBrokenPip(string? text) =>
        text is not null && BrokenPipSignals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PermissionDeniedSignals =
    [
        "permission denied", "access is denied", "[winerror 5]", "errno 13",
        "could not install packages due to an oserror", "consider using the `--user` option",
    ];

    /// <summary>
    /// A merely LOCKED file reports the same "access is denied" wording, and --user is the wrong answer
    /// there: the location is writable, so the retry would quietly plant a second, shadowing copy in the
    /// user site instead of updating the one that is in use. Measured on a per-user Python whose
    /// site-packages is writable, where the lock came from onnxruntime's native DLLs still being loaded
    /// by another process (rapidocr pulls those in). Closing the process is the only real fix.
    /// </summary>
    private static readonly string[] FileInUseSignals =
    [
        "[winerror 32]", "used by another process",
    ];

    /// <summary>True when pip's failure was a read-only install location that a --user install avoids.</summary>
    internal static bool NeedsUserScopeRetry(ProcessResult result)
    {
        if (result.ExitCode == 0)
            return false;

        var output = result.StandardError + "\n" + result.StandardOutput;

        if (FileInUseSignals.Any(signal => output.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            return false;

        return PermissionDeniedSignals.Any(signal => output.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Same executable-per-item routing as install; the base class always runs an uninstall
    /// through the source's single fixed <c>Executable</c>, which cannot know which interpreter a
    /// package belongs to.</summary>
    public override async Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        var packageName = PackageName(item);
        GuardPackageId(packageName);

        var arguments = UninstallArguments(item)
            ?? throw new NotSupportedException($"{Id} cannot uninstall '{packageName}'.");

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunStreamingAsync(
            ExecutableFor(item), arguments, InstallTimeout,
            segment => ReportSegment(progress, item.Name, segment), cancellationToken, Utf8Environment);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: uninstall timed out for '{packageName}'.");

        if (!IsInstallSuccess(result))
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: uninstall failed for '{packageName}'. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }
}
