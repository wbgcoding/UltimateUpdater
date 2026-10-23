using System.IO;
using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Android SDK packages via `sdkmanager`. It is rarely on PATH, Android Studio installs it under the
/// user's own LOCALAPPDATA, so both the classic and cmdline-tools layouts are probed once and cached
/// for the process lifetime rather than re-walked on every scan.
/// </summary>
/// <remarks>
/// `sdkmanager --list` already IS the public feed for versions, the same command already prints the
/// installed and available versions the row shows. There is no per-package publish DATE anywhere
/// (verified live against Google's own manifest: a revision number, not a date), but the
/// manifest does carry a real Windows archive size per package, so that one field is enriched.
/// </remarks>
public sealed class AndroidSdkSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    private const string RepositoryManifestUrl = "https://dl.google.com/android/repository/repository2-3.xml";

    /// <summary>A few MB in practice; refuse anything wildly larger rather than buffer it whole.</summary>
    private const long MaxManifestBytes = 32L * 1024 * 1024;

    private static readonly Lazy<(string? Executable, string? SdkRoot, bool UseAndroidCli)> Resolved =
        new(ResolveTool);

    /// <summary>The newest command-line tools ship `android.exe` beside `sdkmanager.bat`, which only
    /// forwards to it. When it is there it is run directly: no Java needed, and it updates ONE package
    /// (the forwarder's `--update` takes every package and aborts on a single downgrade offer).</summary>
    private static (string? Executable, string? SdkRoot, bool UseAndroidCli) ResolveTool()
    {
        var (sdkManager, sdkRoot) = AndroidSdkLocator.FindSdkManager();
        return AndroidSdkLocator.AndroidCliNextTo(sdkManager) is { } cli
            ? (cli, sdkRoot, true)
            : (sdkManager, sdkRoot, false);
    }

    // sdkmanager.bat is a thin wrapper around a JVM launch, and it aborts with a native crash code
    // (not a normal sdkmanager error) when the java.exe it finds is older than the 17+ its bundled
    // cmdline-tools require. Resolving JAVA_HOME ourselves and handing it to the
    // child process removes the dependency on whatever "java" happens to be first on PATH.
    private static readonly Lazy<string?> ResolvedJavaHome = new(FindJavaHome);

    // Fetched once per process lifetime and shared across every package row this scan enriches, the
    // manifest is the same few-MB file regardless of which package is being looked up, so fetching it
    // again per row would multiply a several-second download by however many packages are outdated.
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private string? _manifestXml;
    private bool _manifestFetched;

    public override string Id => "android-sdk";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => Resolved.Value.Executable ?? "sdkmanager";

    /// <summary>
    /// sdkmanager reads its SDK location from ANDROID_HOME and refuses to run without one. When we found the
    /// tool ourselves we know the root it belongs to, so it is passed explicitly rather than relying on the
    /// user's environment being set, a developer with the command-line tools in place but no ANDROID_HOME
    /// would otherwise get an error instead of a scan. `android.exe` takes the same root as `--sdk=`.
    /// </summary>
    private static IReadOnlyList<string> RootArguments(bool useAndroidCli, string? sdkRoot) =>
        sdkRoot is null ? [] : [useAndroidCli ? $"--sdk={sdkRoot}" : $"--sdk_root={sdkRoot}"];

    internal static IReadOnlyList<string> BuildScanArguments(bool useAndroidCli, string? sdkRoot) =>
        useAndroidCli ? [.. RootArguments(true, sdkRoot), "sdk", "list"] : [.. RootArguments(false, sdkRoot), "--list"];

    /// <summary>One package per row. `sdkmanager --update` would update everything at once, so the package
    /// path is the argument instead: installing a path installs its newest version. Never `--force`,
    /// `--beta` or `--canary`.</summary>
    internal static IReadOnlyList<string> BuildInstallArguments(bool useAndroidCli, string? sdkRoot, string packageId) =>
        useAndroidCli
            ? [.. RootArguments(true, sdkRoot), "sdk", "update", packageId]
            : [.. RootArguments(false, sdkRoot), packageId];

    internal static IReadOnlyList<string> BuildUninstallArguments(bool useAndroidCli, string? sdkRoot, string packageId) =>
        useAndroidCli
            ? [.. RootArguments(true, sdkRoot), "sdk", "remove", packageId]
            : [.. RootArguments(false, sdkRoot), "--uninstall", packageId];

    public override Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Resolved.Value.Executable is not null || ProcessRunner.Exists("sdkmanager"));

    /// <summary>The sdkmanager.bat path crashes on an old system Java exactly as its scan would, so an update
    /// or removal through it gets the same resolved JAVA_HOME the scan ran with.</summary>
    protected override IReadOnlyDictionary<string, string>? ProcessEnvironment =>
        !Resolved.Value.UseAndroidCli && ResolvedJavaHome.Value is { } javaHome
            ? new Dictionary<string, string> { ["JAVA_HOME"] = javaHome }
            : null;

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var (_, sdkRoot, useAndroidCli) = Resolved.Value;

        // android.exe needs no Java; only the .bat path does.
        Dictionary<string, string>? environment = null;
        if (!useAndroidCli)
        {
            // Reported the same way any other scan failure is: an exception before the tool ever runs,
            // rather than a low-level "exited with code -1" that would leave the developer guessing why
            // a tool that is clearly installed just did nothing.
            if (ResolvedJavaHome.Value is not { } javaHome)
                throw new InvalidOperationException(
                    string.Format(LocalizationService.Instance["ScanFailedNoJava"], MinimumJavaMajor));

            environment = new Dictionary<string, string> { ["JAVA_HOME"] = javaHome };
        }

        var result = await RunAsync(BuildScanArguments(useAndroidCli, sdkRoot), cancellationToken, environment);
        return BuildScanResult(result);
    }

    /// <summary>
    /// sdkmanager.bat prints its full, correct listing and THEN exits with a native crash code on some
    /// machines (measured -1073740791 / 0xC0000409 in the field, with a deprecation banner ahead of the
    /// real output). The parse is trusted over the exit code: a listing recognised as either the classic
    /// or the newer layout is a real scan result regardless of how the process itself ended, whether it
    /// produced rows or not (the newer layout has no updates table at all, so "zero rows" there routinely
    /// just means every candidate resolves to an older version). Only output that matched NEITHER known
    /// layout falls back to the normal crash/failure classification. Split out of
    /// <see cref="ScanCoreAsync"/> and left internal so a test can feed a captured real
    /// <see cref="ProcessResult"/> straight in, without running the actual tool.
    /// </summary>
    internal IReadOnlyList<UpdateItem> BuildScanResult(ProcessResult result)
    {
        // A killed listing can still carry a recognisable header; its rows would be a truncated scan.
        if (result.TimedOut)
            EnsureScanCompleted(result);

        var packages = PackageManagerParsers.ParseSdkManagerList(result.StandardOutput, out var recognized);
        if (packages.Count == 0)
        {
            if (!recognized)
                EnsureScanCompleted(result);
            else if (!IsAcceptedScanExitCode(result.ExitCode))
                Logging.Warn($"{Id}: list recognised with zero updates but '{Executable}' exited with code {result.ExitCode}; treating as a clean scan.");
        }
        else if (!IsAcceptedScanExitCode(result.ExitCode))
        {
            Logging.Warn($"{Id}: scan produced {packages.Count} package row(s) but '{Executable}' exited with code {result.ExitCode}; keeping the parsed result.");
        }

        return packages
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    /// <summary>
    /// The JAVA_HOME to run sdkmanager with. Candidates in order of authority: an explicit JAVA_HOME the
    /// user already set, then whatever java.exe PATH resolves (walked back up from "...\bin\java.exe" to
    /// its home), then the JBR (JetBrains Runtime) each Android Studio install ships at
    /// "&lt;studio install&gt;\jbr" (a developer who only ever opens the SDK Manager inside the IDE has never
    /// needed a standalone Java). Null when none of them exists; <see cref="ChooseJavaHome"/> holds the
    /// version rule.
    /// </summary>
    private static string? FindJavaHome()
    {
        var candidates = new List<string>();

        var fromEnvironment = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (fromEnvironment is { Length: > 0 } && File.Exists(Path.Combine(fromEnvironment, "bin", "java.exe")))
            candidates.Add(fromEnvironment);

        if (ProcessRunner.Resolve("java") is { } javaOnPath
            && Path.GetDirectoryName(javaOnPath) is { } binDirectory
            && Path.GetDirectoryName(binDirectory) is { } homeFromPath)
        {
            candidates.Add(homeFromPath);
        }

        foreach (var studio in AndroidSdkLocator.StudioInstallDirectories())
        {
            var jbrHome = Path.Combine(studio, "jbr");
            if (File.Exists(Path.Combine(jbrHome, "bin", "java.exe")))
                candidates.Add(jbrHome);
        }

        return ChooseJavaHome(candidates, ReadJavaMajor);
    }

    /// <summary>The oldest Java the current cmdline-tools run on.</summary>
    private const int MinimumJavaMajor = 17;

    /// <summary>
    /// The first candidate whose Java is new enough for the cmdline-tools, else the first candidate as is:
    /// an older cmdline-tools release may still run on an older Java, so an old Java is a last resort, not
    /// "no Java". A candidate with no readable version never qualifies but still counts as the fallback.
    /// </summary>
    internal static string? ChooseJavaHome(IReadOnlyList<string> candidateHomes, Func<string, int?> majorOf)
    {
        foreach (var home in candidateHomes)
        {
            if (majorOf(home) is { } major && major >= MinimumJavaMajor)
                return home;
        }

        return candidateHomes.Count > 0 ? candidateHomes[0] : null;
    }

    /// <summary>
    /// The Java major version from the JAVA_VERSION line of "&lt;home&gt;\release", which every JDK and JBR
    /// ships. Read as text only: starting java just to ask its version would cost a process per scan.
    /// "1.8.0_392" is major 8, "21.0.3" is major 21. Null when the file or the line is missing or unreadable.
    /// </summary>
    internal static int? ReadJavaMajor(string home)
    {
        try
        {
            var releaseFile = Path.Combine(home, "release");
            if (!File.Exists(releaseFile))
                return null;

            foreach (var line in File.ReadLines(releaseFile))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                    continue;

                var value = trimmed["JAVA_VERSION=".Length..].Trim().Trim('"');
                var parts = value.Split('.', '_', '-', '+');
                var index = parts.Length > 1 && parts[0] == "1" ? 1 : 0;
                return int.TryParse(parts[index], out var major) ? major : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) =>
        BuildInstallArguments(Resolved.Value.UseAndroidCli, Resolved.Value.SdkRoot, item.Id);

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) =>
        BuildUninstallArguments(Resolved.Value.UseAndroidCli, Resolved.Value.SdkRoot, item.Id);

    /// <summary>
    /// `android.exe sdk update` exits 127 even after a good update and also when Java fails on a locked file,
    /// so its exit code says nothing. After the run the listing is read once more and the update counts as
    /// done when the package no longer offers one; otherwise the tool's own output is the failure. The
    /// `sdkmanager.bat` path keeps the plain exit-code rule.
    /// </summary>
    public override async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        var (executable, sdkRoot, useAndroidCli) = Resolved.Value;
        if (!useAndroidCli || executable is null)
        {
            await base.InstallAsync(item, progress, cancellationToken);
            return;
        }

        GuardPackageId(item.Id);
        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var update = await ProcessRunner.RunStreamingAsync(
            executable, BuildInstallArguments(true, sdkRoot, item.Id), InstallTimeout,
            segment => ReportSegment(progress, item.Name, segment), cancellationToken);

        if (update.TimedOut)
            throw new InvalidOperationException($"{Id}: install timed out for '{item.Id}'.");

        var listing = await RunAsync(BuildScanArguments(true, sdkRoot), cancellationToken);
        if (!OffersNoUpdateFor(listing, item.Id))
        {
            var detail = ToolOutput.Summarize(update.StandardOutput, update.StandardError, update.ExitCode);
            throw new InvalidOperationException($"{Id}: install failed for '{item.Id}'. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>The same exit-code blindness applies to a removal, so it is confirmed against the listing too:
    /// done when the listing is readable and no longer holds the package.</summary>
    public override async Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        var (executable, sdkRoot, useAndroidCli) = Resolved.Value;
        if (!useAndroidCli || executable is null)
        {
            await base.UninstallAsync(item, progress, cancellationToken);
            return;
        }

        GuardPackageId(item.Id);
        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var removal = await ProcessRunner.RunStreamingAsync(
            executable, BuildUninstallArguments(true, sdkRoot, item.Id), InstallTimeout,
            segment => ReportSegment(progress, item.Name, segment), cancellationToken);

        if (removal.TimedOut)
            throw new InvalidOperationException($"{Id}: uninstall timed out for '{item.Id}'.");

        var listing = await RunAsync(BuildScanArguments(true, sdkRoot), cancellationToken);
        if (!IsGoneFromListing(listing, item.Id))
        {
            var detail = ToolOutput.Summarize(removal.StandardOutput, removal.StandardError, removal.ExitCode);
            throw new InvalidOperationException($"{Id}: uninstall failed for '{item.Id}'. {detail}");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>True when the listing is readable and no row of it starts with the package path.</summary>
    internal static bool IsGoneFromListing(ProcessResult listing, string packageId)
    {
        PackageManagerParsers.ParseSdkManagerList(listing.StandardOutput, out var recognized);
        return recognized && !listing.StandardOutput
            .Split('\n')
            .Any(line => line.TrimStart().StartsWith(packageId + " ", StringComparison.Ordinal));
    }

    /// <summary>True when the listing is readable and the package no longer offers an update.</summary>
    internal static bool OffersNoUpdateFor(ProcessResult listing, string packageId)
    {
        var packages = PackageManagerParsers.ParseSdkManagerList(listing.StandardOutput, out var recognized);
        // Still listed as installed, too: an update that failed halfway through deleting the old copy
        // leaves no row offering an update either, and must not read as a success.
        return recognized
            && packages.All(p => !string.Equals(p.Id, packageId, StringComparison.Ordinal))
            && !IsGoneFromListing(listing, packageId);
    }

    /// <summary>True when the SDK tools are nowhere to be found, which is what the manual row stands in for.</summary>
    internal static bool ToolMissing => Resolved.Value.Executable is null && !ProcessRunner.Exists("sdkmanager");

    /// <summary>No per-package release notes exist (the manifest carries only a revision number, see the
    /// remark on <see cref="PackageMetadataParsers.ParseAndroidSdkPackageSize"/>), the SDK tools' combined
    /// release notes page is attached instead, real and free of any lookup, so it shows even when the
    /// manifest fetch below fails.</summary>
    private const string ChangelogUrl = "https://developer.android.com/studio/releases/sdk-tools";

    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var manifest = await GetRepositoryManifestAsync(cancellationToken);
        var size = manifest is null ? null : PackageMetadataParsers.ParseAndroidSdkPackageSize(manifest, item.Id);
        return new UpdateEnrichment(null, size, ChangelogUrl: ChangelogUrl);
    }

    private async Task<string?> GetRepositoryManifestAsync(CancellationToken cancellationToken)
    {
        if (_manifestFetched)
            return _manifestXml;

        await _manifestGate.WaitAsync(cancellationToken);
        try
        {
            if (_manifestFetched)
                return _manifestXml;

            using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryManifestUrl);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Content-Length is an early-out only: a CHUNKED response advertises no length
            // at all, and ReadAsStringAsync would then buffer the whole thing regardless of size. The
            // ceiling is enforced on the stream itself, the same pattern RegistryEnrichment uses.
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is not > MaxManifestBytes)
                _manifestXml = await ReadLimitedAsync(response, cancellationToken);

            _manifestFetched = true;
            return _manifestXml;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // NOT marked fetched: a transient network failure should not silently disable size
            // enrichment for the rest of the process's lifetime, the next scan tries again.
            Logging.Warn($"{Id}: could not read the SDK repository manifest: {ex.Message}");
            return null;
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    /// <summary>Reads the body with the ceiling enforced on the STREAM, not just the header, a chunked
    /// response advertises no Content-Length at all.</summary>
    internal static Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        HttpResponseReading.ReadLimitedAsync(response, MaxManifestBytes, cancellationToken);
}
