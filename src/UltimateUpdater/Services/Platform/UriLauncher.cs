using System.Diagnostics;

namespace UltimateUpdater.Services.Platform;

/// <summary>
/// Opens a vendor URI or launcher executable via the shell. This is the only place a provider-defined
/// target (URI or path) is handed to ShellExecute, and it never passes user-supplied text; only the fixed
/// targets the providers define.
/// </summary>
public static class UriLauncher
{
    /// <summary>
    /// The only URI schemes this app opens: the web, plus the fixed launcher protocols its Tier B/C rows hand
    /// off to. A row's open target can come from a vendor's API response (NVIDIA's DetailsURL), and accepting
    /// any scheme with a registered handler would let a click reach an arbitrary local protocol handler. New
    /// schemes belong here explicitly, alongside the source that needs them.
    /// </summary>
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        "steam",                        // SteamSource: downloads view, start, uninstall
        "ms-windows-store",             // MicrosoftStoreSource: downloads-and-updates page
        "com.epicgames.launcher",       // EpicGamesSource fallback when the exe cannot be resolved
        "battlenet",                    // Battle.net client, when only its protocol is registered
    };

    /// <summary>
    /// True when this URI may be handed to the shell. Public so a source can check a target read from a
    /// remote response at ingest time, rather than leaving it to the click.
    /// </summary>
    public static bool IsAllowedUri(string? target) =>
        !string.IsNullOrWhiteSpace(target)
        && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)
        && AllowedSchemes.Contains(uri.Scheme);

    /// <summary>
    /// Fire-and-forget launch for anything triggered from the UI. ShellExecute can hold the call for many
    /// seconds (a large application, a launcher client, a shell extension), which would freeze the window
    /// on the UI thread, and nothing here needs the result. <paramref name="allowNetworkPath"/> is for a folder
    /// of the app's own that the user may have put on a share; see <see cref="IsLaunchable"/>.
    /// </summary>
    public static void LaunchDetached(string target, LoggingService logging, bool allowNetworkPath = false) =>
        _ = Task.Run(() => Launch(target, logging, allowNetworkPath));

    /// <summary>
    /// Opens a folder of the app's own in Explorer, creating it first: Explorer answers a path that does not
    /// exist with an error box rather than a window. <paramref name="label"/> names the folder in the warning
    /// if it cannot be created.
    /// </summary>
    public static void OpenFolderDetached(string folder, string label, LoggingService logging)
    {
        EnsureFolder(folder, label, logging);
        LaunchDetached(folder, logging, allowNetworkPath: true);
    }

    internal static void EnsureFolder(string folder, string label, LoggingService logging)
    {
        try
        {
            System.IO.Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not create the {label}: {ex.Message}");
        }
    }

    public static bool Launch(string target, LoggingService logging, bool allowNetworkPath = false)
    {
        // A bare name would be resolved by ShellExecute's own search order, which includes the working directory,
        // so a planted file could be launched instead. Only a real URI or an existing absolute path is accepted.
        if (!IsLaunchable(target, allowNetworkPath))
        {
            logging.Warn($"Refusing to open '{target}': not an allowed URI scheme, and not an existing path.");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            // Runs inside a fire-and-forget Task.Run that nobody awaits, so any exception left uncaught would go to
            // the process-wide unobserved-task handler. Callers treat a false return as "could not open it" anyway.
            logging.Warn($"Could not open '{target}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// A URI on the allow list, or an existing fully qualified path. A target can stem from registry or vendor
    /// data, so a UNC or device path (<c>\\host\share</c>, <c>\\?\</c>) is refused before any file access:
    /// merely testing it would open an SMB session with the user's credentials. Only a folder of the app's own
    /// opts in with <paramref name="allowNetworkPath"/>.
    /// </summary>
    internal static bool IsLaunchable(string target, bool allowNetworkPath = false)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (System.IO.Path.IsPathFullyQualified(target))
        {
            if (!allowNetworkPath && !IsDriveLetterPath(target))
                return false;

            return System.IO.File.Exists(target) || System.IO.Directory.Exists(target);
        }

        return IsAllowedUri(target);
    }

    /// <summary>True for "X:\..." (or "X:/..."), the only fully qualified form that names a local or mapped drive.</summary>
    internal static bool IsDriveLetterPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
