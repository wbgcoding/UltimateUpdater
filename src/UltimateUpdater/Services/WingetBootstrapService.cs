using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UltimateUpdater.Services;

/// <summary>
/// Offers to install winget itself when the scan finds it missing. This is deliberately
/// NOT part of <see cref="Sources.WingetSource"/> or <see cref="ScanOrchestrator"/>: the "a source may
/// never install, fetch or self-update anything just to be able to check it" rule is about the passive
/// scan path, and stays intact because this class is only ever driven by an explicit, user-consented
/// action from the UI layer after a scan has already finished, never from inside a scan.
/// Asked at most once per running session regardless of the answer: a "no" is not repeated, and once
/// winget is present the question no longer applies.
/// </summary>
public sealed class WingetBootstrapService(
    HttpClient httpClient, LoggingService logging, Func<bool>? isWingetPresent = null)
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";
    private const string AssetName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    private const string DependenciesAssetName = "DesktopAppInstaller_Dependencies.zip";
    private const string MicrosoftOrganizationName = "Microsoft Corporation";
    private const string OrganizationNameOid = "2.5.4.10";

    /// <summary>The App Installer bundle carries every supported architecture in one file; generous but
    /// still bounded so a compromised or redirected response cannot fill the disk.</summary>
    private const long MaxBundleBytes = 400L * 1024 * 1024;

    private static readonly TimeSpan ApiTimeout = Sources.ExternalCallTimeouts.ScanDeadline;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _httpClient = httpClient;
    private readonly LoggingService _logging = logging;
    private bool _offeredThisSession;

    /// <summary>True only up to the first call to <see cref="OfferAsync"/> in this process, and only while
    /// winget is genuinely absent; never true again afterwards, whatever the user answered.</summary>
    public bool NeedsOffer => ShouldOffer(_offeredThisSession, IsWingetPresent);

    /// <summary>Whether winget is on the machine right now; the probe is a seam so a test does not depend on
    /// the host's real install.</summary>
    internal bool IsWingetPresent => isWingetPresent?.Invoke() ?? ProcessRunner.Exists("winget");

    /// <summary>Split out from <see cref="NeedsOffer"/> so the "ask at most once, and only while winget is
    /// actually missing" rule is directly unit-testable without needing to fake <see cref="ProcessRunner"/>
    /// or the machine's real winget install state.</summary>
    internal static bool ShouldOffer(bool alreadyOfferedThisSession, bool wingetIsPresent) =>
        !alreadyOfferedThisSession && !wingetIsPresent;

    /// <summary>
    /// Shows the one-time dialog and, on "yes", downloads and installs winget. Safe to call after every
    /// scan: it is a no-op once winget is present or once this session has already asked. A failure is
    /// logged and handed back as an outcome with its reason, so the caller can tell the user instead of
    /// leaving them to wonder; the caller does not need its own try/catch, the winget source simply stays
    /// absent, exactly like it already does today when nothing installs it.
    /// </summary>
    /// <param name="progress">Download progress of the installer package in percent (0 to 100).</param>
    public async Task<WingetBootstrapOutcome> OfferAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (!NeedsOffer)
            return WingetBootstrapOutcome.NotOffered;

        // Set before the dialog, not after: a dialog dismissed by closing the app, or a re-entrant call
        // that races this one, must still count as "asked" so it can never show twice in one run.
        _offeredThisSession = true;

        if (!Dialogs.Confirm(
                LocalizationService.Instance["WingetBootstrapConfirmHeadline"],
                LocalizationService.Instance["WingetBootstrapConfirmBody"],
                confirmText: LocalizationService.Instance["WingetBootstrapInstall"]))
        {
            return WingetBootstrapOutcome.NotOffered;
        }

        return await InstallAsync(progress, cancellationToken);
    }

    /// <summary>The download-and-install itself, without the question; every failure becomes an outcome.</summary>
    internal async Task<WingetBootstrapOutcome> InstallAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await InstallCoreAsync(progress, cancellationToken);
            if (outcome.Status == WingetBootstrapStatus.Failed)
                _logging.Warn($"winget bootstrap: {outcome.Reason}");
            return outcome;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            _logging.Warn($"winget bootstrap: {ex.Message}");
            return WingetBootstrapOutcome.Failed(
                ex is OperationCanceledException ? "the operation timed out or was cancelled" : ex.Message);
        }
    }

    private async Task<WingetBootstrapOutcome> InstallCoreAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(ReleaseApiUrl, cancellationToken);
        if (json is null)
            return WingetBootstrapOutcome.Failed("the latest App Installer release could not be read");

        var assetUrl = SelectAssetUrl(json);
        if (assetUrl is null)
            return WingetBootstrapOutcome.Failed($"the latest release has no '{AssetName}' package");

        var staging = Path.Combine(StagingRoot ?? Path.GetTempPath(), $"winget-bootstrap-{Guid.NewGuid():N}");
        var target = Path.Combine(staging, "installer.msixbundle");
        var dependencies = new List<FileStream>();
        FileStream? pin = null;
        try
        {
            Directory.CreateDirectory(staging);
            progress?.Report(0);
            await DownloadAsync(assetUrl, target, progress, cancellationToken);
            pin = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Both checks read and hash the whole bundle (about 200 MB); this method continues on the UI
            // thread that showed the confirmation, so they run on the thread pool instead.
            var trusted = await Task.Run(() => IsTrustedPackage(target), cancellationToken);
            if (!trusted)
                return WingetBootstrapOutcome.Failed("the downloaded package is not signed by Microsoft");

            if (Security.MalwareScanner.ScanDownloads)
            {
                var outcome = await new Security.MalwareScanner(_logging).ScanFileAsync(target, cancellationToken);
                if (outcome.IsThreat)
                    return WingetBootstrapOutcome.Failed($"the virus scan flagged the download ({outcome.Detail})");
            }

            // A machine without the framework packages (an LTSC edition, typically) cannot install the
            // bundle alone. Any problem with the optional dependency archive only skips it: the bundle's own
            // error then names what is missing.
            dependencies = await PrepareDependenciesAsync(json, staging, cancellationToken);
            foreach (var dependency in dependencies)
            {
                var dependencyResult = await AddPackageAsync(dependency.Name, cancellationToken);
                if (dependencyResult.ExitCode != 0)
                    _logging.Warn($"winget bootstrap: a dependency did not install (exit {dependencyResult.ExitCode}): {FirstLine(dependencyResult.StandardError)}");
            }

            // Add-AppxPackage installs a .msixbundle directly, picking the entry matching this machine's
            // architecture; no manual extraction needed. Per-user, so no elevation, unlike a winget PACKAGE
            // install (InstallQueue.NeedsElevation), which is a different question about a different tool.
            var result = await AddPackageAsync(target, cancellationToken);
            if (result.TimedOut)
                return WingetBootstrapOutcome.Failed("Windows did not finish installing the package in time");

            if (result.ExitCode != 0)
            {
                var detail = FirstLine(result.StandardError);
                return WingetBootstrapOutcome.Failed(
                    detail.Length > 0 ? detail : $"Windows refused the package (exit {result.ExitCode})");
            }

            _logging.Info("winget was installed on user request.");
            return WingetBootstrapOutcome.Succeeded;
        }
        finally
        {
            pin?.Dispose();
            foreach (var dependency in dependencies)
                dependency.Dispose();
            TryDeleteFolder(staging);
        }
    }

    /// <summary>The first non-empty line of a tool's error text, one line and bounded, fit for a dialog.</summary>
    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        return line.Length > 240 ? line[..240] : line;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // temp leftovers are swept by the OS; nothing to tell the user
        }
    }

    /// <summary>The OS architecture as the folder name the dependency archive uses; null for anything else.
    /// A test seam.</summary>
    internal string? ArchitectureFolder { get; set; } =
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => null,
        };

    /// <summary>Where the staging folder is made; null = the user's temp folder. A test seam.</summary>
    internal string? StagingRoot { get; set; }

    /// <summary>Whether a downloaded package is signed by Microsoft. A test seam.</summary>
    internal Func<string, bool> IsTrustedPackage { get; set; } =
        path => TrustedBinary.IsSigned(path) && IsMicrosoftPublisher(path);

    /// <summary>Installs one package for the current user. A test seam.</summary>
    internal Func<string, CancellationToken, Task<ProcessResult>> AddPackageAsync { get; set; } = RunAddPackageAsync;

    private static Task<ProcessResult> RunAddPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        var command = BuildInstallCommand(packagePath);
        return ProcessRunner.RunAsync("powershell", command.Arguments, InstallTimeout, cancellationToken, command.Environment);
    }

    private const int MaxDependencyPackages = 16;

    /// <summary>
    /// Downloads the dependency archive, unpacks the .appx files for this OS architecture into the staging
    /// folder and checks each one's Microsoft signature. The returned streams hold the files open (read
    /// share) so nothing can swap them between the check and the install. Any problem at all returns an
    /// empty list: the dependencies are an extra, never a reason to refuse the bundle.
    /// </summary>
    private async Task<List<FileStream>> PrepareDependenciesAsync(
        string releaseJson, string staging, CancellationToken cancellationToken)
    {
        var pinned = new List<FileStream>();
        try
        {
            var url = SelectAssetUrl(releaseJson, DependenciesAssetName);
            if (url is null || ArchitectureFolder is not { } architecture)
                return pinned;

            var archivePath = Path.Combine(staging, "dependencies.zip");
            await DownloadAsync(url, archivePath, null, cancellationToken);

            var folder = Path.Combine(staging, "dependencies");
            Directory.CreateDirectory(folder);

            using (var archive = System.IO.Compression.ZipFile.OpenRead(archivePath))
            {
                var entries = archive.Entries.Where(entry => IsDependencyEntry(entry.FullName, architecture)).ToList();
                if (entries.Count == 0 || entries.Count > MaxDependencyPackages)
                    throw new InvalidOperationException("the dependency archive holds no usable packages for this machine");

                long remaining = MaxBundleBytes;
                for (var index = 0; index < entries.Count; index++)
                {
                    // Named by position, never by the archive's own path.
                    var destination = Path.Combine(folder, $"dependency-{index:00}.appx");
                    await using var source = entries[index].Open();
                    await using var output = File.Create(destination);
                    remaining -= await CopyBoundedAsync(source, output, remaining, cancellationToken);
                }
            }

            foreach (var path in Directory.EnumerateFiles(folder, "*.appx").Order(StringComparer.Ordinal))
                pinned.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));

            foreach (var stream in pinned)
            {
                var name = stream.Name;
                if (!await Task.Run(() => IsTrustedPackage(name), cancellationToken))
                    throw new InvalidOperationException("a dependency package is not signed by Microsoft");
            }

            return pinned;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
                or InvalidDataException or OperationCanceledException)
        {
            _logging.Warn($"winget bootstrap: continuing without the dependency packages ({ex.Message}).");
            foreach (var stream in pinned)
                stream.Dispose();
            return [];
        }
    }

    /// <summary>True for an .appx entry that sits under a folder named for the architecture ("x64/...").</summary>
    internal static bool IsDependencyEntry(string entryName, string architecture)
    {
        var parts = entryName.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || parts.Any(part => part == ".."))
            return false;

        return parts[^1].EndsWith(".appx", StringComparison.OrdinalIgnoreCase)
            && parts[..^1].Any(part => string.Equals(part, architecture, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > limit)
                throw new InvalidOperationException("the dependency archive exceeds the size limit; refusing.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return total;
    }

    internal const string PackagePathVariable = "UU_BOOTSTRAP_PACKAGE";

    /// <summary>
    /// The package path travels in an environment variable, never inside the command text: in a
    /// double-quoted PowerShell string `$`, a backtick and `$(...)` are live, so a temp folder named
    /// with them would install from the wrong path or evaluate text as code.
    /// </summary>
    internal static (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) BuildInstallCommand(
        string packagePath) =>
        (["-NoProfile", "-NonInteractive", "-Command", $"Add-AppxPackage -Path $env:{PackagePathVariable}"],
         new Dictionary<string, string> { [PackagePathVariable] = packagePath });

    /// <summary>Pure so it is directly testable against a captured GitHub API payload: no network, no temp
    /// files. Reuses the same release-JSON parser the self-update path already trusts.</summary>
    internal static string? SelectAssetUrl(string releaseJson) => SelectAssetUrl(releaseJson, AssetName);

    internal static string? SelectAssetUrl(string releaseJson, string assetName)
    {
        var release = SelfUpdate.UpdateManifest.ParseRelease(releaseJson, allowPrereleases: false);
        var asset = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal));
        return asset?.DownloadUrl is { Length: > 0 } url ? url : null;
    }

    /// <summary>The release description is a few hundred KB at most; the cap only stops a runaway body.</summary>
    private const long MaxReleaseJsonBytes = 4L * 1024 * 1024;

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UltimateUpdater");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ApiTimeout);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        return response.IsSuccessStatusCode
            ? await Sources.HttpResponseReading.ReadLimitedAsync(response, MaxReleaseJsonBytes, timeoutCts.Token)
            : null;
    }

    private async Task DownloadAsync(string url, string target, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UltimateUpdater");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DownloadTimeout);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        // As with the NVIDIA download: check the FINAL address after every redirect, not the one the
        // release JSON named, since a redirect could carry the request off GitHub entirely.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (finalUrl is null || !SelfUpdate.SelfUpdateService.IsAllowedUrl(finalUrl))
            throw new InvalidOperationException("the download redirected off GitHub; refusing.");

        var total = response.Content.Headers.ContentLength;
        if (total is > MaxBundleBytes)
            throw new InvalidOperationException("the App Installer package exceeds the size limit; refusing.");

        await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, timeoutCts.Token)) > 0)
        {
            received += read;
            if (received > MaxBundleBytes)
                throw new InvalidOperationException("the App Installer package exceeded the size limit mid-download; aborting.");

            await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);

            if (progress is not null && total is > 0)
            {
                var percent = (int)Math.Min(100, received * 100 / total.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(percent);
                }
            }
        }
    }

    internal static bool IsMicrosoftPublisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return IsMicrosoftOrganization(certificate.Subject);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return false;
        }
    }

    /// <summary>Split out from <see cref="IsMicrosoftPublisher"/> so the RDN-matching rule can be
    /// unit-tested against a Subject string directly, the same shape as
    /// <see cref="Sources.NvidiaDriverSource.IsNvidiaOrganization"/>.</summary>
    internal static bool IsMicrosoftOrganization(string subject)
    {
        try
        {
            var subjectName = new X500DistinguishedName(subject);
            return subjectName.EnumerateRelativeDistinguishedNames().Any(rdn =>
                rdn.GetSingleElementType().Value == OrganizationNameOid
                && string.Equals(rdn.GetSingleElementValue(), MicrosoftOrganizationName, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CryptographicException)
        {
            return false;
        }
    }
}

public enum WingetBootstrapStatus
{
    /// <summary>Nothing was offered, or the user declined.</summary>
    NotOffered,
    Installed,
    Failed,
}

/// <summary>What an offer came to. <see cref="Reason"/> is short English text for a failure, fit for the dialog.</summary>
public sealed record WingetBootstrapOutcome(WingetBootstrapStatus Status, string? Reason = null)
{
    public static WingetBootstrapOutcome NotOffered { get; } = new(WingetBootstrapStatus.NotOffered);

    public static WingetBootstrapOutcome Succeeded { get; } = new(WingetBootstrapStatus.Installed);

    public static WingetBootstrapOutcome Failed(string reason) => new(WingetBootstrapStatus.Failed, reason);
}
