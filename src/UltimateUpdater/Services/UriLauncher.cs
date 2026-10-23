using System.Diagnostics;

namespace UltimateUpdater.Services;

/// <summary>
/// Opens a vendor URI or launcher executable via the shell. This is the ONLY place
/// the app uses ShellExecute, and it never passes user-supplied text; only the fixed targets the
/// providers define.
/// </summary>
public static class UriLauncher
{
    /// <summary>
    /// The ONLY URI schemes this app ever opens: the web, plus the fixed launcher protocols its Tier B/C
    /// rows hand off to. Accepting any scheme with a registered handler was a real hole; a row's open
    /// target can come from a vendor's API response (NVIDIA's DetailsURL), and a scheme is exactly how one
    /// would reach an arbitrary local protocol handler on the back of a click in this window. New schemes
    /// belong here explicitly, alongside the source that needs them.
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
    /// True when this URI is one the app is allowed to hand to the shell. Public so a source can check a
    /// target it read from a remote response at INGEST time, rather than leaving it to the click.
    /// </summary>
    public static bool IsAllowedUri(string? target) =>
        !string.IsNullOrWhiteSpace(target)
        && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri)
        && AllowedSchemes.Contains(uri.Scheme);

    /// <summary>
    /// Fire-and-forget launch for anything triggered from the UI. ShellExecute is NOT quick: it starts the
    /// program, and a large application, a launcher client or a shell extension can hold that call for many
    /// seconds; on the UI thread the whole window freezes for exactly that long. Nothing here needs the
    /// result, so it does not wait for it.
    /// </summary>
    public static void LaunchDetached(string target, LoggingService logging) =>
        _ = Task.Run(() => Launch(target, logging));

    public static bool Launch(string target, LoggingService logging)
    {
        // A bare name would be resolved by ShellExecute's own search order, which includes the
        // working directory; a file planted there could be launched instead of the intended
        // launcher. Only a real URI or an existing absolute path is accepted.
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
            // This is called only through LaunchDetached's fire-and-forget Task.Run, never
            // awaited by its callers. The previous catch (Win32Exception or InvalidOperationException:
            // no handler registered for the URI, or the launcher is gone) named the two common cases, but
            // anything else Process.Start could throw had nowhere to go but the process-wide unobserved-
            // task handler. Every caller already treats a false return as "could not open it" and moves
            // on, so widening this to every exception only changes where an unusual failure (e.g.
            // Process.Start running out of handles) is reported, never what the app does about it.
            logging.Warn($"Could not open '{target}': {ex.Message}");
            return false;
        }
    }

    internal static bool IsLaunchable(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (System.IO.Path.IsPathRooted(target))
            return System.IO.File.Exists(target) || System.IO.Directory.Exists(target);

        return IsAllowedUri(target);
    }
}
