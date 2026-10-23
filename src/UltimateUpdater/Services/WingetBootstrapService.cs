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
    /// scan: it is a no-op once winget is present or once this session has already asked. Every failure is
    /// logged and swallowed, the caller does not need its own try/catch, the winget source simply stays
    /// absent, exactly like it already does today when nothing installs it.
    /// </summary>
    public async Task OfferAsync(CancellationToken cancellationToken)
    {
        if (!NeedsOffer)
            return;

        // Set before the dialog, not after: a dialog dismissed by closing the app, or a re-entrant call
        // that races this one, must still count as "asked" so it can never show twice in one run.
        _offeredThisSession = true;

        if (!Dialogs.Confirm(
                LocalizationService.Instance["WingetBootstrapConfirmHeadline"],
                LocalizationService.Instance["WingetBootstrapConfirmBody"],
                confirmText: LocalizationService.Instance["WingetBootstrapInstall"]))
        {
            return;
        }

        try
        {
            await InstallAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
            or TaskCanceledException or OperationCanceledException)
        {
            _logging.Warn($"winget bootstrap: {ex.Message}");
        }
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(ReleaseApiUrl, cancellationToken);
        if (json is null)
        {
            _logging.Warn("winget bootstrap: could not read the latest App Installer release.");
            return;
        }

        var assetUrl = SelectAssetUrl(json);
        if (assetUrl is null)
        {
            _logging.Warn($"winget bootstrap: the latest release has no '{AssetName}' asset.");
            return;
        }

        var target = Path.Combine(Path.GetTempPath(), $"winget-bootstrap-{Guid.NewGuid():N}.msixbundle");
        FileStream? pin = null;
        try
        {
            await DownloadAsync(assetUrl, target, cancellationToken);
            pin = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Both checks read and hash the whole bundle (about 200 MB); this method continues on the UI
            // thread that showed the confirmation, so they run on the thread pool instead.
            var trusted = await Task.Run(() => TrustedBinary.IsSigned(target) && IsMicrosoftPublisher(target), cancellationToken);
            if (!trusted)
            {
                _logging.Warn("winget bootstrap: the downloaded package is not Microsoft-signed; refusing to install it.");
                return;
            }

            if (Security.MalwareScanner.ScanDownloads)
            {
                var outcome = await new Security.MalwareScanner(_logging).ScanFileAsync(target, cancellationToken);
                if (outcome.IsThreat)
                {
                    _logging.Warn($"winget bootstrap: the virus scan flagged the download ({outcome.Detail}); refusing to install it.");
                    return;
                }
            }

            // Add-AppxPackage installs a .msixbundle directly, picking the entry matching this machine's
            // architecture; no manual extraction needed. Per-user, so no elevation, unlike a winget PACKAGE
            // install (InstallQueue.NeedsElevation), which is a different question about a different tool.
            var command = BuildInstallCommand(target);
            var result = await ProcessRunner.RunAsync(
                "powershell", command.Arguments, InstallTimeout, cancellationToken, command.Environment);

            if (result.ExitCode != 0)
            {
                _logging.Warn($"winget bootstrap: Add-AppxPackage exited with {result.ExitCode}: {result.StandardError}");
                return;
            }

            _logging.Info("winget was installed on user request.");
        }
        finally
        {
            pin?.Dispose();
            FileDeletion.TryDelete(target);
        }
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
    internal static string? SelectAssetUrl(string releaseJson)
    {
        var release = SelfUpdate.UpdateManifest.ParseRelease(releaseJson, allowPrereleases: false);
        var asset = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, AssetName, StringComparison.Ordinal));
        return asset?.DownloadUrl is { Length: > 0 } url ? url : null;
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("UltimateUpdater");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ApiTimeout);

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(timeoutCts.Token) : null;
    }

    private async Task DownloadAsync(string url, string target, CancellationToken cancellationToken)
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
        int read;
        while ((read = await source.ReadAsync(buffer, timeoutCts.Token)) > 0)
        {
            received += read;
            if (received > MaxBundleBytes)
                throw new InvalidOperationException("the App Installer package exceeded the size limit mid-download; aborting.");

            await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token);
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
