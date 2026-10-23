using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.SelfUpdate;

public sealed record AvailableUpdate(string Version, ReleaseAsset Asset, ReleaseAsset ChecksumAsset, string ReleaseNotes);

/// <summary>What a check concluded. <see cref="Failed"/> is "could not tell" (network, timeout, unreadable
/// feed), never to be shown as "up to date".</summary>
public enum SelfUpdateCheckOutcome { None, Found, Failed }

public sealed record SelfUpdateCheckResult(SelfUpdateCheckOutcome Outcome, AvailableUpdate? Update = null);

/// <summary>Why a download did not produce a verified file: the network never delivered it, or what arrived
/// was refused by a verification step.</summary>
public enum SelfUpdateDownloadOutcome { Downloaded, NetworkFailed, VerificationFailed }

public sealed record SelfUpdateDownloadResult(SelfUpdateDownloadOutcome Outcome, string? Path = null);

/// <summary>
/// Self-update against GitHub Releases. Every step fails CLOSED: an unreadable
/// response, a missing checksum, a size mismatch or a digest mismatch all abort and delete the
/// download rather than proceeding. Nothing is ever executed that was not hash-verified first.
/// </summary>
public sealed partial class SelfUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/wbgcoding/UltimateUpdater/releases";
    private const string ChecksumFileName = "SHA256SUMS.txt";

    /// <summary>Refuse anything implausibly large before writing it to disk.</summary>
    private const long MaxAssetBytes = 300L * 1024 * 1024;

    /// <summary>
    /// The shared client runs without a timeout on purpose; its 100 second default also covers reading
    /// the response body, and these artifacts are well over 100 MB, so every download died part-way
    /// through. That left the OPERATIONS unbounded instead: a check that never answers hung for the rest
    /// of the process's life, holding whatever the caller passed. Each operation now carries its
    /// own deadline: seconds for the small JSON check, generous but finite for the download.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = ExternalCallTimeouts.Long;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>GitHub serves release downloads from release-assets.githubusercontent.com; the older
    /// objects.githubusercontent.com host stays allowed for assets still redirected there.</summary>
    private static readonly string[] AllowedHosts =
        ["api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    /// <summary>Name prefix of the per-download staging folder (see <c>DownloadVerifiedAsync</c>).</summary>
    internal const string StagingFolderPrefix = "UltimateUpdater-update-";

    /// <summary>Letters and digits only: an extension taken from a release asset gets no more than that.</summary>
    private static readonly System.Buffers.SearchValues<char> SafeExtensionChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

    private readonly HttpClient _httpClient;
    private readonly LoggingService _logging;
    private readonly IUpdateChannel _channel;

    /// <summary>Holds the verified artifact open so it cannot be swapped before it is executed.</summary>
    private FileStream? _verified;

    /// <summary>The digest the verified artifact actually hashed to; see <see cref="ApplyPortable"/>.</summary>
    private string? _verifiedDigest;

    /// <summary>The version the currently held artifact was verified as; carried from download to apply
    /// purely so the "applied" log line can name it; <see cref="Apply"/> only ever takes a path.</summary>
    private string? _verifiedVersion;

    /// <summary>Where the running image lives; a test points it at a temp folder so the portable swap never
    /// touches the test host's own executable.</summary>
    private readonly Func<string?>? _currentImagePath;

    public SelfUpdateService(
        HttpClient httpClient, LoggingService logging, IUpdateChannel channel, Func<string?>? currentImagePath = null)
    {
        _httpClient = httpClient;
        _logging = logging;
        _channel = channel;
        _currentImagePath = currentImagePath;
    }

    /// <summary>
    /// The running version INCLUDING a prerelease suffix ("0.4.0-beta.1"). The assembly version cannot carry
    /// one, so a beta build read as "0.4.0": it was never offered the next beta, nor 0.4.0 itself (equal),
    /// until 0.4.1. The csproj sets InformationalVersion to $(Version) without the commit hash.
    /// </summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public bool IsAvailable => _channel.IsAvailable;

    /// <summary>
    /// None when there is nothing newer (or the release carries no verifiable artifact); Failed when the
    /// check itself could not be completed, so the caller never reports "up to date" for it.
    /// </summary>
    public async Task<SelfUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!_channel.IsAvailable)
            return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);

        try
        {
            var json = await _channel.GetStringAsync(_httpClient, ReleasesApi, deadline.Token);
            if (json is null)
                return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.Failed);

            // The channel decides whether pre-releases count; the shipping channel takes none.
            if (!UpdateManifest.TryParseRelease(json, _channel.AllowPrereleases, out var release))
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, "check: the release feed could not be read");
                return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.Failed);
            }

            if (release is null || !UpdateManifest.IsNewer(CurrentVersion, release.Version))
                return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.None);

            var asset = SelectAssetForThisMachine(release);
            var checksums = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ChecksumFileName, StringComparison.OrdinalIgnoreCase));

            if (asset is null || checksums is null)
            {
                // an unsigned/unverifiable release is not an update we will take
                _logging.EventFailed(ActivityLog.SelfUpdate, "check: no verified artifact for this release");
                return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.None);
            }

            _logging.Event(ActivityLog.SelfUpdate, ActivityLog.SelfUpdateFound(release.Version));
            return new SelfUpdateCheckResult(
                SelfUpdateCheckOutcome.Found,
                new AvailableUpdate(release.Version, asset, checksums, release.ReleaseNotes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // our own deadline, not the caller's: a check that hangs is a failed check, not a cancelled app
            _logging.EventFailed(ActivityLog.SelfUpdate, "check timed out");
            return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.Failed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logging.EventFailed(ActivityLog.SelfUpdate, $"check failed: {ex.Message}");
            return new SelfUpdateCheckResult(SelfUpdateCheckOutcome.Failed);
        }
    }

    /// <summary>
    /// Downloads and verifies the artifact. On success the result carries the verified file path; otherwise
    /// nothing is left on disk and the outcome says whether the network failed (worth retrying) or a
    /// verification step refused the file (not).
    /// </summary>
    public async Task<SelfUpdateDownloadResult> DownloadVerifiedAsync(
        AvailableUpdate update, CancellationToken cancellationToken)
    {
        var callerToken = cancellationToken;

        // Nothing from the release feed may shape this path. The version is the raw tag name, and
        // ParseSemver discards a "+build" suffix, so a tag carrying path separators would pass the
        // is-this-newer check and have the download land outside %TEMP%. The GUID names the file; the
        // extension is the only thing taken from the asset, and only after it is checked.
        var extension = Path.GetExtension(update.Asset.Name);
        if (extension.Length is 0 or > 8 || extension.AsSpan(1).ContainsAnyExcept(SafeExtensionChars))
            extension = ".bin";

        // A fresh folder of its own, created right before the download: the installer may elevate and load
        // DLLs from the folder it runs in, which the shared temp root (writable by any process of this
        // user) must never be.
        var stagingFolder = FileDeletion.NewStagingFolderPath(Path.GetTempPath(), StagingFolderPrefix);
        var targetPath = Path.Combine(stagingFolder, $"UltimateUpdater-{Guid.NewGuid():N}{extension}");

        // Everything below runs under the caller's token AND this deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        cancellationToken = deadline.Token;

        try
        {
            var checksumBody = await _channel.GetStringAsync(_httpClient, ChooseUrl(update.ChecksumAsset), cancellationToken);
            if (checksumBody is null)
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.NetworkFailed);

            var checksums = UpdateManifest.ParseChecksums(checksumBody);
            if (!checksums.TryGetValue(update.Asset.Name, out var expectedDigest))
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, $"no checksum listed for '{update.Asset.Name}'");
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.VerificationFailed);
            }

            if (update.Asset.Size > MaxAssetBytes)
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, "artifact exceeds the size limit");
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.VerificationFailed);
            }

            Directory.CreateDirectory(stagingFolder);
            if (!await _channel.DownloadAsync(_httpClient, ChooseUrl(update.Asset), targetPath, MaxAssetBytes, cancellationToken))
            {
                // a refused or truncated download still created the file; nothing unverified stays behind
                _logging.EventFailed(ActivityLog.SelfUpdate, $"download of '{update.Asset.Name}' failed or was refused");
                TryDeleteStaged(targetPath);
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.NetworkFailed);
            }

            _logging.Event(ActivityLog.SelfUpdate, ActivityLog.SelfUpdateDownloaded(update.Asset.Name, update.Asset.Size));

            // Opened before hashing and held until the file has been started. Without a handle any
            // process running as this user can rewrite the file in %TEMP% between the hash and the
            // launch, and what runs is then not what was verified, which defeats the one control the
            // whole self-update rests on. FileShare.Read denies write and delete to everyone else
            // while still letting the loader read the image.
            _verified?.Dispose();
            _verified = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var actualDigest = await ComputeSha256Async(_verified, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualDigest),
                    Convert.FromHexString(expectedDigest)))
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, $"checksum mismatch for '{update.Asset.Name}', discarded");
                ReleaseVerified();
                TryDeleteStaged(targetPath);
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.VerificationFailed);
            }

            // Second opinion from the installed antivirus, on the file the checksum just proved is the one
            // the release published (opt-out, and fail-open: no scanner never blocks an update). A
            // detection here means the published artifact itself is bad; refuse it and keep the handle
            // released so nothing unverified stays on disk.
            var scan = await new Security.MalwareScanner(_logging).ScanFileAsync(targetPath, cancellationToken);
            if (scan.IsThreat)
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, $"antivirus flagged '{update.Asset.Name}' ({scan.Detail}), discarded");
                ReleaseVerified();
                TryDeleteStaged(targetPath);
                return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.VerificationFailed);
            }

            _verifiedDigest = actualDigest;
            _verifiedVersion = update.Version;
            _logging.Event(ActivityLog.SelfUpdate, ActivityLog.SelfUpdateVerified(update.Version));
            return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, targetPath);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // our own deadline, not the caller's: a download that never finishes is a network failure
            _logging.EventFailed(ActivityLog.SelfUpdate, "download timed out");
            ReleaseVerified();
            TryDeleteStaged(targetPath);
            return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.NetworkFailed);
        }
        catch (OperationCanceledException)
        {
            ReleaseVerified();
            TryDeleteStaged(targetPath);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logging.EventFailed(ActivityLog.SelfUpdate, $"download failed: {ex.Message}");
            ReleaseVerified();
            TryDeleteStaged(targetPath);
            return new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.NetworkFailed);
        }
    }

    /// <summary>Drops the handle that pins the verified artifact (see DownloadVerifiedAsync).</summary>
    private void ReleaseVerified()
    {
        _verified?.Dispose();
        _verified = null;
        _verifiedDigest = null;
        _verifiedVersion = null;
    }

    /// <summary>Win32 ERROR_CANCELLED: what a declined UAC prompt reports.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>True when the running copy sits in a Program Files folder, i.e. it was installed for all
    /// users (the per-user and portable folders are user-writable and live elsewhere).</summary>
    internal static bool IsMachineScopeInstall(string? baseDirectory, IReadOnlyList<string> programFilesRoots)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return false;

        string dir;
        try
        {
            dir = Path.GetFullPath(baseDirectory);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var root in programFilesRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            if (dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// How the verified setup is started. For an all-users install it is started elevated right here, while
    /// the verified file is still pinned, so Setup never has to start its own file again by path to get
    /// elevation. A per-user install needs no elevation and starts as before.
    /// </summary>
    internal static ProcessStartInfo BuildSetupStartInfo(string verifiedPath, bool machineScope)
    {
        var info = new ProcessStartInfo { UseShellExecute = true };
        ConfigureSetupStartInfo(info, verifiedPath, machineScope);
        return info;
    }

    private static void ConfigureSetupStartInfo(ProcessStartInfo info, string verifiedPath, bool machineScope)
    {
        info.FileName = verifiedPath;
        // FORCECLOSEAPPLICATIONS: without it Setup refuses to replace a running copy and
        // exits with code 5, having done nothing. RELAUNCH=1 starts this app again once
        // Setup is finished: the Restart Manager offers to do that and does not do it
        // reliably, so it is asked for explicitly.
        info.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS /RELAUNCH=1";
        if (machineScope)
            info.Verb = "runas";
    }

    /// <summary>
    /// Applies a verified artifact. Portable: swap the running exe aside and restart. Installed:
    /// run the verified installer silently, which updates in place and keeps settings.
    /// </summary>
    public bool Apply(string verifiedPath)
    {
        // Only the artifact THIS service verified and still holds open may be applied. Without these two
        // the method is a "run any file you name" entry point, and the portable digest comparison further
        // down silently passes when there is no digest to compare against.
        if (_verified is null || _verifiedDigest is null)
        {
            _logging.Error("Self-update: refusing to apply an artifact that was not verified in this session.");
            return false;
        }

        if (!string.Equals(
                Path.GetFullPath(verifiedPath), Path.GetFullPath(_verified.Name), StringComparison.OrdinalIgnoreCase))
        {
            _logging.Error("Self-update: refusing to apply a file other than the verified one.");
            return false;
        }

        // The installer keeps running from this file after we return, so only the portable path and
        // every failure path may delete it. What an installed update leaves behind is cleared by
        // SweepStaleDownloads on a later start, once it is old enough that no setup can still use it.
        var artifactStillInUse = false;
        var version = _verifiedVersion ?? "?";
        var installed = verifiedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && AppPaths.IsInstalled;

        try
        {
            if (installed)
            {
                var machineScope = IsMachineScopeInstall(AppContext.BaseDirectory, ProgramFilesPaths.Roots());
                try
                {
                    var setupInfo = new ProcessStartInfo { UseShellExecute = true };
                    ConfigureSetupStartInfo(setupInfo, verifiedPath, machineScope);
                    Process.Start(setupInfo);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    // The user declined the elevation prompt: nothing was started, nothing changes.
                    _logging.EventFailed(ActivityLog.SelfUpdate, "apply cancelled: the elevation prompt was declined");
                    return false;
                }

                artifactStillInUse = true;
                _logging.Event(ActivityLog.SelfUpdate, ActivityLog.SelfUpdateApplied(version, portable: false));
                return true;
            }

            if (ApplyPortable(verifiedPath))
            {
                _logging.Event(ActivityLog.SelfUpdate, ActivityLog.SelfUpdateApplied(version, portable: true));
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Everything, not a list: File.Move and File.Copy also raise NotSupportedException and
            // ArgumentException, and an update that cannot be applied must report a failure, never
            // take the app down.
            _logging.EventFailed(ActivityLog.SelfUpdate, $"apply failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (artifactStillInUse)
            {
                // Setup is already running (elevated for an all-users install, so it never starts its own
                // file again by path) and keeps running from this file after we return, so the pin is kept
                // until this process exits and the file is neither released nor deleted here.
                _verifiedDigest = null;
                _verifiedVersion = null;
            }
            else
            {
                ReleaseVerified();
                TryDeleteStaged(verifiedPath);
            }
        }
    }

    /// <summary>
    /// A running exe cannot be overwritten, but it CAN be renamed, so move it aside, put the new
    /// one in its place, and restart. If the swap fails halfway the old exe is moved back.
    /// </summary>
    private bool ApplyPortable(string verifiedPath) =>
        ApplyPortable(
            verifiedPath, _currentImagePath is null ? Environment.ProcessPath : _currentImagePath(), _verifiedDigest);

    /// <summary>The swap itself, with the image path and the verified digest passed in so a test can run it
    /// against a temp folder.</summary>
    internal bool ApplyPortable(string verifiedPath, string? currentPath, string? verifiedDigest)
    {
        if (currentPath is null)
            return false;

        var backupPath = currentPath + ".old";
        TryDelete(backupPath);

        // ElevationService holds this same file open (FileShare.Read, no delete) for the app's whole
        // lifetime so nobody else can rename it out from under a future elevated job, but that means we
        // cannot rename it either until we let go on purpose.
        ElevationService.ReleaseSelfImageLock();

        try
        {
            File.Move(currentPath, backupPath);
        }
        catch (Exception ex)
        {
            // Nothing was moved, so the running image is still the original: take the lock back, or the
            // rest of the session runs with nothing stopping a swap before the next elevated launch.
            _logging.EventFailed(ActivityLog.SelfUpdate, $"apply: could not move the running image aside: {ex.Message}");
            ElevationService.LockSelfImage(_logging, currentPath);
            return false;
        }

        try
        {
            return SwapIn(verifiedPath, currentPath, backupPath, verifiedDigest);
        }
        catch (Exception ex)
        {
            // Whatever failed after the original was moved aside, the original goes back and the lock with it.
            _logging.EventFailed(ActivityLog.SelfUpdate, $"apply: swap failed unexpectedly, rolling back: {ex.Message}");
            try
            {
                RollBack(backupPath, currentPath, _logging);
            }
            catch (Exception rollbackFailure)
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, $"apply: rollback failed: {rollbackFailure.Message}");
            }

            return false;
        }
    }

    private bool SwapIn(string verifiedPath, string currentPath, string backupPath, string? verifiedDigest)
    {
        // The pin on the DOWNLOAD proves the downloaded bytes are the verified ones, but what gets
        // started is the COPY, and between File.Copy and Process.Start any process running as this user
        // could rewrite it, so the app would launch something no checksum ever covered. The copy
        // is therefore pinned the same way (FileShare.Read: no write, no delete, the loader may still read
        // it) and re-hashed through that very handle before it is started. A mismatch rolls back.
        FileStream? copyPin = null;
        try
        {
            File.Copy(verifiedPath, currentPath, overwrite: true);
            copyPin = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.EventFailed(ActivityLog.SelfUpdate, $"apply: swap failed, rolled back: {ex.Message}");
            copyPin?.Dispose();
            RollBack(backupPath, currentPath, _logging);
            return false;
        }

        // The handle stays open THROUGH the launch; dropping it before Process.Start would reopen the
        // very window this closes.
        using (copyPin)
        {
            // Blocking is safe here: the only caller, Apply(), always runs inside
            // Task.Run(() => _selfUpdate.Apply(...)) (MainViewModel.SelfUpdate.cs), never on the dispatcher.
            var copyDigest = ComputeSha256Async(copyPin, CancellationToken.None).GetAwaiter().GetResult();
            if (verifiedDigest is not { } expected || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(copyDigest), Convert.FromHexString(expected)))
            {
                _logging.EventFailed(ActivityLog.SelfUpdate, "apply: installed copy does not match verified artifact, rolled back");
                copyPin.Dispose();
                RollBack(backupPath, currentPath, _logging);
                return false;
            }

            // Starting the new copy belongs INSIDE the transaction: a new exe that is in place but cannot
            // start must be rolled back, not left with the working old one sitting beside it as a .old
            // file nobody looks at.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentPath,
                    // The new copy waits for this process before it takes the single-instance lock.
                    Arguments = $"{AfterUpdateHandoff.Argument} {Environment.ProcessId}",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                // Everything, not a list, same reasoning as the catch in Apply above: Process.Start's
                // failure surface here is unbounded (Win32Exception, missing shell association, and more),
                // and whatever it is, the swap must roll back rather than leave the new exe stuck in place
                // unable to start.
                _logging.EventFailed(ActivityLog.SelfUpdate, $"apply: new copy would not start, rolled back: {ex.Message}");
                copyPin.Dispose();
                RollBack(backupPath, currentPath, _logging);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Puts the old image back and re-takes the anti-swap lock that had to be given up for the rename.
    /// Without the second half, a failed update left the app running for the rest of the session with
    /// nothing holding its own exe open, and the elevated worker is started BY PATH, which is exactly what
    /// that lock exists to protect. <see cref="ElevationService.LockSelfImage"/> fails closed on its own
    /// (elevation is refused for the session if it cannot retake the handle), so this needs no second
    /// answer for the case where even the rollback cannot be pinned.
    /// </summary>
    internal static void RollBack(string backupPath, string currentPath, LoggingService logging)
    {
        try
        {
            TryDelete(currentPath);
            File.Move(backupPath, currentPath);
        }
        catch
        {
            // The old image did not come back, so what sits at the path may be the unverified copy: do not
            // lock it as if it were trusted, refuse elevation for the session instead.
            ElevationService.RefuseElevation(logging, "the previous image could not be restored after a failed update");
            throw;
        }

        ElevationService.LockSelfImage(logging, currentPath);
    }

    /// <summary>
    /// Removes downloaded setups an earlier update left behind. Only names this class itself produces
    /// qualify (<c>UltimateUpdater-</c>, 32 hex digits, <c>.exe</c>) and only once they are old enough that
    /// no setup can still be running from them. Reparse points are skipped, never followed, and a file
    /// that cannot be removed is logged and left, never thrown.
    /// </summary>
    internal static void SweepStaleDownloads(string tempDirectory, TimeSpan minimumAge, LoggingService logging)
    {
        try
        {
            var cutoff = DateTime.UtcNow - minimumAge;
            foreach (var path in Directory.EnumerateFiles(tempDirectory, "UltimateUpdater-*.exe"))
            {
                if (!DownloadNamePattern().IsMatch(Path.GetFileName(path)))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LastWriteTimeUtc > cutoff)
                        continue;

                    info.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logging.Warn($"Could not remove an old update download: {ex.Message}");
                }
            }

            // The current layout: one fresh staging folder per download, holding the setup. The setup file
            // goes by the same name and age rules; the folder only when it is then empty (non-recursive).
            foreach (var folder in Directory.EnumerateDirectories(tempDirectory, StagingFolderPrefix + "*"))
            {
                if (!StagingFolderPattern().IsMatch(Path.GetFileName(folder)))
                    continue;

                try
                {
                    var folderInfo = new DirectoryInfo(folder);
                    if (folderInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    foreach (var file in folderInfo.EnumerateFiles("UltimateUpdater-*.exe"))
                    {
                        if (DownloadNamePattern().IsMatch(file.Name)
                            && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                            && file.LastWriteTimeUtc <= cutoff)
                            file.Delete();
                    }

                    if (folderInfo.LastWriteTimeUtc <= cutoff && !folderInfo.EnumerateFileSystemInfos().Any())
                        folderInfo.Delete(recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logging.Warn($"Could not remove an old update download folder: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not look for old update downloads: {ex.Message}");
        }
    }

    [GeneratedRegex(@"^UltimateUpdater-[0-9a-fA-F]{32}\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadNamePattern();

    [GeneratedRegex(@"^UltimateUpdater-update-[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex StagingFolderPattern();

    /// <summary>Deletes the leftover from a previous portable update; safe to call on every start.</summary>
    public static void CleanupPreviousUpdate()
    {
        var currentPath = Environment.ProcessPath;
        if (currentPath is not null)
            TryDelete(currentPath + ".old");
    }

    /// <summary>
    /// The fixed portable artifact name build.bat produces: no version, no architecture, and no
    /// "Portable" in it. It must match dist\ exactly: if these drift apart, a portable build can
    /// never find its own update asset. No spaces either: GitHub turns a space in an uploaded file
    /// name into a dot, so the asset would no longer carry this name.
    /// </summary>
    private const string PortableAssetName = "UltimateUpdater.exe";

    private ReleaseAsset? SelectAssetForThisMachine(ReleaseInfo release)
    {
        // installed builds take the installer, portable builds take the fixed-name portable exe
        if (AppPaths.IsInstalled)
        {
            // ONE installer carries both architectures and picks at install time, so its name has no
            // architecture in it. Requiring one here meant an installed copy never found an update.
            return release.Assets.FirstOrDefault(a =>
                a.Name.StartsWith("Setup-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        // The portable download is x64-only, so there is nothing to disambiguate by
        // architecture any more. An ARM64 machine running a portable copy is not a supported shape:
        // ARM64 users take the installer, which is the only place that architecture is offered.
        return release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, PortableAssetName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ChooseUrl(ReleaseAsset asset) =>
        string.IsNullOrEmpty(asset.DownloadUrl) ? asset.ApiUrl : asset.DownloadUrl;

    internal static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Hashes through the caller's own handle, so the bytes hashed are the bytes it holds.</summary>
    private static async Task<string> ComputeSha256Async(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path) => FileDeletion.TryDelete(path);

    private static void TryDeleteStaged(string path) => FileDeletion.TryDeleteStaged(path, StagingFolderPrefix);
}
