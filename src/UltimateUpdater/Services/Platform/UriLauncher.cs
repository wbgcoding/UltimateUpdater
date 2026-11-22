using System.Diagnostics;

namespace UltimateUpdater.Services.Platform;

/// <summary>
/// Opens a vendor URI or launcher executable via the shell. This is the ONLY place
/// the app uses ShellExecute, and it never passes user-supplied text; only the fixed targets the
/// providers define.
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
    /// on the UI thread, and nothing here needs the result.
    /// </summary>
    public static void LaunchDetached(string target, LoggingService logging) =>
        _ = Task.Run(() => Launch(target, logging));

    /// <summary>
    /// Opens a folder of the app's own in Explorer, creating it first: Explorer answers a path that does not
    /// exist with an error box rather than a window. <paramref name="label"/> names the folder in the warning
    /// if it cannot be created.
    /// </summary>
    public static void OpenFolderDetached(string folder, string label, LoggingService logging)
    {
        EnsureFolder(folder, label, logging);
        LaunchDetached(folder, logging);
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

    public static bool Launch(string target, LoggingService logging)
    {
        // A bare name would be resolved by ShellExecute's own search order, which includes the working directory,
        // so a planted file could be launched instead. Only a real URI or an existing absolute path is accepted.
        if (!IsLaunchable(target))
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

    internal static bool IsLaunchable(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (System.IO.Path.IsPathFullyQualified(target))
            return System.IO.File.Exists(target) || System.IO.Directory.Exists(target);

        return IsAllowedUri(target);
    }
}
