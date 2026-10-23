using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Tier A where it can, Tier C where it cannot: NVIDIA graphics drivers.
/// NVIDIA is the one vendor with a usable (if unofficial, long-stable) lookup service, so this detects
/// the GPU, asks NVIDIA for the latest Game Ready driver, and; when it is newer than what WMI reports
/// as installed; offers it as a real, installable row that downloads with live progress and installs
/// silently. The installer is NVIDIA-signed and self-elevates, so no elevated worker is involved (and
/// the "worker writes no files" rule is never touched). If anything about the lookup fails, the row
/// degrades to a Tier C "open NVIDIA's download page" link so the user is never left worse off than the
/// pick-the-safe-option default.
/// No uninstall: a driver removal needs the Registry-`UninstallString` fallback that is out of scope for
/// this app on principle, same reasoning as Windows Update: every other driver source here is Tier C
/// (a link, never an install) and so never reaches the question at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvidiaDriverSource : IUpdateSource
{
    private const string DriversPage = "https://www.nvidia.com/download/index.aspx";
    private const string ProductLookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string ManualLookupBase = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    private const long MaxDriverBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan HttpTimeout = ExternalCallTimeouts.ScanDeadline;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Where the download URL out of the lookup response is allowed to point.</summary>
    private static readonly string[] AllowedDownloadSuffixes = [".nvidia.com", ".geforce.com"];

    private readonly LoggingService _logging;
    private readonly HttpClient _httpClient;

    public NvidiaDriverSource(LoggingService logging, HttpClient httpClient)
    {
        _logging = logging;
        _httpClient = httpClient;
    }

    public string Id => "nvidia-driver";

    public UpdateCategory Category => UpdateCategory.SystemAndDrivers;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(DriverDetection.DetectGpus().Any(g => g.Vendor == GpuVendor.Nvidia));

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var gpu = DriverDetection.DetectGpus().FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia);
        if (gpu is null)
            yield break;

        UpdateItem? row;
        try
        {
            row = await ResolveAsync(gpu, cancellationToken);
        }
        // Only a real cancellation may end the source: NVIDIA's lookup timing out also arrives as a
        // TaskCanceledException, so this guard must check the token rather than just the exception type,
        // or it would swallow the Tier C download-link fallback this source always promises.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _logging.Warn($"nvidia-driver: lookup failed, offering the download page instead: {ex.Message}");
            row = OpenPageRow(gpu);
        }

        if (row is not null)
            yield return row;
    }

    /// <summary>No per-driver release-notes feed exists here; NVIDIA's own download index is the closest
    /// real page to a changelog, and it is already known: no extra network call.</summary>
    public Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<UpdateEnrichment?>(new UpdateEnrichment(null, null, ChangelogUrl: DriversPage));

    private async Task<UpdateItem?> ResolveAsync(GpuInfo gpu, CancellationToken cancellationToken)
    {
        var installed = DriverDetection.DecodeNvidiaVersion(gpu.DriverVersion);

        var productXml = await GetStringAsync(ProductLookupUrl, cancellationToken);
        var productId = NvidiaDriverLookup.FindProductId(productXml, gpu.Name);
        if (productId is null)
        {
            _logging.Info($"nvidia-driver: could not map '{gpu.Name}' to a product id; offering the download page.");
            return OpenPageRow(gpu);
        }

        var osId = Environment.OSVersion.Version.Build >= 22000
            ? NvidiaDriverLookup.OsIdWindows11
            : NvidiaDriverLookup.OsIdWindows10X64;

        var lookupUrl = $"{ManualLookupBase}?func=DriverManualLookup&pfid={productId}&osID={osId}" +
                        "&languageCode=1033&isWHQL=1&dch=1&upCRD=0&beta=0&dltype=-1";
        var json = await GetStringAsync(lookupUrl, cancellationToken);
        var driver = NvidiaDriverLookup.ParseDriverResponse(json);

        // An unparseable response (NVIDIA changed the format, a garbled reply) must fall back to the
        // download page, same as the productId/IsAllowedDownload failures below: this source always
        // offers at least that page. "No newer version" is the one case that correctly yields no row.
        if (driver is null)
        {
            _logging.Warn("nvidia-driver: could not parse the lookup response; offering the download page instead.");
            return OpenPageRow(gpu);
        }

        if (!NvidiaDriverLookup.IsNewer(installed, driver.Version))
            return null;

        if (!IsAllowedDownload(driver.DownloadUrl))
        {
            _logging.Warn("nvidia-driver: lookup returned a download URL off NVIDIA's own hosts; offering the page instead.");
            return OpenPageRow(gpu);
        }

        // The lookup usually reports a size itself; only spend a HEAD on the rare response that omits it.
        var downloadSize = driver.SizeBytes ?? await RegistryEnrichment.HeadContentLengthAsync(
            driver.DownloadUrl, _logging, Id, productId.Value.ToString(), cancellationToken);

        return new UpdateItem
        {
            Id = "nvidia-graphics-driver",
            SourceId = Id,
            Category = Category,
            Name = $"NVIDIA Graphics Driver ({gpu.Name})",
            InstalledVersion = installed,
            AvailableVersion = driver.Version,
            // a GPU driver is not something the app makes a security claim about via its identifier table
            IsCritical = false,
            Tier = SourceTier.A,
            DownloadSize = downloadSize,
            PublishDate = driver.ReleaseDate,
            // the "Open" button shows the release-notes page next to the installable row
            OpenTarget = driver.DetailsUrl,
            Meta = new Dictionary<string, string> { ["downloadUrl"] = driver.DownloadUrl },
        };
    }

    /// <summary>The Tier C fallback: a link to NVIDIA's own download page, nothing this app installs.</summary>
    private UpdateItem OpenPageRow(GpuInfo gpu) => new()
    {
        Id = "nvidia-graphics-driver",
        SourceId = Id,
        Category = Category,
        Name = $"NVIDIA Graphics Driver ({gpu.Name})",
        IsCritical = false,
        Tier = SourceTier.C,
        OpenTarget = DriversPage,
    };

    /// <summary>Name prefix of the per-install staging folder (see <c>InstallAsync</c>).</summary>
    internal const string StagingFolderPrefix = "nvidia-driver-";

    public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!item.Meta.TryGetValue("downloadUrl", out var url) || !IsAllowedDownload(url))
            throw new InvalidOperationException("nvidia-driver: no trusted download URL for this row.");

        // Its own fresh folder, never the shared temp root: the signed installer self-elevates and may load
        // DLLs from the folder it runs in, which any process of this user could have planted in %TEMP%.
        var target = Path.Combine(FileDeletion.NewStagingFolderPath(Path.GetTempPath(), StagingFolderPrefix), "nvidia-driver.exe");
        FileStream? pin = null;
        try
        {
            progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await DownloadAsync(url, target, progress, item.Name, cancellationToken);

            // Pinned open from here until the installer actually starts. Every check below (signature,
            // publisher, malware scan) re-opens the file BY PATH, without a continuously-held handle,
            // any other process running as this same user could swap the file between one check and the
            // next, turning the UAC prompt the signed installer earns into one for whatever it swapped
            // in. FileShare.Read still lets those checks (and the installer itself) open the file for
            // reading; it only denies a concurrent write or delete. Same TOCTOU class, same fix
            // SelfUpdateService already applies to its own verified download.
            pin = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

            // A valid Authenticode signature AND an NVIDIA subject: a signature alone only proves
            // "signed by someone", so the publisher is checked before this runs with the elevation the
            // installer's own manifest will request.
            if (!TrustedBinary.IsSigned(target) || !IsNvidiaPublisher(target))
                throw new InvalidOperationException("nvidia-driver: the downloaded installer is not NVIDIA-signed; refusing to run it.");

            // Second opinion from the installed antivirus (opt-out). Fails open: only a positive
            // identification stops the install, a missing or silent scanner never does.
            await ScanBeforeRunAsync(target, item.Name, progress, cancellationToken);

            progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
            await RunSilentAsync(target, cancellationToken);
        }
        finally
        {
            // Released only here, AFTER RunSilentAsync's Process.Start above: CreateProcess opens its
            // own handle on the image the moment the installer actually launches, so the pin has done
            // its job by the time this runs (same reasoning as SelfUpdateService.Apply's ReleaseVerified).
            pin?.Dispose();
            TryDeleteStaged(target);
        }
    }

    /// <summary>
    /// Hands the downloaded installer to the installed antivirus before it is run. A detection is fatal;
    /// the file is never executed and the row fails with a message that says why. Everything else (no
    /// antivirus, a refusal, a timeout) allows the install: an update must not be blocked because no
    /// scanner was available.
    /// </summary>
    private async Task ScanBeforeRunAsync(
        string target, string name, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!Security.MalwareScanner.ScanDownloads)
            return;

        progress.Report(new InstallProgress
        {
            PercentComplete = 100,
            StatusText = $"{name}: {LocalizationService.Instance["ScanningDownload"]}",
        });

        var outcome = await new Security.MalwareScanner(_logging).ScanFileAsync(target, cancellationToken);
        if (outcome.IsThreat)
        {
            _logging.Error($"nvidia-driver: the antivirus flagged the downloaded installer ({outcome.Detail}); it was not run.");
            throw new InvalidOperationException(LocalizationService.Instance["ScanThreatFound"]);
        }

        if (outcome.Verdict == Security.ScanVerdict.NotScanned)
        {
            _logging.Info($"nvidia-driver: the installer was not virus-scanned ({outcome.Detail}); continuing.");
        }
        else
        {
            // The scan itself must stay visible, not just silently happen: the "scanning…"
            // line above already showed while it ran; this is the result.
            progress.Report(new InstallProgress
            {
                PercentComplete = 100,
                StatusText = $"{name}: {LocalizationService.Instance["ScanClean"]}",
            });
        }
    }

    private async Task DownloadAsync(string url, string target, IProgress<InstallProgress> progress, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HttpTimeout.Add(InstallTimeout));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        // IsAllowedDownload above only checked the URL the lookup API handed back: an HTTP redirect
        // could still have carried the request off nvidia.com/geforce.com entirely; exactly the risk to
        // guard against. RequestMessage.RequestUri is the FINAL
        // address after every redirect HttpClient followed, so this is the one check that actually
        // covers that.
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (finalUrl is null || !IsAllowedDownload(finalUrl))
            throw new InvalidOperationException("nvidia-driver: the download redirected off the vendor's domain; refusing.");

        var total = response.Content.Headers.ContentLength;
        if (total is > MaxDriverBytes)
            throw new InvalidOperationException("nvidia-driver: the installer exceeds the size limit; refusing.");

        await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, timeoutCts.Token)) > 0)
        {
            received += read;
            if (received > MaxDriverBytes)
                throw new InvalidOperationException("nvidia-driver: the installer exceeded the size limit mid-download; aborting.");

            await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);

            progress.Report(new InstallProgress
            {
                PercentComplete = total is > 0 ? Math.Clamp(received * 100.0 / total.Value, 0, 100) : 0,
                StatusText = name,
                BytesReceived = received,
                BytesTotal = total,
            });
        }
    }

    /// <summary>
    /// Runs the NVIDIA installer silently with no reboot. ShellExecute (not ProcessRunner) on purpose:
    /// the installer's manifest asks for elevation, so the shell raises the one UAC prompt and no
    /// elevated worker of ours is involved. "-s -n" is NVIDIA's own documented silent/no-reboot switch.
    /// </summary>
    private async Task RunSilentAsync(string installerPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "-s -n",
            UseShellExecute = true,
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = the user dismissed the installer's UAC prompt
            throw new OperationCanceledException("Elevation for the NVIDIA installer was declined.", ex);
        }

        if (process is null)
            throw new InvalidOperationException("nvidia-driver: the installer did not start.");

        using (process)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(InstallTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // A driver installer is NOT killed on a stop: terminating one mid-flight can leave the
                // display driver half-replaced, which is worse than any outcome of letting it finish. So
                // this job stays TRACKED instead: it simply stops waiting for progress and waits for the
                // real exit. Skipping this wait would let the UI report "stopped" while the installer keeps
                // running unobserved, and would delete the temp installer file out from under it.
                _logging.Warn(
                    "nvidia-driver: the installer is already running and cannot be interrupted safely; "
                    + "waiting for it to finish rather than abandoning it.");

                if (!await WaitForExitBoundedAsync(process, InstallTimeout))
                {
                    throw new InvalidOperationException(
                        "The NVIDIA installer is still running and no longer reports its progress; "
                        + "its result will show in NVIDIA's own installer window.");
                }
            }

            // NVIDIA's installer returns 0 on success; anything else carries its own meaning and is
            // surfaced through the row's Details view.
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"NVIDIA driver install returned exit code {process.ExitCode}.");
        }
    }

    /// <summary>
    /// Waits for the installer without a caller token (a stop must not abandon it) but never for ever:
    /// false says it is still running after <paramref name="limit"/> and the row must say so.
    /// </summary>
    private static async Task<bool> WaitForExitBoundedAsync(Process process, TimeSpan limit)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(limit);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UltimateUpdater");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HttpTimeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeoutCts.Token) : null;
    }

    private static bool IsAllowedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && AllowedDownloadSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The organizationName (OID 2.5.4.10) RDN, matched exactly. A substring search over the
    /// whole Subject would also pass any validly-signed binary whose DN merely MENTIONS "NVIDIA" somewhere
    /// (a partner's OU, or another company's O= containing the word): this is defense-in-depth behind the
    /// nvidia.com download-host allowlist, but should still only trust NVIDIA's own organization.</summary>
    private const string OrganizationNameOid = "2.5.4.10";

    private const string NvidiaOrganizationName = "NVIDIA Corporation";

    private static bool IsNvidiaPublisher(string path)
    {
        try
        {
            // The signer certificate's subject, e.g. "CN=NVIDIA Corporation, O=NVIDIA Corporation, …".
            // CreateFromSignedFile is obsolete in general but is the only managed way to read a PE's
            // Authenticode signer, and it is used here purely to read the subject: no crypto operation
            // that its replacement guards against. Validity is already proven by TrustedBinary.IsSigned.
#pragma warning disable SYSLIB0057
            var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return IsNvidiaOrganization(certificate.Subject);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return false;
        }
    }

    /// <summary>Split out from <see cref="IsNvidiaPublisher"/> so the RDN-matching rule can be unit-tested
    /// against a Subject string directly: a real Authenticode-signed file is not available in a test.</summary>
    internal static bool IsNvidiaOrganization(string subject)
    {
        try
        {
            var subjectName = new X500DistinguishedName(subject);
            return subjectName.EnumerateRelativeDistinguishedNames().Any(rdn =>
                rdn.GetSingleElementType().Value == OrganizationNameOid
                && string.Equals(rdn.GetSingleElementValue(), NvidiaOrganizationName, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private void TryDeleteStaged(string path) =>
        FileDeletion.TryDeleteStaged(path, StagingFolderPrefix,
            ex => _logging.Warn($"nvidia-driver: could not delete the temporary installer: {ex.Message}"));
}
