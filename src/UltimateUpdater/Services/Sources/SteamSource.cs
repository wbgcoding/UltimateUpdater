using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Tier C: Steam. Update state is read from Steam's own appmanifest files, the ACF only ever mirrors
/// what the Steam client already knows, so this reports rather than discovers. Games
/// are information only, deliberately not installable through this app: triggering a
/// Steam update from outside the client proved unreliable, so the row shows real version data, unlike
/// an ordinary Tier C row, but only ever offers "open in Steam", never a checkbox.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamSource : IUpdateSource
{
    private const string DownloadsUri = "steam://open/downloads";

    /// <summary>Steam's own public news API, no API key required.</summary>
    private const string NewsApiUrl =
        "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={0}&count=1&maxlength=3000&format=json";

    private static readonly TimeSpan NewsTimeout = ExternalCallTimeouts.Default;

    private readonly LoggingService _logging;
    private readonly Action<string> _launch;
    private readonly HttpClient? _httpClient;

    /// <summary>
    /// <paramref name="launch"/> defaults to the REAL shell hand-off; tests pass their own so that
    /// exercising the uninstall path never actually calls out to the shell, without this seam,
    /// <c>steam://uninstall/440</c> would fire for real on every `dotnet test`/`build.bat` run, popping
    /// Steam's own uninstall confirmation on whatever machine ran the build. <paramref name="httpClient"/> is optional (trailing,
    /// named at the registry call site) so every existing positional call, none of which exercise
    /// EnrichAsync, keeps compiling unchanged; a null client just means enrichment reports nothing.
    /// </summary>
    public SteamSource(LoggingService logging, Action<string>? launch = null, HttpClient? httpClient = null)
    {
        _logging = logging;
        _launch = launch ?? (uri => UriLauncher.LaunchDetached(uri, logging));
        _httpClient = httpClient;
    }

    public string Id => "steam";

    public UpdateCategory Category => UpdateCategory.Games;

    public SourceTier Tier => SourceTier.C;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FindSteamPath() is not null);

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var steamPath = FindSteamPath();
        if (steamPath is null)
            yield break;

        var apps = await Task.Run(() => ReadPendingUpdates(steamPath, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var games = apps.Select(app => new UpdateItem
        {
            Id = app.AppId,
            SourceId = Id,
            Category = Category,
            Name = app.Name,
            InstalledVersion = app.BuildId,
            AvailableVersion = app.TargetBuildId,
            // a game is not software this app makes a security claim about (SecurityRelevance)
            IsCritical = false,
            Tier = Tier,
            // the downloads view is where the user sees and controls the update Steam already
            // has queued, and it is the one steam:// target that opens no game
            OpenTarget = DownloadsUri,
            PublishDate = app.LastUpdated,
            DownloadSize = app.DownloadSize,
            Meta = app.InstallPath is { Length: > 0 } path
                ? new Dictionary<string, string> { ["installLocation"] = path }
                : new Dictionary<string, string>(),
        }).ToList();

        // ONE row: Steam is a launcher like any other, so it belongs in the
        // "open it yourself" launcher group rather than owning a whole category. The games it has
        // updates for hang under it as a collapsible sub-list, keeping their real build ids and their
        // own Start/Uninstall. The row appears whenever Steam is installed, updates pending or not.
        yield return new UpdateItem
        {
            Id = Id,
            SourceId = Id,
            Category = Category,
            Name = "Steam",
            InstalledVersion = null,
            AvailableVersion = null,
            IsCritical = false,
            Tier = Tier,
            OpenTarget = DownloadsUri,
            Children = games,
        };
    }

    /// <summary>
    /// Steam's own news feed for the app, no key required. Best effort: any failure (offline, the API
    /// down, an unparseable reply) or a genuinely empty feed falls back to the app's public news page
    /// rather than leaving the row with nothing. Never asked for the "Steam" launcher row itself, its id
    /// is not a numeric app id.
    /// </summary>
    public async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (_httpClient is null || !IsAppId(item.Id))
            return null;

        string json;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(NewsTimeout);
            json = await _httpClient.GetStringAsync(string.Format(NewsApiUrl, item.Id), timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logging.Info($"{Id}: could not reach Steam's news feed for {item.Id}: {ex.Message}");
            return null;
        }

        var fallbackUrl = $"https://store.steampowered.com/news/app/{item.Id}";
        var news = SteamNewsParser.ParseFirstNewsItem(json);
        if (news is null)
            return new UpdateEnrichment(null, null, ChangelogUrl: fallbackUrl);

        var body = news.Value.Contents is null
            ? null
            : ChangelogFormatter.Strip(SteamNewsParser.StripMarkup(news.Value.Contents));

        return new UpdateEnrichment(null, null, ChangelogBody: body, ChangelogUrl: news.Value.Url ?? fallbackUrl);
    }

    /// <summary>
    /// Deliberately unsupported: steam://install/&lt;appid&gt; and every other tried
    /// route to trigger a Steam update from outside the client proved unreliable in practice. Rather
    /// than offer a checkbox that sometimes silently does nothing, this row is information only, the
    /// "Open" button hands the user to Steam's own downloads view instead.
    /// </summary>
    public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} cannot install; open it in Steam instead.");

    public bool CanUninstall(UpdateItem item) => IsAppId(item.Id);

    /// <summary>
    /// Opens Steam's own uninstall confirmation (<c>steam://uninstall/&lt;appid&gt;</c>) and reports a
    /// manual handoff, never a completed removal. Nothing comes back from that dialog: whether the user
    /// confirmed, cancelled, or the URI failed to launch at all is genuinely unknown here. Reporting 100% would
    /// make the queue read "returned without throwing" as success and record a cancelled dialog as a
    /// completed removal; the same rule that forbids claiming a game update completed forbids claiming this
    /// one did. <see cref="ManualHandoffException"/> is the queue's own mechanism for exactly this,
    /// distinct from both success and failure.
    /// <see cref="UriLauncher.LaunchDetached"/> on
    /// purpose, not the synchronous overload: this runs off the install queue's async chain, which can
    /// still resume on the UI thread, and a blocking ShellExecute call there is the documented freeze this
    /// project already fixed once.
    /// </summary>
    public Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!IsAppId(item.Id))
        {
            _logging.Warn("steam: refusing an uninstall for a non-numeric app id.");
            throw new InvalidOperationException("steam: invalid app id.");
        }

        progress.Report(new Models.InstallProgress { PercentComplete = 0, StatusText = item.Name });
        _launch($"steam://uninstall/{item.Id}");
        throw new ManualHandoffException(LocalizationService.Instance["SteamUninstallHandedOff"]);
    }

    /// <summary>Starting a game is deliberate and user-initiated (the context menu), unlike an update.</summary>
    public string? LaunchTarget(UpdateItem item) => IsAppId(item.Id) ? $"steam://rungameid/{item.Id}" : null;

    /// <summary>
    /// The id was read out of an ACF file in a library folder that need not belong to this user, and it
    /// reaches the shell through LaunchTarget/UninstallAsync; Steam app ids are numeric, the icon lookup
    /// already insists on that, and so does this, an empty id is not a number either.
    /// </summary>
    private static bool IsAppId(string id) => id.Length > 0 && id.All(char.IsDigit);

    // internal: exercised directly by tests against a real temp directory tree (the malicious-installdir
    // regression needs a genuine appmanifest_*.acf on disk, not a mock).
    internal List<SteamApp> ReadPendingUpdates(string steamPath, CancellationToken cancellationToken)
    {
        var apps = new List<SteamApp>();

        foreach (var library in GetLibraryPaths(steamPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps))
                continue;

            // Materialised inside the guard, and catching access denials too: enumeration is lazy, so an
            // unreadable library on a second drive threw out in the loop below, outside every catch, and
            // took every remaining library's games with it.
            List<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamApps, "appmanifest_*.acf").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var app = SteamAcfParser.ParseAppManifest(File.ReadAllText(manifest));
                    if (app is { UpdateRequired: true })
                    {
                        // The game's files live in <library>\steamapps\common\<installdir>. Resolved here,
                        // where the library folder is known, so the row can offer "open folder". installdir
                        // comes from the manifest text, so a rooted value or a ".." segment must be refused
                        // before it reaches Path.Combine, otherwise "open folder" could point outside the
                        // Steam library entirely (a rooted value discards the library prefix outright).
                        var installPath = app.InstallDir is { Length: > 0 } dir && IsSafeInstallDirName(dir)
                            ? Path.Combine(steamApps, "common", dir)
                            : null;
                        apps.Add(app with { InstallPath = installPath });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // unreadable manifest, skip this game, keep scanning the rest
                }
            }
        }

        return apps;
    }

    /// <summary>A single path segment, never a traversal or an absolute path, same reasoning as
    /// OllamaParsers' model-name check: untrusted text destined for Path.Combine must not carry a
    /// separator, ".." would walk out of steamapps\common, and a rooted value makes Path.Combine discard
    /// the library prefix entirely.</summary>
    private static bool IsSafeInstallDirName(string dir) =>
        dir.IndexOfAny(['\\', '/']) < 0
        && !dir.Contains("..")
        && !Path.IsPathRooted(dir);

    private List<string> GetLibraryPaths(string steamPath)
    {
        var paths = new List<string> { steamPath };

        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFile))
                paths.AddRange(SteamAcfParser.ParseLibraryFolders(File.ReadAllText(libraryFile)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"steam: could not read libraryfolders.vdf: {ex.Message}");
        }

        // SteamPath in the registry is lower-case with forward slashes ("c:/program files (x86)/steam"),
        // while libraryfolders.vdf lists the same folder with backslashes: normalised, so the main library
        // is not scanned (and every pending game listed) twice.
        return paths.Select(NormalizeLibraryPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? NormalizeLibraryPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Internal so the icon cache can find Steam's own icon store for installed games.</summary>
    internal static string? FindSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
